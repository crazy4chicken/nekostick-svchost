# 多节点部署

Deploy the extension with a matched host implementing `IExtensionHostBridge14` and stable Contracts **1.4.0** signatures, with negotiated Host API **>=1.4.0 <2.0.0**. Upgrade every node's host and extension together. The `1.4.0` semantic ABI/API values alone do not prove that the matching stable Contracts signature set is installed.
- 每个节点部署**同一份** svchost 构建产物; host 会对内容摘要漂移的扩展做节点级隔离
- 为每个 `url`、`path`、`release` 来源显式声明 `sha256`. 根级 `strictSources: true` 会拒绝任何缺少显式摘要的来源; 默认 `false` 或省略时仍接受无摘要来源并给出 non-fatal warning.
- A missing local executable starts in Host `Waiting` state. After resolving sources and any Host write, reconciliation best-effort calls `ResumeAsync` for each managed service whose local runtime is `Waiting` and whose desired executable exists. It also nudges on no-op reconciliations; `NoOp` and `Validation` results are informational, and runtime convergence remains asynchronous.
- 产物是节点本地的: 每个节点独立解析来源, `path` 来源必须在每个节点的该路径存在; 新节点无法从其他节点重建本地 path 来源.
- Global `ServiceConfiguration` 保存节点本地的绝对 artifact/working-directory 路径. 多节点共享全局配置时, 所有节点必须使用相同的绝对 data directory 路径.
- Bootstrap key 按节点保存在内存中, 每次启动重新生成且不落盘. 负载均衡请求若携带节点 A 的 bootstrap key 却被转发到节点 B, 会因 key 不同而返回 `401`; 永久 key 设置后各节点使用 settings 中的同一 key.
- Read-only 实例跳过 API/WebUI 注册与全部 reconciles, 不会执行启动、后台或 API 触发的同步.
- `serviceScope: global` (默认) 的配置在每个节点共享 `<data>/svchost/global`. reconcile 按配置名 ordinal 顺序扫描 global 配置, 首个声明某服务名的配置占用该名称; YAML 无法解析的配置不占用名称. 后续 global 配置若重复声明任一已占用名称, 整份配置中所有服务均报告失败, 且其其他非冲突服务名也不会被注册; `document` 配置不参与全局去重.
- global 服务名冲突会阻断整轮 reconcile: 不会替换 Host 配置、持久化 lock 或调用 `ResumeAsync`, 所以占用名称的配置及无关配置 (包括 `document`) 均不会生效. 其他配置的来源仍可能已解析, 个别服务报告可能显示成功, 但不表示服务已上线; 冲突存在期间后续 reconcile 仍会被阻断.
Managed service stdout and stderr are recorded under `<data directory>/svchost/<config|global>/logs/<service>.log`, with rotation at 1,000 lines per file and five files retained. The required API 1.4 bridge exposes service output; if the matched host reports `Unsupported` for log capture, recording is disabled and a warning is logged. This is not an old-host compatibility path.

## Managed service ownership and conflicts

svchost reconciles managed services and routes back to the desired state on every reconciliation. Use the svchost API's `start`, `stop`, and `restart` actions to operate managed services. Direct changes to the Host configuration surface are external drift. Reconciliations that detect it restore svchost's desired state; only startup or Host-version-drift reconciliations emit an external-drift warning. A committed configuration write is reported as corrected; if no write commits, the warning says correction was not committed. If a pending settings event is superseded by a drift tick, the preserved `settings-event` trigger may log the correction at information level rather than as an external-drift warning. Managed-service status is exposed through the API, where reconciliation outcomes and failures are visible.

`RouteConfiguration.OwnerExtensionId` and the existing svchost-managed-route metadata identify owned routes. Stable Contracts `1.4.0` `ServiceConfiguration` carries no owner field; managed service ownership comes from the caller-scoped `IExtensionServiceApi.ReadOwnedAsync` view and svchost configuration locks, not from names in the global snapshot.

`restart` invokes node-local Host `RestartAsync` without changing cluster-wide desired state. `start` and `stop` remain persisted desired-state updates followed by reconciliation.

Reconciliation submits the observed svchost `ExtensionSettings` row with the full Host replacement. Managed services/routes and lock or retirement updates commit atomically at the observed row version; no later lock write can overwrite a concurrent settings change. A settings-version mismatch aborts the replace as a concurrency conflict, and a later reconciliation must consume the updated settings.

## Observability

The optional `observability.logLevel` setting defaults to `information` when omitted, `null`, or unrecognized; `warning` is matched case-insensitively. At `warning`, informational reconciliation summaries and per-service decision lines are suppressed. Startup/Host-version-drift external-drift warnings (including uncommitted corrections) and whole-reconcile failures remain logged at warning level.

