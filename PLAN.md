# nekostick-svchost 实现计划

> nekostick (https://github.com/Nekolla-Team/nekostick) 的扩展: 通过声明式配置文件 100% 可复现地同步 host 上的全局 (非 extension-scoped) microservice, 提供 `/svchost/api` 管理 API 与 `/svchost` WebUI.

本文档基于 nekostick `main` 分支 Contracts **1.3.2** (`HostApiVersion.Current`) 的实际契约调研编写, 所有引用的符号均来自 `Nekolla.Nekostick.Contracts`.

## 0. 已确认的决策

| 决策点 | 结论 |
| --- | --- |
| 配置文件格式 | 自定义简化 YAML (不追求 docker compose 兼容), YamlDotNet 解析 |
| 可复现机制 | lock 文件: 首次解析来源后记录 sha256 等锁定信息, 存进 extension settings, 后续按 lock 复现 |
| 路由管理 | 配置文件内声明路由 (path prefix 等), svchost 同步服务的同时同步 host 路由 |
| API 认证 | `X-Api-Key` 请求头 |
| readonly 检测 | 启动探测: 原样写回 settings, 写操作返回 `Unsupported` 即判定 readonly, 不注册 API 路由 |

## 1. 平台契约要点 (调研结论, 实现的硬约束)

- 扩展是 host 进程内的可信 .NET 程序集, 目标框架 **net10.0**, 唯一引用 `Nekolla.Nekostick.Contracts` **1.3.2**, SDK `10.0.100` (global.json, latestFeature).
- 入口类型实现 `IExtensionEntrypoint` (别名 `IExtensionEntry`): `StartAsync(IExtensionStartContext, CancellationToken)` / `StopAsync(CancellationToken)`; 部署为 `extensions/<id>/` 目录下的 `manifest.json` + dll.
- **所有 Guid 必须是 UUID v7** (`Guid.CreateVersion7()`), 否则 DTO 构造函数抛 `ArgumentException`.
- 配置写操作全部乐观并发: 读快照拿 `Version` → 携带 `expectedVersion` 写 → `ConcurrencyConflict` 则重读重试. 业务失败以 `ConfigurationError.Code` 表达 (`Validation`/`ConcurrencyConflict`/`NotFound`/`Unsupported`/`StorageUnavailable`), 不抛异常.
- **全局服务/路由没有增量 CRUD**: 只有 `IExtensionFullConfigurationApi.ReadAsync()` → `HostConfigurationSnapshot` 和 `ReplaceAsync(expectedVersion, ConfigurationChangeSet)` 的**整体原子替换**; `ConfigurationChangeSet` 中省略的 routes/services/extensionRecords/extensionSettings 会被删除. 因此同步必须是「读全量 → 外科手术式替换 managed set → 整体写回」.
- 全局 `ServiceConfiguration` (ServiceContracts.cs) 含 `Environment` 字典, 支持 env 复现; owner-scoped 的 `ExtensionServiceConfiguration` 反而没有 env —— 所以必须走 FullConfiguration 路线, 不能用 `bridge.Services`.
- `RouteConfiguration` 有 `MetadataJson` (extension-owned) 可打所有权标记; **`ServiceConfiguration` 没有 metadata 字段**, 服务的所有权只能由 settings lock 中记录的 serviceId 追踪.
- 路由匹配 `RouteMatcherConfiguration(RouteMatcherType, pattern, hostPatterns, methods)`, `RouteMatcherType.Prefix` 支持段/原始前缀; 目标 `MicroserviceRouteTargetConfiguration(serviceId)` / `ExtensionHandlerRouteTargetConfiguration(handlerId)`; 转发 `ForwardingConfiguration(ForwardingMode.Preserve|Strip|Replace, ...)`.
- 全局服务**没有** start/stop/restart API (owner-scoped 的 `IExtensionServiceApi.StartAsync` 等只管 extension-owned 服务). 启停 = 改 `Enabled` 后 `ReplaceAsync`, host 监督器自动 reconcile (technical-design §6.2: 新实例先启动并验证健康, 切路由, 再停旧实例; 新实例不健康则保留旧实例). `ReplaceAsync` 发布后启停是**异步**的监督行为, 不保证同步完成.
- 端口由 host 分配 loopback 端口租约, 通过 `$PORT` 替换传入服务参数.
- 设置: `IExtensionConfigurationApi.ReadSettingsAsync()` / `WriteSettingsAsync(expectedVersion, ExtensionSettingsConfiguration)`; `ExtensionSettingsConfiguration(extensionId, schemaVersion, settingsJson, version)` 的 `SettingsJson` 是原始 JSON 字符串; 未初始化时 `bridge.Configuration.Settings` 为 `null`.
- 设置变更事件: `context.Host.Events.TrySubscribe(callback)`, 过滤 `@event.Type == nameof(ExtensionCoreEventKind.ExtensionSettingsChanged)` (API 1.3.2 新增), payload 仅含 `{ extensionId }`, 需要收到后自行 `ReadSettingsAsync`.
- 数据目录: `IExtensionHostBridge13.DataDirectory` (string, 空串 = 不可用, 需要 API >= 1.3.2).
- 自定义文本日志: `IExtensionHostBridge13.LogWriter.WriteText(ExtensionLogLevel, string)` (API 1.3); 分类日志 `IExtensionLogger.Report(ExtensionLogLevel, code)`.
- 流式 handler: `IExtensionStreamingHandler.HandleStreamingAsync(ExtensionStreamingRequest, CancellationToken)` → `ExtensionStreamingResponse(statusCode, headers, bodyStream)`; 响应流所有权在回调结束后移交给 host, host 从当前位置读取; 经 `IExtensionRegistration.TryRegisterStreamingHandler` 注册.
- **readonly 无法查询**: `HostRuntimeOptions.ReadOnly` 不暴露给扩展, 只能从写操作的 `Unsupported` 错误推断 (用户已确认用启动探测).
- 让流量到达 handler 需要持久化一条 `target` 指向 handlerId 的路由配置 (owner-scoped 用 `IExtensionRouteApi.UpsertAsync`).

结论: manifest 的 `requiredHostApiVersion` 写 `">=1.3.2 <2.0.0"` (DataDirectory / 流式 handler / ExtensionSettingsChanged 都是 1.3.2 能力).

## 2. 仓库结构

```
nekostick-svchost/
├── nekostick-svchost.slnx          # XML slnx, /src 与 /tests 两个 solution folder (对齐 host 仓库惯例)
├── global.json                     # sdk 10.0.100, rollForward latestFeature
├── Directory.Build.props           # Nullable=enable, ImplicitUsings, LangVersion, 警告级别
├── Directory.Packages.props        # CPM: Nekolla.Nekostick.Contracts 1.3.2, YamlDotNet, xunit 等
├── src/
│   └── Nekostick.Svchost/
│       ├── Nekostick.Svchost.csproj
│       ├── manifest.json           # 构建时复制到输出目录
│       ├── SvchostEntry.cs         # IExtensionEntry 入口
│       ├── Settings/               # settings JSON 模型与读写
│       ├── Compose/                # YAML 配置模型 + 解析 + 校验
│       ├── Sync/                   # 来源解析, lock, reconcile 管线
│       ├── Api/                    # 管理 API 流式 handler + 路由 + 认证
│       └── Webui/                  # WebUI 流式 handler (嵌资源)
├── tests/
│   └── Nekostick.Svchost.UnitTests/
├── webui/                          # Vue 3 + Vite + naive-ui
└── deploy/                         # 本地部署脚本 (复制 dll+manifest 到 host extensions/)
```

- 程序集名 `Nekostick.Svchost`, 扩展 id `nekostick.svchost` (小写, 符合 manifest id 规则).
- `manifest.json`: `schemaVersion: 1`, `id: nekostick.svchost`, `entryAssembly: Nekostick.Svchost.dll`, `entryType: Nekostick.Svchost.SvchostEntry`, `dependencies: []`, `requiredHostApiVersion: ">=1.3.2 <2.0.0"`.

## 3. Settings 模型与 bootstrap 模式

Settings 是 extension settings 里的单个 JSON 文档 (`SettingsJson`), `schemaVersion = 1`:

```json
{
  "apiKey": null,
  "routes": { "api": "<uuidv7>", "webui": "<uuidv7>" },
  "configs": {
    "<configName>": {
      "yaml": "<原始 YAML 文本>",
      "lock": { }
    }
  }
}
```

- `apiKey`: 用户设置的永久 key; `null`/缺失 = 未设置.
- `routes`: svchost 自己两条 handler 路由的 UUID v7 (见 §6), 首次运行生成后持久化, 保证 reload/重启后 upsert 同一路由而不是制造重复.
- `configName` 校验: `^[a-z0-9][a-z0-9-]{0,62}$`, 防路径穿越 (同时是 data 子目录名).
- 读写全部走 `ReadSettingsAsync` / `WriteSettingsAsync(expectedVersion, ...)`, 带冲突重试 (最多 3 次).

### Bootstrap 状态机 (每次 `StartAsync` 重新判定)

1. `ReadSettingsAsync`:
   - settings 为 `null` (未初始化) → 构造初始 schema (`apiKey: null`, 空 configs) 尝试写入; 写入返回 `Unsupported` → 判定 **readonly** (见 §7).
   - settings 已初始化 → 原样写回一次 (内容不变, version +1); 返回 `Unsupported` → 判定 **readonly**.
   - 返回 `StorageUnavailable` → 不判定 readonly, 上报 `ExtensionStatusKind.Degraded` 并按有限次数重试.
2. 非 readonly 且 `apiKey` 为空 → 进入 **bootstrap 模式**:
   - 用 `RandomNumberGenerator` 生成 256-bit 一次性 key, Base64Url 编码, **只保存在内存字段**, 不落盘.
   - 通过 `bridge13.LogWriter.WriteText(ExtensionLogLevel.Warning, "...bootstrap api key: <key>...")` 写入 host 日志.
   - 每次扩展初始化 (含 reload) 都重新检测、重新生成, 旧 key 立即作废.
3. 用户在 bootstrap 模式下通过 API 设置自己的 key → `WriteSettingsAsync` 持久化 → 内存切换到新 key, **永久退出 bootstrap** (之后启动 `apiKey` 非空, 不再生成一次性 key).

API key 校验统一用 `CryptographicOperations.FixedTimeEquals` 防时序侧信道; 当前生效 key = 持久化的 `apiKey` ?? 内存 bootstrap key.

## 4. 配置文件模型 (自定义简化 YAML)

每个命名配置文件一份 YAML, 原文存 settings, data 目录下对应 `{DataDirectory}/svchost/<configName>/` 子目录:

```yaml
services:
  my-api:
    source:
      url: https://example.com/releases/my-api-linux-x64   # 在线来源 (与 path 二选一)
      # path: /opt/bin/my-api                              # 本地来源
      sha256: <可选的期望哈希>                               # 声明则强校验
    args: ["--serve", "--port", "$PORT"]                    # 参数; $PORT 由 host 替换为分配的 loopback 端口
    env: { MODE: production }                              # 环境变量 (全局 ServiceConfiguration.Environment)
    start: eager | lazy                                    # 默认 eager; lazy = 首个请求才启动
    restart: never | on-failure | always                   # 默认 on-failure
    health:                                                # 默认 { type: process }
      type: process | tcp | http
      path: /healthz                                       # type=http 必填
      timeout: 5s
    route:                                                 # 可选; 声明则由 svchost 同步 host 路由
      prefix: /api/my                                      # RouteMatcherType.Prefix
      strip: true                                          # ForwardingMode.Strip (默认 Preserve)
      methods: [GET, POST]                                 # 可选
      hosts: [api.example.com]                             # 可选
```

校验规则: 服务名 `^[a-z0-9][a-z0-9-]{0,62}$`; `source.url`/`source.path` 恰有一个; `url` 必须是 https (http 拒绝, 复现性前提); `health.type=http` 必须有 `path`; `route.prefix` 必须以 `/` 开头; 未知字段报错 (严格模式, 防拼写错误静默失效).

## 5. 同步管线 (100% 可复现的核心)

### 5.1 数据目录布局

```
{DataDirectory}/
  svchost/
    <configName>/
      artifacts/<serviceName>     # 可执行文件本体 (host 配置 FileName 指向这里)
      tmp/                        # 下载临时文件, 完成后原子 rename
```

`DataDirectory` 为空串 → sync 不可用, 上报 Degraded 并拒绝写类 API 调用.

### 5.2 lock 模型 (存于 settings 的 `configs.<name>.lock`)

```json
{
  "services": {
    "my-api": {
      "source": { "kind": "url", "url": "...", "sha256": "...", "size": 12345, "fetchedAt": "..." },
      "serviceId": "<uuidv7>",
      "routeIds": ["<uuidv7>"]
    }
  }
}
```

### 5.3 来源解析 (SourceResolver)

- **url 来源**:
  - lock 存在且 `source.url` 未变 → 校验本地 artifact 的 sha256 与 lock 一致: 一致则**零网络复现**; artifact 缺失/损坏 → 按原 url 重新下载, 下载结果必须匹配 lock 的 sha256, 不匹配则报错 (来源内容漂移, 无法复现), 该服务标记失败, 不启动.
  - `source.url` 变了 → 视为新来源: 下载到 `tmp/` → 算 sha256 → 原子 rename 到 `artifacts/` → 更新 lock.
  - YAML 里显式声明了 `sha256` → 与解析结果双向校验.
- **path 本地来源**: 每次 sync 对原文件重算 sha256; 与 lock 不同 → 复制进 `artifacts/` 并更新 lock (本地文件漂移视为来源变更); 之后 host 只引用 data 目录内的副本, 原文件被删/改不影响已部署服务.
- 下载: `HttpClient` (handler 池化单例), 限时 + 限大小 (默认上限 512MB, 可配置), 失败重试 2 次指数退避.
- 原子性: 任何情况下先写 `tmp/` 再 rename; 旧 artifact 在新 artifact 校验通过前不删除 (配合 host 的蓝绿式服务切换, 见 §5.4).

### 5.4 Reconciler (声明式调和, 不重启 host)

触发源: 启动 / `ExtensionSettingsChanged` 事件 (防抖 500ms 合并连续变更) / API 写操作 / 手动 sync. 全程串行化 (SemaphoreSlim), 防止并发 reconcile 交错.

1. 读 settings → 解析所有配置文件 → 得出期望服务集 (含 source 解析结果).
2. 计算期望的全局 `ServiceConfiguration` 与 `RouteConfiguration`:
   - `serviceId`/`routeIds` 从 lock 复用 (**稳定身份**, 避免每次新建导致无意义重启); 新服务用 `Guid.CreateVersion7()`.
   - `FileName` = data 目录内 artifact 绝对路径; `WorkingDirectory` = 该 config 子目录; `Environment` 来自 YAML `env`; `Enabled` 默认 true.
   - 路由 `MetadataJson` 写入 `{"owner":"nekostick.svchost","config":"<name>","service":"<svc>"}` 作为所有权标记 (服务无 metadata 字段, 以 lock 中的 id 为准).
3. `FullConfiguration.ReadAsync()` 拿全量快照.
4. 构造 `ConfigurationChangeSet` (**整体替换语义, 遗漏即删除**, 因此):
   - `GlobalSettings` / `ExtensionRecords` / `ExtensionSettings` → 快照原样透传 (含自己的 settings 快照版本, 自己的 settings 更新永远走 `WriteSettingsAsync`, 不经过这里).
   - `Services` = 快照.Services 去掉 (所有 lock 记录的 managed serviceId + 期望集里的 id) ∪ 期望服务集.
   - `Routes` = 快照.Routes 去掉 (managed routeIds ∪ `MetadataJson` 标记为 svchost 所有的孤儿路由) ∪ 期望路由集. 孤儿路由清理是双保险; 孤儿服务无法标记, 依赖 lock 追踪, 在文档中注明该限制.
5. `ReplaceAsync(snapshot.Version, changeSet)`; `ConcurrencyConflict` → 重读快照重建 changeSet 重试 (上限 5 次).
6. 成功后 `WriteSettingsAsync` 持久化更新后的 lock; 若 settings 写冲突同样重试.
7. 每个服务产出 reconcile 结果 (成功/失败原因), 聚合为 sync report 供 API 返回与 status 上报.

启停语义: 停止 = 期望集中 `Enabled=false` 或移除条目 → ReplaceAsync 后监督器停进程; 启动 = 反向; **restart = disable → reconcile → enable → reconcile 两步** (平台无全局 restart API, 如实告知调用方); 更新 = host 侧蓝绿切换 (新实例健康检查后切路由再停旧), 这是 host 内建行为, 天然满足「不重启 host」.

## 6. 管理 API (`/svchost/api`)

- 一个流式 handler `nekostick.svchost.api`, handler 内按 `request.Path` 自路由; 所有端点 JSON 进JSON 出.
- 路由注册: `StartAsync` 时用 `bridge.Routes.UpsertAsync` 幂等建两条属主路由 (id 持久化在 settings.routes):
  - matcher `Prefix /svchost/api` → `ExtensionHandlerRouteTarget("nekostick.svchost.api")`, priority 100
  - matcher `Prefix /svchost` → `ExtensionHandlerRouteTarget("nekostick.svchost.webui")`, priority 10
- 认证: 除 `GET /status` 外全部要求 `X-Api-Key` 与当前生效 key 恒定时间比较; 失败 401.
- readonly 实例: 启动探测判定后**不注册这两条路由** (WebUI 离开 API 无意义, 一并不注册), 记日志说明.

| 端点 | 认证 | 说明 |
| --- | --- | --- |
| `GET /svchost/api/status` | 否 | `{ bootstrap: bool, version, dataDirectoryAvailable, configs: number }`; WebUI 首屏调它判定是否提示设置 key |
| `POST /svchost/api/bootstrap/key` | bootstrap key | 仅 bootstrap 模式可用; body `{ apiKey }`, 校验强度 (>=16 字符) 后持久化, 返回新状态; 退出 bootstrap |
| `GET /svchost/api/configs` | 是 | 列出所有配置文件: name, 服务清单, lock 摘要, 最近 sync 结果 |
| `GET /svchost/api/configs/{name}` | 是 | 单个配置: 原始 YAML + lock |
| `PUT /svchost/api/configs/{name}` | 是 | body `{ yaml }`; 校验 → 写 settings → reconcile → 返回 per-service sync report; 校验失败 422 不落盘 |
| `DELETE /svchost/api/configs/{name}` | 是 | 移除配置 → reconcile (摘除其服务/路由) → 删除 data 子目录 |
| `POST /svchost/api/configs/{name}/sync` | 是 | 强制重新解析来源 (按 lock 校验/重下) 并 reconcile |
| `GET /svchost/api/services` | 是 | managed 服务清单 + 运行态 (经 `IExtensionHostBridge13.Supervisor` 遥测 `ReadAsync`/`GetAsync`) |
| `POST /svchost/api/services/{config}/{service}/start` | 是 | `Enabled=true` + reconcile |
| `POST /svchost/api/services/{config}/{service}/stop` | 是 | `Enabled=false` + reconcile |
| `POST /svchost/api/services/{config}/{service}/restart` | 是 | disable→reconcile→enable→reconcile, 响应中注明该语义 |

错误格式统一 `{ "error": { "code": "...", "message": "..." } }`: 401 未认证 / 403 状态不允许 (如非 bootstrap 调 bootstrap 端点) / 404 配置不存在 / 409 并发冲突重试耗尽 / 422 配置校验失败 / 502 来源拉取失败.

## 7. WebUI (`/svchost`)

- `webui/` 目录: **Vue 3 + Vite + TypeScript + naive-ui**, pnpm 管理, `vite-plugin-singlefile` 产出单个自包含 `dist/index.html`.
- 嵌入: csproj 中 `<EmbeddedResource Include="..\..\webui\dist\index.html" LogicalName="Nekostick.Svchost.webui.index.html" />`; MSBuild 增量 target (Inputs=webui/src 等, Outputs=dist/index.html) 自动跑 `pnpm install --frozen-lockfile` + `pnpm build`; 环境变量 `SVCHOST_SKIP_WEBUI_BUILD=1` 可跳过 (CI/无 node 环境用预构建产物).
-  serving: 流式 handler `nekostick.svchost.webui`; `GET /svchost` 及 `/svchost` 下非 `/api` 路径 → 每次请求新开 manifest resource stream 作为 `ExtensionStreamingResponse` 的 `BodyStream` (位置在 0, host 从当前位置读), `Content-Type: text/html; charset=utf-8`, `Cache-Control: no-cache`.
- 页面行为:
  - 首屏 `GET /svchost/api/status`; `bootstrap: true` → naive-ui `n-alert` 强提醒 + 引导流程: 输入 host 日志里的一次性 key + 设置新 key (`POST /bootstrap/key`), 成功后把新 key 存 `localStorage`, 之后所有请求带 `X-Api-Key`.
  - 视图: 配置列表 / 配置编辑器 (YAML 编辑, naive-ui 表单 + 校验错误展示) / 服务仪表盘 (运行态, start/stop/restart/sync 按钮) / sync report 展示.
- 构建产物约 1MB 级 (naive-ui 全量内联), 单文件流式返回可接受; 如需精简可后续开 naive-ui 按需引入, 计划中不做.

## 8. 生命周期接线 (`SvchostEntry`)

`StartAsync`:
1. 能力检查 `ExtensionAbi.IsApi13Supported(context.Host.ApiVersion)`, 不满足 → 记日志 + `StopAsync` 前直接返回 (manifest 已挡低版本, 双保险).
2. settings 初始化/readonly 探测/bootstrap 判定 (§3).
3. 非 readonly: 注册两个流式 handler + upsert 两条属主路由.
4. 订阅 `Host.Events` 的 `ExtensionSettingsChanged` → 防抖触发 reconcile.
5. `bridge.Tasks.StartAsync("sync", ...)` 启动时全量 reconcile 一次.
6. 上报 `ExtensionStatusKind.Healthy`.

`StopAsync`: 注销 handler (`TryUnregisterHandler`), 取消后台任务, 等待进行中的 reconcile 退出 (带超时). 管理的全局服务/路由**不**随扩展停止而删除 (它们是 host 全局资产, 配置仍在; 如需清理由用户走 API).

## 9. 测试计划 (`tests/Nekostick.Svchost.UnitTests`, xunit)

- YAML 解析/校验: 合法模型, 未知字段拒绝, 双来源冲突, http url 拒绝, health/route 条件校验.
- lock 语义: url 不变零网络路径 (mock HttpClient), artifact 损坏重下 + hash 不匹配报错, 本地来源漂移重锁.
- Reconciler 差分: 纯函数测试 —— 给定快照 + 期望集, 验证 changeSet 中全局设置/扩展记录/扩展设置原样透传, 非 managed 服务/路由不受影响, 孤儿路由被清理.
- settings 状态机: 未初始化 → 初始化; apiKey 空 → bootstrap; 设置 key 后退出 bootstrap; readonly 探测对 `Unsupported`/`StorageUnavailable` 的分支.
- API handler: 认证矩阵 (无 key/错 key/bootstrap key/永久 key), 各端点状态码, bootstrap 端点在非 bootstrap 下 403.

## 10. 实施顺序

1. 仓库脚手架 (slnx/global.json/props/csproj/manifest/空入口).
2. Settings 模型 + 初始化 + readonly 探测 + bootstrap 状态机.
3. YAML 模型 + 解析校验.
4. SourceResolver + lock + 数据目录.
5. Reconciler 差分 + ReplaceAsync 重试循环.
6. API handler + 认证 + 路由注册.
7. webui 脚手架 + 构建嵌入 + WebUI handler.
8. 事件订阅 + 状态/日志接线.
9. 单元测试.
10. 端到端验证: 部署到本地 host, 走通 bootstrap → 设 key → PUT 配置 → 服务起来 → 改配置实时生效 → 删配置清理 的全流程.

## 11. 已知限制 (设计内, 非缺陷)

- 全局服务无 metadata 字段: 孤儿服务 (lock 丢失但服务残留) 无法自动清理, 只能按 lock 追踪; 路由有 `MetadataJson` 双保险.
- restart 语义为 disable/enable 两步, 有秒级窗口; 平台无全局 restart API.
- readonly 探测每次启动使 settings version +1 (内容不变), 用户已确认接受.
- `ReplaceAsync` 后服务启停是 host 监督器的异步行为, API 返回成功 ≠ 进程已就位; 运行态以 `GET /services` 的监督遥测为准.
