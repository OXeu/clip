# Clip Web（静态网页版）

[Clip](../README.md) 的浏览器实现，产物是纯静态文件，可以放在任何静态托管上
（GitHub Pages、Cloudflare Pages、Netlify、对象存储 + CDN）。所有处理都在访问者的
浏览器里完成，素材不会上传到服务器。

## 与桌面版的关系

编辑行为是同一套语义。`src/Clip.Core` 中的 `EditProject`、`VideoClip`、
`ExportOptions` 与 `ExportService` 被逐条移植到 `web/src/model.ts` 与
`web/src/filtergraph.ts`，并且由测试强制保证不分叉：

- `tools/Clip.Conformance` 用**仓库里真正的 C# 实现**生成 filter graph 基准，
  写入 `web/test/fixtures/conformance.json`。
- `web/test/conformance.test.ts` 逐字比较 TypeScript 的输出与这份基准。
  任一侧语义漂移都会立刻失败。
- `web/test/model.test.ts` 覆盖分割、ripple 删除、跨轨移动、变速、撤销重做，
  以及 500 次随机编辑的不变量检查（与 `tests/Clip.Tests` 对应用例一致）。

因此网页版的分割点、变速时长、补静音与拼接结果与桌面版一致。两端导入时都会
在视频轨下方生成固定的伴生音频波形槽，拖动排序时两者作为一组移动；“多选轨道 → 保存并对齐”
可建立多视角对齐组，分割会同步传播到对齐轨及各自的伴生音轨，删除仍只作用于当前片段。

预览画面默认以 `contain` 完整适应可用区域；将鼠标放在画面上滚动可在 25%–400%
之间缩放，双击恢复适应。网页与 Windows 原生版使用相同的交互。

时间轴使用普通滚轮上下浏览轨道，`Shift + 滚轮` 横向移动时间轴窗口；
`Ctrl + 滚轮` 缩放并保持鼠标所指的时间点不动。右侧的“− / 适应 / +”提供相同的缩放控制。

## 刷新恢复

网页版会把当前轨道、片段、播放头、选中状态与缩放比例自动写入当前标签页的
`sessionStorage`。意外刷新后，页面会提示重新选择原视频；浏览器不会保存或上传视频
本体。核对视频时只读取文件头、中段和尾段各 64 KiB 并比较 SHA-256，文件再大也不会
整段计算。文件名或修改时间变化不影响识别，内容或大小不一致则拒绝恢复。

多素材项目可以一次选择或分批补齐所有原视频。用户也可以选择“放弃上次进度”，清除
当前标签页中的自动存档并从空项目重新开始。恢复后的撤销/重做历史为空，但当前剪辑
结果和工作位置会保留。

## 导出路线

导出有两条路线。快速路线优先让音视频都留在 WebCodecs 管线中：

| 路线 | 画面 | 音频与混流 | 适用 |
| --- | --- | --- | --- |
| 快速（默认） | WebCodecs 解码 + 编码（可走平台硬件编码） | AudioDecoder → 48 kHz PCM → WSOLA 保持音高变速 → AudioEncoder（AAC 优先，Opus 次选），由 mp4-muxer 一次封装 | 支持相应编解码器的浏览器 |
| 兜底 | ffmpeg.wasm，使用与桌面版完全相同的 `buildFilter` | 同上，同一次 ffmpeg 调用 | 任何浏览器 |

AAC 编码并不是所有 WebCodecs 实现都提供。启动时会同时探测 AAC 与 Opus：优先 AAC，
AAC 不可用时使用 MP4 中的 Opus；导出前还会逐个验证素材的 AudioDecoder 配置。两种编码
都不可用或运行失败时才把音频回退到 ffmpeg.wasm，视频仍保留 WebCodecs 的速度优势。
静音项目完全不加载 ffmpeg.wasm。

### 两条路线的画质

以**原始 YUV 采样**衡量，两条路线与源的平均差都约为 0.23/255，因此都保真。

有一个容易误判的地方：如果把两条路线的产物都解成 RGB 再比较，会看到 WebCodecs
路线与源相差约 8/255，看上去像是偏色。但这其实不是像素损失，而是元数据差异：
源文件没有标记色彩矩阵，解码器只能猜（常见是 BT.601），而 WebCodecs 的输出会明确
标记为 bt709。比较原始 YUV 采样即可确认两者采样一致。

### 能力探测与降级

启动时用 `isConfigSupported` 探测真实可用性，而不是假设浏览器能力：

- 没有 H.264 编码能力 → 自动改用 ffmpeg.wasm。
- 选了 WebCodecs 但实际配置不受支持 → 导出时自动降级并说明原因。
- 缺少 COOP/COEP → 自动使用单线程 ffmpeg.wasm 核心。

### 关于多线程 ffmpeg.wasm

跨源隔离页面可以用多线程核心，但 `@ffmpeg/core-mt` 会按
`navigator.hardwareConcurrency` 预建 pthread 工作池（上限 32）。在核心数很多或受限的
环境里，`load()` 会成功、`exec()` 却直接挂起，因此**不能只看加载是否成功**。

所以加载后会先做一次真实试编码（一张 320×240 的 SDR 画面，`libx264`，输出到 `null`），
超时即判定多线程不可用并回退到单线程核心。这与桌面版用试编码判断 NVENC 可用性的
思路一致——能力列表不代表能真的工作。结论在会话内缓存，不会每次导出都重试。

## 开发

```bash
cd web
npm install
npm run dev        # http://localhost:5173
npm run typecheck
npm test           # 模型与一致性单元测试（无需浏览器 / FFmpeg）
```

`npm run dev` 与 `npm run preview` 会带上 COOP/COEP 响应头，行为与生产一致。

## 测试

```bash
# 单元测试：编辑模型 + filter graph 一致性 + 刷新恢复
npm test

# 端到端：真实 Chromium 导出，并用 FFprobe 校验产物
npx playwright install chromium
node --experimental-strip-types --test test/e2e.test.ts
```

端到端测试需要 FFmpeg 与 FFprobe，默认从 `PATH` 查找，也可指定：

```bash
CLIP_FFMPEG=/path/to/ffmpeg CLIP_FFPROBE=/path/to/ffprobe \
  node --experimental-strip-types --test test/e2e.test.ts
```

缺少依赖时会**跳过并说明原因**，不会静默通过。测试会实际生成素材并跑完整导出，
当前覆盖：

- 时长、分辨率、帧数、音轨与容器有效性（由 FFprobe 解析真实产物）
- 变速倍率与导出时长的对应关系
- 竖屏素材补黑边（检查像素确实是纯黑边条，而不是拉伸）
- 无声素材不凭空产生音轨；多素材混剪补静音与拼接
- 同一素材切成多段后导出整条轨道（多片段解码状态复用）
- 反复取消导出对话框后编辑与导出仍然正常
- 两条路线音频逐样本一致，以及 YUV 采样层面的画质保真
- 明暗主题下关键区域可见、`hidden` 生效、自定义尺寸校验

产物、FFprobe 结果与界面截图保存在 `web/test/artifacts/`。

一致性基准更新（当 `ExportService` 有意变更时）：

```bash
dotnet run --project tools/Clip.Conformance -c Release -- web/test/fixtures/conformance.json
```

## 构建与部署

```bash
npm run build      # 输出到 web/dist
```

`dist` 是自包含的：ffmpeg.wasm 核心（约 62 MB）与图标都会一起产出。
`npm run build` 会先调用 `scripts/copy-ffmpeg-core.mjs`，从 `node_modules`
复制核心，并把每个约 32 MB 的 WASM 拆成不超过 16 MiB 的静态分片；浏览器加载时
会按清单重新组合。这样可以满足 Cloudflare Workers / Pages 的 25 MiB 单文件上限，
同时不依赖运行时第三方 CDN。脚本也会写入 `version.json` 便于核对线上版本。
部署完成后应确认 `/ffmpeg/version.json` 返回 200。

Cloudflare Workers 项目把 Root directory 设为 `web`，Build command 设为
`npm ci && npm run build`，Deploy command 使用 `npx wrangler deploy`。仓库中的
`wrangler.jsonc` 会确保 Wrangler 只发布 `dist/`；不要用 `--assets=.` 发布整个
`web/` 目录，否则源码、`public/` 和 `node_modules/` 也会被当成线上资产。

部署时请提供跨源隔离响应头，否则会自动退回单线程核心：

- Netlify / Cloudflare Pages / Workers Static Assets：`public/_headers` 会随构建产物发布，
  并包含所需配置。
- nginx / 其他：参照该文件添加 `Cross-Origin-Opener-Policy: same-origin` 与
  `Cross-Origin-Embedder-Policy: require-corp`。

GitHub Pages 无法自定义响应头，多线程不可用；功能仍然完整，只是音频渲染更慢。

## 限制与兼容性

与桌面版一致：不支持 HDR（需先转 SDR）、多轨叠画 / 混音、转场与字幕，
每次只导出一条轨道，帧率取首个素材。

浏览器侧另有以下限制：

- MP4 / MOV 直接进入剪辑管线；MKV / WebM 会先在浏览器内转换为 MP4 容器。编码兼容时
  直接复制音视频数据，不重复压缩；容器不能直接接收源编码时才尝试用 WebCodecs 转码。
  转换过程需要同时保存输入与输出，大型素材会占用更多浏览器内存。
- 预览用 `<video>` 的 `playbackRate`，超出浏览器范围的速度（0.1×–8× 之外的原始值）
  会按可用范围钳制播放，但导出时长始终按设定的速度计算。
- 同一素材被切成多段时，每段会从该段起点之前最近的关键帧开始解码，
  因此段首可能多解码少量帧（不影响输出，只是轻微的解码开销）。

## 自动化接口

带 `?automation=1` 打开时，会在 `window.__clip` 暴露编辑与导出方法，供端到端测试
驱动真实代码路径（不绕过任何业务逻辑）。正常访问不会挂载。
