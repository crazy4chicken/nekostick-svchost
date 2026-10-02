# Microservice 发布指南

本文面向希望通过 svchost `release` 来源部署服务的开发者. 发布时需要同时约定 release ref、asset 文件名及 ZIP 内入口文件.

## 发布流程

1. 推荐使用 SemVer tag, 例如 `v1.2.3`. ref 解析接受小写 `v` 前缀. 注意, 7–40 位十六进制字符串会优先被识别为 commit 前缀, 不会作为同名 tag 处理.
2. CI 按目标节点的进程架构构建产物, 每个 ZIP asset 按 `{serviceId}_{version}_{arch}.zip` 命名. `serviceId` 必须与用户 Compose 中的 `services` key 完全一致, 且匹配 `^[a-z0-9][a-z0-9-]{0,62}$`; asset 名中的服务名前缀和架构/扩展名后缀按大小写敏感方式匹配. `arch` 为 `x64`、`arm64`、`arm` 或 `x86`.

   例如服务 key 为 `my-service`, tag 与 asset version 均为 `v1.2.3` 时, 可发布:

   ```text
   my-service_v1.2.3_x64.zip
   my-service_v1.2.3_arm64.zip
   my-service_v1.2.3_arm.zip
   my-service_v1.2.3_x86.zip
   ```

3. 让 asset 的 `version` 段符合 ref 分类规则. SemVer ref 要求 release tag 和至少一个匹配服务/架构的 asset version 与 ref 的 SemVer identity 相等; build metadata 不参与比较, 没有匹配 asset 时拒绝. Commit ref 要求 GitHub release 的 `target_commitish` 以该前缀开头, 且 asset version 也以该前缀开头; 发布时应将 `target_commitish` 设为目标 commit 的完整 SHA, 而不是通常不匹配的分支名. 其他 ref 按精确 tag 匹配 release; asset version 不与 tag 完全相同只产生 warning, 不会因此拒绝.
4. 每个 ZIP 会整体解压. 入口可执行文件必须直接位于 ZIP 解压根目录, 文件名为 Compose service key (`serviceId`). Windows 也接受 `{serviceId}.exe`; POSIX 使用无扩展名的 `{serviceId}`. POSIX 下入口内容必须是目标平台可执行程序; svchost 安装时只把入口权限设为 `0755` (`rwxr-xr-x`), 不要求 ZIP 保存其 Unix executable mode, 也不会恢复其他文件的 Unix 权限位.
   ZIP 内根路径、越出解压目录的路径会导致安装失败; 指向同一输出路径的重复文件条目也会失败. 服务进程 CWD 是 `<data>/svchost/<config>`, 不是 ZIP 解压目录; 随包资源应通过入口程序自身路径定位, 不要依赖 CWD.
5. 计算每个 ZIP asset 的 SHA-256, 在用户配置中建议声明 `source.sha256`. 摘要针对 ZIP 文件本身, 不是 ZIP 内的入口文件. 同一份 Compose 中 `source.sha256` 是固定值; 若多架构 ZIP 摘要不同, 该值不能同时匹配所有架构节点. `strictSources: true` 会要求每个来源都显式声明摘要, 因此只在所有目标节点都能使用相同摘要时启用.
6. 在服务仓库提交一份 `svchost.compose.yaml` 示例, 并在 GitHub release notes 中链接该文件及各架构 asset, 方便使用者直接复制配置.

## GitHub API 查询行为

GitHub provider 使用未认证的 `GET /repos/{owner}/{repo}/releases?per_page=100`, 只读取第一页最多 100 个 release, 不分页; 会跳过 draft, 但不排除 prerelease. 因不带认证, 只能读取公开仓库且受 GitHub unauthenticated API rate limits 限制. 每次 reconcile 都会对每个 GitHub release source 重新查询 metadata, 然后才检查本地 lock 是否可复用.

## Ref 与 asset version 规则

GitHub provider 按以下顺序分类 ref: 7–40 位十六进制 commit 前缀, SemVer (接受小写 `v` 前缀), 最后是精确 tag. SemVer ref 与 tag/asset 比较时忽略 build metadata; commit 前缀比较不区分大小写. 精确 tag 匹配区分大小写, asset version 与 tag 的 warning 比较也是精确字符串比较.

| 用户填写的 ref | release 选择规则 | asset `version` 规则 | 不匹配结果 |
| --- | --- | --- | --- |
| `v1.2.3` 或 `1.2.3` | release tag 必须解析为相同 SemVer identity. | 至少一个匹配服务与架构的 asset version 必须解析为相同 SemVer identity. | 没有匹配 release 或 asset 时拒绝. |
| 7–40 位十六进制前缀 | `target_commitish` 必须以该前缀开头; 发布时将其设为目标 commit 的完整 SHA. | asset version 必须以该前缀开头. | release 或 asset 不匹配时拒绝. |
| 其他非空 ref | release tag 必须与 ref 完全相同. | 不要求相同. | asset version 与 release tag 字符串不同时只产生 warning. |

请避免用 7–40 位纯十六进制字符串作为普通 tag, 因为它会优先按 commit 前缀解释. 若希望 tag 引用不产生 asset version warning, 让 asset 的 `version` 字符串与 release tag 完全一致.

## 可放入服务仓库 README 的部署模板

下面模板默认保持 `strictSources: false`. 发布者可为单架构部署填写真实 asset ZIP 摘要, 再将 `strictSources` 设为 `true`; 多架构共享配置时, 先确认同一个显式摘要能匹配所有目标节点的 ZIP.

```yaml
strictSources: false # 默认 false; 仅在每个来源均配置了可匹配的 sha256 后设为 true.
services:
  my-service: # 必须与 asset 文件名中的 serviceId 一致.
    source:
      release: "github:owner/repo@v1.2.3" # ref 按 commit/SemVer/tag 规则匹配; 例如 1.2.3 可匹配 tag v1.2.3.
      # sha256: "0000000000000000000000000000000000000000000000000000000000000000" # 单架构示例; 替换为所选 ZIP asset 的真实 SHA-256.
```

上面的 `sha256` 行是注释占位示例, 不是实际摘要. 同一份多架构 Compose 使用一个固定 `source.sha256`; 如果各架构 ZIP 的内容摘要不同, 不要把其中一个架构的摘要当作通用值. `strictSources: true` 时, 即使 release metadata 提供 asset digest, Compose 仍必须显式包含 `source.sha256`.

## 网络受限环境中的下载镜像

镜像由使用者在 svchost 扩展设置中配置, 不写入服务 Compose. 使用带 `X-Api-Key` 的 `PUT /svchost/api/settings` 提交 group:

```json
{
  "releaseProviders": {
    "github": {
      "mirrors": ["https://ghproxy.net/"]
    }
  }
}
```

`mirrors` 是 asset 下载 URL 前缀列表, 按顺序尝试后回退到 GitHub 官方 asset URL. GitHub API metadata 请求仍直连 `https://api.github.com`, 因此 mirror 只代理 asset 下载, 不代理 release 查询. Mirror 接受 HTTP 和 HTTPS, 但 HTTP 不加密; 显式 `source.sha256` 优先校验下载, 否则使用 GitHub asset digest (若存在). 两者都没有时, 首次下载没有上游完整性校验, 但同一 release identity 的后续重下载仍会与已有 lock SHA 比对. `PUT` 按 group 整体替换; 保留已有 provider 配置时, 请求中应包含完整的 `releaseProviders` group. 详见 [管理 API 参考](api.md).
