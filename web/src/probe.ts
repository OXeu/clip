/**
 * 浏览器端素材探测，替代桌面端的 ffprobe。
 *
 * 桌面端用 `ffprobe -show_streams -show_format` 得到 MediaInfo。
 * 这里用 mp4box.js 解析容器，产出结构完全相同的 MediaInfo，
 * 因此校验、时间轴、导出逻辑不需要任何改动。
 */

import { createFile, type MP4ArrayBuffer, type MP4Info } from 'mp4box';

import type { MediaInfo } from './model.ts';

export interface ProbeResult {
  readonly media: MediaInfo;
  /** 供界面提示的非致命问题。 */
  readonly warnings: readonly string[];
}

/** 取第一个非封面视频轨。桌面端会跳过 attached_pic（封面图）。 */
function pickVideoTrack(info: MP4Info): MP4Info['videoTracks'][number] | null {
  return (
    info.videoTracks.find((track) => {
      // mp4box 会把封面图放在视频轨里，用样本数与尺寸可以粗略排除极小的图。
      const width = track.video?.width ?? track.track_width ?? 0;
      const height = track.video?.height ?? track.track_height ?? 0;
      return width >= 2 && height >= 2;
    }) ?? null
  );
}

/**
 * 从 stsd 的 colr 盒判断 HDR。
 * transfer_characteristics 16 = PQ (SMPTE ST 2084)，18 = HLG (ARIB STD-B67)，
 * 与桌面端 ParseProbe 的判断保持一致。
 */
function isHdrTrack(
  file: ReturnType<typeof createFile>,
  trackId: number,
): boolean {
  const trak = file.getTrackById(trackId);
  const entries = trak?.mdia.minf.stbl.stsd.entries;
  if (!entries) return false;
  for (const entry of entries) {
    // mp4box 把 colr 解析为普通属性；只读取存在的字段。
    const colr = (entry as unknown as {
      colr?: { colour_type?: string; transfer_characteristics?: number };
    }).colr;
    if (!colr) continue;
    const transfer = colr.transfer_characteristics;
    if (transfer === 16 || transfer === 18) return true;
  }
  return false;
}

/**
 * 解析一个视频文件。
 * @param file 用户选择的文件
 * @param data 文件字节（由调用方负责读取，便于复用与取消）
 */
export async function probeFile(
  name: string,
  data: ArrayBuffer,
): Promise<ProbeResult> {
  const file = createFile(true);
  let info: MP4Info | null = null;
  let failure: string | null = null;
  let sawMoov = false;

  file.onError = (module: string, error: string) => {
    failure = `${module}: ${error}`;
  };
  file.onReady = (ready: MP4Info) => {
    info = ready;
    sawMoov = true;
  };

  const CHUNK = 8 * 1024 * 1024;
  for (let offset = 0; offset < data.byteLength; offset += CHUNK) {
    const slice = data.slice(offset, Math.min(offset + CHUNK, data.byteLength));
    const buffer = slice as MP4ArrayBuffer;
    buffer.fileStart = offset;
    file.appendBuffer(buffer);
    // 只要 moov 已解析完成就可以停止读入，避免为大文件做无谓扫描。
    if (sawMoov) break;
  }
  file.flush();

  if (failure) throw new Error(`无法解析 ${name}：${failure}`);
  if (!info) throw new Error(`无法解析 ${name}：未找到 moov 信息，可能不是受支持的 MP4/MOV 文件。`);

  const resolved: MP4Info = info;
  const video = pickVideoTrack(resolved);
  if (!video) throw new Error(`${name} 中没有可剪辑的视频轨道。`);

  const warnings: string[] = [];
  const audio = resolved.audioTracks[0] ?? null;

  const duration =
    video.movie_duration !== undefined && resolved.timescale > 0
      ? video.movie_duration / resolved.timescale
      : resolved.timescale > 0
        ? resolved.duration / resolved.timescale
        : 0;
  const trackDurationSeconds = video.timescale > 0 ? video.duration / video.timescale : 0;
  const effectiveDuration = duration > 0 ? duration : trackDurationSeconds;

  if (!Number.isFinite(effectiveDuration) || effectiveDuration <= 0) {
    throw new Error(`${name} 的时长无效；暂不支持直播流与静态图片。`);
  }

  const rawWidth = video.video?.width ?? video.track_width ?? 0;
  const rawHeight = video.video?.height ?? video.track_height ?? 0;
  if (rawWidth < 1 || rawHeight < 1) throw new Error(`${name} 的视频尺寸无效。`);

  // 平均帧率：样本数 / 样本总时长。QuickTime 的 edit list 可能修改轨道展示
  // 时长；若拿它算帧率，会把 30 fps 之类的素材误算成 32.108... fps。
  const sampleDurationSeconds =
    video.samples_duration !== undefined && video.samples_duration > 0 && video.timescale > 0
      ? video.samples_duration / video.timescale
      : trackDurationSeconds;
  const frameRate = sampleDurationSeconds > 0
    ? video.nb_samples / sampleDurationSeconds
    : 30;
  if (!Number.isFinite(frameRate) || frameRate <= 0) {
    warnings.push(`${name} 未提供帧率，已按 30 fps 处理。`);
  }

  const hdr = isHdrTrack(file, video.id);

  return {
    media: {
      path: name,
      duration: effectiveDuration,
      width: rawWidth,
      height: rawHeight,
      frameRate: Number.isFinite(frameRate) && frameRate > 0 ? frameRate : 30,
      videoStreamIndex: 0,
      // 桌面端记录的是全局流下标；浏览器端 filter 不使用它，这里用 0/1 占位。
      audioStreamIndex: audio ? 1 : null,
      codec: video.codec,
      videoTimestampOffset: 0,
      isHdr: hdr,
    },
    warnings,
  };
}
