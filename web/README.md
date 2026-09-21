# Clip Web（静态网页版）

[Clip](../README.md) 的浏览器实现，产物是纯静态文件，可以放在任何静态托管上
（GitHub Pages、Cloudflare Pages、Netlify、对象存储 + CDN）。所有处理都在访问者的
浏览器里完成，素材不会上传到 Clip 服务器。只有用户主动执行“识别字幕”时，检测出的
有声小段会发送给用户配置的腾讯云 ASR 接口。

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
在视频轨下方生成伴生音频波形轨，拖动排序时整组移动；网页版还可在同一视频组内添加
多条 BGM/伴声音轨。“多选轨道 → 保存并对齐”可建立多视角对齐组，分割会同步传播到
对齐轨及覆盖切点的伴生音轨，删除仍只作用于当前片段。

预览画面默认以 `contain` 完整适应可用区域；将鼠标放在画面上滚动可在 25%–400%
之间缩放，双击恢复适应。网页与 Windows 原生版使用相同的交互。

时间轴使用普通滚轮上下浏览轨道，`Shift + 滚轮` 横向移动时间轴窗口；
`Ctrl + 滚轮` 缩放并保持鼠标所指的时间点不动。右侧的“− / 适应 / +”提供相同的缩放控制。

## 刷新恢复

网页版会把当前轨道、片段、播放头、选中状态与缩放比例自动写入当前标签页的
`sessionStorage`。意外刷新后，页面会提示重新选择原始素材；浏览器不会保存或上传素材
本体。核对素材时只读取文件头、中段和尾段各 64 KiB 并比较 SHA-256，文件再大也不会
整段计算。文件名或修改时间变化不影响识别，内容或大小不一致则拒绝恢复。

多素材项目可以一次选择或分批补齐所有原始素材。用户也可以选择“放弃上次进度”，清除
当前标签页中的自动存档并从空项目重新开始。恢复后的撤销/重做历史为空，但当前剪辑
结果和工作位置会保留。

## 剪辑记录文件

顶部“更多”菜单中的“导出项目”会下载一份 `.clip` 文件，其中包含轨道、片段、速度、变调、音量、命名、绑定关系和工作位置，但不包含原始素材。通过“导入项目”、`Ctrl+Shift+O` 或直接拖入 `.clip` 文件，可以在刷新、换设备后继续剪辑，也可以与 Windows 桌面版互相传递项目。

恢复时浏览器仍会要求重新选择原始素材，并使用与刷新恢复相同的轻量 SHA-256 采样核对内容。选择错误文件不会替换当前项目；取消文件恢复时，原先正在编辑的项目也会保持不变。

## 普通音频

可以直接导入 M4A、MP3、WAV、AAC、FLAC、Ogg/Opus 等普通音频。单独导入音频时，
编辑器会建立一条空视频轨，并把音频片段放在其紧邻的伴生音频轨上；音频可预览、
显示波形并使用与视频片段相同的分割、变速、移动和恢复能力。空视频轨本身不会成为
视频导出目标。

右键视频轨选择“添加音频轨道…”可把 BGM、旁白或其他声音直接加入当前视频组；右键音频轨
可继续导入或删除整轨。多条音轨在导出时会先分别拼接、补齐到视频时长，再通过
ffmpeg.wasm 混合并限制峰值；默认只有原声时仍沿用 WebCodecs 快速音频路线。右键音频片段
可设置 0%–200% 的素材音量，右键音频轨左侧可设置轨道音量；导出时每段声音的线性增益为
“轨道音量 × 素材音量”，两级设置都会随项目保存并支持撤销。

右键音频片段打开“音频片段调整”，可在独立窗口组合设置 0.1–8× 倍速与 −12 到 +12
半音变调。窗口会在不修改项目的前提下试听片段开头，确认后才把两项参数作为一次可撤销
操作写入；倍速只改变时长，变调独立改变音高。含变调的导出会自动使用 ffmpeg.wasm 音频
处理链，WebCodecs 视频编码仍可继续使用。

## 字幕伴生轨与自动识别

每个视频轨道组默认折叠，点击左侧展开按钮后显示自带音轨与字幕轨。右键视频轨可添加
多个字幕伴生轨；在字幕轨空白处单击创建字幕，拖动字幕块主体或两侧调整位置与时长，
双击编辑文字。拖动时字幕块会实时改变位置和宽度，播放头保持不动。右键字幕轨可自动
填充字幕间隙，也可设置顶对齐、矩形中心对齐或底部对齐。伴生音频槽左侧按钮可在素材
名称和响度线两种显示模式之间切换。

选中字幕后，预览画面会显示可拖动、四角可缩放的字幕矩形框；水平或垂直居中时会显示
吸附线。选中其他音视频内容时，编辑边框会隐藏，只显示与最终导出一致的字幕画面。
字幕换行宽度不会超过矩形框，文本块高度按内容自然增长。WebCodecs 与
ffmpeg.wasm 两条导出路线都会把伴生字幕烧录到画面。

自动识别目前只支持腾讯云 TokenHub 的 `hy-asr-3.0-preview`。在“更多 → 设置”填写 API
Key（只保存在当前浏览器会话），选中音频轨后点击“识别字幕”，或右键音频轨执行识别。
浏览器先用能量与人声基频做本地 VAD，将静音剔除并拆成短段，再只把有声 WAV 段以 Base64
发送给[腾讯云同步 ASR 接口](https://cloud.tencent.com/document/product/1823/135791)；不调用额外的
打标模型。识别面板会分别显示音频解码、人声检测、云端识别的实时进度；接口失败时保留
中文错误、错误码和请求 ID，且不会留下空字幕轨。识别属于主动联网操作，可能产生腾讯云费用。

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

跨源隔离页面可以用多线程核心，但 `@ffmpeg/core-mt` 0.12.10 会预建 32 个 pthread
Worker。在资源受限的环境里，`load()` 会成功、`exec()` 却直接挂起，因此**不能只看加载
是否成功**。核心脚本与 pthread worker 脚本会先各下载一次并转换为 Blob URL，避免 32 个
Worker 对同一静态 URL 重复发起 HTTP 请求。

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

与桌面版一致：不支持 HDR（需先转 SDR）、多轨叠画 / 混音与转场，
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
