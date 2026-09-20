# Jellyfin 豆瓣同步

[![Release](https://img.shields.io/github/v/release/fudimeng/jellyfin-plugin-douban-sync)](https://github.com/fudimeng/jellyfin-plugin-douban-sync/releases)
[![Release workflow](https://github.com/fudimeng/jellyfin-plugin-douban-sync/actions/workflows/release.yml/badge.svg)](https://github.com/fudimeng/jellyfin-plugin-douban-sync/actions/workflows/release.yml)
[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0--only-blue.svg)](LICENSE)
[![Jellyfin 10.11.11](https://img.shields.io/badge/Jellyfin-10.11.11-00A4DC.svg)](https://jellyfin.org/)

将 Jellyfin 中已观看的电影，以及整季看完的电视剧和动画同步为豆瓣“看过”。

本插件采用每 30 分钟扫描一次的方式工作，不监听实时播放事件。每个 Jellyfin 用户可以绑定独立的豆瓣浏览器 Cookie。

> [!IMPORTANT]
> 这是非官方第三方插件。豆瓣没有为此场景提供稳定的公开写入 API，网页接口变化后插件可能需要更新。

## 功能

- 同步 Jellyfin 已观看电影。
- 电视剧和动画按季同步，所有本地普通集均已看完后才入队。
- 第 0 季、特别篇和虚拟缺失集不计入季完成度。
- 播放完成或手动标记为已观看，都会在下一个扫描周期被识别。
- 可选择在新标记“看过”时分享到豆瓣广播。
- 提交前检查豆瓣收藏状态，已经“看过”的条目不会被更新或重复广播。
- 优先使用 `DoubanID`，缺失时通过 IMDb ID 精确查找并校验，最后才按标题与年份匹配。
- Cookie 使用 ASP.NET Core Data Protection 加密保存，管理页面不会回显 Cookie。
- Cookie 失效时可通过 Bark 和 Discord Webhook 通知；每版 Cookie 只提醒一次，避免重复推送。
- 支持请求限速、持久化队列、失败重试和最近同步结果。

## 兼容性

| 插件版本 | Jellyfin | 运行时 |
|---|---|---|
| 0.1.x | 10.11.11 | .NET 9 |

Jellyfin 插件 API 在小版本之间也可能发生不兼容变化。其他 Jellyfin 版本应使用对应依赖重新构建，不建议强行安装。

## 安装

### 使用插件仓库（推荐）

1. 打开 Jellyfin 控制台 → 插件 → 存储库。
2. 添加仓库名称 `Douban Sync`。
3. 填入仓库 URL：

   ```text
   https://github.com/fudimeng/jellyfin-plugin-douban-sync/releases/latest/download/manifest.json
   ```

4. 返回插件目录，在 `General` 分类中安装“豆瓣同步”。
5. 重启 Jellyfin。

新版本发布后，可以直接在 Jellyfin 插件页面升级。

### 手动安装

1. 从 [GitHub Releases](https://github.com/fudimeng/jellyfin-plugin-douban-sync/releases) 下载与 Jellyfin 版本匹配的 ZIP。
2. 将 ZIP 解压到 Jellyfin 插件目录。
3. 重启 Jellyfin。

常见插件目录：

| 部署方式 | 插件目录 |
|---|---|
| Linux 原生安装 | `/var/lib/jellyfin/plugins/` |
| Docker | 容器内 `/config/data/plugins/` |

## 配置 Cookie

1. 在浏览器中登录豆瓣并打开 [豆瓣电影](https://movie.douban.com/)。
2. 打开开发者工具 → Network，刷新页面。
3. 选中一个发往 `movie.douban.com` 的请求。
4. 在 Request Headers 中复制完整的 `Cookie` 值，至少应包含 `dbcl2` 和 `ck`。
5. 打开 Jellyfin 控制台 → 插件 → 豆瓣同步。
6. 选择 Jellyfin 用户，粘贴 Cookie 并保存。

插件会先验证登录状态，验证成功后才加密保存 Cookie。

## 配置 Cookie 失效通知

在 Jellyfin 控制台 → 插件 → 豆瓣同步的“Cookie 失效通知”区域，可以录入：

- Bark 完整推送地址，例如 `https://api.day.app/设备密钥`；也支持自建 Bark 服务的 HTTP/HTTPS 地址。
- Discord 的 HTTPS Webhook 地址。

通知地址与 Cookie 一样使用 ASP.NET Core Data Protection 加密保存且不会在管理页面回显。当豆瓣拒绝某个已保存的 Cookie 时，插件会向所有已配置渠道发送通知；同一版 Cookie 最多成功提醒一次，重新导入 Cookie 后会重新启用提醒。

> [!WARNING]
> Cookie 等同于豆瓣登录凭据。不要将 Cookie、Jellyfin 插件配置、`DoubanSyncKeys` 目录或包含这些内容的备份上传到 GitHub、Issue、聊天或截图中。怀疑泄露时，请立即退出豆瓣登录并重新登录。

## 同步规则

### 电影

Jellyfin 中的电影被标记为已观看后，会在下一个扫描周期加入同步队列。

### 电视剧与动画

- 同步单位是“季”，不会逐集写入豆瓣。
- 某季必须至少存在一集本地普通集。
- 该季所有本地普通集都已看完后才会同步。
- 第 0 季和特别篇不会触发整季同步。
- 多季剧不会盲目继承剧集主条目的 DoubanID 或 IMDb ID，以免串季。
- 无法可靠匹配具体季时会记录失败，不会猜测提交。

### 豆瓣条目匹配

匹配顺序如下：

1. Jellyfin 元数据中的 `DoubanID` 或 MetaShark `DoubanID`。
2. IMDb ID 精确搜索，并在豆瓣影片页再次核对 IMDb ID。
3. 标题、原始标题、季数和年份匹配。

匹配结果不唯一时，插件会停止该条任务。

## 使用

- 默认每 30 分钟扫描一次。
- 可以在 Jellyfin 控制台 → 计划任务中手动运行或调整触发器。
- 请求最小间隔默认 5 秒，不建议降低，以免触发豆瓣安全验证。
- 首次运行会扫描安装插件之前已经标记为已观看的内容。
- 安装 MetaShark 并保留 `DoubanID` 可以减少搜索请求和误匹配。

## 故障排查

### 从存储库获取插件详情时发生错误

- 确认仓库 URL 完整且没有多余空格。
- 确认已经存在至少一个 GitHub Release。
- 确认 Jellyfin 所在主机或容器可以访问 `github.com`。
- 在浏览器中打开仓库 URL，确认返回的是 JSON 而不是登录页或错误页。

### Cookie 验证失败

- 重新登录豆瓣后复制新的 Cookie。
- 确认 Cookie 同时包含 `dbcl2` 和 `ck`。
- 如果豆瓣要求安全验证，先在与 Jellyfin 相同出口网络的浏览器中完成验证。

### 影片或剧集匹配失败

- 刷新 Jellyfin 元数据。
- 优先通过 MetaShark 写入 `DoubanID`。
- 检查标题、原始标题、年份和季号是否正确。

## 从源码构建

需要 .NET 9 SDK：

```bash
dotnet restore DoubanSync.sln
dotnet test DoubanSync.sln --configuration Release
dotnet msbuild package.proj -t:Package
```

生成的安装包位于 `artifacts/`。

## 发布新版本

更新版本号，并在 `.github/release-notes/` 中添加与标签同名的中文版本说明后，推送符合 `vX.Y.Z` 格式的标签：

```bash
git tag v0.1.7
git push origin v0.1.7
```

GitHub Actions 会自动：

1. 还原依赖并运行测试。
2. 构建版本匹配的插件 ZIP。
3. 计算 Jellyfin 插件仓库所需的校验值。
4. 将该版本的产品变更说明写入 `manifest.json`。
5. 使用同一份变更说明创建 GitHub Release，并上传 ZIP 与 manifest。

## 限制与风险

- 当前不同步评分、标签、短评或取消“看过”状态。
- 豆瓣网页接口变化后，Cookie 验证、搜索或写入可能失效。
- 频繁请求可能触发豆瓣安全验证；串行限速也无法完全避免账号或出口 IP 被风控。
- 数据保护密钥与加密配置同时泄露时，Cookie 和通知地址仍可能被解密，请妥善保护 Jellyfin 配置和备份。

## 许可证

本项目按 [GNU General Public License v3.0 only](LICENSE)（SPDX：`GPL-3.0-only`）许可发布。
