import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { detectVoiceSegments } from '../src/asr.ts';

describe('浏览器端音高分段', () => {
  it('把带基频的有声区间从静音中拆成独立字幕段', () => {
    const sampleRate = 8_000;
    const samples = new Float32Array(sampleRate * 3);
    for (let index = sampleRate; index < sampleRate * 2; index++) {
      samples[index] = Math.sin(2 * Math.PI * 180 * index / sampleRate) * 0.22;
    }
    const buffer = {
      sampleRate,
      length: samples.length,
      duration: samples.length / sampleRate,
      numberOfChannels: 1,
      getChannelData: () => samples,
    } as unknown as AudioBuffer;

    const segments = detectVoiceSegments(buffer);
    assert.equal(segments.length, 1);
    assert.ok(segments[0]!.start >= 0.8 && segments[0]!.start <= 1.05);
    assert.ok(segments[0]!.end >= 1.95 && segments[0]!.end <= 2.2);
  });

  it('纯静音不会产生待收费的 ASR 请求段', () => {
    const samples = new Float32Array(8_000);
    const buffer = {
      sampleRate: 8_000,
      length: samples.length,
      duration: 1,
      numberOfChannels: 1,
      getChannelData: () => samples,
    } as unknown as AudioBuffer;
    assert.deepEqual(detectVoiceSegments(buffer), []);
  });
});
