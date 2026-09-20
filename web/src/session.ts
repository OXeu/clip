/**
 * 刷新恢复：sessionStorage 只保存轻量项目数据，不保存视频字节。
 * 原视频通过固定大小的头/中/尾采样哈希重新关联，超大文件也不会被全量读取。
 */

import type { EditProjectSnapshot } from './model.ts';

export const SESSION_STORAGE_KEY = 'clip.edit-session.v1';
export const FINGERPRINT_SAMPLE_BYTES = 64 * 1024;

export interface SourceFingerprint {
  /** 项目中 MediaInfo.path，通常是最初导入时的文件名。 */
  readonly path: string;
  readonly name: string;
  readonly size: number;
  readonly lastModified: number;
  readonly type: string;
  readonly sampleHash: string;
}

export interface WorkspaceSnapshot {
  readonly activeTrackId: string;
  readonly selectedTrackId: string | null;
  readonly selectedClipId: string | null;
  readonly position: number;
  readonly previewZoom: number;
  readonly timelineZoom: number;
  readonly timelineScrollLeft: number;
}

export interface StoredEditSession {
  readonly version: 1;
  readonly savedAt: number;
  readonly project: EditProjectSnapshot;
  readonly sources: readonly SourceFingerprint[];
  readonly workspace: WorkspaceSnapshot;
}

export interface SessionStorageLike {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

export type FingerprintFile = Blob & {
  readonly name: string;
  readonly lastModified: number;
};

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null;

const validFingerprint = (value: unknown): value is SourceFingerprint =>
  isRecord(value)
  && typeof value.path === 'string'
  && value.path.length > 0
  && typeof value.name === 'string'
  && Number.isFinite(value.size)
  && (value.size as number) >= 0
  && Number.isFinite(value.lastModified)
  && typeof value.type === 'string'
  && typeof value.sampleHash === 'string'
  && /^[0-9a-f]{64}$/.test(value.sampleHash);

function parseStoredSession(value: unknown): StoredEditSession | null {
  if (!isRecord(value) || value.version !== 1 || !Number.isFinite(value.savedAt)) return null;
  if (!isRecord(value.project) || !Array.isArray(value.sources) || !isRecord(value.workspace)) return null;
  if (!value.sources.every(validFingerprint)) return null;
  const workspace = value.workspace;
  if (
    typeof workspace.activeTrackId !== 'string'
    || !(workspace.selectedTrackId === null || typeof workspace.selectedTrackId === 'string')
    || !(workspace.selectedClipId === null || typeof workspace.selectedClipId === 'string')
    || !Number.isFinite(workspace.position)
    || !Number.isFinite(workspace.previewZoom)
    || !Number.isFinite(workspace.timelineZoom)
    || !Number.isFinite(workspace.timelineScrollLeft)
  ) return null;
  return value as unknown as StoredEditSession;
}

export function loadStoredSession(storage: SessionStorageLike): StoredEditSession | null {
  try {
    const raw = storage.getItem(SESSION_STORAGE_KEY);
    if (!raw) return null;
    return parseStoredSession(JSON.parse(raw));
  } catch {
    return null;
  }
}

export function saveStoredSession(storage: SessionStorageLike, session: StoredEditSession): void {
  storage.setItem(SESSION_STORAGE_KEY, JSON.stringify(session));
}

export function clearStoredSession(storage: SessionStorageLike): void {
  try {
    storage.removeItem(SESSION_STORAGE_KEY);
  } catch {
    // 隐私模式或站点策略可能禁用 Web Storage；清理失败不应阻断界面操作。
  }
}

/** 最多读取三个 64 KiB 区块；小文件的重叠区块会自动去重。 */
export async function fingerprintFile(file: FingerprintFile, path = file.name): Promise<SourceFingerprint> {
  const chunkSize = Math.min(FINGERPRINT_SAMPLE_BYTES, file.size);
  const offsets = [...new Set([
    0,
    Math.max(0, Math.floor((file.size - chunkSize) / 2)),
    Math.max(0, file.size - chunkSize),
  ])];
  const chunks = await Promise.all(
    offsets.map((offset) => file.slice(offset, offset + chunkSize).arrayBuffer()),
  );
  const sampledLength = chunks.reduce((sum, chunk) => sum + chunk.byteLength, 0);
  const sampled = new Uint8Array(sampledLength);
  let cursor = 0;
  for (const chunk of chunks) {
    sampled.set(new Uint8Array(chunk), cursor);
    cursor += chunk.byteLength;
  }
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', sampled));
  const sampleHash = [...digest].map((byte) => byte.toString(16).padStart(2, '0')).join('');
  return {
    path,
    name: file.name,
    size: file.size,
    lastModified: file.lastModified,
    type: file.type,
    sampleHash,
  };
}

/** 名称和修改时间可能因复制/重命名而变化，不参与“同一视频”的硬性判断。 */
export function sameVideo(file: SourceFingerprint, expected: SourceFingerprint): boolean {
  return file.size === expected.size && file.sampleHash === expected.sampleHash;
}
