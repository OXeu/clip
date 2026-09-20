import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { hasEbmlHeader, prepareMediaFile } from '../src/media-preparation.ts';
import { probeFile } from '../src/probe.ts';

function pcmWave(durationSeconds = 0.1, sampleRate = 8_000): ArrayBuffer {
  const samples = Math.round(durationSeconds * sampleRate);
  const dataLength = samples * 2;
  const buffer = new ArrayBuffer(44 + dataLength);
  const view = new DataView(buffer);
  const text = (offset: number, value: string): void => {
    for (let index = 0; index < value.length; index++) view.setUint8(offset + index, value.charCodeAt(index));
  };
  text(0, 'RIFF');
  view.setUint32(4, 36 + dataLength, true);
  text(8, 'WAVE');
  text(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  text(36, 'data');
  view.setUint32(40, dataLength, true);
  return buffer;
}

describe('导入容器准备', () => {
  it('通过 EBML 文件头识别 MKV/WebM，而不是依赖乱码 box 错误', () => {
    assert.equal(hasEbmlHeader(new Uint8Array([0x1a, 0x45, 0xdf, 0xa3]).buffer), true);
    assert.equal(hasEbmlHeader(new Uint8Array([0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70]).buffer), false);
  });

  it('MP4/MOV 文件不会被重复转换', async () => {
    const data = new Uint8Array([0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70]).buffer;
    const file = new File([data], 'source.mp4', { type: 'video/mp4' });
    const prepared = await prepareMediaFile(file, data);
    assert.equal(prepared.converted, false);
    assert.equal(prepared.file, file);
    assert.equal(prepared.data, data);
  });

  it('普通 WAV 原样保留，并探测为无视频流的音频素材', async () => {
    const data = pcmWave();
    const file = new File([data], 'tone.wav', { type: 'audio/wav' });
    const prepared = await prepareMediaFile(file, data);
    assert.equal(prepared.converted, false);
    assert.equal(prepared.file, file);

    const result = await probeFile(file.name, prepared.data, file.type);
    assert.equal(result.media.videoStreamIndex, -1);
    assert.equal(result.media.audioStreamIndex, 0);
    assert.equal(result.media.width, 0);
    assert.equal(result.media.height, 0);
    assert.equal(result.media.codec, 'pcm-s16');
    assert.ok(Math.abs(result.media.duration - 0.1) < 0.001);
  });

  it('扩展名伪装成 MKV 时返回可读错误', async () => {
    const data = new Uint8Array([1, 2, 3, 4]).buffer;
    const file = new File([data], 'broken.mkv', { type: 'video/x-matroska' });
    await assert.rejects(
      prepareMediaFile(file, data),
      /内容不是有效的 Matroska 容器/,
    );
  });
});
