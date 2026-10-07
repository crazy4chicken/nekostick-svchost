# Compose 配置参考

svchost 使用自有的简化 YAML 方言, 每份配置必须是单个 YAML 文档; 不是 Docker Compose 格式.

## 完整模板

```yaml
strictSources: false # 可选, 默认 false; true 时每个来源都必须声明 sha256.
serviceScope: global # 可选, 默认 global; document 时按配置名隔离服务目录.
services: # 必填 mapping, key 是服务名.
  my-api: # 服务名也作为 release asset 的 serviceId.
    source: # 必填 mapping; url/path/release 恰好选择一个.
      release: "github:owner/repo@v1.2.3" # release provider 来源, 格式见下文.
      sha256: "0000000000000000000000000000000000000000000000000000000000000000" # 64 位十六进制 SHA-256 占位值, 使用前替换为真实摘要.
    args: # 可选字符串列表; 仅列表值支持 launch templates.
      - "--listen" # 普通命令行参数.
      - "${HOST}:${PORT}" # Host 为本次启动提供的动态值.
    env: # 可选环境变量 mapping; key 不展开, value 支持 launch templates.
      MODE: production # 环境变量普通字符串值.
      UPSTREAM_HOST: "${HOST:UPSTREAM_HOST}" # 透传 Host 环境变量.
    start: eager # eager = 配置同步时启动, lazy = 首次请求时启动; 默认 eager.
    restart: on-failure # never = 不重启, on-failure = 失败后重启, always = 总是重启; 默认 on-failure.
    health: # 可选健康检查 mapping; 默认 type: process, timeout: 5s.
      type: http # process 检查进程存活, tcp 检查 loopback TCP endpoint, http 检查 HTTP endpoint; http 需要 path.
      path: /healthz # HTTP 健康检查路径, 必须以 / 开头.
      timeout: 5s # 正数时长, 支持 ms/s/m/h.
    route: # 可选 host 路由 mapping.
      prefix: /api/my # 必填路径前缀, 必须以 / 开头.
      strip: true # true 转发前移除 prefix; 默认 false.
      methods: [GET, POST] # 可选 HTTP method 字符串列表.
      hosts: [api.example.com] # 可选 host 约束字符串列表.
```

示例中的摘要只是格式占位值, 必须替换为当前来源的真实 SHA-256. `services` 下的 key 必须符合 `^[a-z0-9][a-z0-9-]{0,62}$`.

配置名也必须符合 `^[a-z0-9][a-z0-9-]{0,62}$`; `global` (大小写不敏感) 是保留配置名, settings 校验会以 `configs.{name} has an invalid name.` 拒绝它. 此限制只适用于配置名, `services.global` 仍是合法服务名.

## source 来源与 sha256

`source` 必须是 mapping, 且 `url`、`path`、`release` 中只能提供一个. `sha256` 与所选来源同级, 可用于三种来源:

```yaml
source:
  url: "https://example.com/my-api" # 必须是绝对 HTTP 或 HTTPS URL.
  sha256: "0000000000000000000000000000000000000000000000000000000000000000" # 替换为下载文件的 SHA-256.
```

`http://` URL 来源可用, 但 HTTP 不加密传输或凭据; 显式 `sha256` 仅校验下载字节, 不会加密凭据.

```yaml
source:
  path: "/opt/bin/my-api" # 本节点上的本地文件路径.
  sha256: "0000000000000000000000000000000000000000000000000000000000000000" # 替换为本地文件的 SHA-256.
```

```yaml
source:
  release: "github:owner/repo@v1.2.3" # provider:spec, GitHub spec 为 owner/repo@ref.
  sha256: "0000000000000000000000000000000000000000000000000000000000000000" # 替换为下载 ZIP asset 的 SHA-256.
```

`sha256` 必须恰好包含 64 个十六进制字符, 大小写均可. 若省略 `strictSources`, 其默认值为 `false`; 未声明摘要时来源仍可通过配置校验, 但会产生非致命 warning. 设置 `strictSources: true` 后, `url`、`path`、`release` 都必须显式声明 `sha256`, 缺失会使配置校验失败. URL 摘要校验下载文件, path 摘要校验本地文件, release 摘要校验 ZIP asset.

`source.sha256` 在一份 YAML 中只有一个固定值; release 会按本节点架构选择 ZIP, 并校验该 ZIP 的摘要. 多架构节点共享同一 YAML 时, 若各架构 ZIP 摘要不同, 单个显式 `sha256` 无法同时匹配; `strictSources: true` 仍要求显式摘要.

对于 GitHub release, 显式 `source.sha256` 优先于 API metadata 中的 asset digest. 未声明时, 若 GitHub 提供可识别的 `sha256:<64 位十六进制>` digest, svchost 会用它校验下载; 两者都没有时, 首次下载没有上游完整性校验, 但 lock 会记录实际下载摘要, 同一 release identity 的后续重下载会校验已有 lock SHA.

## GitHub release 来源

`source.release` 使用 `{provider}:{spec}` 格式. 当前内置 provider key 是 `github`, 其 spec 格式为 `owner/repo@ref`, 例如 `github:owner/repo@v1.2.3`. 未注册的 provider 会在同步解析来源时失败. GitHub provider 还要求恰好一个 `/`, owner/repo 非空且只含 ASCII 字母、数字、`_`、`.`、`-` (不允许 `.` 或 `..`), 并且 `ref` 非空; 格式错误会在同步时失败.

- asset 名必须符合 `{serviceId}_{version}_{arch}.zip`; `serviceId` 是 `services` 中的服务 key, `version` 不能为空. `serviceId` 必须匹配 `^[a-z0-9][a-z0-9-]{0,62}$`; asset 名的服务名前缀和架构/扩展名后缀按大小写敏感方式匹配.
- `arch` 按 svchost 所在进程架构选择: `x64`、`arm64`、`arm` 或 `x86`. 其他进程架构无法解析 release asset.
- `ref` 按以下顺序分类: 7–40 位十六进制字符串优先作为 commit 前缀; 否则尝试 SemVer; 都不匹配时作为精确 tag. SemVer 解析接受小写 `v` 前缀.
- commit 前缀按 GitHub release 的 `target_commitish` 前缀匹配, asset 的 `version` 段也必须以该前缀开头; 发布此类 release 时应将 `target_commitish` 设为目标 commit SHA, 而不是通常不匹配的分支名.
- SemVer ref 只有在 release tag 与 ref 的 SemVer identity 匹配, 且至少一个候选 asset 的 `version` 也匹配时才成功; 没有匹配 asset 时拒绝. SemVer identity 忽略 build metadata.
- 其他 ref 必须与 release tag 完全相同. 该分类不以 asset version 拒绝候选; 若选中 asset 的 `version` 字符串与 release tag 不完全相同, 会产生 warning.
- ZIP 会整体解压, 入口文件必须位于解压根目录, 且文件名与 `serviceId` 完全相同. Windows 还接受 `{serviceId}.exe`; POSIX 使用无扩展名的 `{serviceId}`. POSIX 安装时 svchost 只将入口权限设置为 `0755` (`rwxr-xr-x`), 不恢复 ZIP 中其他文件的 Unix 权限位.
- ZIP 内的根路径条目或解压后越出解压目录的路径会导致安装失败; 指向同一输出路径的重复文件条目也会失败.
- 服务进程的 CWD 等于当前 service root, 不是 ZIP 解压目录; 随包资源应通过入口程序自身路径定位, 不要依赖 CWD. 具体路径见下文“路径解析”.

GitHub provider 使用未认证的 `GET /repos/{owner}/{repo}/releases?per_page=100`, 只读取第一页最多 100 个 release, 不分页; 会跳过 draft, 但不排除 prerelease. 因不带认证, 只能读取公开仓库且受 GitHub unauthenticated API rate limits 限制. 每次 reconcile 都会对每个 GitHub release source 重新查询 metadata, 然后才检查本地 lock 是否可复用.

GitHub release asset 下载镜像在扩展设置的 `releaseProviders` group 中配置, 不是 compose 字段:

```json
{
  "releaseProviders": {
    "github": {
      "mirrors": ["https://ghproxy.net/"]
    }
  }
}
```

每个 mirror 是 URL 前缀, svchost 将其与 GitHub 提供的 asset 下载 URL 直接拼接, 按列表顺序尝试, 全部失败后尝试官方 asset URL. GitHub release metadata 始终直接请求 `https://api.github.com`. 该设置的 API 用法见 [管理 API 参考](api.md).

mirror 接受 HTTP 和 HTTPS; HTTP 不提供传输加密. 显式 `source.sha256` 优先用于校验下载, 否则 GitHub asset digest 存在时使用该摘要; 两者都没有时, 首次下载没有上游完整性校验, 但同一 release identity 的后续重下载仍会与已有 lock SHA 比对.

## 路径解析

每份配置通过根级 `serviceScope` 选择 service root. `global` (默认) 与 `document` 的根目录如下; 两种来源解析、artifact、临时下载/解压和服务进程 CWD 都使用对应 root.

| `serviceScope` | service root | source artifacts | 临时文件 | 服务 CWD |
| --- | --- | --- | --- | --- |
| `global` | `<data>/svchost/global` | `<root>/artifacts/sha256/<serviceName>/<sha256>/` | `<root>/tmp/` | `<root>` |
| `document` | `<data>/svchost/<configName>` | `<root>/artifacts/sha256/<serviceName>/<sha256>/` | `<root>/tmp/` | `<root>` |

URL/path 产物位于 `<root>/artifacts/sha256/<serviceName>/<sha256>/<serviceName>`; 同一服务的不同摘要使用独立且不原位覆盖的 generation. Release ZIP 在 `<root>/tmp/` 下载并解压到 staging, 然后整份 bundle 原子安装到其摘要目录, Host executable path 指向该 generation. 旧版 `<root>/artifacts/<serviceName>` 仅在不再被配置/lock、Host service 或活跃 runtime 引用后于 Host commit 之后清理. Content-addressed cleanup sweeps every `svchost` root that holds an `artifacts/sha256` store — including former roots no configuration maps to and roots without an active content-addressed service — and prunes generations that are neither referenced by a committed service nor pinned by a settings lock digest; for an active service, older generations are pruned by any committed reconcile only once supervisor telemetry confirms the committed process generation is running (`Running`/`Starting` with `StartedAt` at or after the commit-time `UpdatedAt`). The generation in use stays protected by the Host service path and the lock digest, and reconciles that commit nothing skip disk cleanup entirely. `global` scope 在所有配置间共享 artifact root; `document` scope 按 config name 隔离.

## args 与 env 模板

只有 `args` 字符串和 `env` 的 value 会经过模板目标改写, `env` 的 key 不处理. Host launch template 的变量名以 ASCII 字母或 `_` 开头, 后续可含 ASCII 字母、数字或 `_`. svchost 只识别 well-formed、未转义的 `${...}`; malformed 或未闭合表达式不会被 svchost 拒绝, 而是原样交给 Host.

- `${PORT}`、`${HOST}` 使用 Host 为本次启动提供的动态值.
- `${NAME}` 从当前服务自己的环境递归读取 `NAME`.
- `${NAME@svc-or-guid}` 从目标服务的运行时环境读取 `NAME`; target 可写服务名或 GUID. 服务名目标在 reconcile 时解析 (规则见下文), YAML parse 不会校验目标是否存在; 无法解析到 Host service ID 时该服务同步失败.
- `${HOST:VAR}` 透传 Host 环境变量 `VAR`.
- `\$` 将 `$` 转义为字面量; `args` 仍支持 legacy `$PORT` 形式.

这些表达式用于 Host launch templates. svchost 不验证整个模板语法; `ComposeTemplate` 会忽略 malformed 或未闭合表达式, 让 Host 接收原文. well-formed `${NAME@target}` 的服务名 target 也不会在 YAML parse 时验证; reconcile 按 `serviceScope` 规则解析, 若没有可用 Host service ID, 对应服务的同步报告失败.

### 全局命名空间与跨配置引用

`serviceScope: global` 的配置共享全局服务命名空间与 `<data>/svchost/global` 根目录. reconcile 按配置名的 ordinal 顺序扫描 global 配置, 首个声明某服务名的配置占用该名称; YAML 无法解析的配置不占用任何名称.

后续 global 配置只要重复声明任一已占用名称, 整份配置就成为 config-level failure, 其中所有服务均报告失败, 且该配置的其他非冲突服务名也不会被注册. 该失败会阻断整轮 reconciliation: 不会调用 Host `ReplaceAsync`, 写入 lock 或调用 `ResumeAsync`, 因此占用名称的配置及无关配置 (包括 `document` 配置) 均不会生效. 其他配置的来源仍可能已完成解析, 个别服务报告也可能显示成功; 这不表示服务已上线. 冲突存在期间, 后续 reconciliation 仍会被阻断.

`serviceScope: document` 的服务名只在各自配置内唯一, 不参与这项全局去重.

`${NAME@svc}` 的查找顺序取决于引用方 scope: document 配置若自己声明了 `svc`, 使用本配置的服务; 否则查全局命名空间. 若本配置声明了该名称但本轮没有 service ID, 不会再回退到 global. global 配置只查全局命名空间, 其中包括自身及其他 global 配置. `${NAME@GUID}` 的 GUID target 直接保留, 不经服务名查找.

## 字段级校验

| 字段 | 类型、默认值与校验 |
| --- | --- |
| 根文档 | 必须恰好一个 YAML document, 根 key 仅允许 `services`、`strictSources`、`serviceScope`; `services` 必填. 重复字段和未知字段会报错. |
| `strictSources` | 可选布尔值, 默认 `false`; 仅接受 `true` 或 `false` (大小写不敏感; `yes`、`no`、`on`、`1` 不接受). |
| `serviceScope` | 可选字符串, 默认 `global`; 接受 `global`、`document` (大小写不敏感). 其他值会产生配置校验错误. |
| `services.<service>` | 必须是 mapping; 服务名匹配 `^[a-z0-9][a-z0-9-]{0,62}$`. |
| `source` | 必填 mapping; 仅允许 `url`、`path`、`release`、`sha256`; `url/path/release` 必须且只能有一个非空值. `url` 必须是绝对 HTTP 或 HTTPS URL; `release` 必须是匹配 `\A[a-z0-9][a-z0-9-]*\z` 的 provider key 加非空 spec. |
| `source.sha256` | 可选字符串, 恰好 64 位十六进制字符; `strictSources: true` 时必填. |
| `args` | 可选字符串 sequence; 每项必须是 scalar string. |
| `env` | 可选 mapping; key 不得为空或全为空白, value 必须是 scalar string. well-formed `${NAME@target}` 的 service-name target 在 reconcile 时按 `serviceScope` 查找, GUID target 直接保留. |
| `start` | 可选字符串, 默认 `eager`; 接受 `eager`、`lazy` (大小写不敏感). |
| `restart` | 可选字符串, 默认 `on-failure`; 接受 `never`、`on-failure`、`always` (大小写不敏感). |
| `health` | 可选 mapping; key 仅允许 `type`、`path`、`timeout`. `type` 默认为 `process`, 接受 `process`、`tcp`、`http` (大小写不敏感); `http` 必须提供以 `/` 开头的 `path`. `timeout` 默认 `5s`, 必须是正且有限的时长, 数值可带小数, 单位为 `ms`、`s`、`m` 或 `h` (大小写不敏感). |
| `route` | 可选 mapping; key 仅允许 `prefix`、`strip`、`methods`、`hosts`. `prefix` 必填且以 `/` 开头; `strip` 为可选布尔值, 默认 `false`, 仅接受大小写不敏感的 `true`/`false`; `methods`、`hosts` 为可选字符串 sequence. |

校验错误带有字段路径, 行号按错误类型提供. `path` 的文件存在性、来源摘要匹配及 release asset/ZIP 布局在同步解析来源时检查; 未注册 provider、GitHub spec 格式无效或模板目标本轮无法解析为 service ID 时也会在同步时失败.
