/**
 * 端到端测试用的自动化接口。
 *
 * 与桌面端的 `--smoke-test` / `App.IsAutomatedRun` / MainWindow.*Verification.cs
 * 是同一类东西：测试需要在不做像素级模拟的前提下驱动真实的编辑与导出路径。
 *
 * 仅在 URL 带 `?automation=1` 时挂载，正常用户访问不会暴露该接口。
 * 它不绕过任何逻辑，只是把界面按钮背后的同一批函数重新暴露出来。
 */

import type { Capabilities } from './capabilities.ts';
import { EncoderRoute } from './capabilities.ts';
import type { ExportResult } from './exporter.ts';
import type { ExportProgress } from './ffmpeg.ts';
import type { ExportOptions, MediaInfo, VideoClip, VideoTrack } from './model.ts';

export interface AutomationState {
  readonly sources: readonly MediaInfo[];
  readonly tracks: readonly VideoTrack[];
  readonly duration: number;
  readonly selectedClipId: string | null;
  readonly activeTrackId: string;
  readonly position: number;
  readonly playing: boolean;
  readonly canUndo: boolean;
  readonly canRedo: boolean;
  readonly exportableTracks: number;
}

export interface AutomationExportRequest {
  /** 目标轨道；省略时使用当前活动轨道。 */
  readonly trackId?: string;
  readonly quality?: ExportOptions['quality'];
  readonly width?: number | null;
  readonly height?: number | null;
  readonly route?: EncoderRoute;
}

export interface AutomationExportResult {
  readonly url: string;
  readonly width: number;
  readonly height: number;
  readonly route: EncoderRoute;
  readonly codec: string | null;
  readonly audioRoute: ExportResult['audioRoute'];
  readonly audioCodec: ExportResult['audioCodec'];
  readonly fellBack?: string;
  readonly byteLength: number;
  readonly progress: readonly number[];
}

export interface AutomationApi {
  state: () => AutomationState;
  capabilities: () => Capabilities | null;
  seek: (time: number) => void;
  split: () => boolean;
  setSpeed: (clipId: string, speed: number) => boolean;
  deleteClip: (clipId: string) => boolean;
  duplicateClip: (clipId: string) => boolean;
  selectTrack: (trackId: string) => void;
  clips: (trackId?: string) => readonly VideoClip[];
  exportToBlob: (request?: AutomationExportRequest) => Promise<AutomationExportResult>;
  revoke: (url: string) => void;
}

export interface AutomationDeps {
  state: () => AutomationState;
  capabilities: () => Capabilities | null;
  seek: (time: number) => void;
  split: () => boolean;
  setSpeed: (clipId: string, speed: number) => boolean;
  deleteClip: (clipId: string) => boolean;
  duplicateClip: (clipId: string) => boolean;
  selectTrack: (trackId: string) => void;
  clips: (trackId?: string) => readonly VideoClip[];
  exportToBytes: (
    request: AutomationExportRequest,
    onProgress: (progress: ExportProgress) => void,
  ) => Promise<ExportResult>;
}

/** 暴露自动化接口；返回是否需要启用。 */
export function shouldInstallAutomation(search: string): boolean {
  return new URLSearchParams(search).get('automation') === '1';
}

export function installAutomation(deps: AutomationDeps): void {
  const api: AutomationApi = {
    state: deps.state,
    capabilities: deps.capabilities,
    seek: deps.seek,
    split: deps.split,
    setSpeed: deps.setSpeed,
    deleteClip: deps.deleteClip,
    duplicateClip: deps.duplicateClip,
    selectTrack: deps.selectTrack,
    clips: deps.clips,
    async exportToBlob(request = {}) {
      const progress: number[] = [];
      const result = await deps.exportToBytes(request, (update) => progress.push(update.fraction));
      const blob = new Blob([result.data as unknown as BlobPart], { type: result.mimeType });
      return {
        url: URL.createObjectURL(blob),
        width: result.width,
        height: result.height,
        route: result.route,
        codec: result.codec,
        audioRoute: result.audioRoute,
        audioCodec: result.audioCodec,
        ...(result.fellBack !== undefined ? { fellBack: result.fellBack } : {}),
        byteLength: result.data.byteLength,
        progress,
      };
    },
    revoke(url: string) {
      URL.revokeObjectURL(url);
    },
  };
  (window as unknown as { __clip: AutomationApi }).__clip = api;
}
