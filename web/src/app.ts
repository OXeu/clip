/**
 * 应用入口：把编辑模型、时间轴、预览、导出与能力探测连起来。
 *
 * 与桌面端 WPF 版的行为对应关系：
 *   MainWindow.xaml.cs      -> 本文件的导入、快捷键、设置
 *   MainWindow.Preview.cs   -> PreviewController（播放、逐帧、跨片段续播）
 *   MainWindow.Editing.cs   -> 编辑动作与状态刷新
 *   MainWindow.Export.cs    -> 导出对话框
 */

import './style.css';

import { installAutomation, shouldInstallAutomation } from './automation.ts';
import {
  type Capabilities,
  EncoderRoute,
  chooseRoute,
  describeCapabilities,
  detectCapabilities,
  sharedArrayBufferAvailable,
} from './capabilities.ts';
import { exportClips } from './exporter.ts';
import type { ExportProgress } from './ffmpeg.ts';
import { prepareMediaFile } from './media-preparation.ts';
import {
  type ClipPosition,
  type ExportOptions,
  type ExportQuality,
  type MediaInfo,
  type VideoClip,
  type VideoTrack,
  EditProject,
  MAXIMUM_SPEED,
  MINIMUM_SPEED,
  VideoEncoder,
  clipDuration,
  defaultExportOptions,
  displayName,
  fileName,
  hasVideo,
  trackDuration,
} from './model.ts';
import { probeFile } from './probe.ts';
import {
  clearStoredSession,
  fingerprintFile,
  loadStoredSession,
  parseProjectFile,
  sameVideo,
  saveStoredSession,
  serializeProjectFile,
  type SourceFingerprint,
  type StoredEditSession,
} from './session.ts';
import { TimelineView, formatTime } from './timeline.ts';
import { extractWaveform } from './waveform.ts';

const $ = <T extends HTMLElement>(id: string): T => {
  const element = document.getElementById(id);
  if (!element) throw new Error(`缺少元素 #${id}`);
  return element as T;
};

// ---------- 状态 ----------

let project = new EditProject();
/** 文件名 -> 可直接预览/解封装的 File；MKV/WebM 在导入时准备为 MP4。 */
const files = new Map<string, File>();
const objectUrls = new Map<string, string>();
const sourceFingerprints = new Map<string, SourceFingerprint>();

function validateRecoveryCandidate(stored: StoredEditSession): StoredEditSession {
  const restored = EditProject.fromSnapshot(stored.project);
  if (restored.sources.length === 0) throw new Error('剪辑记录不包含素材。');
  const paths = new Set(stored.sources.map((source) => source.path));
  if (paths.size !== restored.sources.length
    || !restored.sources.every((source) => paths.has(source.path))) {
    throw new Error('素材指纹与项目不一致。');
  }
  return stored;
}

function loadRecoveryCandidate(): StoredEditSession | null {
  const stored = loadStoredSession(sessionStorage);
  if (!stored) return null;
  try {
    return validateRecoveryCandidate(stored);
  } catch (error) {
    console.warn('已忽略无法恢复的剪辑会话。', error);
    clearStoredSession(sessionStorage);
    return null;
  }
}

let pendingRecovery = loadRecoveryCandidate();
let recoveryOrigin: 'session' | 'file' = 'session';
const recoveryFiles = new Map<string, File>();
const recoveryFingerprints = new Map<string, SourceFingerprint>();
const recoveryWaveforms = new Map<string, Float32Array>();
let sessionSaveHandle = 0;

let capabilities: Capabilities | null = null;
let route: EncoderRoute | 'auto' = loadPreference();
let operation: AbortController | null = null;
let activeTrackId = project.mainTrack.id;
let selectedTrackId: string | null = null;
let selectedClipId: string | null = null;
let multiSelectMode = false;
const multiSelectedTrackIds = new Set<string>();
let position = 0;

// ---------- 元素 ----------

const video = $<HTMLVideoElement>('preview');
const previewFrame = $<HTMLCanvasElement>('preview-frame');
const previewRegion = $('preview-region');
const previewCanvas = $('preview-canvas');
const audioPreview = $('audio-preview');
const audioPreviewName = $('audio-preview-name');
const emptyState = $('empty-state');
const previewFooter = $('preview-footer');
const timelineRegion = $('timeline-region');
const timelineScroll = $('timeline-scroll');
const canvas = $<HTMLCanvasElement>('timeline');
const statusText = $('status-text');
const capabilityText = $('capability-text');
const progress = $<HTMLProgressElement>('progress');
const positionLabel = $('position-text');
const totalLabel = $('total-text');
const playButton = $<HTMLButtonElement>('play-button');
const playIcon = $<HTMLElement>('play-icon');
const importButton = $<HTMLButtonElement>('import-button');
const moreButton = $<HTMLButtonElement>('more-button');
const moreMenuWrap = $('more-menu-wrap');
const moreMenu = $('more-menu');
const moreSettingsButton = $<HTMLButtonElement>('more-settings-button');
const openProjectButton = $<HTMLButtonElement>('open-project-button');
const saveProjectButton = $<HTMLButtonElement>('save-project-button');
const emptyImportButton = $<HTMLButtonElement>('empty-import');
const exportButton = $<HTMLButtonElement>('export-button');
const cancelButton = $<HTMLButtonElement>('cancel-button');
const splitButton = $<HTMLButtonElement>('split-button');
const deleteButton = $<HTMLButtonElement>('delete-button');
const undoButton = $<HTMLButtonElement>('undo-button');
const redoButton = $<HTMLButtonElement>('redo-button');
const duplicateButton = $<HTMLButtonElement>('duplicate-button');
const renameButton = $<HTMLButtonElement>('rename-button');
const multiSelectButton = $<HTMLButtonElement>('multi-select-button');
const bindTracksButton = $<HTMLButtonElement>('bind-tracks-button');
const unbindTracksButton = $<HTMLButtonElement>('unbind-tracks-button');
const stepBackButton = $<HTMLButtonElement>('step-back');
const stepForwardButton = $<HTMLButtonElement>('step-forward');
const fileInput = $<HTMLInputElement>('file-input');
const projectFileInput = $<HTMLInputElement>('project-file-input');
const dropOverlay = $('drop-overlay');
const timelineSummary = $('timeline-summary');
const clipContextMenu = $('clip-context-menu');
const clipSpeedCurrent = $('clip-speed-current');
const speedPresetButtons = Array.from(clipContextMenu.querySelectorAll<HTMLButtonElement>('[data-speed]'));
const customSpeedButton = $<HTMLButtonElement>('custom-speed-button');
const speedDialog = $<HTMLDialogElement>('speed-dialog');
const speedForm = $<HTMLFormElement>('speed-form');
const speedInput = $<HTMLInputElement>('speed-input');
const speedValidation = $('speed-validation');
const speedCancelButton = $<HTMLButtonElement>('speed-cancel');
const exportDialog = $<HTMLDialogElement>('export-dialog');
const settingsDialog = $<HTMLDialogElement>('settings-dialog');
const recoveryDialog = $<HTMLDialogElement>('recovery-dialog');
const recoveryDiscardDialog = $<HTMLDialogElement>('recovery-discard-dialog');
const recoveryInput = $<HTMLInputElement>('recovery-file-input');
const recoveryChooseButton = $<HTMLButtonElement>('recovery-choose');
const recoveryChooseLabel = $('recovery-choose-label');
const recoveryDiscardButton = $<HTMLButtonElement>('recovery-discard');
const recoveryDiscardCancelButton = $<HTMLButtonElement>('recovery-discard-cancel');
const recoveryDiscardConfirmButton = $<HTMLButtonElement>('recovery-discard-confirm');
const recoveryError = $('recovery-error');
const recoveryFileList = $('recovery-file-list');
const recoveryEyebrow = $('recovery-eyebrow');
const recoveryTitle = $('recovery-title');
const recoveryBadge = $('recovery-badge');
const recoveryCopy = $('recovery-copy');

// 同时设置 DOM 属性与兼容属性，避免浏览器在 load/play 时重新启用原生视频层。
video.playsInline = true;
video.controls = false;
video.disablePictureInPicture = true;
video.disableRemotePlayback = true;

const timeline = new TimelineView(canvas, timelineScroll, {
  onSeek: (trackId, time) => seek(trackId, time),
  onSelectClip: (clipId, time) => selectClip(clipId, time),
  onContextClip: (clipId, clientX, clientY) => openClipContextMenu(clipId, clientX, clientY),
  onSelectTrack: (trackId, time) => selectTrack(trackId, time),
  onMoveClip: (clipId, trackId, index) => moveClip(clipId, trackId, index),
  onMoveTrack: (trackId, index) => moveTrack(trackId, index),
  onToggleTrack: (trackId) => toggleMultiSelectedTrack(trackId),
});
timeline.setProject(project);

// ---------- 工具 ----------

function loadPreference(): EncoderRoute | 'auto' {
  const stored = localStorage.getItem('clip.encoder');
  if (stored === 'wasm' || stored === 'webcodecs-video' || stored === 'auto') return stored;
  return 'auto';
}

function activeTrack(): VideoTrack {
  return project.findTrack(activeTrackId) ?? project.mainTrack;
}

function frameDuration(): number {
  const found = project.locate(activeTrackId, position);
  return found ? 1 / (found.clip.media.frameRate * found.clip.speed) : 1 / 30;
}

function status(message: string): void {
  statusText.textContent = message;
}

function setBusy(busy: boolean, message?: string): void {
  if (busy) {
    operation = new AbortController();
    progress.hidden = false;
    progress.value = 0;
    cancelButton.hidden = false;
  } else {
    operation = null;
    progress.hidden = true;
    cancelButton.hidden = true;
  }
  if (busy) {
    setMoreMenuOpen(false);
    closeClipContextMenu();
  }
  importButton.disabled = moreButton.disabled = openProjectButton.disabled = emptyImportButton.disabled = busy;
  saveProjectButton.disabled = busy || project.sources.length === 0;
  if (message !== undefined) status(message);
  refresh();
}

/**
 * 开始一个可取消的操作，并返回它的控制器。
 * 单独返回控制器是为了让 TypeScript 保留非空类型，避免在异步流程中丢失取消能力。
 */
function beginOperation(message: string): AbortController {
  setBusy(true, message);
  if (!operation) throw new Error('无法创建取消令牌。');
  return operation;
}

function objectUrl(media: MediaInfo): string {
  const key = media.path;
  let url = objectUrls.get(key);
  if (!url) {
    const file = files.get(key);
    if (!file) throw new Error(`找不到素材 ${key}`);
    url = URL.createObjectURL(file);
    objectUrls.set(key, url);
  }
  return url;
}

function createSessionSnapshot(): StoredEditSession | null {
  if (project.sources.length === 0) return null;
  const fingerprints = project.sources.map((source) => sourceFingerprints.get(source.path));
  if (fingerprints.some((fingerprint) => fingerprint === undefined)) return null;
  return {
    version: 1,
    savedAt: Date.now(),
    project: project.exportSnapshot(),
    sources: fingerprints as SourceFingerprint[],
    workspace: {
      activeTrackId,
      selectedTrackId,
      selectedClipId,
      position,
      previewZoom,
      timelineZoom: timeline.zoom,
      timelineScrollLeft: timelineScroll.scrollLeft,
    },
  };
}

function saveSessionNow(): void {
  if (sessionSaveHandle) {
    window.clearTimeout(sessionSaveHandle);
    sessionSaveHandle = 0;
  }
  // 恢复决策完成前不能用空项目覆盖待恢复数据。
  if (pendingRecovery) return;
  if (project.sources.length === 0) {
    clearStoredSession(sessionStorage);
    return;
  }
  const snapshot = createSessionSnapshot();
  if (!snapshot) return;
  try {
    saveStoredSession(sessionStorage, snapshot);
  } catch (error) {
    // 存储被禁用或达到配额时不影响当前剪辑。
    console.warn('无法自动保存本次剪辑会话。', error);
  }
}

function scheduleSessionSave(): void {
  if (pendingRecovery || sessionSaveHandle) return;
  sessionSaveHandle = window.setTimeout(saveSessionNow, 750);
}

// ---------- 导入 ----------

async function importFiles(list: readonly File[]): Promise<void> {
  if (operation) return;
  pause();
  const controller = beginOperation('正在导入素材…');
  let imported = 0;
  let importedAudio = 0;
  let cancelled = false;
  const failures: string[] = [];
  let lastClipId: string | null = null;

  try {
    for (const file of list) {
      try {
        const originalData = await file.arrayBuffer();
        const prepared = await prepareMediaFile(
          file,
          originalData,
          (fraction, message) => {
            progress.value = fraction;
            status(`${message} ${(fraction * 100).toFixed(0)}%`);
          },
          controller.signal,
        );
        const parsed = await probeFile(file.name, prepared.data, prepared.file.type || file.type);
        const probe = prepared.frameRate === undefined
          ? parsed
          : { ...parsed, media: { ...parsed.media, frameRate: prepared.frameRate } };
        if (probe.media.isHdr) throw new Error('暂不支持 HDR，请先转换为 SDR。');
        const fingerprint = await fingerprintFile(file, probe.media.path);
        files.set(probe.media.path, prepared.file);
        sourceFingerprints.set(probe.media.path, fingerprint);
        // 同一素材只登记一次；重复导入会新建轨道但复用源文件。
        const existing = project.sources.find((source) => source.path === probe.media.path);
        const source = existing ?? probe.media;
        const separated = hasVideo(source)
          ? project.importSeparated(source)
          : project.importAudio(source);
        lastClipId = hasVideo(source)
          ? separated.videoTrack.clips[0]!.id
          : separated.audioTrack.clips[0]!.id;
        if (!hasVideo(source)) importedAudio++;
        if (probe.media.audioStreamIndex !== null) {
          try {
            timeline.setWaveform(probe.media.path, await extractWaveform(prepared.data));
          } catch {
            // 音频解码失败不阻断编辑，轨道仍可正常裁剪与绑定。
          }
        }
        imported++;
        status(`已导入 ${imported} 个素材…`);
        if (!capabilities && hasVideo(probe.media)) {
          capabilities = await detectCapabilities({
            width: probe.media.width,
            height: probe.media.height,
            frameRate: probe.media.frameRate,
          });
          applyCapabilities();
        }
      } catch (error) {
        if (error instanceof DOMException && error.name === 'AbortError') {
          cancelled = true;
          break;
        }
        const message = error instanceof Error ? error.message : String(error);
        failures.push(message.includes(file.name) ? message : `${file.name}：${message}`);
      }
    }
  } finally {
    setBusy(false);
  }

  if (cancelled) {
    status(imported > 0 ? `已取消导入 · 已保留 ${imported} 个素材` : '已取消导入');
    refresh();
    return;
  }

  if (lastClipId) {
    const found = project.findClip(lastClipId);
    if (found) activatePreview(found, false);
  }
  status(imported > 0
    ? `已导入 ${imported} 个素材${importedAudio > 0 ? ` · ${importedAudio} 个普通音频已放入空视频轨的伴生轨` : ' · 有声音的素材已自动分轨'}`
    : '未导入任何素材');
  if (failures.length > 0) {
    await showAlert('部分素材未导入', `${failures.join('\n')}\n\n其余素材已保留，可继续编辑。`);
  }
  refresh();
}

function applyCapabilities(): void {
  if (!capabilities) return;
  capabilityText.textContent = describeCapabilities(capabilities);
  const settingsCapability = document.getElementById('settings-capability');
  if (settingsCapability) settingsCapability.textContent = describeCapabilities(capabilities);
  const notes = document.getElementById('settings-notes');
  if (notes) {
    notes.textContent = '';
    for (const note of capabilities.notes) {
      const item = document.createElement('li');
      item.textContent = note;
      notes.append(item);
    }
    if (capabilities.notes.length === 0) {
      const item = document.createElement('li');
      item.textContent = '无需回退：画面与音频均可由 WebCodecs 编码并直接封装。';
      notes.append(item);
    }
  }
  const ffmpegNote = document.getElementById('settings-ffmpeg');
  if (ffmpegNote) {
    ffmpegNote.textContent = capabilities.crossOriginIsolated
      ? '页面已启用跨源隔离，ffmpeg.wasm 可使用多线程核心。'
      : '页面未启用跨源隔离（缺少 COOP/COEP 响应头），ffmpeg.wasm 只能使用单线程核心；托管时请参考 web/public/_headers。';
  }
}

// ---------- 预览播放 ----------

let playing = false;
let mediaReady = false;
let playbackClipId: string | null = null;
let resumeOnOpen = false;
let pendingSeek: number | null = null;
let pendingSeekStarted = 0;
let clockHandle = 0;
let previewZoom = 1;

const MIN_PREVIEW_ZOOM = 0.25;
const MAX_PREVIEW_ZOOM = 4;

/**
 * 把隐藏 video 解码出的当前帧画到普通 canvas，绕过移动浏览器对可见 video
 * 元素的原生播放器接管。画布最多按 2× DPR 绘制，避免 4K 手机产生超大缓冲。
 */
function drawPreviewFrame(): void {
  if (!mediaReady || video.readyState < HTMLMediaElement.HAVE_CURRENT_DATA) return;
  if (video.videoWidth <= 0 || video.videoHeight <= 0) return;
  // 解码器可以连续跨过相邻切口，但可见画布绝不能收到删除区间的帧。
  // requestAnimationFrame 与 timeupdate 都可能在 currentTime 越界后才到达，
  // 因此所有绘制入口都必须重新校验剪辑边界。
  if (pendingSeek !== null || !playbackClipId) return;
  const current = project.findClip(playbackClipId);
  const sourceTime = video.currentTime;
  if (!current || sourceTime < current.clip.start - 0.000001) return;
  const playback = project.advancePlayback(activeTrackId, playbackClipId, sourceTime);
  if (!playback || playback.requiresSeek || playback.reachedEnd) return;
  const bounds = previewCanvas.getBoundingClientRect();
  if (bounds.width <= 0 || bounds.height <= 0) return;

  const density = Math.min(window.devicePixelRatio || 1, 2);
  const width = Math.max(2, Math.round(bounds.width * density));
  const height = Math.max(2, Math.round(bounds.height * density));
  if (previewFrame.width !== width || previewFrame.height !== height) {
    previewFrame.width = width;
    previewFrame.height = height;
  }

  const context = previewFrame.getContext('2d', { alpha: false });
  if (!context) return;
  context.fillStyle = getComputedStyle(previewCanvas).backgroundColor || '#171717';
  context.fillRect(0, 0, width, height);
  const scale = Math.min(width / video.videoWidth, height / video.videoHeight);
  const drawWidth = Math.max(1, Math.round(video.videoWidth * scale));
  const drawHeight = Math.max(1, Math.round(video.videoHeight * scale));
  const x = Math.floor((width - drawWidth) / 2);
  const y = Math.floor((height - drawHeight) / 2);
  try {
    context.drawImage(video, x, y, drawWidth, drawHeight);
  } catch {
    // 解码器刚切换素材时可能尚无可绘制帧，下一次 seek/tick 会重试。
  }
}

/** 1× 始终表示 content-fit；滚轮与双指手势都在这个基准上缩放画面。 */
function setPreviewZoom(value: number, announce = true): void {
  const next = Math.min(Math.max(value, MIN_PREVIEW_ZOOM), MAX_PREVIEW_ZOOM);
  if (!Number.isFinite(next)) return;
  previewZoom = next;
  const percent = Math.round(previewZoom * 100);
  previewCanvas.style.setProperty('--preview-zoom', String(previewZoom));
  previewCanvas.dataset.zoom = String(previewZoom);
  previewCanvas.title = `预览 ${percent}% · 滚轮或双指缩放 · 双击恢复适应`;
  if (announce) status(previewZoom === 1 ? '预览已恢复适应' : `预览缩放 ${percent}%`);
  scheduleSessionSave();
}

setPreviewZoom(1, false);

function activatePreview(found: ClipPosition, play: boolean, forceReload = false): void {
  if (selectedTrackId !== found.trackId) selectedTrackId = null;
  activeTrackId = found.trackId;
  playbackClipId = found.clip.id;
  selectedClipId = selectedTrackId === null ? found.clip.id : null;
  position = found.timelineStart + (found.sourceTime - found.clip.start) / found.clip.speed;
  playing = resumeOnOpen = play;

  const media = found.clip.media;
  // 伴生音频槽用于对齐与编辑；普通音频也复用同一个隐藏媒体时钟。
  video.muted = false;
  const needsSource = video.dataset.source !== media.path || forceReload;
  if (needsSource) {
    video.dataset.source = media.path;
    mediaReady = false;
    pendingSeek = null;
    video.src = objectUrl(media);
    video.load();
  } else if (mediaReady) {
    video.playbackRate = clampPlaybackRate(found.clip.speed);
    setPreviewPosition(Math.min(found.sourceTime, Math.max(found.clip.start, found.clip.end - 1 / media.frameRate)));
    if (play) startClock();
    else pause();
  }
  syncPlayButton();
  refresh();
}

/** 浏览器对 playbackRate 有范围限制，超出时按可用上限播放并提示。 */
function clampPlaybackRate(speed: number): number {
  const min = 0.0625;
  const max = 16;
  return Math.min(Math.max(speed, min), max);
}

function setPreviewPosition(sourceTime: number): void {
  pendingSeek = sourceTime;
  pendingSeekStarted = performance.now();
  try {
    video.currentTime = Math.max(0, sourceTime);
  } catch {
    /* 元数据未就绪时忽略，MediaOpened 会再设置一次。 */
  }
}

function startClock(): void {
  playing = true;
  void video.play().catch((error: unknown) => {
    playing = false;
    syncPlayButton();
    status(`无法播放：${error instanceof Error ? error.message : String(error)}`);
  });
  if (!clockHandle) clockHandle = requestAnimationFrame(tick);
  syncPlayButton();
}

function pause(): void {
  playing = resumeOnOpen = false;
  video.pause();
  if (clockHandle) {
    cancelAnimationFrame(clockHandle);
    clockHandle = 0;
  }
  syncPlayButton();
}

function syncPlayButton(): void {
  playIcon.setAttribute('href', `./remixicon.svg#ri-${playing ? 'pause' : 'play'}-fill`);
  playButton.disabled = operation !== null || !mediaReady || activeTrack().clips.length === 0;
}

function tick(): void {
  clockHandle = 0;
  if (!playing || !mediaReady || !playbackClipId) return;

  const sourceTime = video.currentTime;
  if (pendingSeek !== null) {
    const clip = project.findClip(playbackClipId);
    const frame = clip ? 1 / clip.clip.media.frameRate : 1 / 30;
    // seek 未落位前不要推进，否则会误判片段结束。
    if (Math.abs(sourceTime - pendingSeek) > Math.max(0.12, frame * 2) && performance.now() - pendingSeekStarted < 2000) {
      clockHandle = requestAnimationFrame(tick);
      return;
    }
    pendingSeek = null;
  }

  const advance = project.advancePlayback(activeTrackId, playbackClipId, sourceTime);
  if (!advance) {
    pause();
    return;
  }
  const found = advance.position;
  if (advance.requiresSeek) {
    activatePreview(found, true);
    return;
  }
  const changed = playbackClipId !== found.clip.id;
  playbackClipId = found.clip.id;
  position = found.timelineStart + (found.sourceTime - found.clip.start) / found.clip.speed;
  if (changed) {
    selectedClipId = selectedTrackId === null ? found.clip.id : null;
    video.playbackRate = clampPlaybackRate(found.clip.speed);
    refresh();
  }
  if (advance.reachedEnd) {
    pause();
    if (found.trackId === activeTrackId) {
      position = trackDuration(project.findTrack(activeTrackId)!);
    }
    // 回调排队期间媒体时钟可能已经越过切点；除冻结可见画布外，也把隐藏
    // 解码器退回最后一个保留帧，避免内部播放位置停在已删除区间。
    setPreviewPosition(Math.max(found.clip.start, found.clip.end - 1 / found.clip.media.frameRate));
  } else {
    // 当前源时间通过剪辑模型校验后才能绘制，避免延迟回调先闪出删除帧。
    drawPreviewFrame();
  }
  refreshPosition();
  if (playing) clockHandle = requestAnimationFrame(tick);
}

video.addEventListener('loadeddata', () => {
  if (!playbackClipId) return;
  const found = project.findClip(playbackClipId);
  if (!found) return;
  mediaReady = true;
  video.playbackRate = clampPlaybackRate(found.clip.speed);
  setPreviewPosition(Math.min(found.sourceTime, Math.max(found.clip.start, found.clip.end - 1 / found.clip.media.frameRate)));
  drawPreviewFrame();
  if (playing || resumeOnOpen) startClock();
  else video.pause();
  resumeOnOpen = false;
  syncPlayButton();
  refresh();
});

video.addEventListener('seeked', () => {
  // seek 期间解码器可能短暂暴露旧帧；浏览器确认落位前保持画布不变。
  pendingSeek = null;
  drawPreviewFrame();
});
video.addEventListener('timeupdate', drawPreviewFrame);

video.addEventListener('ended', () => {
  if (!playing || !playbackClipId) return;
  const found = project.findClip(playbackClipId);
  if (!found) return;
  const track = project.findTrack(found.trackId);
  if (track && found.index + 1 < track.clips.length) {
    const next = track.clips[found.index + 1]!;
    activatePreview(project.findClip(next.id)!, true);
  } else {
    pause();
    position = trackDuration(track!);
    refreshPosition();
  }
});

video.addEventListener('error', () => {
  if (!video.dataset.source) return;
  mediaReady = false;
  status('当前素材无法在此浏览器中播放，但仍可剪辑与导出。');
  refresh();
});

function seek(trackId: string, time: number): void {
  if (operation) return;
  if (selectedTrackId !== trackId) selectedTrackId = null;
  const found = project.locate(trackId, time);
  if (found) activatePreview(found, false);
  else {
    pause();
    activeTrackId = trackId;
    position = 0;
    selectedClipId = playbackClipId = null;
    refresh();
  }
}

function selectClip(clipId: string, time: number): void {
  if (operation) return;
  const found = project.findClip(clipId);
  if (!found) return;
  selectedTrackId = null;
  const clip = found.clip;
  const source = Math.min(
    Math.max(clip.start + (time - found.timelineStart) * clip.speed, clip.start),
    Math.max(clip.start, clip.end - 1 / clip.media.frameRate),
  );
  activatePreview({ ...found, sourceTime: source }, playing);
}

function selectClipForContextMenu(clipId: string): boolean {
  if (operation || multiSelectMode || !project.findClip(clipId)) return false;
  selectedTrackId = null;
  selectedClipId = clipId;
  refresh();
  return true;
}

function selectTrack(trackId: string, time: number): void {
  if (operation || !project.findTrack(trackId)) return;
  selectedTrackId = trackId;
  seek(trackId, time);
  status('已选中整条轨道');
}

function toggleMultiSelectMode(): void {
  if (operation) return;
  multiSelectMode = !multiSelectMode;
  multiSelectedTrackIds.clear();
  if (multiSelectMode) {
    pause();
    selectedClipId = null;
    selectedTrackId = null;
    status('多选模式：点击需要同步分割的轨道，然后保存并对齐');
  } else {
    status('已退出多选模式');
  }
  refresh();
}

function toggleMultiSelectedTrack(trackId: string): void {
  if (!multiSelectMode || operation) return;
  const track = project.findTrack(trackId);
  if (!track || track.clips.length === 0) return;
  if (multiSelectedTrackIds.has(trackId)) multiSelectedTrackIds.delete(trackId);
  else multiSelectedTrackIds.add(trackId);
  status(`已选择 ${multiSelectedTrackIds.size} 条轨道${multiSelectedTrackIds.size < 2 ? '，至少选择两条' : '，可以保存并对齐'}`);
  refresh();
}

function saveTrackBinding(): void {
  if (!multiSelectMode || operation) return;
  const ids = [...multiSelectedTrackIds];
  if (!project.bindTracks(ids)) {
    status('请至少选择两条包含片段的轨道。');
    return;
  }
  multiSelectMode = false;
  multiSelectedTrackIds.clear();
  status(`已进入对齐模式：${ids.length} 条轨道的分割会同步，删除仍只影响当前片段`);
  refresh();
}

function unbindSelectedTracks(): void {
  if (!multiSelectMode || operation) return;
  if (!project.unbindTracks([...multiSelectedTrackIds])) return;
  multiSelectMode = false;
  multiSelectedTrackIds.clear();
  status('已解除所选轨道的绑定');
  refresh();
}

function togglePlay(): void {
  if (operation) return;
  if (playing) {
    pause();
    return;
  }
  if (!mediaReady || activeTrack().clips.length === 0) return;
  const duration = trackDuration(activeTrack());
  const time = position >= duration - frameDuration() / 2 ? 0 : position;
  const found = project.locate(activeTrackId, time);
  if (found) activatePreview(found, true);
}

// ---------- 编辑动作 ----------

function split(): void {
  if (operation || multiSelectMode) return;
  if (playing) tick();
  const synchronized = project.synchronizedTracks(activeTrackId)
    .filter((track) => track.clips.length > 0).length;
  const id = project.split(activeTrackId, position);
  if (!id) {
    status(synchronized > 1
      ? '关联轨道无法在此时间点同时分割，请检查有内容的伴生音轨和对齐轨是否覆盖该位置。'
      : '请将播放头移到当前轨道的片段内部再分割。');
    return;
  }
  selectedTrackId = null;
  selectedClipId = id;
  if (playing) {
    const found = project.locate(activeTrackId, position);
    selectedClipId = playbackClipId = found?.clip.id ?? id;
  } else {
    activatePreview(project.findClip(id)!, false);
  }
  status(synchronized > 1 ? `已同步分割 ${synchronized} 条关联轨道` : '已分割片段');
  refresh();
}

function deleteSelected(): void {
  if (operation || multiSelectMode || !selectedClipId) return;
  const found = project.findClip(selectedClipId);
  if (!found) return;
  pause();
  if (!project.delete(selectedClipId)) return;
  const next = position >= found.timelineStart
    ? Math.max(found.timelineStart, position - clipDuration(found.clip))
    : position;
  const remainingTrack = project.findTrack(found.trackId);
  const targetTrack = remainingTrack && remainingTrack.clips.length > 0
    ? remainingTrack
    : project.mainTrack;
  seek(targetTrack.id, Math.min(next, trackDuration(targetTrack)));
  status('已删除片段并收拢间隙');
  refresh();
}

function moveClip(clipId: string, trackId: string, index: number): void {
  if (operation) return;
  const before = project.findClip(clipId);
  if (!before) return;
  const source = selectedClipId === clipId
    ? project.locate(activeTrackId, position)?.sourceTime ?? before.clip.start
    : before.clip.start;
  pause();
  if (!project.move(clipId, trackId, index)) return;
  const moved = project.findClip(clipId)!;
  activatePreview(
    { ...moved, sourceTime: Math.min(Math.max(source, moved.clip.start), moved.clip.end) },
    false,
  );
  status('已移动片段');
}

function moveTrack(trackId: string, index: number): void {
  if (operation || multiSelectMode) return;
  if (!project.moveTrack(trackId, index)) return;
  status('已调整音视频轨道组顺序');
  refresh();
}

function restore(redo: boolean): void {
  if (operation) return;
  pause();
  const changed = redo ? project.redo() : project.undo();
  if (!changed) return;
  const target = (selectedClipId ? project.findClip(selectedClipId) : undefined)
    ?? project.locate(activeTrackId, position)
    ?? project.allTracks
      .filter((track) => track.clips.length > 0)
      .map((track) => project.findClip(track.clips[0]!.id))
      .find((found) => found !== undefined);
  if (target) activatePreview(target, false);
  else {
    selectedClipId = playbackClipId = null;
    activeTrackId = project.mainTrack.id;
    position = 0;
  }
  status(redo ? '已重做上一步操作' : '已撤销上一步操作');
  refresh();
}

function duplicateSelected(): void {
  if (operation || !selectedClipId) return;
  pause();
  const copyId = project.duplicate(selectedClipId);
  if (!copyId) return;
  const copy = project.findClip(copyId)!;
  activatePreview(copy, false);
  status(clipDuration(copy.clip) < 10 ? '已复制片段并插入原片段后方' : '已复制片段到下方');
  refresh();
}

function renameSelected(): void {
  if (operation || !selectedClipId) return;
  const found = project.findClip(selectedClipId);
  if (!found) return;
  pause();
  const name = window.prompt('片段名称', displayName(found.clip));
  if (name && project.rename(selectedClipId, name)) {
    status(`片段已命名为“${name}”`);
    refresh();
  }
}

function setSelectedSpeed(speed: number): void {
  if (operation || multiSelectMode || !selectedClipId || !Number.isFinite(speed)
    || speed < MINIMUM_SPEED || speed > MAXIMUM_SPEED) return;
  const cursor = project.locate(activeTrackId, position);
  if (!project.setSpeed(selectedClipId, speed)) {
    closeClipContextMenu();
    return;
  }
  if (cursor) {
    const current = project.findClip(cursor.clip.id);
    if (current) {
      const sourceTime = Math.min(Math.max(cursor.sourceTime, current.clip.start), current.clip.end);
      position = current.timelineStart + (sourceTime - current.clip.start) / current.clip.speed;
      if (mediaReady && playbackClipId === current.clip.id) {
        video.playbackRate = clampPlaybackRate(current.clip.speed);
      }
    }
  }
  closeClipContextMenu();
  status(`片段倍速已设为 ${speed.toFixed(3).replace(/\.?0+$/, '')}× · 可撤销`);
  refresh();
}

function closeClipContextMenu(): void {
  clipContextMenu.hidden = true;
}

function openClipContextMenu(clipId: string | null, clientX: number, clientY: number): void {
  closeClipContextMenu();
  if (!clipId || !selectClipForContextMenu(clipId)) return;
  const found = project.findClip(clipId);
  if (!found) return;
  const speed = found.clip.speed;
  clipSpeedCurrent.textContent = `${speed.toFixed(3).replace(/\.?0+$/, '')}×`;
  for (const button of speedPresetButtons) {
    const selected = Math.abs(Number(button.dataset.speed) - speed) < 0.000001;
    button.setAttribute('aria-checked', String(selected));
  }
  clipContextMenu.hidden = false;
  const bounds = clipContextMenu.getBoundingClientRect();
  clipContextMenu.style.left = `${Math.max(8, Math.min(clientX, window.innerWidth - bounds.width - 8))}px`;
  clipContextMenu.style.top = `${Math.max(8, Math.min(clientY, window.innerHeight - bounds.height - 8))}px`;
  (speedPresetButtons.find((button) => button.getAttribute('aria-checked') === 'true') ?? speedPresetButtons[0])
    ?.focus({ preventScroll: true });
}

function openCustomSpeedDialog(): void {
  if (!selectedClipId) return;
  const found = project.findClip(selectedClipId);
  if (!found) return;
  closeClipContextMenu();
  speedInput.value = found.clip.speed.toFixed(3).replace(/\.?0+$/, '');
  speedValidation.hidden = true;
  speedDialog.showModal();
  queueMicrotask(() => {
    speedInput.focus({ preventScroll: true });
    speedInput.select();
  });
}

// ---------- 刷新 ----------

function refresh(): void {
  const ready = operation === null;
  const hasMedia = project.sources.length > 0;
  if (selectedTrackId && !project.findTrack(selectedTrackId)) selectedTrackId = null;
  for (const id of multiSelectedTrackIds) if (!project.findTrack(id)) multiSelectedTrackIds.delete(id);

  timelineRegion.hidden = !hasMedia;
  previewFooter.hidden = !hasMedia;
  previewRegion.classList.toggle('is-empty', !hasMedia);
  exportButton.disabled = !ready || project.exportableTracks.length === 0;
  importButton.hidden = !hasMedia;
  moreButton.disabled = !ready;
  saveProjectButton.disabled = !ready || !hasMedia;
  openProjectButton.disabled = !ready;
  emptyState.hidden = hasMedia;

  const track = activeTrack();
  const any = track.clips.length > 0;
  const activeMedia = project.locate(track.id, position)?.clip.media ?? track.clips[0]?.media;
  const audioOnly = activeMedia !== undefined && !hasVideo(activeMedia);
  splitButton.disabled = !(ready && any && !multiSelectMode);
  deleteButton.disabled = duplicateButton.disabled = renameButton.disabled =
    !(ready && selectedClipId !== null && project.findClip(selectedClipId) !== undefined);
  undoButton.disabled = !(ready && project.canUndo);
  redoButton.disabled = !(ready && project.canRedo);
  multiSelectButton.disabled = !ready || project.allTracks.every((candidate) => candidate.clips.length === 0);
  multiSelectButton.setAttribute('aria-pressed', String(multiSelectMode));
  multiSelectButton.textContent = multiSelectMode ? '退出多选' : '多选轨道';
  bindTracksButton.hidden = unbindTracksButton.hidden = !multiSelectMode;
  bindTracksButton.disabled = !ready || multiSelectedTrackIds.size < 2;
  unbindTracksButton.disabled = !ready
    || ![...multiSelectedTrackIds].some((id) => project.findTrack(id)?.bindingId);
  timelineRegion.classList.toggle('is-multi-select', multiSelectMode);
  stepBackButton.disabled = stepForwardButton.disabled = !(ready && any);
  syncPlayButton();

  previewFrame.hidden = audioOnly || !(any && mediaReady);
  audioPreview.hidden = !audioOnly;
  audioPreviewName.textContent = audioOnly ? fileName(activeMedia) : '';
  previewRegion.classList.toggle('is-audio-only', audioOnly);

  timeline.activeTrackId = activeTrackId;
  timeline.selectedTrackId = selectedTrackId;
  timeline.selectedClipId = selectedClipId;
  timeline.multiSelectMode = multiSelectMode;
  timeline.multiSelectedTrackIds = multiSelectedTrackIds;
  timeline.position = position;
  timeline.resize();

  const clips = track ? track.clips.length : 0;
  const seconds = track ? trackDuration(track) : 0;
  const bound = project.bindingTracks(track.id).length;
  const trackType = track.kind === 'audio'
    ? (track.companionGroupId ? '伴生音频槽' : '音频轨')
    : (track.companionGroupId ? '视频轨 · 含伴生音频' : '视频轨');
  timelineSummary.textContent = `${trackType} · ${clips} 片段 · ${seconds.toFixed(2)} 秒${bound > 1 ? ` · 对齐组 ${bound} 轨` : ''}`;
  document.title = hasMedia ? `${project.sources.length} 个素材 — 视频剪辑` : 'Clip · 视频剪辑';
  refreshPosition();
  scheduleSessionSave();
}

function refreshPosition(): void {
  timeline.position = position;
  timeline.draw();
  positionLabel.textContent = formatTime(position);
  totalLabel.textContent = `/ ${formatTime(trackDuration(activeTrack()))}`;
}

// ---------- 导出 ----------

interface ExportWritable {
  write: (data: Uint8Array) => Promise<void>;
  close: () => Promise<void>;
}

interface ExportFileHandle {
  createWritable: () => Promise<ExportWritable>;
}

interface ExportDestination {
  readonly suggestedName: string;
  readonly handle: ExportFileHandle | null;
}

const suggestedExportName = (clips: readonly VideoClip[]): string =>
  `${(clips[0]?.media.path ?? 'clip').replace(/\.[^.]+$/, '')}_clip.mp4`;

/**
 * 文件选择器必须在用户点击事件仍处于激活状态时调用。不能等编码完成后再打开，
 * 否则 Chromium 会以“Must be handling a user gesture”拒绝请求。
 */
async function chooseExportDestination(
  suggestedName: string,
): Promise<ExportDestination | 'cancelled'> {
  // 移动浏览器可能暴露 showSaveFilePicker，却无法可靠显示系统保存面板，
  // Promise 会一直停在 pending，表现为导出对话框不关闭、按钮置灰。
  // 触控优先设备直接在编码完成后使用浏览器下载，避免把导出阻塞在这里。
  if (window.matchMedia('(pointer: coarse)').matches) {
    return { suggestedName, handle: null };
  }

  const picker = (window as unknown as {
    showSaveFilePicker?: (options: unknown) => Promise<ExportFileHandle>;
  }).showSaveFilePicker;
  if (typeof picker !== 'function') return { suggestedName, handle: null };

  try {
    // 此调用必须是函数中的第一个异步边界，并由“开始导出”的 click 直接触发。
    const handle = await picker.call(window, {
      suggestedName,
      types: [{ description: 'MP4 视频', accept: { 'video/mp4': ['.mp4'] } }],
    });
    return { suggestedName, handle };
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') return 'cancelled';
    // 部分移动浏览器暴露了不完整的 API；此时仍可退回普通下载。
    console.warn('无法打开保存位置选择器，改用浏览器下载。', error);
    return { suggestedName, handle: null };
  }
}

function openExportDialog(): Promise<void> {
  return new Promise((resolve) => {
    const quality = $<HTMLSelectElement>('quality');
    const size = $<HTMLSelectElement>('size');
    const encoder = $<HTMLSelectElement>('encoder');
    const widthInput = $<HTMLInputElement>('width');
    const heightInput = $<HTMLInputElement>('height');
    const dimensionsField = $('dimensions-field');
    const sizeHint = $('size-hint');
    const qualityHint = $('quality-hint');
    const encoderHint = $('encoder-hint');
    const routeCallout = $('route-callout');
    const validation = $('validation-text');

    const first = project.exportableTracks[0]?.clips[0]?.media ?? project.sources[0]!;
    const selected = project.resolveExportTrack(selectedTrackId) ?? project.exportableTracks[0]!;
    const media = selected.clips[0]!.media;
    void first;

    quality.value = 'original';
    qualityHint.textContent = '原画按所选轨道首个素材的分辨率进行高质量重编码，并非无损复制。';
    size.value = 'source';
    widthInput.value = String(media.width);
    heightInput.value = String(media.height);

    encoder.textContent = '';
    const routes: { value: string; label: string }[] = [];
    if (capabilities?.webCodecsVideo) {
      routes.push({
        value: EncoderRoute.WebCodecsVideo,
        label: `WebCodecs H.264${capabilities.aacEncoder ? ' + AAC' : capabilities.opusEncoder ? ' + Opus' : ''}${capabilities.h264Hardware ? ' · 硬件优先' : ' · 平台软件编码'}`,
      });
    }
    routes.push({ value: EncoderRoute.Wasm, label: 'FFmpeg.wasm · 与桌面端一致的软件编码' });
    for (const item of routes) {
      const option = document.createElement('option');
      option.value = item.value;
      option.textContent = item.label;
      encoder.append(option);
    }
    const preferred = chooseRoute(capabilities ?? emptyCapabilities(), route);
    encoder.value = routes.some((item) => item.value === preferred) ? preferred : routes[0]!.value;

    const describeRoute = (): string => {
      if (encoder.value === EncoderRoute.WebCodecsVideo) {
        if (capabilities?.aacEncoder || capabilities?.opusEncoder) {
          const audioCodec = capabilities.aacEncoder ? 'AAC' : 'Opus';
          return `画面与 ${audioCodec} 音频均由 WebCodecs 编码，再直接封装为 MP4；音频会保持音高变速。`;
        }
        return '画面由 WebCodecs 编码；此浏览器缺少 AAC 与 Opus 编码能力，音频会自动回退到 ffmpeg.wasm。';
      }
      return '全部交给 ffmpeg.wasm 软件编码，与桌面端结果最接近，但速度明显更慢。';
    };

    const validate = (): ExportOptions | null => {
      try {
        let width: number | null = null;
        let height: number | null = null;
        if (size.value === '1080p') { width = 1920; height = 1080; }
        else if (size.value === '720p') { width = 1280; height = 720; }
        else if (size.value === '4k') { width = 3840; height = 2160; }
        else if (size.value === 'custom') {
          width = Number(widthInput.value);
          height = Number(heightInput.value);
          if (!Number.isFinite(width) || !Number.isFinite(height)) throw new Error('请输入有效的宽高。');
        }
        const options: ExportOptions = {
          ...defaultExportOptions,
          quality: quality.value as ExportQuality,
          width,
          height,
          encoder: encoder.value === EncoderRoute.Wasm ? VideoEncoder.Software : VideoEncoder.WebCodecs,
          hardwareDecode: encoder.value === EncoderRoute.WebCodecsVideo,
        };
        // 复用与渲染同一套校验逻辑。
        const dimensions = dimensionsOf(options, media);
        sizeHint.textContent = `输出 ${dimensions.width} × ${dimensions.height}；比例不一致时补黑边。`;
        validation.hidden = true;
        return options;
      } catch (error) {
        validation.hidden = false;
        validation.textContent = error instanceof Error ? error.message : String(error);
        return null;
      }
    };

    const update = (): void => {
      dimensionsField.hidden = size.value !== 'custom';
      routeCallout.textContent = `${describeRoute()}\n素材：${media.path}（${selected.clips.length} 个片段）`;
      encoderHint.textContent = capabilities?.webCodecsVideo
        ? 'WebCodecs 不受硬件解码开关影响；具体素材无法由浏览器解码时，仅音频部分会自动回退。'
        : '此浏览器不支持 H.264 的 WebCodecs 编码，因此只有 ffmpeg.wasm 可用。';
      validate();
    };

    quality.addEventListener('change', update);
    size.addEventListener('change', update);
    encoder.addEventListener('change', update);
    widthInput.addEventListener('input', update);
    heightInput.addEventListener('input', update);

    const confirmButton = $<HTMLButtonElement>('export-confirm');
    // 该按钮在选择保存位置期间会被禁用。导出任务结束后对话框节点会被复用，
    // 因此每次打开时都要恢复状态，否则一次失败会让后续导出无法开始。
    confirmButton.disabled = false;
    const cleanup = (): void => {
      confirmButton.removeEventListener('click', onConfirm);
      $('export-cancel').removeEventListener('click', onCancel);
      exportDialog.removeEventListener('close', onCancel);
    };
    const onConfirm = async (): Promise<void> => {
      const options = validate();
      if (!options) return;
      const chosen = encoder.value as EncoderRoute;
      confirmButton.disabled = true;
      const destination = await chooseExportDestination(suggestedExportName(selected.clips));
      if (destination === 'cancelled') {
        confirmButton.disabled = false;
        return;
      }
      cleanup();
      exportDialog.close();
      void runExport(selected.clips, options, chosen, destination);
      resolve();
    };
    const onCancel = (): void => {
      cleanup();
      if (exportDialog.open) exportDialog.close();
      resolve();
    };

    confirmButton.addEventListener('click', onConfirm);
    $('export-cancel').addEventListener('click', onCancel);
    exportDialog.addEventListener('close', onCancel);
    update();
    exportDialog.showModal();
  });
}

/** 与 model.getDimensions 等价，但允许在此处直接复用。 */
function dimensionsOf(options: ExportOptions, media: MediaInfo): { width: number; height: number } {
  const width = options.width ?? media.width;
  const height = options.height ?? media.height;
  if (width < 2 || height < 2 || width > 7680 || height > 7680) {
    throw new Error('输出宽高必须在 2–7680 像素之间。');
  }
  if (options.width !== null && (width % 2 !== 0 || height % 2 !== 0)) {
    throw new Error('H.264 输出宽高必须为偶数。');
  }
  return { width: width + (width % 2), height: height + (height % 2) };
}

const emptyCapabilities = (): Capabilities => ({
  webCodecsVideo: false,
  webCodecsAudio: false,
  sharedArrayBuffer: sharedArrayBufferAvailable(),
  crossOriginIsolated: globalThis.crossOriginIsolated === true,
  h264Codec: null,
  h264Hardware: false,
  aacEncoder: false,
  opusEncoder: false,
  notes: [],
});

async function runExport(
  clips: readonly VideoClip[],
  options: ExportOptions,
  chosen: EncoderRoute,
  destination: ExportDestination,
): Promise<void> {
  if (operation) return;
  pause();
  const controller = beginOperation('准备导出…');
  try {
    const result = await exportClips({
      clips,
      options,
      route: chosen,
      capabilities: capabilities ?? emptyCapabilities(),
      ffmpegBase: new URL('ffmpeg/', document.baseURI).href,
      readSource: async (path) => {
        const file = files.get(path);
        if (!file) throw new Error(`找不到素材 ${path}`);
        return file.arrayBuffer();
      },
      onProgress: (update: ExportProgress) => {
        progress.value = update.fraction;
        status(`${update.message} ${(update.fraction * 100).toFixed(0)}%`);
      },
      signal: controller.signal,
    });

    await saveBlob(result.data, destination);
    const routeLabel = result.route === EncoderRoute.WebCodecsVideo
      ? `WebCodecs（音频：${result.audioRoute === 'webcodecs' ? `WebCodecs ${result.audioCodec === 'opus' ? 'Opus' : 'AAC'}` : result.audioRoute === 'none' ? '无' : 'FFmpeg.wasm AAC'}）`
      : 'FFmpeg.wasm';
    const notes = [result.fellBack ? `已从 WebCodecs 回退：${result.fellBack}` : '', result.threading ?? '']
      .filter((note) => note.length > 0)
      .join('；');
    status(`导出完成 · ${result.width}×${result.height} · ${routeLabel}${notes ? `（${notes}）` : ''}`);
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      status('已取消导出');
    } else {
      pause();
      await showAlert('导出未完成', error instanceof Error ? error.message : String(error));
      status('导出失败');
    }
  } finally {
    setBusy(false);
  }
}

/** 写入用户在导出前选好的位置；不支持文件系统 API 时退回浏览器下载。 */
async function saveBlob(data: Uint8Array, destination: ExportDestination): Promise<void> {
  if (destination.handle) {
    const writable = await destination.handle.createWritable();
    await writable.write(data);
    await writable.close();
    return;
  }

  const blob = new Blob([data as unknown as BlobPart], { type: 'video/mp4' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = destination.suggestedName;
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
  // 交给浏览器处理下载后再释放。
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

function showAlert(title: string, message: string): Promise<void> {
  return new Promise((resolve) => {
    const dialog = document.createElement('dialog');
    dialog.innerHTML = `
      <form method="dialog">
        <div class="dialog-header"><h2></h2></div>
        <div class="dialog-body"><div class="callout warn"></div></div>
        <div class="dialog-footer">
          <span></span>
          <span class="actions"><button class="btn btn-primary" value="ok">知道了</button></span>
        </div>
      </form>`;
    dialog.querySelector('h2')!.textContent = title;
    dialog.querySelector('.callout')!.textContent = message;
    dialog.addEventListener('close', () => {
      dialog.remove();
      resolve();
    });
    document.body.append(dialog);
    dialog.showModal();
  });
}

function exportProjectFile(): void {
  if (operation) return;
  const snapshot = createSessionSnapshot();
  if (!snapshot) {
    void showAlert('无法导出项目', '请先导入素材；素材仍在处理中时，请稍后再试。');
    return;
  }
  const stamp = new Date().toISOString().slice(0, 16).replace(/[-:T]/g, '');
  const name = `Clip-${stamp}.clip`;
  const blob = new Blob([serializeProjectFile(snapshot)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = name;
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
  status(`项目已导出 · ${name}（不包含原始素材）`);
}

async function openProjectFile(file: File): Promise<void> {
  if (operation) return;
  try {
    const stored = validateRecoveryCandidate(parseProjectFile(await file.text()));
    pause();
    pendingRecovery = stored;
    recoveryOrigin = 'file';
    recoveryFiles.clear();
    recoveryFingerprints.clear();
    recoveryWaveforms.clear();
    offerRecovery();
  } catch (error) {
    await showAlert('无法导入项目', error instanceof Error ? error.message : String(error));
  }
}

// ---------- 刷新恢复 ----------

function missingRecoverySources(): readonly SourceFingerprint[] {
  return pendingRecovery?.sources.filter((source) => !recoveryFiles.has(source.path)) ?? [];
}

function renderRecoverySources(): void {
  if (!pendingRecovery) return;
  recoveryFileList.textContent = '';
  for (const source of pendingRecovery.sources) {
    const ready = recoveryFiles.has(source.path);
    const item = document.createElement('li');
    item.className = `recovery-file${ready ? ' is-ready' : ''}`;
    const name = document.createElement('span');
    name.className = 'recovery-file-name';
    name.textContent = source.name;
    name.title = source.name;
    const state = document.createElement('span');
    state.className = 'recovery-file-state';
    state.textContent = ready ? '已核对' : '等待选择';
    item.append(name, state);
    recoveryFileList.append(item);
  }
  const missing = missingRecoverySources().length;
  recoveryChooseLabel.textContent = missing === pendingRecovery.sources.length
    ? '重新选择原始素材'
    : `继续选择（还差 ${missing} 个）`;
}

function finishRecovery(): void {
  const stored = pendingRecovery;
  if (!stored) return;
  try {
    const restored = EditProject.fromSnapshot(stored.project);
    for (const source of restored.sources) {
      const file = recoveryFiles.get(source.path);
      if (!file) throw new Error(`还没有选择 ${source.path}`);
    }

    pause();
    for (const url of objectUrls.values()) URL.revokeObjectURL(url);
    objectUrls.clear();
    files.clear();
    sourceFingerprints.clear();
    timeline.clearWaveforms();
    for (const source of restored.sources) {
      files.set(source.path, recoveryFiles.get(source.path)!);
      const expected = stored.sources.find((candidate) => candidate.path === source.path)!;
      sourceFingerprints.set(source.path, recoveryFingerprints.get(source.path) ?? expected);
      const waveform = recoveryWaveforms.get(source.path);
      if (waveform) timeline.setWaveform(source.path, waveform);
    }

    project = restored;
    timeline.setProject(project);
    const workspace = stored.workspace;
    activeTrackId = project.findTrack(workspace.activeTrackId)?.id ?? project.mainTrack.id;
    selectedTrackId = workspace.selectedTrackId && project.findTrack(workspace.selectedTrackId)
      ? workspace.selectedTrackId
      : null;
    selectedClipId = workspace.selectedClipId && project.findClip(workspace.selectedClipId)
      ? workspace.selectedClipId
      : null;
    position = Math.min(Math.max(workspace.position, 0), trackDuration(activeTrack()));
    setPreviewZoom(workspace.previewZoom, false);
    timeline.setZoom(workspace.timelineZoom);

    const origin = recoveryOrigin;
    pendingRecovery = null;
    recoveryOrigin = 'session';
    recoveryFiles.clear();
    recoveryFingerprints.clear();
    recoveryWaveforms.clear();
    recoveryDialog.close();
    const found = project.locate(activeTrackId, position);
    if (found) activatePreview(found, false, true);
    else refresh();
    timelineScroll.scrollLeft = Math.max(0, workspace.timelineScrollLeft);
    status(origin === 'file' ? '项目已导入，可以继续工作' : '已恢复上次剪辑，可以继续工作');
    saveSessionNow();
  } catch (error) {
    recoveryError.hidden = false;
    recoveryError.textContent = error instanceof Error ? error.message : String(error);
    recoveryChooseButton.disabled = false;
    recoveryDiscardButton.disabled = false;
  }
}

async function acceptRecoveryFiles(selectedFiles: readonly File[]): Promise<void> {
  if (!pendingRecovery || selectedFiles.length === 0) return;
  recoveryChooseButton.disabled = true;
  recoveryDiscardButton.disabled = true;
  recoveryError.hidden = true;
  const rejected: string[] = [];
  try {
    for (const file of selectedFiles) {
      const candidates = missingRecoverySources().filter((source) => source.size === file.size);
      if (candidates.length === 0) {
        rejected.push(file.name);
        continue;
      }
      const actual = await fingerprintFile(file);
      const exactName = candidates.find((source) => source.name === file.name && sameVideo(actual, source));
      const matched = exactName ?? candidates.find((source) => sameVideo(actual, source));
      if (!matched) {
        rejected.push(file.name);
        continue;
      }
      const data = await file.arrayBuffer();
      const prepared = await prepareMediaFile(file, data, (fraction, message) => {
        recoveryError.hidden = false;
        recoveryError.textContent = `${message} ${(fraction * 100).toFixed(0)}%`;
      });
      recoveryFiles.set(matched.path, prepared.file);
      const media = pendingRecovery.project.sources.find((source) => source.path === matched.path);
      if (media && media.audioStreamIndex !== null) {
        try { recoveryWaveforms.set(matched.path, await extractWaveform(prepared.data)); }
        catch { /* 恢复项目不因浏览器缺少音频解码器而失败。 */ }
      }
      recoveryFingerprints.set(matched.path, { ...actual, path: matched.path });
    }
    renderRecoverySources();
    const missing = missingRecoverySources();
    if (missing.length === 0) {
      finishRecovery();
      return;
    }
    if (rejected.length > 0) {
      recoveryError.hidden = false;
      recoveryError.textContent = `“${rejected.join('”、“')}”与上次使用的素材不一致。请重新选择；已核对的素材会保留。`;
    } else {
      recoveryError.hidden = false;
      recoveryError.textContent = `还需要选择：${missing.map((source) => source.name).join('、')}`;
    }
  } catch (error) {
    recoveryError.hidden = false;
    recoveryError.textContent = `无法核对所选素材：${error instanceof Error ? error.message : String(error)}`;
  } finally {
    if (pendingRecovery) {
      recoveryChooseButton.disabled = false;
      recoveryDiscardButton.disabled = false;
    }
  }
}

function offerRecovery(): void {
  if (!pendingRecovery) return;
  const fromFile = recoveryOrigin === 'file';
  recoveryEyebrow.textContent = fromFile ? '导入项目' : '未完成的剪辑';
  recoveryTitle.textContent = fromFile ? '导入这份项目？' : '继续上次的工作？';
  recoveryBadge.textContent = fromFile ? '.clip 文件' : '已自动保存';
  recoveryCopy.textContent = fromFile
    ? '轨道、片段和工作位置已读取。项目不包含素材本体，请重新选择原始素材完成核对。'
    : '剪辑点和工作位置已经找回。出于浏览器隐私限制，还需要重新选择原始素材才能继续。';
  recoveryDiscardButton.textContent = fromFile ? '取消导入' : '放弃上次进度';
  renderRecoverySources();
  $('recovery-saved-at').textContent = `${fromFile ? '项目导出于' : '保存于'} ${new Date(pendingRecovery.savedAt).toLocaleString()}`;
  recoveryError.hidden = true;
  recoveryDialog.showModal();
  queueMicrotask(() => recoveryChooseButton.focus({ preventScroll: true }));
}

// ---------- 设置 ----------

function openSettings(): void {
  applyCapabilities();
  const select = $<HTMLSelectElement>('settings-encoder');
  select.value = route;
  const onClose = (): void => {
    route = select.value as EncoderRoute | 'auto';
    localStorage.setItem('clip.encoder', route);
    settingsDialog.removeEventListener('close', onClose);
  };
  settingsDialog.addEventListener('close', onClose);
  $('settings-close').addEventListener('click', () => settingsDialog.close(), { once: true });
  settingsDialog.showModal();
}

function setMoreMenuOpen(open: boolean, moveFocus = false): void {
  moreMenu.hidden = !open;
  moreButton.setAttribute('aria-expanded', String(open));
  if (open && moveFocus) {
    queueMicrotask(() => moreMenu.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus());
  }
}

function moreMenuItems(): HTMLButtonElement[] {
  return Array.from(moreMenu.querySelectorAll<HTMLButtonElement>('button:not(:disabled)'));
}

// ---------- 事件绑定 ----------

importButton.addEventListener('click', () => fileInput.click());
emptyImportButton.addEventListener('click', () => fileInput.click());
moreButton.addEventListener('click', () => setMoreMenuOpen(moreMenu.hidden, moreMenu.hidden));
moreButton.addEventListener('keydown', (event) => {
  if (event.key !== 'ArrowDown' && event.key !== 'Enter' && event.key !== ' ') return;
  event.preventDefault();
  setMoreMenuOpen(true, true);
});
moreMenu.addEventListener('keydown', (event) => {
  const items = moreMenuItems();
  const current = items.indexOf(document.activeElement as HTMLButtonElement);
  let next = current;
  if (event.key === 'ArrowDown') next = (current + 1) % items.length;
  else if (event.key === 'ArrowUp') next = (current - 1 + items.length) % items.length;
  else if (event.key === 'Home') next = 0;
  else if (event.key === 'End') next = items.length - 1;
  else if (event.key === 'Escape') {
    event.preventDefault();
    setMoreMenuOpen(false);
    moreButton.focus();
    return;
  } else if (event.key === 'Tab') {
    setMoreMenuOpen(false);
    return;
  } else return;
  event.preventDefault();
  items[next]?.focus();
});
document.addEventListener('pointerdown', (event) => {
  if (!moreMenu.hidden && !moreMenuWrap.contains(event.target as Node)) setMoreMenuOpen(false);
  if (!clipContextMenu.hidden && !clipContextMenu.contains(event.target as Node)) closeClipContextMenu();
});
clipContextMenu.addEventListener('keydown', (event) => {
  const items = Array.from(clipContextMenu.querySelectorAll<HTMLButtonElement>('button:not(:disabled)'));
  const current = items.indexOf(document.activeElement as HTMLButtonElement);
  let next = current;
  if (event.key === 'ArrowDown' || event.key === 'ArrowRight') next = (current + 1) % items.length;
  else if (event.key === 'ArrowUp' || event.key === 'ArrowLeft') next = (current - 1 + items.length) % items.length;
  else if (event.key === 'Home') next = 0;
  else if (event.key === 'End') next = items.length - 1;
  else if (event.key === 'Escape') {
    event.preventDefault();
    closeClipContextMenu();
    return;
  } else if (event.key === 'Tab') {
    closeClipContextMenu();
    return;
  } else return;
  event.preventDefault();
  items[next]?.focus({ preventScroll: true });
});
moreSettingsButton.addEventListener('click', () => {
  setMoreMenuOpen(false);
  openSettings();
});
openProjectButton.addEventListener('click', () => {
  setMoreMenuOpen(false);
  moreButton.focus({ preventScroll: true });
  projectFileInput.click();
});
saveProjectButton.addEventListener('click', () => {
  setMoreMenuOpen(false);
  moreButton.focus({ preventScroll: true });
  exportProjectFile();
});
fileInput.addEventListener('change', () => {
  const list = Array.from(fileInput.files ?? []);
  fileInput.value = '';
  if (list.length > 0) void importFiles(list);
});
projectFileInput.addEventListener('change', () => {
  const file = projectFileInput.files?.[0];
  projectFileInput.value = '';
  if (file) void openProjectFile(file);
});
recoveryChooseButton.addEventListener('click', () => recoveryInput.click());
recoveryInput.addEventListener('change', () => {
  const list = Array.from(recoveryInput.files ?? []);
  recoveryInput.value = '';
  if (list.length > 0) void acceptRecoveryFiles(list);
});
function closeDiscardConfirmation(): void {
  if (recoveryDiscardDialog.open) recoveryDiscardDialog.close();
  queueMicrotask(() => {
    if (recoveryDialog.open) recoveryChooseButton.focus({ preventScroll: true });
  });
}

function discardPendingRecovery(): void {
  const fromFile = recoveryOrigin === 'file';
  pendingRecovery = null;
  recoveryOrigin = 'session';
  recoveryFiles.clear();
  recoveryFingerprints.clear();
  recoveryWaveforms.clear();
  if (!fromFile) {
    sourceFingerprints.clear();
    clearStoredSession(sessionStorage);
  }
  recoveryDialog.close();
  status(fromFile ? '已取消导入项目，当前剪辑未改变' : '已放弃上次进度 · 导入素材开始新的剪辑');
}

recoveryDiscardButton.addEventListener('click', () => {
  if (!pendingRecovery || recoveryDiscardDialog.open) return;
  if (recoveryOrigin === 'file') {
    discardPendingRecovery();
    return;
  }
  recoveryDiscardDialog.showModal();
  queueMicrotask(() => recoveryDiscardCancelButton.focus({ preventScroll: true }));
});
recoveryDiscardCancelButton.addEventListener('click', closeDiscardConfirmation);
recoveryDiscardConfirmButton.addEventListener('click', () => {
  if (!pendingRecovery) return;
  if (recoveryDiscardDialog.open) recoveryDiscardDialog.close();
  discardPendingRecovery();
});
recoveryDiscardDialog.addEventListener('cancel', (event) => {
  event.preventDefault();
  closeDiscardConfirmation();
});
recoveryDialog.addEventListener('cancel', (event) => {
  event.preventDefault();
  if (recoveryOrigin === 'file') discardPendingRecovery();
});

for (const button of speedPresetButtons) {
  button.addEventListener('click', () => setSelectedSpeed(Number(button.dataset.speed)));
}
customSpeedButton.addEventListener('click', openCustomSpeedDialog);
speedInput.addEventListener('input', () => { speedValidation.hidden = true; });
speedCancelButton.addEventListener('click', () => speedDialog.close());
speedForm.addEventListener('submit', (event) => {
  event.preventDefault();
  const speed = Number(speedInput.value);
  if (!Number.isFinite(speed) || speed < MINIMUM_SPEED || speed > MAXIMUM_SPEED) {
    speedValidation.hidden = false;
    speedInput.focus();
    speedInput.select();
    return;
  }
  speedDialog.close();
  setSelectedSpeed(speed);
});

exportButton.addEventListener('click', () => void openExportDialog());
cancelButton.addEventListener('click', () => operation?.abort());
playButton.addEventListener('click', () => togglePlay());
stepBackButton.addEventListener('click', () => seek(activeTrackId, position - frameDuration()));
stepForwardButton.addEventListener('click', () => seek(activeTrackId, position + frameDuration()));
splitButton.addEventListener('click', () => split());
deleteButton.addEventListener('click', () => deleteSelected());
undoButton.addEventListener('click', () => restore(false));
redoButton.addEventListener('click', () => restore(true));
duplicateButton.addEventListener('click', () => duplicateSelected());
renameButton.addEventListener('click', () => renameSelected());
multiSelectButton.addEventListener('click', () => toggleMultiSelectMode());
bindTracksButton.addEventListener('click', () => saveTrackBinding());
unbindTracksButton.addEventListener('click', () => unbindSelectedTracks());
$('zoom-in').addEventListener('click', () => setZoom(timeline.zoom * 1.25));
$('zoom-out').addEventListener('click', () => setZoom(timeline.zoom / 1.25));
$('zoom-fit').addEventListener('click', () => setZoom(1));

previewCanvas.addEventListener(
  'wheel',
  (event) => {
    if (project.sources.length === 0 || event.deltaY === 0) return;
    event.preventDefault();
    // 触控板保留连续变化，鼠标滚轮每格约缩放 12%。
    setPreviewZoom(previewZoom * Math.pow(1.12, -event.deltaY / 120));
  },
  { passive: false },
);
previewCanvas.addEventListener('dblclick', () => {
  if (project.sources.length > 0) setPreviewZoom(1);
});

const previewTouches = new Map<number, { x: number; y: number }>();
let previewPinch: { distance: number; zoom: number } | null = null;

const touchDistance = (points: readonly { x: number; y: number }[]): number =>
  points.length < 2
    ? 0
    : Math.hypot(points[0]!.x - points[1]!.x, points[0]!.y - points[1]!.y);

previewCanvas.addEventListener('pointerdown', (event) => {
  if (event.pointerType !== 'touch' || project.sources.length === 0) return;
  previewTouches.set(event.pointerId, { x: event.clientX, y: event.clientY });
  previewCanvas.setPointerCapture(event.pointerId);
  if (previewTouches.size === 2) {
    const distance = touchDistance([...previewTouches.values()]);
    if (distance > 0) previewPinch = { distance, zoom: previewZoom };
  }
});

previewCanvas.addEventListener('pointermove', (event) => {
  if (event.pointerType !== 'touch' || !previewTouches.has(event.pointerId)) return;
  previewTouches.set(event.pointerId, { x: event.clientX, y: event.clientY });
  if (!previewPinch || previewTouches.size < 2) return;
  event.preventDefault();
  const distance = touchDistance([...previewTouches.values()]);
  if (distance > 0) setPreviewZoom(previewPinch.zoom * distance / previewPinch.distance, false);
});

const finishPreviewTouch = (event: PointerEvent): void => {
  if (event.pointerType !== 'touch') return;
  const wasPinching = previewPinch !== null;
  previewTouches.delete(event.pointerId);
  if (previewCanvas.hasPointerCapture(event.pointerId)) {
    previewCanvas.releasePointerCapture(event.pointerId);
  }
  if (previewTouches.size < 2) {
    previewPinch = null;
    if (wasPinching) {
      const percent = Math.round(previewZoom * 100);
      status(previewZoom === 1 ? '预览已恢复适应' : `预览缩放 ${percent}%`);
    }
  }
};

previewCanvas.addEventListener('pointerup', finishPreviewTouch);
previewCanvas.addEventListener('pointercancel', finishPreviewTouch);

function setZoom(value: number, anchorViewportX?: number): void {
  timeline.setZoom(value, anchorViewportX);
  scheduleSessionSave();
}

window.addEventListener('keydown', (event) => {
  const target = event.target as HTMLElement | null;
  if (target && (target.tagName === 'INPUT' || target.tagName === 'SELECT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) {
    return;
  }
  if (exportDialog.open || settingsDialog.open || speedDialog.open || recoveryDialog.open) return;
  if (!clipContextMenu.hidden) {
    if (event.key === 'Escape') {
      event.preventDefault();
      closeClipContextMenu();
      canvas.focus({ preventScroll: true });
    }
    return;
  }
  if (!event.ctrlKey && !event.metaKey && target && moreMenuWrap.contains(target)) return;
  if (event.key === ' ' && !event.ctrlKey && !event.metaKey) {
    event.preventDefault();
    if (!event.repeat) togglePlay();
    return;
  }
  if (operation) return;
  const modifier = event.ctrlKey || event.metaKey;
  if (modifier && event.shiftKey && event.key.toLowerCase() === 'o') {
    event.preventDefault();
    projectFileInput.click();
    return;
  }
  if (modifier && event.key.toLowerCase() === 's') {
    event.preventDefault();
    exportProjectFile();
    return;
  }
  if (modifier && event.key.toLowerCase() === 'o') {
    event.preventDefault();
    fileInput.click();
    return;
  }
  if (modifier && event.key.toLowerCase() === 'z') {
    event.preventDefault();
    restore(event.shiftKey);
    return;
  }
  if (modifier && event.key.toLowerCase() === 'y') {
    event.preventDefault();
    restore(true);
    return;
  }
  if (modifier) return;
  switch (event.key) {
    case 's':
    case 'S':
      event.preventDefault();
      split();
      break;
    case 'Delete':
    case 'x':
    case 'X':
      event.preventDefault();
      deleteSelected();
      break;
    case 'ArrowLeft':
      event.preventDefault();
      seek(activeTrackId, position - frameDuration());
      break;
    case 'ArrowRight':
      event.preventDefault();
      seek(activeTrackId, position + frameDuration());
      break;
    case 'Home':
      event.preventDefault();
      seek(activeTrackId, 0);
      break;
    case 'End':
      event.preventDefault();
      seek(activeTrackId, trackDuration(activeTrack()));
      break;
    default:
      break;
  }
});

// 拖放导入
let dragDepth = 0;
window.addEventListener('dragenter', (event) => {
  if (!event.dataTransfer?.types.includes('Files')) return;
  event.preventDefault();
  dragDepth++;
  dropOverlay.hidden = false;
});
window.addEventListener('dragover', (event) => {
  if (!event.dataTransfer?.types.includes('Files')) return;
  event.preventDefault();
  event.dataTransfer.dropEffect = 'copy';
});
window.addEventListener('dragleave', () => {
  dragDepth = Math.max(0, dragDepth - 1);
  if (dragDepth === 0) dropOverlay.hidden = true;
});
window.addEventListener('drop', (event) => {
  if (!event.dataTransfer?.files.length) return;
  event.preventDefault();
  dragDepth = 0;
  dropOverlay.hidden = true;
  const dropped = Array.from(event.dataTransfer.files);
  if (recoveryDialog.open) void acceptRecoveryFiles(dropped);
  else if (!operation && dropped.length === 1 && dropped[0]!.name.toLowerCase().endsWith('.clip')) void openProjectFile(dropped[0]!);
  else if (!operation) void importFiles(dropped);
});

// 普通滚轮纵向浏览轨道；Shift 横向移动；Ctrl 以指针为锚缩放。
timelineScroll.addEventListener(
  'wheel',
  (event) => {
    if (operation || project.sources.length === 0) return;
    const unit = event.deltaMode === WheelEvent.DOM_DELTA_LINE
      ? 16
      : event.deltaMode === WheelEvent.DOM_DELTA_PAGE
        ? timelineScroll.clientHeight
        : 1;

    const rawDelta = event.deltaY !== 0 ? event.deltaY : event.deltaX;
    if (rawDelta === 0) return;

    if (event.ctrlKey) {
      event.preventDefault();
      const factor = Math.pow(1.2, -(rawDelta * unit) / 120);
      const rect = timelineScroll.getBoundingClientRect();
      setZoom(timeline.zoom * factor, event.clientX - rect.left);
      return;
    }

    event.preventDefault();
    if (event.shiftKey) timelineScroll.scrollLeft += rawDelta * unit;
    else timelineScroll.scrollTop += rawDelta * unit;
  },
  { passive: false },
);
timelineScroll.addEventListener('scroll', () => {
  closeClipContextMenu();
  timeline.draw();
  scheduleSessionSave();
}, { passive: true });

// 跟随系统主题
const darkQuery = window.matchMedia('(prefers-color-scheme: dark)');
darkQuery.addEventListener('change', () => {
  timeline.refreshTheme();
  drawPreviewFrame();
});

window.addEventListener('beforeunload', (event) => {
  saveSessionNow();
  if (project.canUndo && !operation) {
    event.preventDefault();
    event.returnValue = '';
  }
});

window.addEventListener('resize', () => {
  closeClipContextMenu();
  timeline.resize();
  drawPreviewFrame();
});

document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'hidden') saveSessionNow();
});

// 关闭页面时释放 object URL。
window.addEventListener('pagehide', () => {
  saveSessionNow();
  for (const url of objectUrls.values()) URL.revokeObjectURL(url);
  objectUrls.clear();
});

// 首屏：探测能力（尺寸未知时用 1920x1080 作为默认假设）。
void (async () => {
  capabilities = await detectCapabilities({ width: 1920, height: 1080, frameRate: 30 });
  applyCapabilities();
  refresh();
  offerRecovery();

  // 端到端测试入口；仅 ?automation=1 时挂载，且不绕过任何业务逻辑。
  if (shouldInstallAutomation(window.location.search)) {
    installAutomation({
      state: () => ({
        sources: project.sources,
        tracks: project.allTracks,
        duration: project.duration,
        selectedClipId,
        activeTrackId,
        position,
        playing,
        canUndo: project.canUndo,
        canRedo: project.canRedo,
        exportableTracks: project.exportableTracks.length,
      }),
      capabilities: () => capabilities,
      seek: (time) => seek(activeTrackId, time),
      split: () => {
        const before = project.allTracks.flatMap((track) => track.clips).length;
        split();
        return project.allTracks.flatMap((track) => track.clips).length > before;
      },
      setSpeed: (clipId, speed) => {
        const changed = project.setSpeed(clipId, speed);
        refresh();
        return changed;
      },
      deleteClip: (clipId) => {
        const changed = project.delete(clipId);
        refresh();
        return changed;
      },
      duplicateClip: (clipId) => {
        const id = project.duplicate(clipId);
        refresh();
        return id !== undefined;
      },
      moveClip: (clipId, trackId, index) => {
        const changed = project.move(clipId, trackId, index);
        refresh();
        return changed;
      },
      selectTrack: (trackId) => {
        selectedTrackId = trackId;
        refresh();
      },
      clips: (trackId) => {
        const id = trackId ?? activeTrackId;
        return project.findTrack(id)?.clips ?? [];
      },
      exportToBytes: async (request, onProgress) => {
        const trackId = request.trackId ?? activeTrackId;
        const clips = project.exportClips(trackId);
        const media = clips[0]!.media;
        const options: ExportOptions = {
          ...defaultExportOptions,
          ...(request.quality !== undefined ? { quality: request.quality } : {}),
          width: request.width ?? null,
          height: request.height ?? null,
          encoder: request.route === EncoderRoute.Wasm ? VideoEncoder.Software : VideoEncoder.WebCodecs,
          hardwareDecode: (request.route ?? EncoderRoute.WebCodecsVideo) === EncoderRoute.WebCodecsVideo,
        };
        void media;
        return exportClips({
          clips,
          options,
          route: request.route ?? chooseRoute(capabilities ?? emptyCapabilities(), route),
          capabilities: capabilities ?? emptyCapabilities(),
          ffmpegBase: new URL('ffmpeg/', document.baseURI).href,
          readSource: async (path) => {
            const file = files.get(path);
            if (!file) throw new Error(`找不到素材 ${path}`);
            return file.arrayBuffer();
          },
          onProgress,
        });
      },
    });
  }
})();
