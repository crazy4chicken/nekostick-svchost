# nekostick-svchost

[![build](https://github.com/Nekolla-Team/nekostick-svchost/actions/workflows/build.yml/badge.svg)](https://github.com/Nekolla-Team/nekostick-svchost/actions/workflows/build.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dot.net)
[![license](https://img.shields.io/badge/license-AGPL--3.0-blue)](LICENSE)

[nekostick](https://github.com/Nekolla-Team/nekostick) 的扩展: 用一份声明式 YAML 配置文件, 100% 可复现地同步 host 上的**全局** microservice —— 改配置即生效, 不需要重启 host. 内置 WebUI 与管理 API.

## 功能总览

- **声明式同步**: 每个配置文件描述一组服务 (来源/参数/环境变量/健康检查/路由), 扩展持续把 host 实际状态调和到配置描述的状态
- **可复现来源**: 服务本体支持 https URL、本地文件或 release provider (如 GitHub release); 首次解析后锁定 sha256, 之后每次同步校验或按锁重建, 内容漂移直接报错
- **多配置文件**: 任意多份命名配置, 各自的产物隔离在 data 目录的独立子目录
- **实时启停**: 监听设置变更事件, 不重启 host 即可应用更新; 服务更新走 host 内建的蓝绿切换
- **WebUI + API**: `/svchost` 提供管理界面, `/svchost/api` 提供 REST API, API key 认证

## 使用方式

### 安装

1. 从 [Actions](https://github.com/Nekolla-Team/nekostick-svchost/actions/workflows/build.yml) 下载最新的 `nekostick-svchost.<sha>.zip` 构建产物
2. 解压到 host 的 `extensions/nekostick.svchost/` 目录 (内含 `manifest.json` + dll)
3. 重启或重载 host 以加载扩展

首次启动重命名后的扩展时，会自动将设置从旧扩展 ID `nekolla.nekostick.svchost` 迁移到新 ID。

> readonly 实例上管理 API 与 WebUI 不会开放.

### 首次启动 (bootstrap)

扩展启动时如果没有配置过 API key, 会在 host 日志里打印一个**一次性 bootstrap key** (每次启动重新生成, 不落盘). 打开 `http://<host>/svchost`, 页面会引导你用该 key 设置自己的永久 key, 之后永久 key 生效并退出 bootstrap 模式.

### 配置文件示例

```yaml
strictSources: false                                           # true 时拒绝未声明 sha256 的 url/path/release 来源
services:
  my-api:
    source:
      release: "github:owner/repo@v1.2.3"                    # or url: https://example.com/my-api or path: /opt/bin/my-api
    args: ["--serve", "--port", "$PORT"]                    # $PORT 由 host 替换
    env: { MODE: production }
    start: eager                                            # eager | lazy
    restart: on-failure                                     # never | on-failure | always
    health: { type: http, path: /healthz, timeout: 5s }
    route: { prefix: /api/my, strip: true }
```

### Release 来源

用 `source.release: "github:owner/repo@ref"` 声明一个 release 来源; 被选中的 release 必须包含名为 `{serviceId}_{version}_{arch}.zip` 的 asset, 其中 `serviceId` 是服务在 YAML 里的 key, `arch` 按当前节点架构取 `x64` / `arm64` / `arm` / `x86`. zip 整体解压为产物目录, 入口是根目录下与 `serviceId` 同名的可执行文件 (Windows 允许 `.exe`).

ref 的解读顺序: 先按 7–40 位十六进制 commit 前缀, 再按 SemVer (可带 `v` 前缀), 否则按精确 tag. SemVer ref 选中版本一致的 release, asset 版本与 ref 不一致直接拒绝; commit ref 选中 `target_commitish` 以该前缀开头的 release, asset 版本不以此前缀开头直接拒绝; 其他 ref 选中完全同名的 tag, asset 版本与 tag 不一致时只警告不拒绝.

镜像源在扩展设置里按 provider 配置, 按列表顺序尝试, 全部失败后 fallback 到官方地址:

```json
{
  "releaseProviders": {
    "github": {
      "mirrors": ["https://ghproxy.net/"]
    }
  }
}
```

镜像只是 asset 下载 URL 的前缀代理; release 元数据始终直接请求 `https://api.github.com`.

### 参数与环境变量模板

`args` 中的字符串以及 `env` 中的每个 value 都支持 host launch templates：

- `${PORT}`、`${HOST}` 使用 host 为本次启动分配的动态值；`${NAME}` 从当前服务自己的环境递归读取 `NAME`。
- `${NAME@svc-or-guid}` 从目标服务发布的运行时环境读取 `NAME`；同一份 YAML 的 `services:` key 可以直接作为 `svc`，也可以填写 host service GUID。
- `${HOST:VAR}` 直接透传 host 环境变量 `VAR`。
- `\$` 转义为字面量 `$`；参数中的 legacy `$PORT` 形式仍受支持。

只有 `args` 和 `env` values 会展开模板，`env` keys 不会展开。

在 WebUI 里新建配置、粘贴 YAML 即可; 也可直接调 API (`X-Api-Key` 头认证):

| 端点 | 说明 |
| --- | --- |
| `GET /svchost/api/status` | 状态 (含 bootstrap 标志, 免认证) |
| `PUT /svchost/api/configs/{name}` | 创建/更新配置并同步 |
| `DELETE /svchost/api/configs/{name}` | 删除配置及其服务/路由/产物 |
| `POST /svchost/api/configs/{name}/sync` | 强制重新同步 |
| `GET /svchost/api/services` | 服务运行状态 |
| `POST /svchost/api/services/{config}/{service}/{start\|stop\|restart}` | 单服务启停 |

## 多节点部署

- 每个节点部署**同一份** svchost 构建产物; host 会对内容摘要漂移的扩展做节点级隔离
- 为每个 url/path/release 来源声明 `sha256`, 或在配置根部设置 `strictSources: true`; 两者都没有时同步会接受可变来源但给出警告
- 缺少本地前置 (如产物未同步到本节点) 的服务进入 `Waiting` 状态; host 退避重试并自动恢复, svchost 会通过 `ResumeAsync` 主动催醒受管服务
- 产物是节点本地的: 每个节点独立解析来源, `path` 来源必须在每个节点的该路径存在; 新节点无法从其他节点重建本地 path 来源

## 开发

环境要求: .NET SDK 10.0.100+ (见 `global.json`), Node 24+, pnpm 11+.

```bash
dotnet build nekostick-svchost.slnx    # 构建 (自动构建并嵌入 webui)
dotnet test nekostick-svchost.slnx     # 运行测试
SVCHOST_SKIP_WEBUI_BUILD=1 dotnet build nekostick-svchost.slnx   # 跳过 webui 构建 (需已有 webui/dist)
```

仓库结构:

- `src/Nekostick.ServiceHost/` — 扩展本体 (Settings / Compose / Sync / Api / Webui)
- `webui/` — Vue 3 + Vite + naive-ui 管理界面, 构建为单文件后以嵌入资源打进 dll
- `tests/` — xunit 单元测试
- `.github/workflows/build.yml` — test → build → pack → artifact (hash 固定 action 版本)
- `PLAN.md` — 完整设计与平台契约细节 (数据目录布局, 调和语义, 错误契约, 已知限制等)

## 许可证

[AGPL-3.0](LICENSE)
