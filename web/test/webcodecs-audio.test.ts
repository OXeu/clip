import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { editMediaTimeOffset, findAudioSpecificConfig } from '../src/mp4demux.ts';
import {
  pitchAndTimeStretchPcm,
  timeStretchPcm,
  WEB_AUDIO_SAMPLE_RATE,
  type StereoPcm,
} from '../src/webcodecs-audio.ts';

function sine(frequency: number, seconds: number): StereoPcm {
  const length = Math.round(seconds * WEB_AUDIO_SAMPLE_RATE);
  const left = new Float32Array(length);
  const right = new Float32Array(length);
  for (let index = 0; index < length; index++) {
    const sample = Math.sin(2 * Math.PI * frequency * index / WEB_AUDIO_SAMPLE_RATE);
    left[index] = sample;
    right[index] = sample;
  }
  return { left, right };
}

function estimateFrequency(samples: Float32Array): number {
  // 去掉窗口拼接两端，只统计正向过零点。
  const start = Math.floor(samples.length * 0.15);
  const end = Math.floor(samples.length * 0.85);
  let crossings = 0;
  for (let index = start + 1; index < end; index++) {
    if (samples[index - 1]! < 0 && samples[index]! >= 0) crossings++;
  }
  return crossings * WEB_AUDIO_SAMPLE_RATE / (end - start);
}

describe('WebCodecs 音频处理', () => {
  for (const [speed, outputLength] of [[2, 24_000], [0.5, 96_000]] as const) {
    it(`${speed}x 变速会改变时长但保持音高`, () => {
      const output = timeStretchPcm(sine(440, 1), speed, outputLength);
      assert.equal(output.left.length, outputLength);
      assert.equal(output.right.length, outputLength);
      assert.ok(
        Math.abs(estimateFrequency(output.left) - 440) < 12,
        `变速后音高偏差过大：${estimateFrequency(output.left).toFixed(1)} Hz`,
      );
    });
  }

  it('1x 会逐样本保留 PCM', () => {
    const input = sine(220, 0.05);
    const output = timeStretchPcm(input, 1, input.left.length);
    assert.deepEqual(output.left, input.left);
    assert.deepEqual(output.right, input.right);
  });

  it('变调与倍速相互独立', () => {
    const octaveUp = pitchAndTimeStretchPcm(sine(440, 1), 1, 12);
    assert.equal(octaveUp.left.length, WEB_AUDIO_SAMPLE_RATE);
    assert.ok(
      Math.abs(estimateFrequency(octaveUp.left) - 880) < 24,
      `升高八度后的频率错误：${estimateFrequency(octaveUp.left).toFixed(1)} Hz`,
    );

    const faster = pitchAndTimeStretchPcm(sine(440, 1), 2, 12);
    assert.equal(faster.left.length, WEB_AUDIO_SAMPLE_RATE / 2);
    assert.ok(
      Math.abs(estimateFrequency(faster.left) - 880) < 24,
      `变速加变调后的频率错误：${estimateFrequency(faster.left).toFixed(1)} Hz`,
    );
  });

  it('从嵌套 esds descriptor 提取 AAC AudioSpecificConfig', () => {
    const asc = new Uint8Array([0x11, 0x90, 0x56, 0xe5, 0x00]);
    const extracted = findAudioSpecificConfig({
      tag: 3,
      descs: [{ tag: 4, descs: [{ tag: 5, data: asc }] }],
    });
    assert.deepEqual(extracted, asc);
  });

  it('应用 AAC 预热 edit list，并保留前置空白 edit', () => {
    assert.equal(editMediaTimeOffset([
      { segment_duration: 4_000, media_time: 1_024, media_rate_integer: 1, media_rate_fraction: 0 },
    ], 48_000, 1_000), 1_024 / 48_000);
    assert.equal(editMediaTimeOffset([
      { segment_duration: 100, media_time: -1, media_rate_integer: 1, media_rate_fraction: 0 },
      { segment_duration: 4_000, media_time: 0, media_rate_integer: 1, media_rate_fraction: 0 },
    ], 48_000, 1_000), -0.1);
  });
});
