import { clipDuration, type VideoTrack } from './model.ts';

const TENCENT_SYNC_ASR = 'https://tokenhub.tencentmaas.com/v1/wand/asrproxy/sync_transcribe';
const MODEL = 'hy-asr-3.0-preview';

export interface VoiceSegment {
  readonly start: number;
  readonly end: number;
}

export interface RecognizedSubtitle {
  readonly start: number;
  readonly end: number;
  readonly text: string;
}

interface TencentAsrResponse {
  readonly status?: string;
  readonly output?: {
    readonly text?: string;
  };
  readonly message?: string;
  readonly error?: { readonly message?: string };
}

const monoSample = (buffer: AudioBuffer, index: number): number => {
  let value = 0;
  for (let channel = 0; channel < buffer.numberOfChannels; channel++) {
    value += buffer.getChannelData(channel)[index] ?? 0;
  }
  return value / Math.max(1, buffer.numberOfChannels);
};

/**
 * 浏览器端语音活动检测：RMS 过滤底噪，再用 70–400Hz 自相关确认人声基频。
 * 短停顿使用 hangover 合并，输出的一段天然对应一条待识别字幕。
 */
export function detectVoiceSegments(
  buffer: AudioBuffer,
  rangeStart = 0,
  rangeEnd = buffer.duration,
): readonly VoiceSegment[] {
  const sampleRate = buffer.sampleRate;
  const startSample = Math.max(0, Math.floor(rangeStart * sampleRate));
  const endSample = Math.min(buffer.length, Math.ceil(rangeEnd * sampleRate));
  const frameSize = Math.max(128, Math.round(sampleRate * 0.03));
  const minLag = Math.max(2, Math.floor(sampleRate / 400));
  const maxLag = Math.min(frameSize - 2, Math.ceil(sampleRate / 70));
  const frames: { time: number; rms: number; pitched: boolean }[] = [];

  for (let offset = startSample; offset + frameSize <= endSample; offset += frameSize) {
    const samples = new Float32Array(frameSize);
    let energy = 0;
    let zeroCrossings = 0;
    let previous = monoSample(buffer, offset);
    for (let index = 0; index < frameSize; index++) {
      const value = monoSample(buffer, offset + index);
      samples[index] = value;
      energy += value * value;
      if ((value >= 0) !== (previous >= 0)) zeroCrossings++;
      previous = value;
    }
    const rms = Math.sqrt(energy / frameSize);
    let bestCorrelation = 0;
    if (rms > 0.002) {
      for (let lag = minLag; lag <= maxLag; lag += 2) {
        let correlation = 0;
        let leftEnergy = 0;
        let rightEnergy = 0;
        for (let index = 0; index < frameSize - lag; index += 4) {
          const left = samples[index]!;
          const right = samples[index + lag]!;
          correlation += left * right;
          leftEnergy += left * left;
          rightEnergy += right * right;
        }
        const normalized = correlation / Math.sqrt(leftEnergy * rightEnergy + 1e-12);
        if (normalized > bestCorrelation) bestCorrelation = normalized;
      }
    }
    const crossingRate = zeroCrossings / frameSize;
    frames.push({
      time: offset / sampleRate,
      rms,
      pitched: bestCorrelation >= 0.34 || (bestCorrelation >= 0.2 && crossingRate >= 0.015 && crossingRate <= 0.24),
    });
  }

  if (frames.length === 0) return [];
  const sortedRms = frames.map((frame) => frame.rms).sort((a, b) => a - b);
  const noiseFloor = sortedRms[Math.floor(sortedRms.length * 0.2)] ?? 0;
  const threshold = Math.max(0.006, noiseFloor * 2.6);
  const voiced = frames.map((frame) => frame.pitched && frame.rms >= threshold);
  // 允许最长约 240ms 的字间停顿。
  const hangoverFrames = Math.max(1, Math.round(0.24 / 0.03));
  for (let index = 0; index < voiced.length; index++) {
    if (voiced[index]) continue;
    let gapEnd = index;
    while (gapEnd < voiced.length && !voiced[gapEnd]) gapEnd++;
    if (index > 0 && gapEnd < voiced.length && gapEnd - index <= hangoverFrames) {
      for (let fill = index; fill < gapEnd; fill++) voiced[fill] = true;
    }
    index = gapEnd;
  }

  const segments: VoiceSegment[] = [];
  let voiceStart = -1;
  for (let index = 0; index <= voiced.length; index++) {
    if (index < voiced.length && voiced[index]) {
      if (voiceStart < 0) voiceStart = frames[index]!.time;
      continue;
    }
    if (voiceStart < 0) continue;
    const end = Math.min(rangeEnd, (frames[Math.max(0, index - 1)]?.time ?? rangeEnd) + frameSize / sampleRate);
    const start = Math.max(rangeStart, voiceStart - 0.08);
    const paddedEnd = Math.min(end + 0.12, rangeEnd);
    if (paddedEnd - start >= 0.3) {
      // 连续讲话也要限制单次上传大小，避免生成无法阅读的超长字幕。
      const partCount = Math.ceil((paddedEnd - start) / 15);
      const partDuration = (paddedEnd - start) / partCount;
      for (let part = 0; part < partCount; part++) {
        segments.push({
          start: start + part * partDuration,
          end: part === partCount - 1 ? paddedEnd : start + (part + 1) * partDuration,
        });
      }
    }
    voiceStart = -1;
  }
  return segments;
}

function encodeSegmentAsWav(buffer: AudioBuffer, segment: VoiceSegment, outputRate = 16_000): Uint8Array {
  const count = Math.max(1, Math.ceil((segment.end - segment.start) * outputRate));
  const bytes = new Uint8Array(44 + count * 2);
  const view = new DataView(bytes.buffer);
  const text = (offset: number, value: string): void => {
    for (let index = 0; index < value.length; index++) view.setUint8(offset + index, value.charCodeAt(index));
  };
  text(0, 'RIFF');
  view.setUint32(4, 36 + count * 2, true);
  text(8, 'WAVE');
  text(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, outputRate, true);
  view.setUint32(28, outputRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  text(36, 'data');
  view.setUint32(40, count * 2, true);
  for (let index = 0; index < count; index++) {
    const source = (segment.start + index / outputRate) * buffer.sampleRate;
    const left = Math.min(buffer.length - 1, Math.max(0, Math.floor(source)));
    const right = Math.min(buffer.length - 1, left + 1);
    const fraction = source - left;
    const sample = monoSample(buffer, left) * (1 - fraction) + monoSample(buffer, right) * fraction;
    view.setInt16(44 + index * 2, Math.round(Math.max(-1, Math.min(1, sample)) * 32767), true);
  }
  return bytes;
}

function base64(bytes: Uint8Array): string {
  let result = '';
  const chunk = 0x8000;
  for (let index = 0; index < bytes.length; index += chunk) {
    result += String.fromCharCode(...bytes.subarray(index, index + chunk));
  }
  return btoa(result);
}

async function recognizeSegment(bytes: Uint8Array, apiKey: string, signal?: AbortSignal): Promise<string> {
  const response = await fetch(TENCENT_SYNC_ASR, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${apiKey}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ model: MODEL, data: base64(bytes), voice_encode_format: 'wav' }),
    ...(signal ? { signal } : {}),
  });
  let payload: TencentAsrResponse;
  try {
    payload = await response.json() as TencentAsrResponse;
  } catch {
    throw new Error(`腾讯云 ASR 返回了无法解析的响应（HTTP ${response.status}）。`);
  }
  const text = payload.output?.text?.trim();
  if (!response.ok || payload.status && payload.status !== 'completed' || !text) {
    throw new Error(payload.error?.message ?? payload.message ?? `腾讯云 ASR 识别失败（HTTP ${response.status}）。`);
  }
  return text;
}

export async function recognizeAudioTrack(
  track: VideoTrack,
  readSource: (path: string) => Promise<ArrayBuffer>,
  apiKey: string,
  report: (completed: number, total: number, message: string) => void,
  signal?: AbortSignal,
): Promise<readonly RecognizedSubtitle[]> {
  if (!apiKey.trim()) throw new Error('请先在设置中填写腾讯云 ASR API Key。');
  const audioContext = new AudioContext();
  const work: {
    buffer: AudioBuffer;
    segment: VoiceSegment;
    timelineStart: number;
    clipStart: number;
    speed: number;
  }[] = [];
  let clipOffset = 0;
  try {
    for (const clip of track.clips) {
      if (signal?.aborted) throw new DOMException('操作已取消。', 'AbortError');
      const buffer = await audioContext.decodeAudioData((await readSource(clip.media.path)).slice(0));
      const segments = detectVoiceSegments(buffer, clip.start, clip.end);
      for (const segment of segments) {
        work.push({ buffer, segment, timelineStart: clipOffset, clipStart: clip.start, speed: clip.speed });
      }
      clipOffset += clipDuration(clip);
    }
    const result: RecognizedSubtitle[] = [];
    for (let index = 0; index < work.length; index++) {
      if (signal?.aborted) throw new DOMException('操作已取消。', 'AbortError');
      const item = work[index]!;
      report(index, work.length, `正在识别第 ${index + 1}/${work.length} 段语音…`);
      const text = await recognizeSegment(encodeSegmentAsWav(item.buffer, item.segment), apiKey.trim(), signal);
      result.push({
        start: item.timelineStart + (item.segment.start - item.clipStart) / item.speed,
        end: item.timelineStart + (item.segment.end - item.clipStart) / item.speed,
        text,
      });
    }
    report(work.length, work.length, work.length === 0 ? '未检测到清晰人声。' : '字幕识别完成。');
    return result;
  } finally {
    void audioContext.close();
  }
}
