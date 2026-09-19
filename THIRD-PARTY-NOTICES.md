# Third-party components

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
