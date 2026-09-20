import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { hasEbmlHeader, prepareMediaFile } from '../src/media-preparation.ts';

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

  it('扩展名伪装成 MKV 时返回可读错误', async () => {
    const data = new Uint8Array([1, 2, 3, 4]).buffer;
    const file = new File([data], 'broken.mkv', { type: 'video/x-matroska' });
    await assert.rejects(
      prepareMediaFile(file, data),
      /内容不是有效的 Matroska 容器/,
    );
  });
});
