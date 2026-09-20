/**
 * WebCodecs 音频导出管线。
 *
 * MP4Box AAC 样本 → AudioDecoder → 48 kHz 立体声 PCM → WSOLA 变速
 * → AudioEncoder AAC/Opus → mp4-muxer。按片段处理，避免展开整条时间轴的 PCM。
 */

import type { ArrayBufferTarget, Muxer } from 'mp4-muxer';

import type { DemuxedFile } from './mp4demux.ts';
import { clipDuration, hasAudio, type MediaInfo, type VideoClip } from './model.ts';

export const WEB_AUDIO_SAMPLE_RATE = 48_000;
const WEB_AUDIO_CHANNELS = 2;
const AUDIO_BITRATE = 192_000;

export type WebCodecsAudioCodec = 'aac' | 'opus';

export class WebCodecsAudioUnavailableError extends Error {}

export interface StereoPcm {
  readonly left: Float32Array;
  readonly right: Float32Array;
}

export type AudioSourceProvider = (path: string) => Promise<DemuxedFile>;

const checkAborted = (signal?: AbortSignal): void => {
  if (signal?.aborted) throw new DOMException('已取消导出', 'AbortError');
};

const audioEncoderConfig = (codec: WebCodecsAudioCodec): AudioEncoderConfig => ({
  codec: codec === 'aac' ? 'mp4a.40.2' : 'opus',
  sampleRate: WEB_AUDIO_SAMPLE_RATE,
  numberOfChannels: WEB_AUDIO_CHANNELS,
  bitrate: AUDIO_BITRATE,
});

function audioDecoderConfig(source: DemuxedFile): AudioDecoderConfig {
  const track = source.audio;
  if (!track) throw new WebCodecsAudioUnavailableError('素材没有音频轨道。');
  if (track.codec.startsWith('mp4a') && !track.description?.byteLength) {
    throw new WebCodecsAudioUnavailableError('AAC 音轨缺少 AudioSpecificConfig。');
  }
  return {
    codec: track.codec,
    sampleRate: track.sampleRate,
    numberOfChannels: track.channels,
    ...(track.description ? { description: track.description } : {}),
  };
}

/** 在视频编码开始前完成能力复核，尽量避免编码到一半才回退。 */
export async function assertWebCodecsAudioSupported(
  clips: readonly VideoClip[],
  sourceFor: AudioSourceProvider,
  outputCodec: WebCodecsAudioCodec,
  signal?: AbortSignal,
): Promise<void> {
  if (typeof AudioDecoder === 'undefined' || typeof AudioEncoder === 'undefined') {
    throw new WebCodecsAudioUnavailableError('此浏览器没有完整的 WebCodecs 音频编解码器。');
  }
  let encoderSupport: AudioEncoderSupport;
  try {
    encoderSupport = await AudioEncoder.isConfigSupported(audioEncoderConfig(outputCodec));
  } catch (error) {
    throw new WebCodecsAudioUnavailableError(
      `无法验证 WebCodecs ${outputCodec === 'aac' ? 'AAC' : 'Opus'} 编码能力：${error instanceof Error ? error.message : String(error)}`,
    );
  }
  if (!encoderSupport.supported) {
    throw new WebCodecsAudioUnavailableError(
      `此浏览器不支持 WebCodecs ${outputCodec === 'aac' ? 'AAC' : 'Opus'} 编码。`,
    );
  }

  const seen = new Set<string>();
  for (const clip of clips) {
    if (!hasAudio(clip.media)) continue;
    const key = clip.media.path.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    checkAborted(signal);
    const source = await sourceFor(clip.media.path);
    let support: AudioDecoderSupport;
    try {
      support = await AudioDecoder.isConfigSupported(audioDecoderConfig(source));
    } catch (error) {
      if (error instanceof WebCodecsAudioUnavailableError) throw error;
      throw new WebCodecsAudioUnavailableError(
        `无法验证 ${clip.media.path} 的音频解码能力：${error instanceof Error ? error.message : String(error)}`,
      );
    }
    if (!support.supported) {
      throw new WebCodecsAudioUnavailableError(`浏览器不支持解码 ${clip.media.path} 的 ${source.audio?.codec ?? '音频'}。`);
    }
  }
}

interface DecodedAudioFrame {
  readonly timestamp: number;
  readonly sampleRate: number;
  readonly numberOfFrames: number;
  readonly channels: readonly Float32Array[];
}

async function waitForDecoderCapacity(decoder: AudioDecoder, signal?: AbortSignal): Promise<void> {
  while (decoder.decodeQueueSize > 24) {
    checkAborted(signal);
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }
}

async function decodeAudioInterval(
  source: DemuxedFile,
  start: number,
  end: number,
  signal?: AbortSignal,
): Promise<readonly DecodedAudioFrame[]> {
  const track = source.audio;
  if (!track) return [];
  const allSamples = source.samples.get(track.id) ?? [];
  // 多取约 120 ms，给 AAC 解码器与插值留出边界样本。
  const padding = 0.12;
  const samples = allSamples.filter((sample) => {
    const sampleStart = sample.cts / track.timescale;
    const sampleEnd = (sample.cts + sample.duration) / track.timescale;
    return sampleEnd >= start - padding && sampleStart <= end + padding;
  });
  if (samples.length === 0) {
    throw new WebCodecsAudioUnavailableError('音频片段没有可解码的样本。');
  }

  const frames: DecodedAudioFrame[] = [];
  let decoderError: Error | null = null;
  const decoder = new AudioDecoder({
    output: (data) => {
      try {
        const channels: Float32Array[] = [];
        for (let channel = 0; channel < data.numberOfChannels; channel++) {
          const samples = new Float32Array(data.numberOfFrames);
          data.copyTo(samples, { planeIndex: channel, format: 'f32-planar' });
          channels.push(samples);
        }
        frames.push({
          timestamp: data.timestamp,
          sampleRate: data.sampleRate,
          numberOfFrames: data.numberOfFrames,
          channels,
        });
      } catch (error) {
        decoderError = error instanceof Error ? error : new Error(String(error));
      } finally {
        data.close();
      }
    },
    error: (error) => {
      decoderError = error instanceof Error ? error : new Error(String(error));
    },
  });

  try {
    decoder.configure(audioDecoderConfig(source));
    for (const sample of samples) {
      checkAborted(signal);
      if (decoderError) throw decoderError;
      await waitForDecoderCapacity(decoder, signal);
      decoder.decode(new EncodedAudioChunk({
        type: 'key',
        timestamp: Math.round(sample.cts / track.timescale * 1_000_000),
        duration: Math.max(1, Math.round(sample.duration / track.timescale * 1_000_000)),
        data: sample.data,
      }));
    }
    await decoder.flush();
    if (decoderError) throw decoderError;
  } catch (error) {
    if (signal?.aborted) throw new DOMException('已取消导出', 'AbortError');
    if (error instanceof WebCodecsAudioUnavailableError) throw error;
    throw new WebCodecsAudioUnavailableError(
      `WebCodecs 音频解码失败：${error instanceof Error ? error.message : String(error)}`,
    );
  } finally {
    decoder.close();
  }
  frames.sort((left, right) => left.timestamp - right.timestamp);
  if (frames.length === 0) throw new WebCodecsAudioUnavailableError('音频解码器没有输出 PCM。');
  return frames;
}

function channelSample(frame: DecodedAudioFrame, index: number): readonly [number, number] {
  const channels = frame.channels;
  if (channels.length === 0) return [0, 0];
  const clamped = Math.min(Math.max(index, 0), frame.numberOfFrames - 1);
  if (channels.length === 1) {
    const mono = channels[0]![clamped] ?? 0;
    return [mono, mono];
  }
  let left = channels[0]![clamped] ?? 0;
  let right = channels[1]![clamped] ?? 0;
  // 多声道素材做保守下混；中心/环绕以 -6 dB 同时加入左右声道。
  for (let channel = 2; channel < channels.length; channel++) {
    const value = channels[channel]![clamped] ?? 0;
    left += value * 0.5;
    right += value * 0.5;
  }
  const normalization = 1 + Math.max(0, channels.length - 2) * 0.5;
  return [left / normalization, right / normalization];
}

/** 把解码帧按时间戳重采样为指定区间的 48 kHz 立体声 PCM。 */
function resampleInterval(
  frames: readonly DecodedAudioFrame[],
  media: MediaInfo,
  mediaTimeOffset: number,
  start: number,
  end: number,
): StereoPcm {
  const length = Math.max(1, Math.round((end - start) * WEB_AUDIO_SAMPLE_RATE));
  const left = new Float32Array(length);
  const right = new Float32Array(length);
  let frameIndex = 0;

  for (let output = 0; output < length; output++) {
    // FFmpeg 解封装器会自动应用 MP4 edit list；直接消费原始样本时需要显式
    // 加回 media_time，才能跳过 AAC 编码器预热并保持音画同步。
    const sourceTime = start + media.videoTimestampOffset
      + mediaTimeOffset + output / WEB_AUDIO_SAMPLE_RATE;
    while (
      frameIndex + 1 < frames.length
      && frames[frameIndex + 1]!.timestamp / 1_000_000 <= sourceTime
    ) frameIndex++;
    const frame = frames[frameIndex]!;
    const frameStart = frame.timestamp / 1_000_000;
    const exact = (sourceTime - frameStart) * frame.sampleRate;
    if (exact < -0.5 || exact >= frame.numberOfFrames + 0.5) continue;
    const firstIndex = Math.min(Math.max(Math.floor(exact), 0), frame.numberOfFrames - 1);
    const fraction = Math.min(Math.max(exact - firstIndex, 0), 1);
    const first = channelSample(frame, firstIndex);
    let second = first;
    if (firstIndex + 1 < frame.numberOfFrames) {
      second = channelSample(frame, firstIndex + 1);
    } else if (frameIndex + 1 < frames.length) {
      second = channelSample(frames[frameIndex + 1]!, 0);
    }
    left[output] = first[0] + (second[0] - first[0]) * fraction;
    right[output] = first[1] + (second[1] - first[1]) * fraction;
  }
  return { left, right };
}

function linearStretch(input: StereoPcm, outputLength: number): StereoPcm {
  const left = new Float32Array(outputLength);
  const right = new Float32Array(outputLength);
  const inputLength = input.left.length;
  for (let output = 0; output < outputLength; output++) {
    const exact = outputLength <= 1 ? 0 : output * (inputLength - 1) / (outputLength - 1);
    const index = Math.floor(exact);
    const fraction = exact - index;
    const next = Math.min(index + 1, inputLength - 1);
    left[output] = input.left[index]! + (input.left[next]! - input.left[index]!) * fraction;
    right[output] = input.right[index]! + (input.right[next]! - input.right[index]!) * fraction;
  }
  return { left, right };
}

/**
 * Waveform Similarity Overlap-Add：改变时长但尽量保持音高。
 * 片段极短（不足 64 个输出采样）时退回线性缩放，避免窗口算法无有效重叠区。
 */
export function timeStretchPcm(input: StereoPcm, speed: number, outputLength: number): StereoPcm {
  if (!Number.isFinite(speed) || speed <= 0) throw new RangeError('speed');
  if (outputLength <= 0) return { left: new Float32Array(), right: new Float32Array() };
  if (input.left.length !== input.right.length || input.left.length === 0) {
    return { left: new Float32Array(outputLength), right: new Float32Array(outputLength) };
  }
  if (Math.abs(speed - 1) < 0.000001) {
    const left = new Float32Array(outputLength);
    const right = new Float32Array(outputLength);
    left.set(input.left.subarray(0, outputLength));
    right.set(input.right.subarray(0, outputLength));
    return { left, right };
  }
  if (outputLength < 64 || input.left.length < 64) return linearStretch(input, outputLength);

  let windowSize = Math.min(2048, input.left.length, outputLength);
  windowSize -= windowSize % 2;
  const overlap = Math.max(16, Math.floor(windowSize / 2));
  const synthesisHop = Math.max(16, windowSize - overlap);
  const analysisHop = synthesisHop * speed;
  const searchRadius = Math.min(512, Math.max(32, Math.floor(windowSize / 4)));
  const maxInputStart = Math.max(0, input.left.length - windowSize);
  const left = new Float32Array(outputLength);
  const right = new Float32Array(outputLength);
  const initial = Math.min(windowSize, outputLength);
  left.set(input.left.subarray(0, initial));
  right.set(input.right.subarray(0, initial));

  let previousInput = 0;
  for (let outputStart = synthesisHop; outputStart < outputLength; outputStart += synthesisHop) {
    const expected = Math.min(maxInputStart, Math.max(0, Math.round(previousInput + analysisHop)));
    const minimum = Math.max(previousInput + 1, expected - searchRadius, 0);
    const maximum = Math.min(maxInputStart, expected + searchRadius);
    let best = Math.min(expected, maxInputStart);
    let bestScore = Number.NEGATIVE_INFINITY;

    for (let candidate = minimum; candidate <= maximum; candidate += 8) {
      let cross = 0;
      let existingPower = 0;
      let candidatePower = 0;
      const compare = Math.min(overlap, outputLength - outputStart);
      for (let offset = 0; offset < compare; offset += 8) {
        const existing = (left[outputStart + offset]! + right[outputStart + offset]!) * 0.5;
        const incoming = (input.left[candidate + offset]! + input.right[candidate + offset]!) * 0.5;
        cross += existing * incoming;
        existingPower += existing * existing;
        candidatePower += incoming * incoming;
      }
      const score = cross / Math.sqrt(Math.max(1e-12, existingPower * candidatePower));
      if (score > bestScore) {
        bestScore = score;
        best = candidate;
      }
    }

    const overlapCount = Math.min(overlap, outputLength - outputStart, input.left.length - best);
    for (let offset = 0; offset < overlapCount; offset++) {
      const mix = offset / Math.max(1, overlapCount);
      left[outputStart + offset] = left[outputStart + offset]! * (1 - mix) + input.left[best + offset]! * mix;
      right[outputStart + offset] = right[outputStart + offset]! * (1 - mix) + input.right[best + offset]! * mix;
    }
    const copyEnd = Math.min(windowSize, outputLength - outputStart, input.left.length - best);
    for (let offset = overlapCount; offset < copyEnd; offset++) {
      left[outputStart + offset] = input.left[best + offset]!;
      right[outputStart + offset] = input.right[best + offset]!;
    }
    previousInput = best;
  }
  return { left, right };
}

async function waitForEncoderCapacity(encoder: AudioEncoder, signal?: AbortSignal): Promise<void> {
  while (encoder.encodeQueueSize > 12) {
    checkAborted(signal);
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }
}

async function feedPcm(
  encoder: AudioEncoder,
  pcm: StereoPcm | null,
  frameCount: number,
  timelineStart: number,
  encoderFrameSize: number,
  signal?: AbortSignal,
): Promise<void> {
  for (let offset = 0; offset < frameCount; offset += encoderFrameSize) {
    checkAborted(signal);
    await waitForEncoderCapacity(encoder, signal);
    const count = Math.min(encoderFrameSize, frameCount - offset);
    const planar = new Float32Array(count * WEB_AUDIO_CHANNELS);
    if (pcm) {
      planar.set(pcm.left.subarray(offset, offset + count), 0);
      planar.set(pcm.right.subarray(offset, offset + count), count);
    }
    const data = new AudioData({
      format: 'f32-planar',
      sampleRate: WEB_AUDIO_SAMPLE_RATE,
      numberOfFrames: count,
      numberOfChannels: WEB_AUDIO_CHANNELS,
      timestamp: Math.round((timelineStart + offset) / WEB_AUDIO_SAMPLE_RATE * 1_000_000),
      data: planar,
    });
    encoder.encode(data);
    data.close();
  }
}

/** 逐片段渲染并编码 AAC/Opus，直接加入已有的 MP4 muxer。 */
export async function encodeWebCodecsAudio(
  clips: readonly VideoClip[],
  sourceFor: AudioSourceProvider,
  muxer: Muxer<ArrayBufferTarget>,
  outputCodec: WebCodecsAudioCodec,
  onProgress?: (fraction: number) => void,
  signal?: AbortSignal,
): Promise<void> {
  await assertWebCodecsAudioSupported(clips, sourceFor, outputCodec, signal);
  const totalFrames = clips.reduce(
    (total, clip) => total + Math.max(1, Math.round(clipDuration(clip) * WEB_AUDIO_SAMPLE_RATE)),
    0,
  );
  let encoderError: Error | null = null;
  const encoder = new AudioEncoder({
    output: (chunk, metadata) => {
      try {
        muxer.addAudioChunk(chunk, metadata);
      } catch (error) {
        encoderError = error instanceof Error ? error : new Error(String(error));
      }
    },
    error: (error) => {
      encoderError = error instanceof Error ? error : new Error(String(error));
    },
  });
  let timelineFrame = 0;

  try {
    encoder.configure(audioEncoderConfig(outputCodec));
    // AAC-LC 通常以 1024 帧为一包；Opus 默认 20 ms，即 48 kHz 下 960 帧。
    const encoderFrameSize = outputCodec === 'aac' ? 1024 : 960;
    for (const clip of clips) {
      checkAborted(signal);
      if (encoderError) throw encoderError;
      const outputFrames = Math.max(1, Math.round(clipDuration(clip) * WEB_AUDIO_SAMPLE_RATE));
      if (!hasAudio(clip.media)) {
        await feedPcm(encoder, null, outputFrames, timelineFrame, encoderFrameSize, signal);
      } else {
        const source = await sourceFor(clip.media.path);
        const mediaTimeOffset = source.audio?.mediaTimeOffset ?? 0;
        const decoded = await decodeAudioInterval(
          source,
          clip.start + clip.media.videoTimestampOffset + mediaTimeOffset,
          clip.end + clip.media.videoTimestampOffset + mediaTimeOffset,
          signal,
        );
        const normalSpeed = resampleInterval(
          decoded,
          clip.media,
          mediaTimeOffset,
          clip.start,
          clip.end,
        );
        const stretched = timeStretchPcm(normalSpeed, clip.speed, outputFrames);
        await feedPcm(encoder, stretched, outputFrames, timelineFrame, encoderFrameSize, signal);
      }
      timelineFrame += outputFrames;
      onProgress?.(Math.min(0.99, timelineFrame / totalFrames));
    }
    await encoder.flush();
    if (encoderError) throw encoderError;
    onProgress?.(1);
  } catch (error) {
    if (signal?.aborted) throw new DOMException('已取消导出', 'AbortError');
    if (error instanceof WebCodecsAudioUnavailableError) throw error;
    throw new WebCodecsAudioUnavailableError(
      `WebCodecs ${outputCodec === 'aac' ? 'AAC' : 'Opus'} 编码失败：${error instanceof Error ? error.message : String(error)}`,
    );
  } finally {
    if (encoder.state !== 'closed') encoder.close();
  }
}
