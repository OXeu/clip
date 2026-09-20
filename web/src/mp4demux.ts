/**
 * MP4 解封装与编解码器配置提取。
 *
 * 用途有二：
 * 1. 探测素材元数据，替代桌面端的 ffprobe。
 * 2. 为 WebCodecs 的 VideoDecoder / AudioDecoder 提供 description（avcC/esds）。
 */

import {
  DataStream,
  createFile,
  type MP4ArrayBuffer,
  type MP4Descriptor,
  type MP4Info,
  type MP4Sample,
} from 'mp4box';

export interface DemuxedTrack {
  readonly id: number;
  readonly codec: string;
  readonly timescale: number;
  readonly width: number;
  readonly height: number;
  readonly sampleRate: number;
  readonly channels: number;
  /** 容器 edit list 中，展示时间 0 对应的媒体时间（秒）。 */
  readonly mediaTimeOffset: number;
  readonly description?: Uint8Array;
}

export interface DemuxedFile {
  readonly info: MP4Info;
  readonly video: DemuxedTrack | null;
  readonly audio: DemuxedTrack | null;
  readonly samples: Map<number, MP4Sample[]>;
}

interface EditListEntry {
  readonly segment_duration: number;
  readonly media_time: number;
  readonly media_rate_integer: number;
  readonly media_rate_fraction: number;
}

/** 取第一个非封面视频轨、第一个音频轨，与桌面端 ParseProbe 的取法一致。 */
function pickTracks(info: MP4Info): { video: MP4Info['videoTracks'][number] | null; audio: MP4Info['audioTracks'][number] | null } {
  const video = info.videoTracks.find((track) => track.type === 'video') ?? null;
  const audio = info.audioTracks[0] ?? null;
  return { video, audio };
}

/** 在 MPEG-4 descriptor 树中寻找 tag=5 的 DecoderSpecificInfo（AAC ASC）。 */
export function findAudioSpecificConfig(
  descriptor: MP4Descriptor | undefined,
): Uint8Array | undefined {
  if (!descriptor) return undefined;
  if (descriptor.tag === 5 && descriptor.data?.byteLength) {
    return new Uint8Array(descriptor.data);
  }
  for (const child of descriptor.descs ?? []) {
    const found = findAudioSpecificConfig(child);
    if (found) return found;
  }
  return undefined;
}

/** 把 edit list 映射换算成“展示时间 + 此偏移 = 原始媒体时间”。 */
export function editMediaTimeOffset(
  entries: readonly EditListEntry[] | undefined,
  mediaTimescale: number,
  movieTimescale: number,
): number {
  if (!entries || mediaTimescale <= 0 || movieTimescale <= 0) return 0;
  let presentationStart = 0;
  for (const entry of entries) {
    if (entry.media_time < 0) {
      presentationStart += entry.segment_duration / movieTimescale;
      continue;
    }
    // 非 1.0 播放率极少见，也不属于当前编辑器支持的输入语义。
    if (entry.media_rate_integer !== 1 || entry.media_rate_fraction !== 0) return 0;
    return entry.media_time / mediaTimescale - presentationStart;
  }
  return 0;
}

/** 从 stsd 中取出 avcC / hvcC 等配置盒并去掉盒头，得到 WebCodecs 需要的 description。 */
function extractDescription(
  file: ReturnType<typeof createFile>,
  trackId: number,
): Uint8Array | undefined {
  const trak = file.getTrackById(trackId);
  if (!trak) return undefined;
  for (const entry of trak.mdia.minf.stbl.stsd.entries) {
    // AAC 的 WebCodecs description 需要 esds 中 tag=5 的 AudioSpecificConfig，
    // 不能把完整 esds 盒（或 descriptor header）直接交给 AudioDecoder。
    const audioSpecificConfig = findAudioSpecificConfig(entry.esds?.esd);
    if (audioSpecificConfig) return audioSpecificConfig;

    const box = entry.avcC ?? entry.hvcC ?? entry.vpcC ?? entry.av1C;
    if (!box) continue;
    // DataStream 默认为 ISO BMFF 使用的大端序；不依赖旧版已移除的静态常量。
    const stream = new DataStream();
    box.write(stream);
    // 去掉 8 字节盒头，只保留配置内容。
    return new Uint8Array(stream.buffer.slice(8));
  }
  return undefined;
}

function toTrack(
  file: ReturnType<typeof createFile>,
  track: MP4Info['videoTracks'][number] | MP4Info['audioTracks'][number],
  kind: 'video' | 'audio',
  movieTimescale: number,
): DemuxedTrack {
  const description = extractDescription(file, track.id);
  const internalTrack = file.getTrackById(track.id);
  const mediaTimeOffset = editMediaTimeOffset(
    internalTrack?.edts?.elst?.entries,
    track.timescale,
    movieTimescale,
  );
  return {
    id: track.id,
    codec: track.codec,
    timescale: track.timescale,
    width: kind === 'video' ? (track.video?.width ?? 0) : 0,
    height: kind === 'video' ? (track.video?.height ?? 0) : 0,
    sampleRate: kind === 'audio' ? (track.audio?.sample_rate ?? 48000) : 0,
    channels: kind === 'audio' ? (track.audio?.channel_count ?? 2) : 0,
    mediaTimeOffset,
    ...(description ? { description } : {}),
  };
}

/**
 * 解析一个 MP4/MOV 文件的全部样本。
 *
 * 注意：为了支持按区间取样本，这里保留全部样本数据（keepMdatData）。长视频会
 * 占用较多内存，因此调用方应尽早释放（见 dispose）。
 */
export async function demuxFile(
  data: ArrayBuffer,
  onProgress?: (fraction: number) => void,
): Promise<DemuxedFile> {
  const file = createFile(true);
  const samples = new Map<number, MP4Sample[]>();
  let info: MP4Info | null = null;
  let failure: string | null = null;

  file.onError = (module: string, error: string) => {
    failure = `${module}: ${error}`;
  };
  file.onReady = (ready: MP4Info) => {
    info = ready;
    for (const track of [...ready.videoTracks, ...ready.audioTracks]) {
      file.setExtractionOptions(track.id, null, { nbSamples: 1000 });
    }
    file.start();
  };
  file.onSamples = (trackId: number, _user: unknown, batch: MP4Sample[]) => {
    const list = samples.get(trackId);
    if (list) list.push(...batch);
    else samples.set(trackId, [...batch]);
  };

  // 分块追加，避免一次性请求超大 buffer 的假死；MP4Box 依赖 fileStart 定位。
  const CHUNK = 8 * 1024 * 1024;
  for (let offset = 0; offset < data.byteLength; offset += CHUNK) {
    const slice = data.slice(offset, Math.min(offset + CHUNK, data.byteLength));
    const buffer = slice as MP4ArrayBuffer;
    buffer.fileStart = offset;
    file.appendBuffer(buffer);
    onProgress?.(Math.min(1, (offset + CHUNK) / data.byteLength));
  }
  file.flush();

  if (failure) throw new Error(`无法解析视频容器：${failure}`);
  if (!info) throw new Error('无法解析视频容器：未找到 moov 信息。');
  // 让 TS 收窄类型（回调赋值无法被控制流分析追踪）。
  const resolved: MP4Info = info;
  const { video, audio } = pickTracks(resolved);

  return {
    info: resolved,
    video: video ? toTrack(file, video, 'video', resolved.timescale) : null,
    audio: audio ? toTrack(file, audio, 'audio', resolved.timescale) : null,
    samples,
  };
}
