# 开发 Clip

Clip 使用 C#、.NET 10 和 WPF，视频处理由 FFmpeg 完成。先安装 [global.json](../global.json) 指定版本的 .NET SDK。以下命令均在仓库根目录执行；Windows 脚本使用 PowerShell 7。

## 本地运行

在 Windows 上下载 FFmpeg 并启动应用：

```powershell
./scripts/Get-FFmpeg.ps1
$env:CLIP_FFMPEG_DIR = (Resolve-Path .tools/ffmpeg).Path
dotnet run --project src/Clip.Desktop
```

下载脚本会校验 [ffmpeg-version.json](../scripts/ffmpeg-version.json) 中固定的 SHA-256。已有 FFmpeg 时，也可以将 `CLIP_FFMPEG_DIR` 指向同时包含 FFmpeg 和 FFprobe 的目录，或通过 PATH 提供它们。

Linux 可以运行核心测试、FFmpeg 集成测试、网页版测试和交叉编译；WPF 窗口只能在 Windows 上运行。

## 网页版

[`web/`](../web/README.md) 是浏览器实现，与桌面版共用同一套编辑语义。

```bash
cd web
npm install
npm test                                    # 模型与 filter graph 一致性（无需浏览器）
node --experimental-strip-types --test test/e2e.test.ts   # 真实 Chromium + FFprobe
npm run build                               # 输出 web/dist
```

网页版的一致性由 [`tools/Clip.Conformance`](../tools/Clip.Conformance) 保证：它用
`src/Clip.Core` 里真正的 `ExportService` 生成 filter graph 基准，网页版测试逐字对比。
修改 `ExportService` 后需要重新生成基准：

```bash
dotnet run --project tools/Clip.Conformance -c Release -- web/test/fixtures/conformance.json
```

CI 会重跑一次并要求无差异；未同步更新基准会导致构建失败。

## 测试

```bash
dotnet build src/Clip.Desktop -c Release
dotnet run --project tests/Clip.Tests -c Release
dotnet run --project tests/Clip.UpdateTests -c Release
```

安装包含 `libx264` 的 FFmpeg 后，可运行真实视频导出测试。若 FFmpeg 和 FFprobe 已在 PATH 中，无需设置环境变量：

```bash
dotnet run --project tests/Clip.Tests -c Release -- --integration
```

测试入口是控制台程序，失败时返回非零退出码。[Windows CI](../.github/workflows/windows.yml) 还会验证应用启动、桌面交互、更新进程和压缩包完整性。界面截图与启动日志在 `Windows-UI-smoke-*` 产物中，更新诊断在 `Windows-update-smoke-*` 中。

默认分支的推送或手动构建全部通过后，独立的 `Refresh README screenshots` 任务下载本次 smoke test 的浅色、深色主界面截图，自动更新 `docs/images/screenshot.png`、`docs/images/screenshot-dark.png` 和 README 的来源记录。截图至少为 3000 × 2000，直接使用 WPF 的高 DPI 渲染结果。

回写任务使用仓库的 `GITHUB_TOKEN`，单独授予 `contents: write`；它产生的提交不会再次触发 push 流水线。PR、标签和其他分支不回写；图片字节相同则不提交。默认分支已前进时跳过旧构建，不重放、不强制推送。分支保护如禁止机器人直接提交，任务会报告失败，不修改仓库规则。

回写脚本的验证复用本次真实 smoke 截图，并在临时 Git 仓库中测试更新、无变化、失败产物、低分辨率、缺失主题、过期构建和脏工作区。也可本地运行：

```powershell
./scripts/Test-ReadmeScreenshots.ps1 -CaptureDirectory artifacts/Clip-win-x64
```

## 打包发布

在 Windows 上运行：

```powershell
./scripts/Publish-Windows.ps1 -Version 1.2.3
./scripts/Package-Windows.ps1
```

发布目录为 `artifacts/Clip-win-x64`，包含运行时、FFmpeg 和启动诊断工具。打包需要安装 7-Zip，默认输出 `artifacts/Clip-win-x64.zip`，并生成 `.sha256` 和 `.manifest.json`。脚本会实际解压并逐文件校验；已有同名 ZIP 时，用 `-Output` 指定新文件名。

正式发布只需在包含最新 CI 配置的提交上创建并推送版本标签：

```bash
git tag -a v1.2.3 -m "Clip v1.2.3"
git push origin v1.2.3
```

`vMAJOR.MINOR.PATCH` 标签会自动触发 Windows 构建，版本号取自标签。核心、FFmpeg、界面、更新与打包验证全部通过后，`Publish GitHub Release` 任务生成 release notes，附加 `Clip-win-x64.zip`、`.sha256` 和 `.manifest.json`，校验服务器端摘要后公开 Release。说明包含 GitHub 自动归类的 PR 记录、直接提交记录、下载说明和校验值；分类配置在 [.github/release.yml](../.github/release.yml)。

`v1.2.3-rc.1`、`v1.2.3-beta.1` 等带后缀的标签生成预发布，不会成为默认更新通道的最新正式版。分支和 PR 构建只上传 Actions 产物；本地打包未指定版本时仍默认为 `1.0.0`。

发布任务使用仅授予该任务的 `contents: write` 权限。附件上传期间 Release 保持草稿；失败后可重新运行工作流，保留草稿中的说明和已校验附件。已公开的 Release 不会被覆盖，应使用新标签发布修订版。发布任务只接收当前构建产物，并核对标签仍指向被测试的提交。

发布逻辑可用 Node.js 20 或更高版本在本地验证，不会访问 GitHub 或创建测试 Release：

```bash
node --test scripts/publish-release.test.cjs
```

应用内更新只接受完整 ZIP，详见[更新指南](updates.md)。

## 代码入口

| 目录 | 内容 |
| --- | --- |
| [src/Clip.Core](../src/Clip.Core) | 时间轴、媒体探测、FFmpeg 导出与更新逻辑 |
| [src/Clip.Desktop](../src/Clip.Desktop) | WPF 窗口、预览、时间轴交互、资源管理器集成 |
| [web](../web) | 静态网页版：编辑模型、WebCodecs 导出、ffmpeg.wasm 兜底 |
| [tools/Clip.Conformance](../tools/Clip.Conformance) | 用桌面端实现生成网页版一致性基准 |
| [tests/Clip.Tests](../tests/Clip.Tests) | 核心编辑与 FFmpeg 集成测试 |
| [tests/Clip.UpdateTests](../tests/Clip.UpdateTests) | 更新检测、安装、回滚与 Windows 进程测试 |
| [scripts](../scripts) | FFmpeg 下载、Windows 发布、打包和启动诊断 |

界面基于 WPF Fluent，采用 Peace 的工作区布局和 Clip 标志的玫瑰色，跟随系统明暗主题。颜色集中在 [UiTheme.cs](../src/Clip.Desktop/Design/UiTheme.cs)，尺度与样式复用 [FluentTokens.xaml](../src/Clip.Desktop/Design/FluentTokens.xaml) 和 [FluentStyles.xaml](../src/Clip.Desktop/Design/FluentStyles.xaml)，见[界面设计](design.md)。图标使用 [RemixGeometry.xaml](../src/Clip.Desktop/Design/RemixGeometry.xaml) 与 [RemixIcon.cs](../src/Clip.Desktop/RemixIcon.cs)，第三方许可保留在 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

## 发布前手动检查

自动化检查之外，在 Windows 桌面上确认以下场景：

- 在 100% 和 150% 缩放下操作主窗口、右键菜单和导出窗口，确认内容不裁切、控件可用。
- 混合导入横竖屏、有声和无声素材，分割、跨轨拖放、复制、命名并撤销；逐轨导出后检查顺序、声音和画面比例。
- 导出 `.clip` 剪辑记录，分别在原路径、移动素材后和网页版中恢复；确认错误素材被拒绝且取消恢复不覆盖当前项目。
- 检查有声素材自动生成音频波形轨；多选两条已对齐轨道保存绑定后，从任一成员分割应同步，删除应只影响当前片段。
- 播放中分割、滚动和缩放；右键删除后按空格，应只切换播放。输入文字时，编辑快捷键不应影响时间轴。
- 取消长视频导出，确认临时文件清理；添加并移除资源管理器右键菜单，确认默认播放器未改变。
- 在 NVIDIA 实机上分别用 NVENC 和 CPU 导出，检查结果与检测详情；CI 不提供真实 NVIDIA 硬件验证。
- 更新时检查下载进度与取消、取消关闭、只读安装目录和文件被另一实例占用时的提示；成功后确认自动重启且备份仍在。
