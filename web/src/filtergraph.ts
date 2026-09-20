/**
 * FFmpeg filter graph 生成，移植自 src/Clip.Core/ExportService.cs。
 *
 * 两条导出路线共用这里的音频半边：
 *   快速路线  WebCodecs 编码视频 → Mediabunny 封成 video.mp4，再用 ffmpeg.wasm
 *             只为音频执行 buildAudioFilter，最后 -c:v copy 混流。
 *   兜底路线  用 buildFilter 生成与桌面端完全相同的完整 filter 脚本。
 * 这样「变速、补静音、拼接、补帧」的语义只有一份实现。
 */

import {
  type ExportOptions,
  type MediaInfo,
  type VideoClip,
  VideoEncoder,
  clipDuration,
  getDimensions,
  hasAudio,
  sameSource,
  validateClip,
  qualityValue,
} from './model.ts';

/** 与 C# 的 "0.#########" 一致：最多 9 位小数、去掉尾随零、不用科学计数法。 */
export function n(value: number): string {
  if (!Number.isFinite(value)) throw new Error(`无法格式化的数值：${value}`);
  let text = value.toFixed(9);
  if (text.includes('.')) text = text.replace(/0+$/, '').replace(/\.$/, '');
  if (text === '-0') text = '0';
  return text;
}

/** 与桌面端一致：每级速度保持在 0.5–2，避免音频跳采样。 */
export function buildTempoFilter(speed: number): string {
  if (!Number.isFinite(speed) || speed < 0.1 || speed > 8) {
    throw new Error('片段速度必须在 0.1–8 倍之间。');
  }
  const filters: string[] = [];
  let remaining = speed;
  while (remaining < 0.5) {
    filters.push('atempo=0.5');
    remaining /= 0.5;
  }
  while (remaining > 2) {
    filters.push('atempo=2');
    remaining /= 2;
  }
  filters.push(`atempo=${n(remaining)}`);
  return filters.join(',');
}

const distinctSources = (clips: readonly VideoClip[]): MediaInfo[] => {
  const seen = new Set<string>();
  const result: MediaInfo[] = [];
  for (const clip of clips) {
    const key = clip.media.path.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    result.push(clip.media);
  }
  return result;
};

const indexesOf = (clips: readonly VideoClip[], source: MediaInfo): number[] =>
  clips.map((clip, index) => (sameSource(clip.media, source) ? index : -1)).filter((i) => i >= 0);

/** 静音轨，用于给无声素材补静音，保持 concat 的音频段数一致。 */
const silenceChain = (clip: VideoClip): string =>
  `anullsrc=r=48000:cl=stereo,atrim=duration=${n(clipDuration(clip))},asetpts=PTS-STARTPTS`;

const audioClipChain = (clip: VideoClip): string =>
  `atrim=start=${n(clip.start)}:end=${n(clip.end)},asetpts=PTS-STARTPTS,` +
  `${buildTempoFilter(clip.speed)},apad,atrim=duration=${n(clipDuration(clip))}`;

const videoClipChain = (clip: VideoClip, width: number, height: number, fps: number): string =>
  `trim=start=${n(clip.start)}:end=${n(clip.end)},settb=AVTB,setpts=(PTS-STARTPTS)/${n(clip.speed)},` +
  `scale=w='trunc(ih*dar/2)*2':h=ih,setsar=1,` +
  `scale=${width}:${height}:force_original_aspect_ratio=decrease:force_divisible_by=2,` +
  `pad=${width}:${height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p,` +
  `fps=${n(fps)}:eof_action=pass,` +
  `tpad=stop_mode=clone:stop_duration=${n(Math.max(1 / (clip.media.frameRate * clip.speed), 2 / fps))},` +
  `trim=duration=${n(Math.max(clipDuration(clip), 1 / fps))},settb=AVTB`;

function validate(clips: readonly VideoClip[], options: ExportOptions): void {
  if (clips.length === 0) throw new Error('所选轨道为空，请选择包含片段的轨道。');
  for (const clip of clips) {
    validateClip(clip);
    if (clip.media.isHdr) {
      throw new Error('HDR 转 SDR 需要色调映射，请先将素材转换为 SDR。');
    }
  }
  getDimensions(options, clips[0]!.media);
}

/**
 * 桌面端的完整 filter_complex：为每个源建立输入标签，逐片段裁剪/变速/缩放/补帧，
 * 补静音后 concat。兜底路线直接使用它。
 */
export interface FilterBuildOverrides {
  readonly outputFrameRate?: number;
  readonly forceAudio?: boolean;
}

export function buildFilter(
  clips: readonly VideoClip[],
  options: ExportOptions,
  overrides: FilterBuildOverrides = {},
): string {
  validate(clips, options);
  const { width, height } = getDimensions(options, clips[0]!.media);
  const sources = distinctSources(clips);
  const anyAudio = overrides.forceAudio === true || clips.some((clip) => hasAudio(clip.media));
  const fps = overrides.outputFrameRate ?? clips[0]!.media.frameRate;
  const lines: string[] = [];

  sources.forEach((source, input) => {
    const indexes = indexesOf(clips, source);
    lines.push(
      `[${input}:${source.videoStreamIndex}]setpts=PTS-STARTPTS,split=${indexes.length}` +
        indexes.map((i) => `[vs${i}]`).join('') +
        ';',
    );
    if (!hasAudio(source)) return;
    lines.push(
      `[${input}:${source.audioStreamIndex}]asetpts=PTS-(${n(source.videoTimestampOffset)})/TB,` +
        `aresample=48000:async=1:first_pts=0,` +
        `aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,apad,` +
        `asplit=${indexes.length}` +
        indexes.map((i) => `[as${i}]`).join('') +
        ';',
    );
  });

  clips.forEach((clip, i) => {
    lines.push(`[vs${i}]${videoClipChain(clip, width, height, fps)}[v${i}];`);
    if (hasAudio(clip.media)) {
      lines.push(`[as${i}]${audioClipChain(clip)}[a${i}];`);
    } else if (anyAudio) {
      lines.push(`${silenceChain(clip)}[a${i}];`);
    }
  });

  const inputs = clips.map((_, i) => `[v${i}]${anyAudio ? `[a${i}]` : ''}`).join('');
  lines.push(
    `${inputs}concat=n=${clips.length}:v=1:a=${anyAudio ? 1 : 0}[video]` +
      (anyAudio ? '[audio]' : ''),
  );
  return lines.join('\n');
}

/**
 * 仅生成音频的半边 filter graph，输出 [audio]。
 *
 * 用于快速路线：输入 0 是 WebCodecs 编好的无声 video.mp4，
 * 其后依次是各素材的音频源，因此源下标从 1 开始。
 * 音频段与 buildFilter 完全一致，保证两条路线声音相同。
 */
export function buildAudioFilter(clips: readonly VideoClip[]): string {
  if (clips.length === 0) throw new Error('所选轨道为空。');
  for (const clip of clips) validateClip(clip);
  const sources = distinctSources(clips).filter((source) => hasAudio(source));
  const lines: string[] = [];

  sources.forEach((source, index) => {
    const input = index + 1; // 0 号输入是 video.mp4
    const indexes = indexesOf(clips, source);
    lines.push(
      `[${input}:${source.audioStreamIndex}]asetpts=PTS-(${n(source.videoTimestampOffset)})/TB,` +
        `aresample=48000:async=1:first_pts=0,` +
        `aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,apad,` +
        `asplit=${indexes.length}` +
        indexes.map((i) => `[as${i}]`).join('') +
        ';',
    );
  });

  clips.forEach((clip, i) => {
    if (hasAudio(clip.media)) lines.push(`[as${i}]${audioClipChain(clip)}[a${i}];`);
    else lines.push(`${silenceChain(clip)}[a${i}];`);
  });

  lines.push(
    `${clips.map((_, i) => `[a${i}]`).join('')}concat=n=${clips.length}:v=0:a=1[audio]`,
  );
  return lines.join('\n');
}

/** 兜底路线：与桌面端 BuildArguments 相同，但强制软件编码且不带 NVDEC。 */
export function buildWasmExportArguments(
  clips: readonly VideoClip[],
  options: ExportOptions,
  filterFile: string,
  output: string,
  forceAudio = false,
): string[] {
  const sources = distinctSources(clips);
  const anyAudio = forceAudio || clips.some((clip) => hasAudio(clip.media));
  const args = [
    '-hide_banner', '-nostdin', '-y', '-loglevel', 'warning',
    '-filter_complex_threads', '1', '-copyts', '-start_at_zero',
  ];
  for (const media of sources) args.push('-i', media.path);
  args.push('-filter_complex_script', filterFile, '-map', '[video]');
  if (anyAudio) args.push('-map', '[audio]', '-c:a', 'aac', '-b:a', '192k');
  else args.push('-an');
  if (options.encoder === VideoEncoder.Nvidia) {
    args.push('-c:v', 'h264_nvenc', '-preset', 'p5', '-tune', 'hq', '-rc', 'vbr',
      '-cq', String(qualityValue(options.quality)), '-b:v', '0');
  } else {
    args.push('-c:v', 'libx264', '-preset', 'medium',
      '-crf', String(qualityValue(options.quality)), '-threads', '2');
  }
  args.push('-fps_mode', 'vfr', '-map_metadata', '-1', '-metadata:s:v:0', 'rotate=0',
    '-movflags', '+faststart', output);
  return args;
}

/**
 * WebCodecs AAC 不可用时的混合路线：把 WebCodecs 产出的无声 video.mp4
 * 与按音频图渲染出的 AAC 音轨混流。视频流直接 copy，不做二次编码。
 */
export function buildAudioMuxArguments(
  clips: readonly VideoClip[],
  videoInput: string,
  filterFile: string,
  output: string,
): string[] {
  const sources = distinctSources(clips).filter((source) => hasAudio(source));
  const args = [
    '-hide_banner', '-nostdin', '-y', '-loglevel', 'warning', '-copyts', '-start_at_zero',
    '-i', videoInput,
  ];
  for (const media of sources) args.push('-i', media.path);
  args.push(
    '-filter_complex_script', filterFile,
    '-map', '0:v', '-map', '[audio]',
    '-c:v', 'copy', '-c:a', 'aac', '-b:a', '192k',
    '-map_metadata', '-1', '-movflags', '+faststart',
    output,
  );
  return args;
}
