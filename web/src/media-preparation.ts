/**
 * 把浏览器原生播放/现有 MP4Box 管线不能直接消费的 Matroska 素材准备成 MP4。
 * 优先复制已编码数据；只有目标容器不能接收源编码时，Mediabunny 才会尝试
 * 使用浏览器 WebCodecs 转码。整个过程都在本机浏览器内完成。
 */

import type { Conversion as MediaConversion } from 'mediabunny';

export interface PreparedMediaFile {
  readonly file: File;
  readonly data: ArrayBuffer;
  readonly converted: boolean;
  /** 转换前按帧时间格估算的帧率，避免 Matroska 毫秒时间基带来平均值漂移。 */
  readonly frameRate?: number;
}

export type MediaPreparationProgress = (fraction: number, message: string) => void;

/** EBML 文件头；MKV 与 WebM 都属于 Matroska/EBML 家族。 */
export function hasEbmlHeader(data: ArrayBuffer): boolean {
  if (data.byteLength < 4) return false;
  const bytes = new Uint8Array(data, 0, 4);
  return bytes[0] === 0x1a && bytes[1] === 0x45 && bytes[2] === 0xdf && bytes[3] === 0xa3;
}

const hasMatroskaExtension = (name: string): boolean => /\.(?:mkv|webm)$/i.test(name);

async function discardedTrackSummary(conversion: MediaConversion): Promise<string> {
  const details = await Promise.all(conversion.discardedTracks.map(async ({ track, reason }) => {
    const codec = await track.getCodec();
    return `${track.type}/${codec ?? '未知编码'}（${reason}）`;
  }));
  return details.join('、');
}

/**
 * 普通 MP4/MOV 原样返回；MKV/WebM 则转换容器后返回同名、MIME 为 video/mp4 的 File。
 */
export async function prepareMediaFile(
  file: File,
  data: ArrayBuffer,
  onProgress?: MediaPreparationProgress,
  signal?: AbortSignal,
): Promise<PreparedMediaFile> {
  const matroska = hasEbmlHeader(data);
  if (!matroska && !hasMatroskaExtension(file.name)) {
    return { file, data, converted: false };
  }
  if (!matroska) {
    throw new Error('文件扩展名是 MKV/WebM，但内容不是有效的 Matroska 容器。');
  }
  if (signal?.aborted) throw new DOMException('已取消导入', 'AbortError');

  // 普通 MP4/MOV 用户不需要下载 Matroska 转换代码；仅在实际导入时按需加载。
  const {
    BlobSource,
    BufferTarget,
    Conversion,
    Input,
    MATROSKA,
    Mp4OutputFormat,
    Output,
    WEBM,
  } = await import('mediabunny');

  const input = new Input({
    source: new BlobSource(new Blob([data], { type: file.type || 'video/x-matroska' })),
    formats: [MATROSKA, WEBM],
  });
  let conversion: MediaConversion | null = null;
  const onAbort = (): void => {
    if (conversion) void conversion.cancel();
  };
  signal?.addEventListener('abort', onAbort, { once: true });

  try {
    onProgress?.(0.01, '正在读取 MKV/WebM 轨道…');
    const [videoTrack, audioTrack] = await Promise.all([
      input.getPrimaryVideoTrack(),
      input.getPrimaryAudioTrack(),
    ]);
    if (!videoTrack) throw new Error('MKV/WebM 中没有可剪辑的视频轨道。');
    const frameRateMetrics = await videoTrack.computeFrameRateMetrics();
    const frameRate = frameRateMetrics.bestGuessFrameRate;

    const target = new BufferTarget();
    const output = new Output({
      format: new Mp4OutputFormat({ fastStart: 'in-memory' }),
      target,
    });
    conversion = await Conversion.init({
      input,
      output,
      tracks: 'primary',
      copy: { mode: 'preferred', shiftTolerance: Infinity },
      tags: {},
      showWarnings: false,
    });

    const requiredTracks = audioTrack ? [videoTrack, audioTrack] : [videoTrack];
    const missingRequiredTrack = requiredTracks.some(
      (track) => !conversion!.utilizedTracks.includes(track),
    );
    if (!conversion.isValid || missingRequiredTrack) {
      const detail = await discardedTrackSummary(conversion);
      throw new Error(
        `MKV/WebM 的主要音视频轨道无法转换为 MP4${detail ? `：${detail}` : '。'}`,
      );
    }

    conversion.onProgress = (fraction) => {
      onProgress?.(Math.max(0.01, Math.min(0.99, fraction)), '正在将 MKV/WebM 准备为 MP4…');
    };
    if (signal?.aborted) throw new DOMException('已取消导入', 'AbortError');
    await conversion.execute();
    if (signal?.aborted) throw new DOMException('已取消导入', 'AbortError');

    const buffer = target.buffer;
    if (!buffer || buffer.byteLength === 0) throw new Error('MKV/WebM 转换后没有产生有效数据。');
    const prepared = new File([buffer], file.name, {
      type: 'video/mp4',
      lastModified: file.lastModified,
    });
    onProgress?.(1, 'MKV/WebM 准备完成');
    return {
      file: prepared,
      data: buffer,
      converted: true,
      ...(Number.isFinite(frameRate) && frameRate > 0 ? { frameRate } : {}),
    };
  } catch (error) {
    if (signal?.aborted) throw new DOMException('已取消导入', 'AbortError');
    throw new Error(
      `无法准备 MKV/WebM 素材：${error instanceof Error ? error.message : String(error)}`,
    );
  } finally {
    signal?.removeEventListener('abort', onAbort);
    input.dispose();
  }
}
