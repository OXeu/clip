# Clip · Windows 原生视频剪辑

使用 **C# / .NET 10 + WPF** 构建的多素材、多轨道剪辑工具：每条有内容的轨道均可独立导出。视频探测、缩略图、兼容预览转换和导出由 FFmpeg 驱动；窗口与播放控件使用 Windows 原生桌面技术，界面遵循 Fluent 2，操作图标采用 Remix Icon。

## 获取 Windows 程序

每次 `push`、Pull Request 或手动运行都会触发 [Windows build](../../actions/workflows/windows.yml)。成功后，在该次运行的 **Artifacts** 中下载 `Clip-win-x64.zip`，解压并运行 `Clip.exe`。

- 发布包包含 .NET 运行时，以及经过 SHA-256 校验的 FFmpeg / FFprobe，无需自行安装 .NET。
- 使用自包含目录发布，运行时 DLL 与 `Clip.exe` 一起分发，不依赖启动时的单文件自解压。请解压整个目录，勿只复制 EXE。
- 支持 Windows 10/11 x64。Windows N/KN 版的预览需要安装系统媒体功能包。
- 默认使用检测通过的 NVIDIA NVENC；没有可用显卡时可用 CPU 完整剪辑导出。
- 导出界面使用软件解码，可选择 NVIDIA NVENC 硬件编码或 CPU 软件编码。
- 程序尚未签名，发布包仅用于第一期功能验证。

## 使用

1. 点击“导入素材”、按 `Ctrl+O`，或将多个视频一起拖进窗口。每个视频进入独立的候选轨，不替换现有编辑，也不会自动进入主轨。
2. 选中任意轨道上的片段即可预览该素材。空格播放 / 暂停；片段结束后沿同一轨道继续播放下一段，必要时自动切换素材，不会跳到其他轨道。主编辑区中的按钮不会将空格误当点击；文字输入框仍保留文字编辑行为。
3. 在时间轴窗口内右键，可打开分割、删除选中片段、撤销和重做菜单。右键片段会先选中并定位到点击处；右键标尺或空白区域保留当前选择和播放头。也可按 `S` 在播放头处分割、按 `Delete` / `X` 删除选中片段；播放中按 `S` 分割不会中断播放。
4. 拖动片段可跨轨移动，或在同轨内重排。插入线标明落点；当前采用顺序插入、轨内自动收拢，不允许重叠或留黑场。
5. 右键片段选择“复制片段”：时间轴时长严格小于 **10 秒**时，在原片段后方插入副本；**10 秒及以上**时，在原轨道正下方新建候选轨道放置副本。副本保留源范围、速度和名称，但可独立编辑。
6. 右键片段选择“命名片段…”，可添加识别名称。名称显示在时间轴中，仅属于该片段，不会重命名源文件或修改同源的其他片段。
7. `Ctrl+Z` 撤销，`Ctrl+Y` / `Ctrl+Shift+Z` 重做；导入、跨轨移动、排序、分割、删除、复制和命名均支持项目级撤销。左右方向键逐帧定位。
8. 点击轨道行内空白处（包括片段上方的留白）选中整轨，再点击“导出视频”；选中片段不算选中轨道。有多条非空轨道时，导出按钮右侧显示下拉箭头，可直接选择轨道打开对应导出窗口。未选整轨时，单条非空轨道可直接导出，多条则打开轨道列表。每次只合并指定轨道，不包含其他轨道的视频或声音。选择新的 MP4 文件名；可取消，失败和取消会清理临时输出。

鼠标放在时间轴的标尺或片段上：**滚轮横向滚动，Shift + 滚轮缩放**（也支持 `Ctrl + 滚轮`）。向上放大、向下缩小，缩放围绕鼠标位置，播放不会中断；缩小到最小倍率时恢复完整时间轴。界面不再显示缩放滑块和“适应”按钮。文本输入、下拉菜单中的按键不触发剪辑快捷键。

剪辑页已移除片段属性面板，预览使用整行宽度；预览面板和时间轴均不显示 header，也不保留其高度。时间轴不显示左侧轨道名称、说明及其占位；最上方为主轨，下方为候选轨，当前轨道可在时间轴底部摘要中确认。选中整轨时显示整行高亮，状态栏提示选中的轨道。轨道较多时，可使用右侧滚动条或 `Alt + 滚轮` 上下浏览；拖拽靠近视口边缘会自动滚动，共享标尺始终保持可见。

### 导出选项

| 选项 | 行为 |
| --- | --- |
| 原画 | 以所选轨道首个素材的分辨率为基准，高质量 H.264 编码，CQ/CRF 16；奇数宽高补齐为偶数 |
| 高清 / 均衡 / 小体积 | CQ/CRF 19 / 23 / 28，数值越低质量越高、文件通常越大 |
| 输出尺寸 | 跟随源视频、1080p、720p、2160p、自定义偶数宽高；竖屏素材的预设随方向调整 |
| 比例不一致 | 保持显示比例并补黑边，不拉伸画面 |
| NVIDIA NVENC | `h264_nvenc` 编码；初始化时用 640×360、YUV420P 画面实际编码一帧检测可用性 |

“原画”是高质量重新编码，**不是无损码流复制**。输出分辨率和帧率默认参考所选轨道首个素材；不同尺寸按比例缩放、补边，不同帧率归一后再拼合。任意位置精确切割不受关键帧限制；速度通过视频时间戳和音频 `atempo` 同步调整，导出音频保持音调。只读取每个素材的首个普通视频流和首个音频流，不导出字幕或封面。所选轨道混合有声和无声素材时，无声部分补静音；整轨全部无声时不生成音轨。音频输出为 48 kHz、双声道 AAC 192 kbps。极短片段的最终时长受输出帧率限制，至少保留一帧。目标尺寸是像素宽高，不是指定文件大小。

导出会在目标目录写入本次任务专用的隐藏临时 MP4，成功后再移动到最终位置；不覆盖源视频或已有目标。处理大视频前请确保目标盘有足够空间。

### 资源管理器右键打开

将发布包放到固定目录，运行 Clip，在“设置”中选择“添加资源管理器右键菜单”。之后右键视频选择“使用 Clip 剪辑”即可直接打开素材。Windows 11 中该项位于“显示更多选项”。注册只写当前用户的 `HKCU\Software\Classes\SystemFileAssociations` 下 `video` 和上述视频扩展名各自的 `shell\Clip.Edit`，不改变默认播放器，无需管理员权限；设置中可以移除。移动或删除程序前请先移除，移动后可重新注册。

也可以直接传入命令行参数：

```powershell
.\Clip.exe "D:\Videos\我的视频.mp4"
```

### 预览与第一期范围

WPF `MediaElement` 播放源视频。系统不支持某种编码时，自动使用 FFmpeg 生成最长边不超过 1280、短边不超过 720 的 H.264 兼容预览；大文件可能需要等待，可以取消。兼容预览不会代替导出源文件。临时预览在正常退出时清理。**预览使用系统解码器，不保证使用 NVIDIA**；导出界面使用软件解码。

当前支持多个 SDR 素材、主轨与多个候选轨、跨轨编排和单片段倍速；不包含项目保存、多轨叠画 / 混音、转场、字幕、HDR 色调映射或指定文件大小。HDR 素材会明确拒绝导入。同一素材的连续分割点不会重定位播放器；跨删除区间或切换素材时可能有短暂停顿。可变帧率素材按标称帧率定位和分割。

非 1 倍速时使用系统播放器的 `SpeedRatio` 预览，系统可能不输出声音；这不影响 FFmpeg 导出时的音画同步变速。参见 [微软播放速度说明](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.mediaplayer.speedratio)。

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
