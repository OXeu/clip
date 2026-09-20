import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
  FINGERPRINT_SAMPLE_BYTES,
  SESSION_STORAGE_KEY,
  clearStoredSession,
  fingerprintFile,
  loadStoredSession,
  sameVideo,
  saveStoredSession,
  type SessionStorageLike,
  type StoredEditSession,
} from '../src/session.ts';

const namedBlob = (data: BlobPart[], name: string, lastModified = 1): Blob & { name: string; lastModified: number } => {
  const blob = new Blob(data, { type: 'video/mp4' });
  return Object.assign(blob, { name, lastModified });
};

class MemoryStorage implements SessionStorageLike {
  readonly values = new Map<string, string>();
  getItem(key: string): string | null { return this.values.get(key) ?? null; }
  setItem(key: string, value: string): void { this.values.set(key, value); }
  removeItem(key: string): void { this.values.delete(key); }
}

describe('视频轻量指纹', () => {
  it('相同内容在重命名或修改时间变化后仍可识别', async () => {
    const bytes = new Uint8Array(FINGERPRINT_SAMPLE_BYTES * 4 + 17).map((_, index) => index % 251);
    const first = await fingerprintFile(namedBlob([bytes], 'original.mp4', 100));
    const renamed = await fingerprintFile(namedBlob([bytes], 'renamed.mp4', 200));
    assert.equal(sameVideo(renamed, first), true);
  });

  it('能发现头、中、尾采样区的内容差异', async () => {
    const size = FINGERPRINT_SAMPLE_BYTES * 6;
    const original = new Uint8Array(size);
    const changed = original.slice();
    changed[Math.floor(size / 2)] = 1;
    const first = await fingerprintFile(namedBlob([original], 'video.mp4'));
    const second = await fingerprintFile(namedBlob([changed], 'video.mp4'));
    assert.equal(sameVideo(second, first), false);
  });

  it('文件大小不同会直接判定为不同视频', async () => {
    const first = await fingerprintFile(namedBlob([new Uint8Array(32)], 'video.mp4'));
    const second = await fingerprintFile(namedBlob([new Uint8Array(33)], 'video.mp4'));
    assert.equal(sameVideo(second, first), false);
  });

  it('超大文件也只读取最多三个固定大小的采样区块', async () => {
    const file = namedBlob([new Uint8Array(FINGERPRINT_SAMPLE_BYTES * 20)], 'large.mp4');
    const slice = file.slice.bind(file);
    let requestedBytes = 0;
    file.slice = (start, end, contentType) => {
      requestedBytes += Math.max(0, (end ?? file.size) - (start ?? 0));
      return slice(start, end, contentType);
    };
    await fingerprintFile(file);
    assert.equal(requestedBytes, FINGERPRINT_SAMPLE_BYTES * 3);
  });
});

describe('sessionStorage 数据', () => {
  const session: StoredEditSession = {
    version: 1,
    savedAt: 123,
    project: {
      tracks: [{ id: 'main', name: '轨道 1', isMain: true, clips: [] }],
      sources: [],
    },
    sources: [],
    workspace: {
      activeTrackId: 'main',
      selectedTrackId: null,
      selectedClipId: null,
      position: 0,
      previewZoom: 1,
      timelineZoom: 1,
      timelineScrollLeft: 0,
    },
  };

  it('可保存、读取和清除', () => {
    const storage = new MemoryStorage();
    saveStoredSession(storage, session);
    assert.deepEqual(loadStoredSession(storage), session);
    clearStoredSession(storage);
    assert.equal(storage.getItem(SESSION_STORAGE_KEY), null);
  });

  it('损坏或版本不兼容的数据不会进入恢复流程', () => {
    const storage = new MemoryStorage();
    storage.setItem(SESSION_STORAGE_KEY, '{bad json');
    assert.equal(loadStoredSession(storage), null);
    storage.setItem(SESSION_STORAGE_KEY, JSON.stringify({ ...session, version: 2 }));
    assert.equal(loadStoredSession(storage), null);
  });
});
