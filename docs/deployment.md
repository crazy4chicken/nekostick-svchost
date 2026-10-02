# 多节点部署

- 每个节点部署**同一份** svchost 构建产物; host 会对内容摘要漂移的扩展做节点级隔离
- 为每个 `url`、`path`、`release` 来源显式声明 `sha256`. 根级 `strictSources: true` 会拒绝任何缺少显式摘要的来源; 默认 `false` 或省略时仍接受无摘要来源并给出 non-fatal warning.
- 缺少本地 executable 时服务进入 `Waiting` 及其 backoff/retry 属于 Host 行为. 只有 reconciliation 成功调用 `ReplaceAsync` 后, svchost 才会对当前处于 `Waiting` 的受管服务尽力调用 `ResumeAsync`; 失败只写入 Debug 日志, 无变化的 no-op reconcile 不会触发 nudge.
- 产物是节点本地的: 每个节点独立解析来源, `path` 来源必须在每个节点的该路径存在; 新节点无法从其他节点重建本地 path 来源.
- Global `ServiceConfiguration` 保存节点本地的绝对 artifact/working-directory 路径. 多节点共享全局配置时, 所有节点必须使用相同的绝对 data directory 路径.
- Bootstrap key 按节点保存在内存中, 每次启动重新生成且不落盘. 负载均衡请求若携带节点 A 的 bootstrap key 却被转发到节点 B, 会因 key 不同而返回 `401`; 永久 key 设置后各节点使用 settings 中的同一 key.
- Read-only 实例跳过 API/WebUI 注册与全部 reconciles, 不会执行启动、后台或 API 触发的同步.
- `serviceScope: global` (默认) 的配置在每个节点共享 `<data>/svchost/global`. reconcile 按配置名 ordinal 顺序扫描 global 配置, 首个声明某服务名的配置占用该名称; YAML 无法解析的配置不占用名称. 后续 global 配置若重复声明任一已占用名称, 整份配置中所有服务均报告失败, 且其其他非冲突服务名也不会被注册; `document` 配置不参与全局去重.
- global 服务名冲突会阻断整轮 reconcile: 不会替换 Host 配置、持久化 lock 或调用 `ResumeAsync`, 所以占用名称的配置及无关配置 (包括 `document`) 均不会生效. 其他配置的来源仍可能已解析, 个别服务报告可能显示成功, 但不表示服务已上线; 冲突存在期间后续 reconcile 仍会被阻断.
Managed service stdout and stderr are recorded under `<data directory>/svchost/<config|global>/logs/<service>.log`, with rotation at 1,000 lines per file and five files retained. This is enabled only when the host provides the API 1.4 service-output capability; otherwise it is silently disabled.

