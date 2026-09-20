# Third-party components

Clip's own code is licensed under the [MIT License](LICENSE). The third-party components and documents below retain their respective licenses; the project's MIT license does not replace those terms.

## Contributor Covenant

`CODE_OF_CONDUCT.md` is adapted from the [Contributor Covenant 2.1 Chinese translation](https://www.contributor-covenant.org/zh-cn/version/2/1/code_of_conduct/), by Coraline Ada Ehmke and Contributor Covenant contributors, under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Its wording, project scope, and reporting contacts have been adapted for Clip; the document retains its attribution and license links.

## Remix Icon

Application action icons use original SVG paths from [Remix Icon](https://remixicon.com/), revision [9fb7967c0a4c09910161192bde99efd3df09f5eb](https://github.com/Remix-Design/RemixIcon/tree/9fb7967c0a4c09910161192bde99efd3df09f5eb), under the Remix Icon License v1.0. Source SVGs and the license are retained in `src/Clip.Desktop/Assets/RemixIcon`; the Windows package includes `licenses/RemixIcon.txt`. The WPF geometry resource preserves those SVG paths. The Clip wordmark is separate from the functional icon library.

## FFmpeg and .NET

Clip invokes FFmpeg and FFprobe as separate executable processes. The Windows CI bundle includes the unmodified **Gyan FFmpeg 8.1.2 essentials build** pinned in `scripts/ffmpeg-version.json`.

- FFmpeg project: https://ffmpeg.org/
- Distributor, build configuration and source information: https://www.gyan.dev/ffmpeg/builds/
- Exact archive: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip
- Upstream source release: https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz
- Source revision identified by the distributor: https://github.com/FFmpeg/FFmpeg/commit/38b88335f9
- License information: https://ffmpeg.org/legal.html

The distributor identifies this essentials distribution as GPL v3. It contains GPL-enabled components, including libx264. The publishing script preserves its original `LICENSE` and `README.txt` as `ffmpeg/LICENSE-FFmpeg.txt` and `ffmpeg/README-FFmpeg.txt`. Refer to those files for the authoritative build and licensing details. Anyone redistributing this bundle must meet the applicable licenses, including corresponding-source obligations for the exact binaries and their dependencies; upstream FFmpeg source alone may not satisfy all such obligations.

The application package also contains Microsoft .NET runtime components, under their included license terms. Microsoft .NET source and notices: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf.

NVIDIA NVENC/NVDEC execution requires a supported NVIDIA GPU and driver supplied by the end user. No NVIDIA driver or CUDA toolkit is bundled.

## Web 版（Clip Web）

`web/` 目录是 Clip 的浏览器实现，自有代码同样采用 MIT License。它引入以下第三方组件：

### ffmpeg.wasm

[ffmpeg.wasm](https://github.com/ffmpegwasm/ffmpeg)（`@ffmpeg/ffmpeg`、`@ffmpeg/core`、
`@ffmpeg/core-mt`，0.12.10 系列）在浏览器中通过 WebAssembly 运行 FFmpeg，采用
[MIT License](https://github.com/ffmpegwasm/ffmpeg/blob/main/LICENSE)。

构建产物包含 `${package}/dist/esm/ffmpeg-core.wasm` 等**未经修改的**核心文件，由
`web/scripts/copy-ffmpeg-core.mjs` 复制到 `web/public/ffmpeg/`，部署时随站点一起托管。
这些核心内嵌了 GPL 组件（含 **libx264**），因此**分发站点时必须满足 FFmpeg 的 GPL 义务**，
包括提供对应源码。上游 FFmpeg 源码本身可能不足以满足全部义务，需以实际构建的对应源码为准。

### mp4box.js

[mp4box.js](https://github.com/gpac/mp4box.js)（2.4.1；包含 1.0.6 起发布的 QuickTime `meta` 解析修复）用于 MP4/MOV 解封装，采用
[BSD-3-Clause](https://github.com/gpac/mp4box.js/blob/master/LICENSE)。

### mp4-muxer

[mp4-muxer](https://github.com/Vanilagy/mp4-muxer)（5.2.2）用于把 WebCodecs 产出的
编码块封装成 MP4，采用 [MIT License](https://github.com/Vanilagy/mp4-muxer/blob/main/LICENSE)。

### 构建工具

包括 [Vite](https://vite.dev/) 与 TypeScript；Playwright 仅用于测试，不会进入发布产物。
