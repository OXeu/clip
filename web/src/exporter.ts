/**
 * 导出实现。
 *
 * 两条路线：
 *
 * 快速路线（WebCodecs 可用时）
 *   VideoDecoder 逐段解码 → OffscreenCanvas 缩放/补边 → VideoEncoder 硬件编码
 *   音频由 AudioDecoder 解码、WSOLA 保持音高变速，再由 AudioEncoder 编成 AAC/Opus
 *   → mp4-muxer 一次封装音视频。两种编码都不可用时仅回退音频到 ffmpeg.wasm。
 *
 * 兜底路线（无 WebCodecs 时）
 *   完全交给 ffmpeg.wasm，使用与桌面端相同的 buildFilter 与参数。
 *
 * 视频编码前会先用 isConfigSupported 复核实际输出尺寸；若不可用则自动
 * 降级到兜底路线，保证「导出成功」优先于「跑得快」。
 */

import { ArrayBufferTarget, Muxer } from 'mp4-muxer';

import { EncoderRoute, h264CodecFor, type Capabilities } from './capabilities.ts';
import { FfmpegRunner, type ExportProgress } from './ffmpeg.ts';
import {
  buildAudioFilter,
  buildAudioMuxArguments,
  buildFilter,
  buildWasmExportArguments,
} from './filtergraph.ts';
import {
  type ExportOptions,
  ExportQuality,
  type MediaInfo,
  type VideoClip,
  clipDuration,
  getDimensions,
  hasAudio,
} from './model.ts';
import { type DemuxedFile, demuxFile } from './mp4demux.ts';
import {
  assertWebCodecsAudioSupported,
  encodeWebCodecsAudio,
  WEB_AUDIO_SAMPLE_RATE,
  WebCodecsAudioUnavailableError,
  type WebCodecsAudioCodec,
} from './webcodecs-audio.ts';

export interface ExportRequest {
  readonly clips: readonly VideoClip[];
  readonly options: ExportOptions;
  readonly route: EncoderRoute;
  readonly capabilities: Capabilities;
  /** ffmpeg.wasm 静态资源基地址。 */
  readonly ffmpegBase: string;
  /** 读取素材字节（浏览器里通常是 File 对象）。 */
  readonly readSource: (path: string) => Promise<ArrayBuffer>;
  readonly onProgress?: (progress: ExportProgress) => void;
  readonly signal?: AbortSignal;
  readonly onLog?: (line: string) => void;
}

export interface ExportResult {
  readonly data: Uint8Array;
  readonly route: EncoderRoute;
  readonly mimeType: string;
  readonly width: number;
  readonly height: number;
  /** 实际用于编码的 H.264 配置（兜底路线为 null）。 */
  readonly codec: string | null;
  /** 最终音轨使用的编码路线；无音轨时为 none。 */
  readonly audioRoute: 'webcodecs' | 'ffmpeg-wasm' | 'none';
  /** 最终音轨的实际编码格式；无音轨时为 null。 */
  readonly audioCodec: WebCodecsAudioCodec | null;
  /** 说明是否因为 WebCodecs 配置不受支持而自动降级。 */
  readonly fellBack?: string;
  /** 说明 ffmpeg.wasm 是否从多线程降级到单线程。 */
  readonly threading?: string;
}

const checkAborted = (signal?: AbortSignal): void => {
  if (signal?.aborted) throw new DOMException('已取消导出', 'AbortError');
};

/** 按画质档位换算目标码率。WebCodecs 没有 CRF，只能用码率表达画质意图。 */
export function bitrateFor(
  quality: ExportQuality,
  width: number,
  height: number,
  frameRate: number,
): number {
  const multiplier: Record<ExportQuality, number> = {
    [ExportQuality.Original]: 0.12,
    [ExportQuality.High]: 0.09,
    [ExportQuality.Balanced]: 0.06,
    [ExportQuality.Compact]: 0.035,
  };
  const raw = width * height * frameRate * multiplier[quality];
  return Math.round(Math.min(60_000_000, Math.max(1_500_000, raw)));
}

/**
 * mp4-muxer 只接受正整数 frameRate。非整数帧率不能直接传入；省略后它会
 * 使用高精度 timescale，并继续按 EncodedVideoChunk 的真实时间戳封装。
 */
export function muxerFrameRate(frameRate: number): number | undefined {
  return Number.isInteger(frameRate) && frameRate > 0 ? frameRate : undefined;
}

/**
 * mp4-muxer 必须从 VideoEncoder 的输出元数据取得 avcC；部分浏览器虽然声称
 * 支持 H.264 编码，却可能不输出 chunk，或省略 decoderConfig。继续 finalize()
 * 会在依赖内部以 `decoderConfig is null` 崩溃，因此应先回退到 wasm 路线。
 */
export function videoMuxerReadinessError(
  encodedChunkCount: number,
  receivedDecoderConfig: boolean,
): string | null {
  if (encodedChunkCount === 0) return '浏览器的视频编码器没有输出任何数据。';
  if (!receivedDecoderConfig) {
    return '浏览器的视频编码器没有提供 MP4 封装所需的 decoderConfig。';
  }
  return null;
}

/** 只有项目确实包含源音轨时才声明输出音轨。 */
export function preferredWebCodecsAudioCodec(
  anyAudio: boolean,
  capabilities: Pick<Capabilities, 'aacEncoder' | 'opusEncoder'>,
): WebCodecsAudioCodec | null {
  if (!anyAudio) return null;
  if (capabilities.aacEncoder) return 'aac';
  if (capabilities.opusEncoder) return 'opus';
  return null;
}

/** 缓存已解封装的素材，避免同一素材被多个片段重复解析。 */
class SourceCache {
  private readonly files = new Map<string, Promise<DemuxedFile>>();
  private readonly readSource: (path: string) => Promise<ArrayBuffer>;

  constructor(readSource: (path: string) => Promise<ArrayBuffer>) {
    this.readSource = readSource;
  }

  get(path: string): Promise<DemuxedFile> {
    const key = path.toLowerCase();
    let entry = this.files.get(key);
    if (!entry) {
      entry = this.readSource(path).then((data) => demuxFile(data));
      this.files.set(key, entry);
    }
    return entry;
  }

  clear(): void {
    this.files.clear();
  }
}

/**
 * 逐帧队列：把解码回调变成可 await 的流，并对 VideoFrame 施加背压。
 *
 * 关键约束来自 Chromium 的实测行为：VideoFrame 必须在用完时 close()，
 * 否则解码器会停滞，最终整个导出挂起（控制台报
 * "A VideoFrame was garbage collected without being closed"）。
 * 因此队列在超过高水位时暂停喂样本，而不是无限堆积帧。
 */
export class FrameQueue {
  private readonly queue: VideoFrame[] = [];
  private waiter: (() => void) | null = null;
  private failure: Error | null = null;
  private finished = false;
  /** 消费方提前满足需求后置位，用于解除投喂方的背压等待。 */
  private stopped = false;
  private readonly highWaterMark: number;
  private backpressure: (() => void) | null = null;

  constructor(highWaterMark = 6) {
    this.highWaterMark = highWaterMark;
  }

  get isStopped(): boolean {
    return this.stopped;
  }

  push(frame: VideoFrame): void {
    // 已经不需要更多帧时立即释放，避免 VideoFrame 未关闭导致的解码器停滞。
    if (this.stopped || this.failure) {
      frame.close();
      return;
    }
    this.queue.push(frame);
    this.wake();
    if (this.queue.length >= this.highWaterMark) this.backpressure?.();
  }

  fail(error: Error): void {
    this.failure = error;
    this.wake();
    this.backpressure?.();
  }

  finish(): void {
    this.finished = true;
    this.wake();
    this.backpressure?.();
  }

  private wake(): void {
    const waiter = this.waiter;
    this.waiter = null;
    waiter?.();
  }

  async next(): Promise<VideoFrame | null> {
    for (;;) {
      if (this.failure) throw this.failure;
      const frame = this.queue.shift();
      if (frame) {
        // 生产方可能正因为达到高水位而停在 readyForMore()。消费一帧后必须
        // 主动唤醒它；否则队列会被读空，而生产方仍永久等待，形成 0% 死锁。
        if (this.queue.length < this.highWaterMark) {
          const backpressure = this.backpressure;
          this.backpressure = null;
          backpressure?.();
        }
        return frame;
      }
      if (this.finished) return null;
      await new Promise<void>((resolve) => {
        this.waiter = resolve;
      });
    }
  }

  /** 队列积压时挂起投喂，直到消费到低水位、结束或被停止。 */
  async readyForMore(): Promise<void> {
    while (
      this.queue.length >= this.highWaterMark &&
      !this.finished &&
      !this.failure &&
      !this.stopped
    ) {
      await new Promise<void>((resolve) => {
        this.backpressure = resolve;
      });
    }
    if (this.failure) throw this.failure;
  }

  /** 消费方已经取够帧：解挂投喂并释放剩余帧。 */
  stop(): void {
    this.stopped = true;
    this.wake();
    this.backpressure?.();
    while (this.queue.length > 0) this.queue.shift()!.close();
  }

  /**
   * 为下一个片段重置状态。
   *
   * 同一素材的多个片段共用同一个解码器与队列（避免重复解析容器），
   * 但 stop()/finish() 是每个片段独立的生命周期；若不复位，
   * 「分割后导出整条轨道」会在第二个片段处直接判定为已结束而得到 0 帧。
   */
  reset(): void {
    this.stopped = false;
    this.finished = false;
    this.failure = null;
    this.waiter = null;
    this.backpressure = null;
    while (this.queue.length > 0) this.queue.shift()!.close();
  }
}

/** 单个素材的解码器与样本列表。 */
interface SourceDecoder {
  readonly decoder: VideoDecoder;
  readonly demuxed: DemuxedFile;
  readonly queue: FrameQueue;
}

/**
 * 把片段内 [start,end] 的样本送入解码器，然后 flush 并标记完成。
 *
 * Decode() 只是入队，帧的输出是异步的；因此必须在所有输出真正回调上来之后
 * 才能调用 finish()，否则消费方会看到空队列而误以为解码结束。
 * 起点回退到目标时间之前最近的关键帧（WebCodecs 没有精确定位 API）。
 */
async function feedClipSamples(
  source: SourceDecoder,
  startTime: number,
  endTime: number,
): Promise<void> {
  const track = source.demuxed.video;
  if (!track) throw new Error('素材中没有视频轨。');
  const samples = source.demuxed.samples.get(track.id) ?? [];
  if (samples.length === 0) throw new Error('素材中没有可解码的样本。');
  const timescale = track.timescale;
  const startTs = startTime * timescale;
  const endTs = endTime * timescale;

  let startIndex = 0;
  for (let index = samples.length - 1; index >= 0; index--) {
    if (samples[index]!.cts <= startTs && samples[index]!.is_sync) {
      startIndex = index;
      break;
    }
  }

  for (let index = startIndex; index < samples.length; index++) {
    const sample = samples[index]!;
    if (sample.cts > endTs) break;
    if (source.queue.isStopped) return;
    await source.queue.readyForMore();
    const chunk = new EncodedVideoChunk({
      type: sample.is_sync ? 'key' : 'delta',
      timestamp: Math.round((sample.cts / timescale) * 1_000_000),
      duration: Math.round((sample.duration / timescale) * 1_000_000),
      data: sample.data,
    });
    source.decoder.decode(chunk);
  }

  // 等待解码器把所有待输出帧交付完毕，再宣告结束。
  if (!source.queue.isStopped) await source.decoder.flush();
  source.queue.finish();
}

async function createSourceDecoder(
  demuxed: DemuxedFile,
  queue: FrameQueue,
): Promise<VideoDecoder> {
  const track = demuxed.video;
  if (!track) throw new Error('素材中没有视频轨。');
  const decoder = new VideoDecoder({
    output: (frame) => queue.push(frame),
    error: (error) => queue.fail(error instanceof Error ? error : new Error(String(error))),
  });
  const config: VideoDecoderConfig = {
    codec: track.codec,
    codedWidth: track.width,
    codedHeight: track.height,
    optimizeForLatency: false,
  };
  // avcC/hvcC 配置盒；WebCodecs 用它确定 profile 与参数集。
  if (track.description) config.description = track.description;
  const support = await VideoDecoder.isConfigSupported(config);
  if (!support.supported) {
    throw new Error(`浏览器无法解码 ${track.codec}，请改用 ffmpeg.wasm 路线。`);
  }
  decoder.configure(config);
  return decoder;
}

/** 片段内需要输出的帧时刻（源时间，升序），帧率取首个素材。 */
function outputTimesFor(clip: VideoClip, targetFps: number): number[] {
  const step = 1 / targetFps;
  const duration = clipDuration(clip);
  const count = Math.max(1, Math.round(duration * targetFps));
  const times: number[] = [];
  for (let index = 0; index < count; index++) {
    times.push(Math.min(clip.start + index * step * clip.speed, clip.end));
  }
  return times;
}

interface VideoTrackResult {
  readonly data: Uint8Array;
  readonly codec: string;
  readonly fellBack?: string;
}

/**
 * 用 WebCodecs 编码视频轨，返回无声 MP4。
 * 若配置不受支持则抛出 WebCodecsUnavailableError，由调用方降级。
 */
class WebCodecsUnavailableError extends Error {}

interface WebCodecsAudioStage {
  readonly codec: WebCodecsAudioCodec;
  readonly encode: (muxer: Muxer<ArrayBufferTarget>) => Promise<void>;
}

async function encodeVideoTrack(
  clips: readonly VideoClip[],
  options: ExportOptions,
  capabilities: Capabilities,
  sources: SourceCache,
  report: (progress: ExportProgress) => void,
  signal?: AbortSignal,
  audioStage?: WebCodecsAudioStage,
): Promise<VideoTrackResult> {
  const first = clips[0]!;
  const { width, height } = getDimensions(options, first.media);
  const fps = first.media.frameRate > 0 ? first.media.frameRate : 30;
  const codec = capabilities.h264Codec ?? h264CodecFor(width, height, fps);
  const bitrate = bitrateFor(options.quality, width, height, fps);

  // 先复核实际输出配置；不支持就不要开始编码。
  if (typeof VideoEncoder === 'undefined') {
    throw new WebCodecsUnavailableError('此浏览器没有 VideoEncoder。');
  }
  const supported = await VideoEncoder.isConfigSupported({
    codec,
    width,
    height,
    bitrate,
    framerate: fps,
  });
  if (!supported.supported) {
    throw new WebCodecsUnavailableError(`WebCodecs 不支持 ${codec} @ ${width}×${height}。`);
  }

  const canvas = new OffscreenCanvas(width, height);
  const context = canvas.getContext('2d', { alpha: false });
  if (!context) throw new Error('无法创建 2D 画布用于缩放输出。');

  const muxerFps = muxerFrameRate(fps);
  const muxer = new Muxer({
    target: new ArrayBufferTarget(),
    video: {
      codec: 'avc',
      width,
      height,
      ...(muxerFps === undefined ? {} : { frameRate: muxerFps }),
    },
    ...(audioStage ? {
      audio: {
        codec: audioStage.codec,
        numberOfChannels: 2,
        sampleRate: WEB_AUDIO_SAMPLE_RATE,
      },
    } : {}),
    fastStart: 'in-memory',
    // 部分 WebCodecs 实现输出的首个编码块并非恰好从 DTS=0 开始（例如一帧后）。
    // 逐轨归零，避免 mp4-muxer 的 strict 模式拒绝这类合法输出。
    firstTimestampBehavior: 'offset',
  });

  let encoderError: Error | null = null;
  let encodedChunkCount = 0;
  let receivedDecoderConfig = false;
  const encoder = new VideoEncoder({
    output: (chunk, meta) => {
      try {
        muxer.addVideoChunk(chunk, meta);
        encodedChunkCount++;
        receivedDecoderConfig ||= Boolean(meta?.decoderConfig);
      } catch (error) {
        encoderError = error instanceof Error ? error : new Error(String(error));
      }
    },
    error: (error) => {
      encoderError = error instanceof Error ? error : new Error(String(error));
    },
  });
  encoder.configure({
    codec,
    width,
    height,
    bitrate,
    framerate: fps,
    latencyMode: 'quality',
  });

  const totalFrames = clips.reduce(
    (total, clip) => total + Math.max(1, Math.round(clipDuration(clip) * fps)),
    0,
  );
  let emitted = 0;

  /**
   * 送入编码器。帧尺寸与输出一致时直接透传，否则经画布缩放/补黑边。
   *
   * 直接透传是一次性能优化：省掉一次全帧 drawImage 与 YUV→RGB→YUV 往返。
   * 实测两条分支的输出质量相同——原始 YUV 采样与源的平均差都约为 0.23/255，
   * 因此这里只是为了少做一次全帧转换，并不是为了修正画质。
   *
   * 关于「色彩偏移」的澄清：如果只看解成 RGB 的像素，会看到 WebCodecs 输出与源
   * 相差约 8/255，但那不是像素损失，而是元数据差异。源未标记色彩矩阵，
   * 播放器只能猜（常猜成 BT.601），而 WebCodecs 输出明确标记为 bt709。
   * 以原始 YUV 为准可以确认采样本身是一致的。
   */
  const emit = async (frame: VideoFrame): Promise<void> => {
    if (encoderError) throw encoderError;
    const needsResample = frame.displayWidth !== width || frame.displayHeight !== height;
    let source: CanvasImageSource = frame;
    if (needsResample) {
      const mediaAspect = frame.displayWidth / Math.max(1, frame.displayHeight);
      const targetAspect = width / height;
      let drawWidth = width;
      let drawHeight = height;
      if (mediaAspect > targetAspect) drawHeight = Math.max(2, Math.round(width / mediaAspect));
      else drawWidth = Math.max(2, Math.round(height * mediaAspect));
      const offsetX = Math.floor((width - drawWidth) / 2);
      const offsetY = Math.floor((height - drawHeight) / 2);
      context.fillStyle = '#000000';
      context.fillRect(0, 0, width, height);
      context.drawImage(frame, offsetX, offsetY, drawWidth, drawHeight);
      source = canvas;
    }

    const output = new VideoFrame(source, {
      timestamp: Math.round((emitted / fps) * 1_000_000),
      duration: Math.round((1 / fps) * 1_000_000),
    });
    try {
      // 约每 2 秒一个关键帧。
      encoder.encode(output, { keyFrame: emitted % Math.max(1, Math.round(fps * 2)) === 0 });
    } finally {
      output.close();
    }
    emitted++;
    if (emitted === 1 || emitted % 8 === 0 || emitted === totalFrames) {
      report({
        fraction: Math.max(0.01, Math.min(0.98, emitted / totalFrames)),
        message: '正在用 WebCodecs 编码画面…',
      });
    }
    // 编码队列有界，避免内存无限增长，同时让出主线程。
    while (encoder.encodeQueueSize > 8) {
      await new Promise<void>((resolve) => setTimeout(resolve, 0));
      if (encoderError) throw encoderError;
      checkAborted(signal);
    }
  };

  const openDecoders = new Map<string, SourceDecoder>();
  try {
    for (const clip of clips) {
      checkAborted(signal);
      if (encoderError) throw encoderError;
      const key = clip.media.path.toLowerCase();
      let source = openDecoders.get(key);
      if (!source) {
        const demuxed = await sources.get(clip.media.path);
        const queue = new FrameQueue();
        const decoder = await createSourceDecoder(demuxed, queue);
        source = { decoder, demuxed, queue };
        openDecoders.set(key, source);
      }

      const times = outputTimesFor(clip, fps);
      // 复用同一素材的解码器时，必须复位队列状态（见 reset 的说明）。
      source.queue.reset();
      // 解码与消费并行：投喂在后台推进，这里按输出时刻取帧。
      const feeding = feedClipSamples(source, clip.start, clip.end).catch((error: unknown) => {
        source.queue.fail(error instanceof Error ? error : new Error(String(error)));
      });

      let timeIndex = 0;
      let current: VideoFrame | null = null;
      try {
        for (;;) {
          const next = await source.queue.next();
          if (!next) {
            // 解码结束：剩余输出时刻复用最后一帧（接近 tpad=clone 的效果）。
            if (current) {
              while (timeIndex < times.length) {
                await emit(current);
                timeIndex++;
              }
            }
            break;
          }
          if (current) {
            const nextSeconds = next.timestamp / 1_000_000;
            // 上一帧覆盖到下一帧出现为止，因此先补齐落在该区间内的输出时刻。
            while (timeIndex < times.length && times[timeIndex]! < nextSeconds) {
              await emit(current);
              timeIndex++;
            }
            current.close();
          }
          current = next;
          if (timeIndex >= times.length) {
            current.close();
            current = null;
            // 该片段所需的输出帧已足够，停止消费并释放剩余帧。
            break;
          }
        }
      } finally {
        if (current) current.close();
        // 先停止再等投喂结束，避免投喂方卡在背压上。
        source.queue.stop();
        await feeding;
      }

      if (timeIndex < times.length) {
        throw new Error(`片段解码帧不足：需要 ${times.length} 帧，得到 ${timeIndex} 帧。`);
      }
    }

    await encoder.flush();
    if (encoderError) throw encoderError;
    const readinessError = videoMuxerReadinessError(encodedChunkCount, receivedDecoderConfig);
    if (readinessError) throw new WebCodecsUnavailableError(readinessError);
    report({ fraction: 1, message: '画面编码完成' });
    if (audioStage) await audioStage.encode(muxer);
    muxer.finalize();
    const buffer = muxer.target.buffer;
    if (!buffer || buffer.byteLength === 0) throw new Error('MP4 封装未产生数据。');
    return { data: new Uint8Array(buffer), codec };
  } catch (error) {
    // 异常路径也要释放已解码但未消费的帧。
    for (const source of openDecoders.values()) source.queue.stop();
    throw error;
  } finally {
    for (const source of openDecoders.values()) {
      if (source.decoder.state !== 'closed') source.decoder.close();
    }
    if (encoder.state !== 'closed') encoder.close();
  }
}


/** 唯一的源素材，按出现顺序去重（与桌面端 Sources 一致）。 */
function distinctMedia(clips: readonly VideoClip[]): MediaInfo[] {
  const seen = new Set<string>();
  const result: MediaInfo[] = [];
  for (const clip of clips) {
    const key = clip.media.path.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    result.push(clip.media);
  }
  return result;
}

/**
 * wasm 虚拟文件系统里的输入文件名。
 * 用稳定且安全的文件名，避免中文与空格在 filter 脚本里需要额外转义。
 */
export function ffmpegInputName(media: MediaInfo): string {
  const base = media.path.split(/[/\\]/).pop() ?? 'input';
  const safe = base.replace(/[^\w.-]/g, '_');
  return `in_${safe}`;
}

/** 把 filter 脚本里的原始路径替换为 wasm 内的输入文件名。 */
function rewritePaths(graph: string, clips: readonly VideoClip[]): string {
  let result = graph;
  for (const media of distinctMedia(clips)) {
    result = result.split(media.path).join(ffmpegInputName(media));
  }
  return result;
}

function withMappedPath(clip: VideoClip): VideoClip {
  return { ...clip, media: { ...clip.media, path: ffmpegInputName(clip.media) } };
}

/** 用 ffmpeg.wasm 完成整套导出（兜底路线）。 */
async function exportWithWasm(
  request: ExportRequest,
  runner: FfmpegRunner,
  report: (progress: ExportProgress) => void,
  width: number,
  height: number,
): Promise<ExportResult> {
  const { clips, options, capabilities, ffmpegBase, readSource, signal, onLog } = request;
  report({ fraction: 0.01, message: '正在加载 ffmpeg.wasm 核心…' });
  const loaded = await runner.load(ffmpegBase, capabilities.sharedArrayBuffer, onLog, signal);
  const threading = loaded.fellBackFromMultithreaded
    ? '多线程核心不可用，已改用单线程核心（速度较慢）。'
    : undefined;

  const filterName = 'clip-export.ffgraph';
  const filter = rewritePaths(buildFilter(clips, options), clips);
  report({ fraction: 0.02, message: '正在准备导出素材…' });
  await runner.writeFile(filterName, new TextEncoder().encode(filter));

  const inputs: { name: string; data: Uint8Array }[] = [];
  for (const media of distinctMedia(clips)) {
    inputs.push({ name: ffmpegInputName(media), data: new Uint8Array(await readSource(media.path)) });
  }

  const duration = clips.reduce((total, clip) => total + clipDuration(clip), 0);
  const result = await runner.run({
    args: buildWasmExportArguments(clips.map(withMappedPath), options, filterName, 'output.mp4'),
    inputs,
    output: 'output.mp4',
    duration,
    onProgress: (update) => report({
      fraction: 0.02 + update.fraction * 0.98,
      message: update.message,
    }),
    ...(signal ? { signal } : {}),
  });
  await runner.deleteFile(filterName);

  return {
    data: result.data,
    route: EncoderRoute.Wasm,
    mimeType: 'video/mp4',
    width,
    height,
    codec: null,
    audioRoute: clips.some((clip) => hasAudio(clip.media)) ? 'ffmpeg-wasm' : 'none',
    audioCodec: clips.some((clip) => hasAudio(clip.media)) ? 'aac' : null,
    ...(threading ? { threading } : {}),
  };
}

export async function exportClips(request: ExportRequest): Promise<ExportResult> {
  const { clips, options, route, capabilities, ffmpegBase, readSource, onProgress, onLog, signal } =
    request;
  if (clips.length === 0) throw new Error('所选轨道为空，请选择包含片段的轨道。');
  const { width, height } = getDimensions(options, clips[0]!.media);
  let lastReportedFraction = 0;
  const report = (update: ExportProgress): void => {
    const fraction = Math.min(1, Math.max(lastReportedFraction, update.fraction));
    lastReportedFraction = fraction;
    onProgress?.({ ...update, fraction });
  };

  const cache = new SourceCache(readSource);
  const runner = new FfmpegRunner();
  let fellBack: string | undefined;

  try {
    // ---- 快速路线：WebCodecs 编码视频，音频优先 AAC、其次 Opus ----
    if (route === EncoderRoute.WebCodecsVideo && capabilities.webCodecsVideo) {
      report({ fraction: 0.01, message: '正在解析并启动 WebCodecs 编码…' });
      const anyAudio = clips.some((clip) => hasAudio(clip.media));
      let webCodecsAudioCodec = preferredWebCodecsAudioCodec(anyAudio, capabilities);
      let audioFallback: string | undefined;
      if (anyAudio && webCodecsAudioCodec) {
        try {
          await assertWebCodecsAudioSupported(
            clips,
            (path) => cache.get(path),
            webCodecsAudioCodec,
            signal,
          );
        } catch (error) {
          if (error instanceof WebCodecsAudioUnavailableError) audioFallback = error.message;
          else throw error;
        }
      } else if (anyAudio) {
        audioFallback = '此浏览器不支持 WebCodecs AAC 或 Opus 编码。';
      }

      let encoded: VideoTrackResult | null = null;
      try {
        encoded = await encodeVideoTrack(
          clips,
          options,
          capabilities,
          cache,
          (update) => report({
            fraction: 0.01 + update.fraction * (webCodecsAudioCodec ? 0.69 : 0.79),
            message: update.message,
          }),
          signal,
          webCodecsAudioCodec
            ? {
              codec: webCodecsAudioCodec,
              encode: (muxer) => encodeWebCodecsAudio(
                clips,
                (path) => cache.get(path),
                muxer,
                webCodecsAudioCodec!,
                (fraction) => report({
                  // 为运行期音频编码失败后的 wasm 兜底保留进度区间。
                  fraction: 0.70 + fraction * 0.12,
                  message: `正在用 WebCodecs 编码 ${webCodecsAudioCodec === 'aac' ? 'AAC' : 'Opus'} 音频…`,
                }),
                signal,
              ),
            }
            : undefined,
        );
      } catch (error) {
        if (error instanceof WebCodecsAudioUnavailableError) {
          // 能力探测通过后仍可能因具体素材或平台编码器失败。丢弃未完成的
          // muxer，重新生成无声视频，随后沿用成熟的 ffmpeg.wasm 音频路线。
          audioFallback = error.message;
          webCodecsAudioCodec = null;
          report({ fraction: 0.70, message: `WebCodecs 音频不可用，正在回退：${error.message}` });
          encoded = await encodeVideoTrack(
            clips,
            options,
            capabilities,
            cache,
            (update) => report({
              fraction: 0.01 + update.fraction * 0.79,
              message: update.message,
            }),
            signal,
          );
        } else if (error instanceof WebCodecsUnavailableError) {
          fellBack = error.message;
        } else {
          throw error;
        }
      }

      if (encoded) {
        if (!anyAudio || webCodecsAudioCodec) {
          report({ fraction: 1, message: '导出完成' });
          return {
            data: encoded.data,
            route: EncoderRoute.WebCodecsVideo,
            mimeType: 'video/mp4',
            width,
            height,
            codec: encoded.codec,
            audioRoute: anyAudio ? 'webcodecs' : 'none',
            audioCodec: anyAudio ? webCodecsAudioCodec : null,
          };
        }

        report({ fraction: 0.82, message: '正在加载 ffmpeg.wasm 音频核心…' });
        const loaded = await runner.load(ffmpegBase, capabilities.sharedArrayBuffer, onLog, signal);
        const threading = loaded.fellBackFromMultithreaded
          ? '多线程核心不可用，音频混流改用单线程核心。'
          : undefined;
        const audioFilterName = 'clip-audio.ffgraph';
        const mapped = clips.map(withMappedPath);
        const audioGraph = rewritePaths(buildAudioFilter(clips), clips);
        report({ fraction: 0.84, message: '正在准备音频素材…' });
        await runner.writeFile(audioFilterName, new TextEncoder().encode(audioGraph));

        const inputs: { name: string; data: Uint8Array }[] = [
          { name: 'video.mp4', data: encoded.data },
        ];
        for (const media of distinctMedia(clips)) {
          if (!hasAudio(media)) continue;
          inputs.push({
            name: ffmpegInputName(media),
            data: new Uint8Array(await readSource(media.path)),
          });
        }

        const duration = clips.reduce((total, clip) => total + clipDuration(clip), 0);
        const result = await runner.run({
          args: buildAudioMuxArguments(mapped, 'video.mp4', audioFilterName, 'output.mp4'),
          inputs,
          output: 'output.mp4',
          duration,
          onProgress: (update) => report({
            fraction: 0.84 + update.fraction * 0.16,
            message: '正在渲染音频并混流…',
          }),
          ...(signal ? { signal } : {}),
        });
        await runner.deleteFile(audioFilterName);
        return {
          data: result.data,
          route: EncoderRoute.WebCodecsVideo,
          mimeType: 'video/mp4',
          width,
          height,
          codec: encoded.codec,
          audioRoute: 'ffmpeg-wasm',
          audioCodec: 'aac',
          ...(audioFallback ? { fellBack: audioFallback } : {}),
          ...(threading ? { threading } : {}),
        };
      }
      report({ fraction: 0, message: `WebCodecs 不可用，改用 ffmpeg.wasm：${fellBack}` });
    }

    // ---- 兜底路线 ----
    const result = await exportWithWasm(
      { ...request, route: EncoderRoute.Wasm },
      runner,
      report,
      width,
      height,
    );
    return fellBack ? { ...result, fellBack } : result;
  } finally {
    cache.clear();
    await runner.dispose();
  }
}
