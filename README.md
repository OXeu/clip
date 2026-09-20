<div align="center">
  <img src="src/Clip.Desktop/Assets/Clip.png" alt="Clip logo" width="128" height="128" />
  <h1>Clip</h1>
  <p>轻量的 Windows 视频剪辑工具。拖入素材，剪出需要的片段，按轨道导出。</p>
  <p>
    <a href="https://github.com/OXeu/clip/releases/latest">下载 Windows 版</a> ·
    <a href="https://github.com/OXeu/clip/blob/master/docs/usage.md">使用指南</a> ·
    <a href="https://github.com/OXeu/clip/issues">反馈问题</a>
  </p>
</div>

## 界面预览

<!-- clip-ui-screenshots: run=35515579692; commit=49b5de251cfcfb2b3fa09b919e088ac36045761d; size=3024x2043; density=3x -->
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/screenshot-dark.png" />
  <img src="docs/images/screenshot.png" alt="Clip 多素材、多轨道剪辑界面" width="1008" />
</picture>

## 功能

- 导入后自动分离视频轨与音频轨，音频以真实波形显示。
- 分割、删除、拖拽排序，支持撤销与重做。
- 可将完整剪辑记录导出为 `.clip` 文件，之后重新核对原视频继续剪辑。
- 轨道多选绑定：分割同步到同组视角，删除只影响当前片段。
- 多个素材、独立轨道，每条轨道单独导出 MP4。
- 本机处理，不修改源文件；支持 NVIDIA NVENC 和 CPU 编码。

## 开始使用

Windows 10 / 11 x64，内置 .NET 和 FFmpeg，无需另行安装。

1. 打开上方下载链接，在最新 Release 的 **Assets** 中下载 `Clip-win-x64.zip`。
2. 完整解压，运行 `Clip.exe`。拖入视频，按 `S` 分割、`Delete` 删除，拖动片段调整顺序；需要稍后继续时点击“更多”→“导出项目”。
3. 点击轨道空白处选中整轨，再点击“导出视频”。

> `.clip` 文件只保存剪辑记录，不包含原视频；恢复时需保留或重新选择原素材。当前发布包尚未签名。

## 文档与参考

[开发指南](https://github.com/OXeu/clip/blob/master/docs/development.md) · [网页版](web/README.md) · [更新指南](https://github.com/OXeu/clip/blob/master/docs/updates.md) · [第三方许可](THIRD-PARTY-NOTICES.md)

## 网页版

[`web/`](web/README.md) 提供同一套剪辑逻辑的浏览器实现：纯静态产物，素材不上传，
在访问者本机完成处理。编辑语义与桌面版一致（由一致性测试强制保证），导出默认走
WebCodecs 优先完成音视频编码；AAC 不可用时自动改用 Opus，两者均不可用时才只将音频
回退到 ffmpeg.wasm；视频编码不可用时再回退到完整的 ffmpeg.wasm 软件编码。

构造说明与部署要求见[网页版文档](web/README.md)。

## 参与贡献

欢迎反馈问题、提出建议或提交 PR。开始前请阅读[贡献指南](https://github.com/OXeu/clip/blob/master/CONTRIBUTING.md)和[行为准则](https://github.com/OXeu/clip/blob/master/CODE_OF_CONDUCT.md)；[新建 Issue](https://github.com/OXeu/clip/issues/new/choose) 时可选择问题反馈或功能建议模板。

## 许可证

Clip 自有代码采用 [MIT License](LICENSE)。FFmpeg、Remix Icon、.NET 运行时等第三方内容仍适用各自的许可证，详见[第三方许可说明](THIRD-PARTY-NOTICES.md)。行为准则的许可和署名见其文末说明。
