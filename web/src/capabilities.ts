/**
 * 能力探测与编码路线决策。
 *
 * 浏览器之间的 WebCodecs 支持差异很大，而且同一个浏览器在不同平台上也不同：
 * 实测 Linux 版 Chrome 不支持 AAC 编码，但支持 H.264 编码。
 * 因此路线不是写死的，而是启动时探测、并允许用户覆盖。
 */

export interface Capabilities {
  readonly webCodecsVideo: boolean;
  readonly webCodecsAudio: boolean;
  readonly sharedArrayBuffer: boolean;
  readonly crossOriginIsolated: boolean;
  /** WebCodecs 可用的 H.264 配置，优先高档 profile。 */
  readonly h264Codec: string | null;
  readonly h264Hardware: boolean;
  readonly aacEncoder: boolean;
  readonly opusEncoder: boolean;
  readonly notes: readonly string[];
}

export interface CapabilityProbeOptions {
  readonly width: number;
  readonly height: number;
  readonly frameRate: number;
  readonly bitrate?: number;
}

/** 与 src/Clip.Core 的 getDimensions 类似：H.264 需要偶数宽高。 */
const even = (value: number): number => Math.max(2, Math.floor(value / 2) * 2);

/**
 * 按分辨率挑选 H.264 codec 字符串。
 * level 必须够大，否则 isConfigSupported 会拒绝高分辨率配置。
 */
export function h264CodecFor(width: number, height: number, frameRate: number): string {
  // level 3.1 支持到 1280x720@30；再大需要更高 level。
  const macroblocks = Math.ceil(width / 16) * Math.ceil(height / 16);
  const mbPerSecond = macroblocks * frameRate;
  // High profile (0x64)，约束见 H.264 Annex A 表 A-1。
  const level =
    mbPerSecond <= 108_000 ? '1f' : // 3.1
    mbPerSecond <= 245_760 ? '20' : // 4.0
    mbPerSecond <= 522_240 ? '28' : // 4.2
    mbPerSecond <= 983_040 ? '32' : // 5.1
    '33'; // 5.2
  return `avc1.6400${level}`;
}

/**
 * 为实际输出规格生成 H.264 候选配置。
 *
 * 能力探测通常发生在首屏的 1080p30；导出尺寸或帧率更高时，不能继续复用
 * 探测结果里的旧 level，否则 isConfigSupported 会把本来可编码的配置判为不支持。
 * 保留已探测成功的 profile/compatibility，仅把 level 提升到当前输出所需值，
 * 再依次尝试 High、Main 与 Baseline profile。
 */
export function h264CodecCandidates(
  width: number,
  height: number,
  frameRate: number,
  preferred?: string | null,
): string[] {
  const high = h264CodecFor(width, height, frameRate);
  const level = high.slice(-2);
  const candidates: string[] = [];
  const match = preferred?.match(/^(avc1\.[0-9a-f]{4})[0-9a-f]{2}$/i);
  if (match) candidates.push(`${match[1]}${level}`);
  candidates.push(high, `avc1.4d00${level}`, `avc1.4200${level}`);
  return [...new Set(candidates)];
}

async function videoSupported(
  codec: string,
  width: number,
  height: number,
  frameRate: number,
  bitrate: number,
  hardwareAcceleration?: HardwareAcceleration,
): Promise<boolean> {
  if (typeof VideoEncoder === 'undefined') return false;
  try {
    const config: VideoEncoderConfig = {
      codec,
      width: even(width),
      height: even(height),
      bitrate,
      framerate: frameRate,
    };
    if (hardwareAcceleration) config.hardwareAcceleration = hardwareAcceleration;
    const support = await VideoEncoder.isConfigSupported(config);
    return support.supported === true;
  } catch {
    return false;
  }
}

async function audioSupported(codec: string): Promise<boolean> {
  if (typeof AudioEncoder === 'undefined') return false;
  try {
    const support = await AudioEncoder.isConfigSupported({
      codec,
      sampleRate: 48000,
      numberOfChannels: 2,
      bitrate: 192_000,
    });
    return support.supported === true;
  } catch {
    return false;
  }
}

export const sharedArrayBufferAvailable = (): boolean =>
  typeof SharedArrayBuffer === 'function' && globalThis.crossOriginIsolated === true;

export async function detectCapabilities(
  options: CapabilityProbeOptions,
): Promise<Capabilities> {
  const width = even(options.width);
  const height = even(options.height);
  const frameRate = options.frameRate > 0 ? options.frameRate : 30;
  // 目标码率粗略按像素率估算，只为探测可用性，不参与最终导出。
  const bitrate = options.bitrate ?? Math.min(
    40_000_000,
    Math.max(1_000_000, Math.round(width * height * frameRate * 0.09)),
  );

  const notes: string[] = [];
  const isolated = globalThis.crossOriginIsolated === true;
  const sab = sharedArrayBufferAvailable();

  if (!isolated) {
    notes.push(
      '页面未启用跨源隔离（COOP/COEP），ffmpeg.wasm 只能使用单线程核心，编码会明显变慢。',
    );
  }

  // 依次尝试：硬件优先 -> 默认 -> 软件。记录第一个可用的。
  let h264Codec: string | null = null;
  let h264Hardware = false;
  const codecs = h264CodecCandidates(width, height, frameRate);
  for (const codec of codecs) {
    if (await videoSupported(codec, width, height, frameRate, bitrate, 'prefer-hardware')) {
      h264Codec = codec;
      h264Hardware = true;
      break;
    }
    if (await videoSupported(codec, width, height, frameRate, bitrate)) {
      h264Codec = codec;
      h264Hardware = false;
      break;
    }
    if (await videoSupported(codec, width, height, frameRate, bitrate, 'prefer-software')) {
      h264Codec = codec;
      h264Hardware = false;
      break;
    }
  }

  const webCodecsVideo = h264Codec !== null;
  if (!webCodecsVideo) {
    notes.push('此浏览器的 WebCodecs 不支持 H.264 编码，将回退到 ffmpeg.wasm 软件编码。');
  } else if (!h264Hardware) {
    notes.push('未发现 H.264 硬件编码器，WebCodecs 将使用平台软件编码。');
  }

  const [aac, opus] = await Promise.all([audioSupported('mp4a.40.2'), audioSupported('opus')]);
  if (!aac && opus) {
    notes.push('浏览器不支持 AAC 编码，WebCodecs 音频将改用 Opus。');
  } else if (!aac && !opus) {
    notes.push('浏览器不支持 AAC 或 Opus 编码，音频将交给 ffmpeg.wasm 处理。');
  }

  return {
    webCodecsVideo,
    webCodecsAudio: aac || opus,
    sharedArrayBuffer: sab,
    crossOriginIsolated: isolated,
    h264Codec,
    h264Hardware,
    aacEncoder: aac,
    opusEncoder: opus,
    notes,
  };
}

export const EncoderRoute = {
  /** WebCodecs 编码音视频；AAC 不可用时尝试 Opus，再回退到 ffmpeg.wasm。 */
  WebCodecsVideo: 'webcodecs-video',
  /** 全部交给 ffmpeg.wasm，与桌面端行为最接近。 */
  Wasm: 'wasm',
} as const;
export type EncoderRoute = (typeof EncoderRoute)[keyof typeof EncoderRoute];

/**
 * 选择编码路线。
 *
 * WebCodecs 路线会优先使用浏览器的 H.264 与 AAC 编码器，AAC 不可用时改用
 * Opus；具体素材无法解码或两种音频编码都不可用时才回退音频部分。
 */
export function chooseRoute(
  capabilities: Capabilities,
  preference: EncoderRoute | 'auto' = 'auto',
): EncoderRoute {
  if (preference !== 'auto') return preference;
  return capabilities.webCodecsVideo ? EncoderRoute.WebCodecsVideo : EncoderRoute.Wasm;
}

/** 供界面展示的一行摘要。 */
export function describeCapabilities(capabilities: Capabilities): string {
  const video = capabilities.webCodecsVideo
    ? `WebCodecs H.264${capabilities.h264Hardware ? '（硬件）' : '（软件）'}`
    : 'FFmpeg.wasm 软件编码';
  const audio = capabilities.aacEncoder
    ? 'WebCodecs AAC'
    : capabilities.opusEncoder ? 'WebCodecs Opus' : 'FFmpeg.wasm AAC';
  const threads = capabilities.sharedArrayBuffer ? '多线程' : '单线程';
  return `${video} · 音频 ${audio} · ffmpeg.wasm ${threads}`;
}
