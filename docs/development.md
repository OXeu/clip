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

Linux 可以运行核心测试、FFmpeg 集成测试和交叉编译；WPF 窗口只能在 Windows 上运行。

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

## 打包发布

在 Windows 上运行：

```powershell
./scripts/Publish-Windows.ps1 -Version 1.2.3
./scripts/Package-Windows.ps1
```

发布目录为 `artifacts/Clip-win-x64`，包含运行时、FFmpeg 和启动诊断工具。打包需要安装 7-Zip，默认输出 `artifacts/Clip-win-x64.zip`，并生成 `.sha256` 和 `.manifest.json`。脚本会实际解压并逐文件校验；已有同名 ZIP 时，用 `-Output` 指定新文件名。

发布正式版时，将 ZIP 附加到 `v1.2.3` Release，标签应与构建版本一致。标签构建会自动取标签中的版本号；未指定版本的本地构建默认为 `1.0.0`。CI 上传构建产物，不会自动创建 Release。应用内更新只接受完整 ZIP，详见[更新指南](updates.md)。

## 代码入口

| 目录 | 内容 |
| --- | --- |
| [src/Clip.Core](../src/Clip.Core) | 时间轴、媒体探测、FFmpeg 导出与更新逻辑 |
| [src/Clip.Desktop](../src/Clip.Desktop) | WPF 窗口、预览、时间轴交互、资源管理器集成 |
| [tests/Clip.Tests](../tests/Clip.Tests) | 核心编辑与 FFmpeg 集成测试 |
| [tests/Clip.UpdateTests](../tests/Clip.UpdateTests) | 更新检测、安装、回滚与 Windows 进程测试 |
| [scripts](../scripts) | FFmpeg 下载、Windows 发布、打包和启动诊断 |

界面基于 WPF Fluent，采用 Peace 的工作区布局和 Clip 标志的玫瑰色，跟随系统明暗主题。颜色集中在 [UiTheme.cs](../src/Clip.Desktop/Design/UiTheme.cs)，尺度与样式复用 [FluentTokens.xaml](../src/Clip.Desktop/Design/FluentTokens.xaml) 和 [FluentStyles.xaml](../src/Clip.Desktop/Design/FluentStyles.xaml)，见[界面设计](design.md)。图标使用 [RemixGeometry.xaml](../src/Clip.Desktop/Design/RemixGeometry.xaml) 与 [RemixIcon.cs](../src/Clip.Desktop/RemixIcon.cs)，第三方许可保留在 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

## 发布前手动检查

自动化检查之外，在 Windows 桌面上确认以下场景：

- 在 100% 和 150% 缩放下操作主窗口、右键菜单和导出窗口，确认内容不裁切、控件可用。
- 混合导入横竖屏、有声和无声素材，分割、跨轨拖放、复制、命名并撤销；逐轨导出后检查顺序、声音和画面比例。
- 播放中分割、滚动和缩放；右键删除后按空格，应只切换播放。输入文字时，编辑快捷键不应影响时间轴。
- 取消长视频导出，确认临时文件清理；添加并移除资源管理器右键菜单，确认默认播放器未改变。
- 在 NVIDIA 实机上分别用 NVENC 和 CPU 导出，检查结果与检测详情；CI 不提供真实 NVIDIA 硬件验证。
- 更新时检查下载进度与取消、取消关闭、只读安装目录和文件被另一实例占用时的提示；成功后确认自动重启且备份仍在。
