# 检查更新与原目录更新

在设置菜单中选择“检查更新…”，检查完成后点击“更新并重启”。窗口显示下载字节数、百分比、SHA-256 校验、解压和准备进度。准备完毕后沿用编辑器的关闭确认；取消关闭或仍有导出任务时，不启动文件替换。

旧进程退出后，独立更新窗口显示替换进度，完成后自动启动原目录下的 `Clip.exe`。替换或启动进程失败时恢复本次修改过的文件；恢复也失败时显示备份位置。应用成功启动进程之后发生的崩溃仍由原有启动诊断处理，不视为文件替换失败。

“当前目录”使用 `AppContext.BaseDirectory`，即当前 `Clip.exe` 所在目录，而非快捷方式或资源管理器传入的工作目录。新程序继续安装在这里。下载包、解压文件、独立更新程序和旧文件备份均放在其 `.clip-updates/<唯一编号>/` 子目录中，不迁移安装目录。成功后保留该目录供恢复；确认新版本正常并关闭更新程序后可以手动删除对应编号目录。目录必须可写，更新不自动提权。建议预留安装包、解压文件、更新程序运行时副本和原文件备份所需空间。

## 更新通道

- 默认：GitHub 最新正式 Release，排除草稿和预发布。比较数字版本，支持 `v1.2.3`、版本元数据，以及从同版本预发布升级到正式版；不会提示降级。
- 开启“Dev 模式（GitHub Actions）”：检查 `OXeu/clip` 默认分支上的 `windows.yml`，只选择成功的 `push` / `workflow_dispatch` 构建，跳过 PR、fork、其他分支、失败和没有可用产物的运行。安装包必须名为 `Clip-win-x64.zip`，且未过期。CI 写入当前运行编号、重跑次数和 commit，避免相同或更旧构建反复提示。本地旧包没有构建编号时会明确说明无法比较，并允许手动安装最新可用构建。
- Dev 开关单独存储在 `%LOCALAPPDATA%\Clip\updates.json`，不受 FFmpeg 目录设置影响。切换后立即生效，并在下次启动保留。
- 每次启动后台检查一次，在设置菜单提示发现更新；后台检查失败只记录日志，不打断编辑。手动检查会显示网络、超时、权限或限流错误。

仓库目前为私有仓库。检查和下载需要有该仓库 **Contents: Read** 与 **Actions: Read** 权限的 GitHub 令牌。在更新窗口中输入后点击“检查更新”，令牌仅保存在当前会话内存，也可通过 `CLIP_GITHUB_TOKEN` 环境变量提供。留空沿用当前会话或环境中的令牌；令牌不会写入更新设置、安装包或日志。公开 Release 无需令牌，Actions 下载仍可能需要登录凭据。

GitHub API 返回的 SHA-256 摘要是下载校验依据，缺少摘要的旧产物不能自动安装。下载跳转至对象存储时不会携带 GitHub Authorization。仅支持完整 Windows x64 ZIP；源码压缩包、仅 EXE、7z、不完整包、不安全路径和符号链接均拒绝安装。流程依据 [GitHub Releases API](https://docs.github.com/en/rest/releases/releases)、[Actions artifacts API](https://docs.github.com/en/rest/actions/artifacts) 和 [upload-artifact 原文件上传格式](https://github.com/actions/upload-artifact#upload-an-individual-file-unzipped)。

## 版本与打包

```powershell
./scripts/Publish-Windows.ps1 -Version 1.2.3
./scripts/Package-Windows.ps1
```

将验证过的 `Clip-win-x64.zip` 作为 `v1.2.3` Release 的附件。版本号必须与发布标签一致；标签触发的 CI 自动从 `GITHUB_REF_NAME` 去掉 `v` 前缀传给构建。没有指定版本的本地构建沿用 .NET 项目默认版本 `1.0.0`。这项功能不自动创建或发布 GitHub Release。

## 维护与验证

更新核心在 `src/Clip.Core/Updates/`，独立窗口、菜单适配和进程交接在 `src/Clip.Desktop/Updates/`。`App.xaml.cs` 只增加菜单注册及独立更新进程入口。主窗口 XAML、样式、时间轴和现有测试入口没有为更新功能作修改。后续 UI 重构若改掉 `SettingsButton` 的名称或 ContextMenu，只需调整 `UpdateController.Attach` 的接入点。

```bash
dotnet run --project tests/Clip.UpdateTests -c Release
dotnet build src/Clip.Desktop -c Release
```

测试覆盖版本比较、通道筛选与分页、权限错误、过期产物、令牌跳转隔离、取消、校验、路径穿越、文件替换、备份及回滚。Windows CI 另运行真实更新进程测试：

```powershell
dotnet run --project tests/Clip.UpdateTests -c Release -- --windows-smoke artifacts/Clip-win-x64 artifacts/Clip-win-x64.zip
```

该测试在隔离目录中启动旧应用与更新窗口，验证旧进程存活时不替换、退出后替换、保留备份、自动重启并完成 WPF 初始化。诊断上传为 `Windows-update-smoke-<编号>`。人工验收时还应检查慢速网络下的下载进度和取消、私有仓库令牌输入、设置菜单入口、取消编辑器关闭、另一实例占用文件，以及只读安装目录的错误提示。
