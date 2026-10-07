# nekostick-svchost

[![build](https://github.com/Nekolla-Team/nekostick-svchost/actions/workflows/build.yml/badge.svg)](https://github.com/Nekolla-Team/nekostick-svchost/actions/workflows/build.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dot.net)
[![license](https://img.shields.io/badge/license-AGPL--3.0-blue)](LICENSE)

[nekostick](https://github.com/Nekolla-Team/nekostick) 的扩展: 用声明式 YAML 配置同步 host 上的**全局** microservice —— 改配置即生效, 不需要重启 host. 所有来源都声明 `sha256` 时可固定来源内容; 默认 `strictSources: false`, 无摘要来源会被接受并产生 warning. 内置 WebUI 与管理 API.

## 功能总览

- **声明式同步**: 每个配置文件描述一组服务 (来源/参数/环境变量/健康检查/路由), 扩展持续把 host 实际状态调和到配置描述的状态
- **可复现来源**: 支持 HTTP/HTTPS URL、本地文件或 release provider (如 GitHub release). HTTP 不加密传输或凭据; 显式 `sha256` 仅校验下载内容, 不加密凭据. URL 首次解析后锁定 SHA; 锁定产物缺失或损坏而需要重下时, 内容与锁定 SHA 不符会报错. path 每次同步重新计算 SHA; 未显式声明 `sha256` 时内容变化会作为新来源更新 lock, 显式摘要不符时报错. release 按 provider/spec/tag/version/assetName 及可用的上游 digest 复用; GitHub 提供的 digest 变化时会重新下载并更新 lock. 显式 `source.sha256` 才能固定用户要求的内容; 缺少摘要时默认接受可变来源并给出 warning.
- **多配置文件**: 任意多份命名配置; `serviceScope: global` (默认) 共用 `<data>/svchost/global`, `document` 按配置名隔离; 详见 [Compose 配置参考](docs/compose.md).
- **实时启停**: 监听设置变更事件, 不重启 host 即可应用更新; 服务更新走 host 内建的蓝绿切换
- **WebUI + API**: `/svchost` 提供管理界面, `/svchost/api` 提供 REST API, API key 认证

## 使用方式

### 安装

This extension requires a matched host built against the stable `Nekolla.Nekostick.Contracts` **1.4.0** API and implementing `IExtensionHostBridge14`, with negotiated Host API **>=1.4.0 <2.0.0**. The upstream contract baseline is [`da9c55a434812f5a326b3eb5e263851ff30cb7b4`](https://github.com/Nekolla-Team/nekostick/tree/da9c55a434812f5a326b3eb5e263851ff30cb7b4). Upgrade the host and extension together. `HostApiVersion.Current` and `ExtensionAbi.Version` are `1.4.0`; that semantic version alone does not establish that the stable Contracts signatures are present.

1. 从 [Actions](https://github.com/Nekolla-Team/nekostick-svchost/actions/workflows/build.yml) 下载最新的 `nekostick-svchost.<sha>.zip` 构建产物
2. 解压到 host 的 `extensions/nekostick.svchost/` 目录 (内含 `manifest.json` + dll)
3. 重启或重载 host 以加载扩展

首次启动重命名后的扩展时，会自动将设置从旧扩展 ID `nekolla.nekostick.svchost` 迁移到新 ID。

> readonly 实例上管理 API 与 WebUI 不会开放.

### 首次启动 (bootstrap)

已有 settings 但没有永久 API key 时，writable Host 会按常规进入 bootstrap（read-only Host 不会生成 key）。若 settings 文档缺失，只有在可读 full Host snapshot 与 caller-owned services snapshot 都确认不存在 svchost-owned routes、caller-owned services 或 pre-rename `nekolla.nekostick.svchost` settings row 时，writable Host 才会写入初始 settings 并生成仅保存在内存中的 bootstrap key。若已有受管配置，扩展会保持 `settings-missing`，不生成 key、不清理 routes 或禁用 services；请恢复原 settings。任一 snapshot 不可读时 fail closed 并报告 `settings-unavailable`。

### 配置文件示例

```yaml
strictSources: false                                           # true 时拒绝未声明 sha256 的 url/path/release 来源
serviceScope: global                                         # 可选, 默认 global; document 时按配置名隔离.
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

详见 [docs/compose.md](docs/compose.md).

### 参数与环境变量模板

详见 [docs/compose.md](docs/compose.md).

### 管理 API

详见 [docs/api.md](docs/api.md).

## 多节点部署

详见 [docs/deployment.md](docs/deployment.md).

## 文档

- [docs/compose.md](docs/compose.md): Compose 配置字段、来源与模板语法参考.
- [docs/api.md](docs/api.md): 管理 API 路由、认证与 settings groups.
- [docs/deployment.md](docs/deployment.md): 多节点部署约束.
- [docs/publishing.md](docs/publishing.md): 面向 microservice 开发者的 release 发布指南.

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
- `.github/workflows/build.yml` — build → test → pack → artifact (hash 固定 action 版本)
- `PLAN.md` — 完整设计与平台契约细节 (数据目录布局, 调和语义, 错误契约, 已知限制等)

## 许可证

[AGPL-3.0](LICENSE)
