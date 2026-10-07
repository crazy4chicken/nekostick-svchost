# 管理 API 参考

管理 API 的根路径为 `/svchost/api`. 除 `GET /svchost/api/status` 外, 其他端点都要求在 `X-Api-Key` 请求头中提供当前有效 key; 未通过认证时返回 `401`. Bootstrap 模式下使用日志中的 bootstrap key, 它也用于调用 `POST /svchost/api/bootstrap/key`.
只有成功读取并确认自身 settings 文档不存在时，`ExtensionSettingsChanged` 事件才会清除当前进程中的 permanent/bootstrap API key；读取失败时保留现有认证状态。事件处理不会自动重建 settings 或生成新的 bootstrap key。当前进程的受保护 API 只有在 Host 再次提供含有效 `apiKey` 的 settings 后才可认证。启动时若 settings 缺失但可读 Host snapshot 中仍有 svchost-owned routes、caller-owned services 或 pre-rename `nekolla.nekostick.svchost` settings row，扩展会 fail closed 并报告 `settings-missing`，要求恢复原 settings，不会清理 Host 配置；快照不可读时报告 `settings-unavailable`。

The extension requires a matched host with stable Contracts **1.4.0** signatures, `IExtensionHostBridge14`, and negotiated Host API **>=1.4.0 <2.0.0**. Upgrade the host and extension together. `HostApiVersion.Current` and `ExtensionAbi.Version` are `1.4.0`, so ABI/API negotiation alone does not establish stable package signature compatibility. See the pinned [upstream API contract](https://github.com/Nekolla-Team/nekostick/blob/da9c55a434812f5a326b3eb5e263851ff30cb7b4/docs/extension-api/api-1.4.md).

The host API result migration does not change `/svchost/api` routes, HTTP status mappings, JSON fields, or SSE framing. Errors retain the local `{ "error": { "code", "message" } }` shape and stable local codes; diagnostic messages preserve the operation's precise failure reason, rather than acting as machine keys.

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
| `DELETE` | `/svchost/api/configs/{name}` | `X-Api-Key` | 删除指定配置并在 settings 中将其有效 lock service IDs 持久化为 retirement entries; reconciliation 的 Stage A 原子提交关联服务禁用、受管路由更新和 settings, 不等待 Stage B; `report` 可显示 `removalPending`. 本地数据只在 Host 原子提交移除后清理. |
| `POST` | `/svchost/api/configs/{name}/sync` | `X-Api-Key` | 要求该配置存在, 然后触发全量 reconciliation; 本次会读取并处理 settings 中的所有配置. |
| `GET` | `/svchost/api/services` | `X-Api-Key` | 返回受管服务的启用状态、lock source、生命周期/健康状态、运行时信息及 reconciliation 状态. |
| `POST` | `/svchost/api/services/{config}/{service}/{action}` | `X-Api-Key` | 请求单个服务 `start`、`stop` 或 `restart`; `start`/`stop` 会持久化更新配置的 `stopped` 列表并触发全量 reconcile. `restart` 直接调用本节点 Host `RestartAsync`, 不写入全局配置或执行全量 reconcile; 进程状态由 Host 异步处理. |
| `GET` | `/svchost/api/configs/{config}/services/{service}/logs?file=N` | `X-Api-Key` | Read one page from a service's rotating log files. |
| `GET` | `/svchost/api/configs/{config}/services/{service}/logs/tail?fromLine=M` | `X-Api-Key` | Replay current-file lines and stream new lines using SSE. |

## Service logs

`GET /svchost/api/configs/{config}/services/{service}/logs?file=N` returns one existing log file:

```json
{
  "service": "api",
  "file": 0,
  "fileCount": 2,
  "lineCount": 2,
  "lines": ["<raw log line>", "<raw log line>"]
}
```

`file` is zero-based: `0` is the current/newest file, and `1` through `4` are progressively older archives. It defaults to `0` when omitted. `fileCount` reports the number of existing files; `lineCount` and `lines` describe the selected file, with lines ordered oldest to newest. An unknown configuration or service, or an unavailable file index, returns `404`.

`GET /svchost/api/configs/{config}/services/{service}/logs/tail?fromLine=M` returns `Content-Type: text/event-stream`. `fromLine` is the zero-based index in the current file to replay and defaults to `0`; replay is best-effort if rotation occurs. Each line is sent as `data: {"line":"<JSON-escaped log line>"}\n\n`. The stream sends a `: ping` comment every 15 seconds and ends when the request is cancelled. If live logging is unavailable, the current-file replay is sent and the stream then ends.

## Service status

`GET /svchost/api/services` is read-only. Each managed-service entry includes desired state (`enabled`), lock identity (`serviceId`, `routeIds`), host-runtime fields, and the following status fields:

- `source`: the full source-lock object, using the same shape as config lock responses. Depending on the source it can include `kind`, `url`, `path`, `providerKey`, `spec`, `tag`, `assetName`, `size`, `version`, `sha256`, `fetchedAt`, and other source-specific metadata; it is `null` when the service has no lock entry.
- `consecutiveFailures`: the number of consecutive `Failed` lifecycle observations (read-path polls and post-reconcile checks), not a count of distinct failures. The counter resets on any other observed lifecycle state and is cleared when the current runtime snapshot is absent; it is in memory and resets when the extension restarts. Its value scales with poll and reconcile frequency. It is `0` when the current runtime snapshot is absent.
- `lastReconcile`: `null` when there is no applicable cached report. A matching service entry projects `{ "completedAt", "succeeded", "trigger", "decision", "diffs" }`, where `succeeded` is the service entry's outcome and `diffs` contains `{ "field", "oldValue", "newValue" }` values (environment values are already masked). If the effective report is a failed whole-run report with an empty service list, it instead projects `{ "completedAt", "succeeded": false, "trigger", "decision": null, "diffs": [], "error?", "errorKind?" }`.
- `decision`: wire values are `reused`, `updated`, `preserved`, `failed`, `skipped`, and `removalPending`; the last indicates that the service is disabled while Host removal is still pending.
- `driftCorrected`: `true` only when the matching service decision is `updated`, the trigger is `startup` or `drift-host-version`, and `diffs` is non-empty; otherwise `false`.

With the matched Contracts 1.4.0 host, the runtime snapshot carries failure diagnostics: `runtime.failureCode` / `runtime.failureReason` / `runtime.processExitCode` / `runtime.restartCount` / `runtime.retryAt` (null or zero while healthy), and a failed service's top-level `detail` shows `failureReason` instead of the stale health state. `lastReconcile` comes from the handler's in-memory cache and is `null` until an applicable service or run-level failure report is recorded; it resets on extension restart.
Read-path and post-reconcile supervisor reads are fetched before entering the tracker lock, so concurrent observations may apply out of order; the count is advisory.

`PUT /svchost/api/configs/{name}` 会校验并保存单份 Compose YAML, 但不会预先检查它与其他配置的 global 服务名冲突. 若保存后发现冲突, 本次请求仍返回 `200` 和 `succeeded: false` 的同步报告; 配置已写入, 冲突会继续阻断后续 reconciliation, 直到修复.

`DELETE /svchost/api/configs/{name}` 会从 settings 删除配置, 并将 lock 中的有效 service ID 记录到 `retiring`; 首次 reconciliation 原子提交服务禁用、受管路由更新和 settings, 不等待 Stage B. 后续 reconciliation 才尝试移除服务, 并仅在 Host 接受移除时才从 Host 与 `retiring` 中一起删除. 若本轮没有提交 removal (例如 Stage A 刚禁用服务, 或 Stage B 收到 Host `Validation` 如仍有活跃 port lease), 报告包含 `errorKind: "removalPending"` 和 service `decision: "removalPending"`; 后续 reconciliation 会重试. 本地清理只发生在对应 Host removal commit 之后: `document` scope 删除未引用的 `artifacts`/`tmp` 内容, 但保留 `logs` 与其他用户数据; `global` scope 保留仍被其他 global 配置声明的 service 名称. 当前服务/lock 引用或可能仍在运行的 runtime 状态会保护相应 artifacts. YAML 无法解析时, 删除操作将 scope 按 `global` 处理.
若 supervisor telemetry 缺失或读取失败，本轮会跳过 artifact 与 document-directory 清理. Content-addressed cleanup sweeps every `svchost` root that holds an `artifacts/sha256` store — including former roots no configuration maps to and roots without an active content-addressed service — and prunes generations that are neither referenced by a committed service nor pinned by a settings lock digest; for an active service, older generations are pruned by any committed reconcile only once telemetry confirms the committed process generation is running (`Running`/`Starting` with `StartedAt` at or after the commit's `UpdatedAt`). The generation in use stays protected by the Host service path and the lock digest, and reconciles without a commit skip disk cleanup entirely.

响应状态: 配置 PUT 和手动 sync 对已接受的 reconciliation 结果返回 `200` report; 最终 Host concurrency conflict 返回标准 `{ "error": { "code": "conflict", "message": ... } }` envelope 和 `409`, settings write conflict 也返回 `409`. DELETE 在配置删除的 settings 写入成功后返回 `200` `{ "deleted": true, "name", "report" }`, 即使 report 表示 reconcile 失败或 `removalPending`; settings 写入本身失败时仍返回错误状态. 服务 start/stop/restart 的 Host 或其他操作失败使用 `200` action payload 且 `succeeded: false`; 因而 disabled service 的 Host `RestartAsync` `Validation` 不会变成 HTTP `422`. 不存在的配置、服务或 action 返回 `404`; 无效 Compose 在保存或服务操作中返回 `422`.

配置列表和详情的 `lastSync` 仅保存在当前 API handler 的内存中, 并在每次 reconcile 完成时更新, 包括 API 触发、启动和事件驱动的 reconcile. 运行级失败（包括 reconciler exception）也会被记录; 对于配置级报告缺失或早于该运行级失败的配置, `lastSync` 会显示运行级失败, 否则显示较新的配置级报告. 扩展重启后 `lastSync` 为 `null`; 配置级报告仅在 report.Services 含该配置时更新.

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
