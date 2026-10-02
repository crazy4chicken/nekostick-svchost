# 管理 API 参考

管理 API 的根路径为 `/svchost/api`. 除 `GET /svchost/api/status` 外, 其他端点都要求在 `X-Api-Key` 请求头中提供当前有效 key; 未通过认证时返回 `401`. Bootstrap 模式下使用日志中的 bootstrap key, 它也用于调用 `POST /svchost/api/bootstrap/key`.

## 端点

| 方法 | 路径 | 认证 | 说明 |
| --- | --- | --- | --- |
| `GET` | `/svchost/api/status` | 无 | 返回 bootstrap 状态、扩展版本、data directory 可用状态及配置数量. |
| `POST` | `/svchost/api/bootstrap/key` | `X-Api-Key` | Bootstrap 模式下设置永久 API key; body 为 `{"apiKey":"..."}`, `apiKey` 字符串长度至少为 16. 不在 bootstrap 模式时返回 `403`. |
| `GET` | `/svchost/api/settings` | `X-Api-Key` | 读取 settings API 当前注册的 settings groups. |
| `PUT` | `/svchost/api/settings` | `X-Api-Key` | 按 settings group 更新扩展设置, 详见下文. |
| `GET` | `/svchost/api/configs` | `X-Api-Key` | 列出配置名称、服务、`serviceScope`、`strictSources`、lock 摘要及最近一次同步报告; YAML 无法解析时 `serviceScope` 与 `strictSources` 为 `null`. |
| `GET` | `/svchost/api/configs/{name}` | `X-Api-Key` | 读取指定配置的 YAML、lock 和最近一次同步报告. |
| `PUT` | `/svchost/api/configs/{name}` | `X-Api-Key` | 创建或更新配置并触发全量 reconciliation; body 为 `{"yaml":"<compose YAML>"}`. |
| `DELETE` | `/svchost/api/configs/{name}` | `X-Api-Key` | 删除指定配置, 并触发其服务、路由及本地配置产物的清理; 清理方式按 `serviceScope` 区分, 详见下文. |
| `POST` | `/svchost/api/configs/{name}/sync` | `X-Api-Key` | 要求该配置存在, 然后触发全量 reconciliation; 本次会读取并处理 settings 中的所有配置. |
| `GET` | `/svchost/api/services` | `X-Api-Key` | 返回受管服务的启用状态、生命周期/健康状态及运行时信息. |
| `POST` | `/svchost/api/services/{config}/{service}/{action}` | `X-Api-Key` | 请求单个服务 `start`、`stop` 或 `restart`; `start`/`stop` 会持久化更新配置的 `stopped` 列表并触发全量 reconcile. `restart` 先禁用并全量 reconcile, 成功后再启用并执行第二次全量 reconcile. 生命周期变更由 Host 异步调和, 不保证请求返回时进程状态已完成切换. |

`PUT /svchost/api/configs/{name}` 会校验并保存单份 Compose YAML, 但不会预先检查它与其他配置的 global 服务名冲突. 若保存后发现冲突, 本次请求仍返回 `200` 和 `succeeded: false` 的同步报告; 配置已写入, 冲突会继续阻断后续 reconciliation, 直到修复.

`DELETE /svchost/api/configs/{name}` 按该配置可解析出的 `serviceScope` 清理数据: `document` 删除整个 `<data>/svchost/{name}` service root; `global` 只删除该配置对应的 `<data>/svchost/global/artifacts/<serviceName>` 目录, 服务名取可解析 YAML 的声明与 lock 条目的并集, 并保留仍由其他 global 配置声明或锁定的名称. 若该配置 YAML 无法解析, scope 默认按 `global` 处理, 此时可用服务名来自 lock.

配置列表和详情的 `lastSync` 仅保存在当前 API handler 的内存中, 并在每次 reconcile 完成时更新, 包括 API 触发、启动和事件驱动的 reconcile. 扩展重启后 `lastSync` 为 `null`; 若某次 report 中不含某配置, 该配置的缓存不会更新.

配置 YAML 的字段与校验规则见 [Compose 配置参考](compose.md).

## Settings groups

`GET /svchost/api/settings` 返回一个以 group 名为 key 的 JSON object. 当前注册的 group 为 `releaseProviders`:

```json
{
  "releaseProviders": {
    "github": {
      "mirrors": ["https://ghproxy.net/"]
    }
  }
}
```

`PUT /svchost/api/settings` 接受 JSON object, 每个顶层属性必须是已注册的 group. Body 是部分 settings document: 未出现的 group 保持不变; 出现的 group 整体替换, 不做 group 内的递归合并. 例如只提交 `releaseProviders` 会保留其他未提交 group, 但会以请求中的整个 `releaseProviders` dictionary 替换现有值.

```http
PUT /svchost/api/settings
X-Api-Key: <api-key>
Content-Type: application/json

{"releaseProviders":{"github":{"mirrors":["https://ghproxy.net/"]}}}
```

`releaseProviders` 的形状为 `{ "<provider>": { "mirrors": ["<URL prefix>"] } }`. 每个 mirror 必须是非空的绝对 HTTP 或 HTTPS URL, 且包含 host. Mirror URL 作为 asset 下载 URL 的前缀使用; HTTP 可用但不加密. 未知 group、无效 group 内容或无效 mirror 会返回 `400`.

provider key 不会与已注册的 provider 列表比对, 因此未知 key 可被 settings API 接受, 但对应 release source 会在同步时因 provider 不存在而失败. provider object 中未知字段会被 JSON deserializer 忽略; 省略 `mirrors` 时默认为空列表, 显式 `null` 会校验失败. 下载时显式 `source.sha256` 优先, 否则 GitHub asset digest 存在时使用该摘要; 两者都没有时首次下载没有上游完整性校验, 但同一 release identity 的后续重下载会校验已有 lock SHA.

`releaseProviders` 仅影响 release asset 下载候选地址; 列表按配置顺序尝试, 然后回退到 provider 返回的官方 asset URL. GitHub release metadata 仍直接请求 GitHub API.

## 校验与错误

配置 YAML 校验失败以及 bootstrap key 请求体/`apiKey` 校验失败返回 `422`; settings JSON、group 或 mirror 校验失败返回 `400`. `{name}`、`{config}` 或 `{service}` 不符合 `^[a-z0-9][a-z0-9-]{0,62}$` 时返回 `404`. 配置名 `{name}`、`{config}` 还不能是 `global` (大小写不敏感); settings 校验将其报告为非法配置名, 对应 API 路径返回 `404`. 此保留规则只适用于配置名, 服务名 `global` 合法.
