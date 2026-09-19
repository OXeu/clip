# Clip · Windows 原生视频剪辑

使用 **C# / .NET 10 + WPF** 构建的单视频、单轨道剪辑工具。视频探测、缩略图、兼容预览转换和导出由 FFmpeg 驱动；窗口与播放控件使用 Windows 原生桌面技术。

## 获取 Windows 程序

每次 `push`、Pull Request 或手动运行都会触发 [Windows build](../../actions/workflows/windows.yml)。成功后，在该次运行的 **Artifacts** 中下载 `Clip-win-x64.zip`，解压并运行 `Clip.exe`。

- 发布包包含 .NET 运行时，以及经过 SHA-256 校验的 FFmpeg / FFprobe，无需自行安装 .NET。
- 使用自包含目录发布，运行时 DLL 与 `Clip.exe` 一起分发，不依赖启动时的单文件自解压。请解压整个目录，勿只复制 EXE。
- 支持 Windows 10/11 x64。Windows N/KN 版的预览需要安装系统媒体功能包。
- 默认使用检测通过的 NVIDIA NVENC；没有可用显卡时可用 CPU 完整剪辑导出。
- NVIDIA 硬件解码取决于显卡型号、驱动和视频编码格式。NVENC 是编码器，NVDEC / CUDA 才是硬件解码路径。
- 程序尚未签名，发布包仅用于第一期功能验证。

## 使用

1. 点击“导入视频”、按 `Ctrl+O`，或者将一个视频拖进窗口。支持 MP4、MOV、MKV、AVI、WEBM 等 FFmpeg 可读取的视频容器。
2. 在时间轴点击或拖动定位播放头，按 `S` 分割；播放中也可立即分割，不暂停、不重定位视频。单击片段选择，按 `Delete` 或 `X`，或右键选择 **删除片段**。剩余片段自动收拢，源文件不会改变。
3. 按空格播放 / 暂停剩余内容；主编辑窗口中，即使焦点停留在删除等按钮或缩放滑块上，空格也只控制播放 / 暂停，长按只切换一次。左右方向键按标称帧率逐帧移动。用 `Ctrl+Z` 撤销，`Ctrl+Y` / `Ctrl+Shift+Z` 重做。可以删除所有片段，再通过撤销恢复。
4. 点击“导出视频”，选择质量、尺寸、编码器和硬件解码。选择一个新的 MP4 文件名开始导出。支持取消，失败和取消会清理本次临时文件。

鼠标放在时间轴的标尺或片段上：**滚轮横向滚动，Shift + 滚轮缩放**（也支持 `Ctrl + 滚轮`）。向上放大、向下缩小，缩放围绕鼠标位置，播放不会中断。完整适应时无需横向滚动，可先放大查看局部；点击“适应”恢复完整时间轴。文本输入、下拉菜单中的按键不触发剪辑快捷键。

### 导出选项

| 选项 | 行为 |
| --- | --- |
| 原画 | 原分辨率、高质量 H.264 编码，CQ/CRF 16；奇数宽高向上补齐为偶数 |
| 高清 / 均衡 / 小体积 | CQ/CRF 19 / 23 / 28，数值越低质量越高、文件通常越大 |
| 输出尺寸 | 跟随源视频、1080p、720p、2160p、自定义偶数宽高；竖屏素材的预设随方向调整 |
| 比例不一致 | 保持显示比例并补黑边，不拉伸画面 |
| NVIDIA NVENC | `h264_nvenc` 编码；初始化时用 640×360、YUV420P 画面实际编码一帧检测可用性 |
| NVIDIA 硬件解码 | FFmpeg `-hwaccel cuda`；可单独关闭，保留 NVENC 编码 |

“原画”表示保留原分辨率并高质量重新编码，**不是无损码流复制**。任意位置精确切割使用 `trim / atrim + concat`，不受关键帧限制；导出仅保留编辑后的区间。导出音轨为 AAC 192 kbps。只导出首个普通视频轨道和首个音频轨道，不导出封面、字幕及其他音轨。目标尺寸是像素宽高，不是指定文件大小。

导出会在目标目录写入本次任务专用的隐藏临时 MP4，成功后再移动到最终位置；不覆盖源视频或已有目标。处理大视频前请确保目标盘有足够空间。

### 资源管理器右键打开

将发布包放到固定目录，运行 Clip，在“设置”中选择“添加资源管理器右键菜单”。之后右键视频选择“使用 Clip 剪辑”即可直接打开素材。Windows 11 中该项位于“显示更多选项”。注册只写当前用户的 `HKCU\Software\Classes\SystemFileAssociations` 下 `video` 和上述视频扩展名各自的 `shell\Clip.Edit`，不改变默认播放器，无需管理员权限；设置中可以移除。移动或删除程序前请先移除，移动后可重新注册。

也可以直接传入命令行参数：

```powershell
.\Clip.exe "D:\Videos\我的视频.mp4"
```

### 预览与第一期范围

WPF `MediaElement` 播放源视频。系统不支持某种编码时，自动使用 FFmpeg 生成最长边不超过 1280、短边不超过 720 的 H.264 兼容预览；大文件可能需要等待，可以取消。兼容预览不会代替导出源文件。临时预览在正常退出时清理。**预览使用系统解码器，不保证使用 NVIDIA**；导出的硬件解码选项明确控制 FFmpeg NVDEC 路径。

第一期支持一个源视频、多段保留区间和 SDR 素材。导入新视频会提示替换当前时间轴。不包含多素材编排、项目保存、转场、字幕、HDR 色调映射和精确文件大小控制；HDR 素材会明确拒绝导入。预览跨删除区间时依赖系统播放器 seek，可能有短暂停顿；导出使用精确过滤器拼接。可变帧率素材按标称帧率定位和分割，导出保留被选区间中的实际视频帧。

## 开发与验证

SDK 版本固定在 `global.json`。Windows 上运行：

```powershell
./scripts/Get-FFmpeg.ps1
$env:CLIP_FFMPEG_DIR = (Resolve-Path .tools/ffmpeg).Path
dotnet run --project src/Clip.Desktop
```

Linux 可以编译核心、运行 FFmpeg 集成测试，也可以交叉编译 WPF；WPF 窗口只能在 Windows 上运行：

```bash
dotnet build src/Clip.Desktop -c Release
dotnet run --project tests/Clip.Tests -c Release
CLIP_FFMPEG_DIR=/path/to/ffmpeg/bin dotnet run --project tests/Clip.Tests -c Release -- --integration
```

测试程序无第三方测试框架依赖，失败时返回非零退出码。覆盖时间轴映射、边界分割、撤销/重做、随机编辑不变量、旋转视频探测、独立硬件开关、区域性数字格式，以及实际 FFmpeg 精确切割、删除画面排除、无音轨导出、短音轨补静音、音频延迟、取消清理、源文件和已有文件保护。

发布独立 Windows 程序：

```powershell
./scripts/Publish-Windows.ps1
```

输出在 `artifacts/Clip-win-x64`。FFmpeg 也可放到程序旁的 `ffmpeg` 目录，或通过设置、`CLIP_FFMPEG_DIR`、PATH 指定；自定义目录需同时包含 `ffmpeg.exe` 和 `ffprobe.exe`。

安装 7-Zip 后运行 `./scripts/Package-Windows.ps1` 可生成经过校验的 ZIP（已有同名包时需通过 `-Output` 选择新文件名）。脚本会测试压缩包、实际解压，并逐文件比较字节数和 SHA-256；全部一致后才发布最终文件，同时生成 `.sha256` 和 `.manifest.json`。也支持 `-Format 7z -Output artifacts/Clip-win-x64.7z` 生成更小的完整分发包，或用 `-SevenZip` 指定 7-Zip 可执行文件。

若下载后提示“文件末端错误”或“数据错误”，先在 PowerShell 检查实际下载文件：

```powershell
(Get-Item "$env:USERPROFILE\Downloads\Clip-win-x64.zip").Length
(Get-FileHash "$env:USERPROFILE\Downloads\Clip-win-x64.zip" -Algorithm SHA256).Hash
```

与对应 Actions 运行摘要中的字节数和 SHA-256 比较。数值不一致说明下载文件与发布文件不同，需要重新下载；两者均一致但解压仍报错时，请记录解压软件名称、版本及完整错误信息。校验清单也在 `Windows-package-checksums-<运行编号>` 构建产物中。

### 双击没有窗口或启动后立即退出

请先把完整发布包解压到新目录，再运行 `Clip.exe`。应用会在 `%LOCALAPPDATA%\Clip\logs` 写入每次启动的阶段日志，包含完整异常；WPF 初始化前的异常也会记录并显示错误对话框。

如果仍然没有窗口，双击程序旁的 `Start-Clip-Diagnostics.cmd`。它会启动程序并收集启动退出码、.NET 宿主加载日志、应用日志及本次 Clip 相关的 Windows 应用程序事件，随后打开诊断报告。报告保存在 `%LOCALAPPDATA%\Clip\diagnostics`，不会要求安装额外运行时或修改系统设置。启动器只为本次诊断 PowerShell 进程设置脚本执行选项，不更改全局执行策略。

### NVENC 检测失败，但其他软件能使用 NVIDIA

旧版使用 128×128 的测试画面，低于部分 NVIDIA 显卡的 NVENC 最小编码尺寸，会把可用显卡误判为不可用。现在改用 640×360 的 YUV420P 画面，并匹配导出使用的编码参数；仍以实际编码成功为准。

在“设置 → 重新检测 NVIDIA NVENC”中重试。“设置 → NVIDIA 检测详情…”会显示当前 FFmpeg 路径、检测参数、退出码和原始错误；这些信息也会写入 `%LOCALAPPDATA%\Clip\logs` 下的启动日志。

若错误为 `Driver does not support the required nvenc API version`，表示当前 FFmpeg 所需的 NVENC API 高于驱动支持的版本。可按错误中的要求更新驱动，或在设置中选择与现有驱动兼容且包含 `h264_nvenc` 的 FFmpeg 目录。其他剪辑软件可能使用不同版本的编码接口，因此其可用性不能保证当前 FFmpeg 也兼容。检测失败时仍可选择 CPU 导出。

## GitHub Actions 与缓存

[`.github/workflows/windows.yml`](.github/workflows/windows.yml) 在 `windows-2025` 上完成恢复依赖 → 核心与 FFmpeg 测试 → WPF 编译与独立发布 → 完整启动（包括 FFmpeg 检测）、渲染截图 → 启动异常日志回归测试 → 打包并逐文件验证解压结果 → 上传可运行程序。

- **NuGet 缓存**：`actions/setup-dotnet` 缓存仓库内 `.nuget/packages`，缓存键包含锁文件、SDK 版本、公共构建属性和发布配置，使用 `--locked-mode` 恢复。
- **FFmpeg 缓存**：缓存验证、解压后的 `.tools/ffmpeg`；键包含系统、架构、版本清单和下载脚本。命中后无需重复下载约 110 MB 的分发包。
- **固定版本与完整性**：`scripts/ffmpeg-version.json` 固定下载 URL 与 SHA-256；升级时同时更新版本、URL、摘要。
- **避免重复工作**：发布使用 `--no-restore`；同一分支的新运行取消旧运行。编译 `bin/obj` 不跨运行缓存，避免陈旧构建状态。
- **发布完整性**：7-Zip 打包后实际解压，比较所有发布文件的 SHA-256。验证后的 ZIP 使用 `archive: false` 原样上传，避免再次压缩，下载可直接与发布摘要校验。程序保留 14 天，UI 截图保留 7 天。
- **权限**：工作流仅需 `contents: read`，不自动发布 Release、不写仓库。

标准 GitHub Windows 托管机器没有 NVIDIA GPU；CI 验证 CPU 完整导出链路与 NVIDIA 参数配置，实际 NVDEC/NVENC 性能和驱动兼容性需要在 NVIDIA Windows 机器上按 [手工验收清单](docs/windows-qa.md) 验证。

## 结构

```text
src/Clip.Core       时间轴模型、媒体探测、FFmpeg 进程和导出
src/Clip.Desktop    WPF 窗口、时间轴绘制、预览、右键集成
tests/Clip.Tests    跨平台核心与真实 FFmpeg 集成测试
scripts            固定版本 FFmpeg 下载与 Windows 发布
.github/workflows  带缓存的 Windows CI
```

技术参考：[WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)、[FFmpeg filters](https://ffmpeg.org/ffmpeg-filters.html)、[NVIDIA FFmpeg 硬件加速](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/ffmpeg-with-nvidia-gpu/index.html)、[setup-dotnet 缓存](https://github.com/actions/setup-dotnet)。第三方分发说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
