import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { detectVoiceSegments, recognizeTencentWav } from '../src/asr.ts';

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

  it('连续讲话没有静音基线时仍能检测，并以 8 kHz 复杂度分析高采样率素材', () => {
    const sampleRate = 48_000;
    const samples = new Float32Array(sampleRate * 4);
    for (let index = 0; index < samples.length; index++) {
      samples[index] = Math.sin(2 * Math.PI * 180 * index / sampleRate) * 0.16;
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
    assert.ok(segments[0]!.start <= 0.1);
    assert.ok(segments[0]!.end >= 3.8);
  });

  it('优先显示腾讯云中文错误、错误码与请求 ID', async () => {
    const originalFetch = globalThis.fetch;
    globalThis.fetch = async () => new Response(JSON.stringify({
      error: {
        message: 'authentication failed',
        message_zh: '测试鉴权失败',
        code: '401002',
        request_id: 'request-test',
      },
    }), { status: 401, headers: { 'Content-Type': 'application/json' } });
    try {
      await assert.rejects(
        recognizeTencentWav(new Uint8Array([1, 2, 3]), 'invalid'),
        /测试鉴权失败 · 错误码 401002 · 请求 ID request-test/,
      );
    } finally {
      globalThis.fetch = originalFetch;
    }
  });

  it('响应没有全文时会拼接腾讯云分句结果', async () => {
    const originalFetch = globalThis.fetch;
    globalThis.fetch = async () => new Response(JSON.stringify({
      status: 'completed',
      output: { sentences: [{ text: '第一句' }, { text: '第二句' }] },
    }), { status: 200, headers: { 'Content-Type': 'application/json' } });
    try {
      assert.equal(await recognizeTencentWav(new Uint8Array([1]), 'test'), '第一句 第二句');
    } finally {
      globalThis.fetch = originalFetch;
    }
  });
});
