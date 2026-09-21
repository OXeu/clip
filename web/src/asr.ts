import { clipDuration, hasAudio, type VideoTrack } from './model.ts';

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
    readonly sentences?: readonly { readonly text?: string }[];
  };
  readonly message?: string;
  readonly request_id?: string;
  readonly error?: {
    readonly message?: string;
    readonly message_zh?: string;
    readonly code?: string;
    readonly request_id?: string;
  };
}

export type RecognitionProgressReporter = (fraction: number, message: string) => void;

const monoSample = (buffer: AudioBuffer, index: number): number => {
  let value = 0;
  for (let channel = 0; channel < buffer.numberOfChannels; channel++) {
    value += buffer.getChannelData(channel)[index] ?? 0;
  }
  return value / Math.max(1, buffer.numberOfChannels);
};

interface VoiceFrame {
  readonly time: number;
  readonly rms: number;
  readonly pitched: boolean;
  readonly crossingRate: number;
}

interface VoiceAnalysis {
  readonly startSample: number;
  readonly endSample: number;
  readonly sourceFrameSize: number;
  readonly analysisFrameSize: number;
  readonly analysisRate: number;
  readonly sourceStep: number;
  readonly minLag: number;
  readonly maxLag: number;
}

function voiceAnalysis(buffer: AudioBuffer, rangeStart: number, rangeEnd: number): VoiceAnalysis {
  // 语音基频分析无需保留 48 kHz。固定到约 8 kHz 后，自相关成本不再随源采样率暴涨。
  const analysisRate = Math.min(8_000, buffer.sampleRate);
  const sourceStep = buffer.sampleRate / analysisRate;
  const analysisFrameSize = Math.max(128, Math.round(analysisRate * 0.03));
  return {
    startSample: Math.max(0, Math.floor(rangeStart * buffer.sampleRate)),
    endSample: Math.min(buffer.length, Math.ceil(rangeEnd * buffer.sampleRate)),
    sourceFrameSize: analysisFrameSize * sourceStep,
    analysisFrameSize,
    analysisRate,
    sourceStep,
    minLag: Math.max(2, Math.floor(analysisRate / 400)),
    maxLag: Math.min(analysisFrameSize - 2, Math.ceil(analysisRate / 70)),
  };
}

function analyzeVoiceFrame(buffer: AudioBuffer, offset: number, analysis: VoiceAnalysis): VoiceFrame {
  const samples = new Float32Array(analysis.analysisFrameSize);
  let energy = 0;
  let zeroCrossings = 0;
  let previous = monoSample(buffer, Math.floor(offset));
  for (let index = 0; index < analysis.analysisFrameSize; index++) {
    const value = monoSample(buffer, Math.floor(offset + index * analysis.sourceStep));
    samples[index] = value;
    energy += value * value;
    if ((value >= 0) !== (previous >= 0)) zeroCrossings++;
    previous = value;
  }
  const rms = Math.sqrt(energy / analysis.analysisFrameSize);
  let bestCorrelation = 0;
  if (rms > 0.002) {
    for (let lag = analysis.minLag; lag <= analysis.maxLag; lag += 2) {
      let correlation = 0;
      let leftEnergy = 0;
      let rightEnergy = 0;
      for (let index = 0; index < analysis.analysisFrameSize - lag; index += 4) {
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
  const crossingRate = zeroCrossings / analysis.analysisFrameSize;
  return {
    time: offset / buffer.sampleRate,
    rms,
    pitched: bestCorrelation >= 0.34 || (bestCorrelation >= 0.2 && crossingRate >= 0.015 && crossingRate <= 0.24),
    crossingRate,
  };
}

function voiceSegmentsFromFrames(
  frames: readonly VoiceFrame[],
  rangeStart: number,
  rangeEnd: number,
  frameDuration: number,
): readonly VoiceSegment[] {
  if (frames.length === 0) return [];
  const sortedRms = frames.map((frame) => frame.rms).sort((a, b) => a - b);
  const noiseFloor = sortedRms[Math.floor(sortedRms.length * 0.2)] ?? 0;
  const upperRms = sortedRms[Math.floor(sortedRms.length * 0.9)] ?? 0;
  // 连续讲话可能完全没有静音基线。阈值必须受高分位能量约束，不能高于人声本身。
  const threshold = Math.max(0.003, Math.min(noiseFloor * 2.4, upperRms * 0.36));
  const voiced = frames.map((frame) => {
    if (frame.rms < threshold) return false;
    // 浊音优先采用基频；清辅音允许使用语音常见过零率与稍高能量进入 hangover。
    return frame.pitched
      || (frame.rms >= threshold * 1.35 && frame.crossingRate >= 0.01 && frame.crossingRate <= 0.32);
  });
  // 允许最长约 240ms 的字间停顿。
  const hangoverFrames = Math.max(1, Math.round(0.24 / frameDuration));
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
    const end = Math.min(rangeEnd, (frames[Math.max(0, index - 1)]?.time ?? rangeEnd) + frameDuration);
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

/**
 * 浏览器端语音活动检测：RMS 过滤底噪，再用 70–400Hz 自相关确认人声基频。
 * 短停顿使用 hangover 合并，输出的一段天然对应一条待识别字幕。
 */
export function detectVoiceSegments(
  buffer: AudioBuffer,
  rangeStart = 0,
  rangeEnd = buffer.duration,
): readonly VoiceSegment[] {
  const analysis = voiceAnalysis(buffer, rangeStart, rangeEnd);
  const frames: VoiceFrame[] = [];
  for (let offset = analysis.startSample;
    offset + analysis.sourceFrameSize <= analysis.endSample;
    offset += analysis.sourceFrameSize) {
    frames.push(analyzeVoiceFrame(buffer, offset, analysis));
  }
  return voiceSegmentsFromFrames(frames, rangeStart, rangeEnd, analysis.analysisFrameSize / analysis.analysisRate);
}

const yieldToBrowser = (): Promise<void> => new Promise((resolve) => {
  if (typeof requestAnimationFrame === 'function') requestAnimationFrame(() => resolve());
  else setTimeout(resolve, 0);
});

async function detectVoiceSegmentsResponsive(
  buffer: AudioBuffer,
  rangeStart: number,
  rangeEnd: number,
  report: (fraction: number) => void,
  signal?: AbortSignal,
): Promise<readonly VoiceSegment[]> {
  const analysis = voiceAnalysis(buffer, rangeStart, rangeEnd);
  const frames: VoiceFrame[] = [];
  const total = Math.max(1, Math.floor((analysis.endSample - analysis.startSample) / analysis.sourceFrameSize));
  let processed = 0;
  for (let offset = analysis.startSample;
    offset + analysis.sourceFrameSize <= analysis.endSample;
    offset += analysis.sourceFrameSize) {
    if (signal?.aborted) throw new DOMException('操作已取消。', 'AbortError');
    frames.push(analyzeVoiceFrame(buffer, offset, analysis));
    processed++;
    if (processed % 48 === 0) {
      report(Math.min(1, processed / total));
      await yieldToBrowser();
    }
  }
  report(1);
  return voiceSegmentsFromFrames(frames, rangeStart, rangeEnd, analysis.analysisFrameSize / analysis.analysisRate);
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

export async function recognizeTencentWav(
  bytes: Uint8Array,
  apiKey: string,
  signal?: AbortSignal,
): Promise<string> {
  if (signal?.aborted) throw new DOMException('操作已取消。', 'AbortError');
  const request = new AbortController();
  let timedOut = false;
  const onAbort = (): void => request.abort(signal?.reason);
  signal?.addEventListener('abort', onAbort, { once: true });
  const timeout = setTimeout(() => {
    timedOut = true;
    request.abort();
  }, 120_000);
  let response: Response;
  try {
    response = await fetch(TENCENT_SYNC_ASR, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${apiKey}`,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ model: MODEL, data: base64(bytes), voice_encode_format: 'wav' }),
      signal: request.signal,
    });
  } catch (error) {
    if (signal?.aborted) throw new DOMException('操作已取消。', 'AbortError');
    if (timedOut) throw new Error('腾讯云 ASR 请求超过 120 秒未响应，请稍后重试。');
    throw new Error(`无法连接腾讯云 ASR：${error instanceof Error ? error.message : String(error)}`);
  } finally {
    clearTimeout(timeout);
    signal?.removeEventListener('abort', onAbort);
  }
  let payload: TencentAsrResponse;
  try {
    payload = await response.json() as TencentAsrResponse;
  } catch {
    throw new Error(`腾讯云 ASR 返回了无法解析的响应（HTTP ${response.status}）。`);
  }
  const text = payload.output?.text?.trim()
    || payload.output?.sentences?.map((sentence) => sentence.text?.trim() ?? '').filter(Boolean).join(' ');
  if (!response.ok || payload.status && payload.status !== 'completed' || !text) {
    const message = payload.error?.message_zh ?? payload.error?.message ?? payload.message
      ?? `腾讯云 ASR 识别失败（HTTP ${response.status}）。`;
    const code = payload.error?.code ? `错误码 ${payload.error.code}` : '';
    const requestId = payload.error?.request_id ?? payload.request_id;
    throw new Error([message, code, requestId ? `请求 ID ${requestId}` : ''].filter(Boolean).join(' · '));
  }
  return text;
}

export async function recognizeAudioTrack(
  track: VideoTrack,
  readSource: (path: string) => Promise<ArrayBuffer>,
  apiKey: string,
  report: RecognitionProgressReporter,
  signal?: AbortSignal,
): Promise<readonly RecognizedSubtitle[]> {
  if (!apiKey.trim()) throw new Error('请先在设置中填写腾讯云 ASR API Key。');
  if (typeof AudioContext === 'undefined') throw new Error('当前浏览器无法解码音频，不能执行字幕识别。');
  report(0.01, '正在准备音频解码器…');
  await yieldToBrowser();
  const audioContext = new AudioContext();
  const decodedSources = new Map<string, AudioBuffer>();
  const work: {
    buffer: AudioBuffer;
    segment: VoiceSegment;
    timelineStart: number;
    clipStart: number;
    speed: number;
  }[] = [];
  let clipOffset = 0;
  try {
    for (let clipIndex = 0; clipIndex < track.clips.length; clipIndex++) {
      const clip = track.clips[clipIndex]!;
      if (signal?.aborted) throw new DOMException('操作已取消。', 'AbortError');
      const clipCount = Math.max(1, track.clips.length);
      const phaseStart = 0.03 + 0.37 * clipIndex / clipCount;
      const phaseSpan = 0.37 / clipCount;
      if (!hasAudio(clip.media)) {
        report(phaseStart + phaseSpan, `已跳过无声片段 ${clipIndex + 1}/${track.clips.length}。`);
        clipOffset += clipDuration(clip);
        continue;
      }
      report(phaseStart, `正在解码音频 ${clipIndex + 1}/${track.clips.length}…`);
      let buffer = decodedSources.get(clip.media.path);
      if (!buffer) {
        try {
          buffer = await audioContext.decodeAudioData((await readSource(clip.media.path)).slice(0));
        } catch (error) {
          throw new Error(`无法解码音频“${clip.media.path}”：${error instanceof Error ? error.message : String(error)}`);
        }
        decodedSources.set(clip.media.path, buffer);
      }
      const segments = await detectVoiceSegmentsResponsive(
        buffer,
        clip.start,
        clip.end,
        (fraction) => report(
          phaseStart + phaseSpan * (0.2 + 0.8 * fraction),
          `正在检测人声 ${clipIndex + 1}/${track.clips.length} · ${Math.round(fraction * 100)}%`,
        ),
        signal,
      );
      for (const segment of segments) {
        work.push({ buffer, segment, timelineStart: clipOffset, clipStart: clip.start, speed: clip.speed });
      }
      clipOffset += clipDuration(clip);
    }
    if (work.length === 0) {
      report(1, '未检测到可识别的人声。');
      return [];
    }
    const result: RecognizedSubtitle[] = [];
    for (let index = 0; index < work.length; index++) {
      if (signal?.aborted) throw new DOMException('操作已取消。', 'AbortError');
      const item = work[index]!;
      report(0.4 + 0.58 * index / work.length, `正在请求腾讯云 ASR · 第 ${index + 1}/${work.length} 段…`);
      const text = await recognizeTencentWav(
        encodeSegmentAsWav(item.buffer, item.segment),
        apiKey.trim(),
        signal,
      );
      result.push({
        start: item.timelineStart + (item.segment.start - item.clipStart) / item.speed,
        end: item.timelineStart + (item.segment.end - item.clipStart) / item.speed,
        text,
      });
      report(0.4 + 0.58 * (index + 1) / work.length, `已识别 ${index + 1}/${work.length} 段语音。`);
    }
    report(1, '字幕识别完成。');
    return result;
  } finally {
    void audioContext.close();
  }
}
