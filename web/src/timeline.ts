/**
 * 时间轴绘制与交互，移植自 src/Clip.Desktop/TimelineControl.cs。
 *
 * 保留桌面端的几何常量与交互模型：
 *   行高 68、片段高 48、标尺高 28、内容内边距 32；
 *   点击片段只选中片段，点击轨道空白处才选中整轨；
 *   拖拽跨轨移动，拖到边缘自动滚动。
 *
 * 与 WPF 版的区别是用 Canvas 2D 绘制，并用指针事件实现拖放。
 */

import {
  type ClipPosition,
  EditProject,
  type VideoClip,
  type VideoTrack,
  TrackKind,
  clipDuration,
  displayName,
  trackDuration,
} from './model.ts';

export const RULER_HEIGHT = 28;
export const ROW_HEIGHT = 68;
export const CLIP_HEIGHT = 48;
export const CONTENT_INSET = 32;
export const TRACK_HANDLE_WIDTH = 24;

export interface TimelineCallbacks {
  onSeek: (trackId: string, time: number) => void;
  onSelectClip: (clipId: string, time: number) => void;
  onSelectTrack: (trackId: string, time: number) => void;
  onMoveClip: (clipId: string, trackId: string, index: number) => void;
  onMoveTrack: (trackId: string, index: number) => void;
  onToggleTrack: (trackId: string) => void;
}

interface ThemeColors {
  background: string;
  track: string;
  trackSelected: string;
  stroke: string;
  subtle: string;
  clip: string;
  clipBorder: string;
  clipSelected: string;
  clipSelectedBorder: string;
  foreground: string;
  foregroundMuted: string;
  selectionForeground: string;
  playhead: string;
  brand: string;
  waveform: string;
  binding: string;
}

function readColors(element: HTMLElement): ThemeColors {
  const style = getComputedStyle(element);
  const get = (name: string): string => style.getPropertyValue(name).trim() || '#888';
  return {
    background: get('--neutral-background-1'),
    track: get('--neutral-background-2'),
    trackSelected: get('--selection-background'),
    stroke: get('--neutral-stroke-2'),
    subtle: get('--subtle-stroke'),
    clip: get('--clip-background'),
    clipBorder: get('--clip-border'),
    clipSelected: get('--clip-selected'),
    clipSelectedBorder: get('--clip-selected-border'),
    foreground: get('--neutral-foreground-1'),
    foregroundMuted: get('--neutral-foreground-3'),
    selectionForeground: get('--selection-foreground'),
    playhead: get('--playhead'),
    brand: get('--brand-background'),
    waveform: get('--waveform'),
    binding: get('--binding-stroke'),
  };
}

const TIME_INTERVALS = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600, 7200, 21600, 86400];

/** 与桌面端 TimelineControl.FormatTime 一致。 */
export function formatTime(value: number): string {
  const safe = Number.isFinite(value) ? Math.max(0, value) : 0;
  const totalMs = Math.round(safe * 1000);
  const hours = Math.floor(totalMs / 3_600_000);
  const minutes = Math.floor((totalMs % 3_600_000) / 60_000);
  const seconds = Math.floor((totalMs % 60_000) / 1000);
  const milliseconds = totalMs % 1000;
  const pad = (n: number, width = 2): string => String(n).padStart(width, '0');
  return `${pad(hours)}:${pad(minutes)}:${pad(seconds)}.${pad(milliseconds, 3)}`;
}

export interface DropTarget {
  readonly trackId: string;
  readonly index: number;
}

export class TimelineView {
  private readonly canvas: HTMLCanvasElement;
  private readonly container: HTMLElement;
  private readonly callbacks: TimelineCallbacks;
  private project: EditProject | null = null;
  private colors: ThemeColors;

  activeTrackId = '';
  selectedTrackId: string | null = null;
  selectedClipId: string | null = null;
  position = 0;
  zoom = 1;
  exportPreviewTrackId: string | null = null;
  multiSelectMode = false;
  multiSelectedTrackIds: ReadonlySet<string> = new Set();
  private readonly waveforms = new Map<string, Float32Array>();

  private dragging: { clipId: string; started: boolean; origin: { x: number; y: number } } | null = null;
  private draggingTrack: { trackId: string; started: boolean; origin: { x: number; y: number } } | null = null;
  private dropTarget: DropTarget | null = null;
  private trackDropIndex: number | null = null;
  private seeking = false;
  private readonly touchPointers = new Map<number, { x: number; y: number }>();
  private touchGesture: {
    readonly startDistance: number;
    readonly startZoom: number;
    readonly anchorTime: number;
  } | null = null;
  private suppressTouchInteraction = false;

  constructor(canvas: HTMLCanvasElement, container: HTMLElement, callbacks: TimelineCallbacks) {
    this.canvas = canvas;
    this.container = container;
    this.callbacks = callbacks;
    this.colors = readColors(document.documentElement);
    this.attachEvents();
  }

  setProject(project: EditProject): void {
    this.project = project;
    this.resize();
  }

  /** 主题或尺寸变化后重绘。 */
  refreshTheme(): void {
    this.colors = readColors(document.documentElement);
    this.draw();
  }

  setWaveform(path: string, peaks: Float32Array): void {
    this.waveforms.set(path.toLowerCase(), peaks);
    this.draw();
  }

  private get duration(): number {
    return this.project ? this.project.duration : 0;
  }

  private scale(): number {
    const available = Math.max(1, this.cssWidth() - CONTENT_INSET - 20);
    return available / Math.max(this.duration, 1);
  }

  timeAtX(x: number): number {
    const scale = this.scale();
    const time = (x - CONTENT_INSET) / scale;
    return Math.min(Math.max(time, 0), this.duration);
  }

  xAtTime(time: number): number {
    return CONTENT_INSET + Math.min(Math.max(time, 0), this.duration) * this.scale();
  }

  /** 内容总高度，用于撑开滚动容器。 */
  contentHeight(): number {
    const rows = this.project ? this.project.allTracks.length : 1;
    return RULER_HEIGHT + rows * ROW_HEIGHT + 8;
  }

  private cssWidth(): number {
    const width = this.canvas.clientWidth || this.container.clientWidth;
    return Math.max(1, width);
  }

  /**
   * 以滚动窗口内的某一点为锚缩放；默认锚点为视口中心。
   * 画布宽度始终由容器基准宽度 × zoom 得出，不能在旧画布宽度上重复累乘。
   */
  setZoom(value: number, anchorViewportX = this.container.clientWidth / 2): void {
    const next = Math.min(Math.max(value, 1), 20);
    if (!Number.isFinite(next) || next === this.zoom) return;
    const anchorX = Math.min(Math.max(anchorViewportX, 0), this.container.clientWidth);
    const anchorTime = this.timeAtX(this.container.scrollLeft + anchorX);
    this.zoom = next;
    this.resize();
    this.container.scrollLeft = this.xAtTime(anchorTime) - anchorX;
  }

  /** 依据容器可用宽度与缩放设置画布尺寸（考虑 HiDPI）。 */
  resize(): void {
    const dpr = window.devicePixelRatio || 1;
    const cssWidth = Math.max(1, this.container.clientWidth) * this.zoom;
    const cssHeight = this.contentHeight();
    this.canvas.style.width = `${cssWidth}px`;
    this.canvas.style.height = `${cssHeight}px`;
    this.canvas.width = Math.round(cssWidth * dpr);
    this.canvas.height = Math.round(cssHeight * dpr);
    const context = this.canvas.getContext('2d');
    if (context) {
      context.setTransform(dpr, 0, 0, dpr, 0, 0);
    }
    this.draw();
  }

  clipBounds(clipId: string): { x: number; y: number; width: number; height: number } | null {
    const project = this.project;
    if (!project) return null;
    const found = project.findClip(clipId);
    if (!found) return null;
    const row = project.allTracks.findIndex((track) => track.id === found.trackId);
    return {
      x: this.xAtTime(found.timelineStart),
      y: RULER_HEIGHT + row * ROW_HEIGHT + 10,
      width: Math.max(2, clipDuration(found.clip) * this.scale() - 3),
      height: CLIP_HEIGHT,
    };
  }

  /** 指针位置对应的插入点，用于拖放。 */
  insertionAt(x: number, y: number): DropTarget | null {
    const project = this.project;
    if (!project) return null;
    if (y < RULER_HEIGHT) return null;
    const row = Math.floor((y - RULER_HEIGHT) / ROW_HEIGHT);
    if (row < 0 || row >= project.allTracks.length) return null;
    const track = project.allTracks[row]!;
    const time = this.timeAtX(x);
    let offset = 0;
    for (let index = 0; index < track.clips.length; index++) {
      const clip = track.clips[index]!;
      if (time < offset + clipDuration(clip) / 2) return { trackId: track.id, index };
      offset += clipDuration(clip);
    }
    return { trackId: track.id, index: track.clips.length };
  }

  trackInsertionAt(y: number): number | null {
    const project = this.project;
    if (!project || y < RULER_HEIGHT) return null;
    const raw = Math.round((y - RULER_HEIGHT) / ROW_HEIGHT);
    return project.trackInsertionBoundaries().reduce((best, boundary) =>
      Math.abs(boundary - raw) <= Math.abs(best - raw) ? boundary : best);
  }

  private roundedRect(
    context: CanvasRenderingContext2D,
    x: number,
    y: number,
    width: number,
    height: number,
    radius: number,
  ): void {
    const r = Math.min(radius, width / 2, height / 2);
    context.beginPath();
    context.moveTo(x + r, y);
    context.arcTo(x + width, y, x + width, y + height, r);
    context.arcTo(x + width, y + height, x, y + height, r);
    context.arcTo(x, y + height, x, y, r);
    context.arcTo(x, y, x + width, y, r);
    context.closePath();
  }

  draw(): void {
    const context = this.canvas.getContext('2d');
    if (!context) return;
    const project = this.project;
    const width = this.canvas.width / (window.devicePixelRatio || 1);
    const height = this.canvas.height / (window.devicePixelRatio || 1);
    const colors = this.colors;

    context.fillStyle = colors.background;
    context.fillRect(0, 0, width, height);
    if (!project) return;

    context.font = `12px ${getComputedStyle(document.body).fontFamily}`;
    context.textBaseline = 'alphabetic';

    project.allTracks.forEach((track, row) => {
      const top = RULER_HEIGHT + row * ROW_HEIGHT;
      const multiSelected = this.multiSelectedTrackIds.has(track.id);
      if (track.id === this.selectedTrackId || multiSelected) {
        context.fillStyle = colors.trackSelected;
        context.fillRect(0, top, width, ROW_HEIGHT);
      } else if (track.id === this.activeTrackId) {
        context.fillStyle = colors.track;
        context.fillRect(0, top, width, ROW_HEIGHT);
      }
      context.strokeStyle = colors.subtle;
      context.lineWidth = 1;
      context.beginPath();
      context.moveTo(0, top + ROW_HEIGHT + 0.5);
      context.lineTo(width, top + ROW_HEIGHT + 0.5);
      context.stroke();

      if (track.clips.length === 0) {
        context.fillStyle = track.id === this.selectedTrackId ? colors.selectionForeground : colors.foregroundMuted;
        context.fillText(track.kind === TrackKind.Audio && track.companionGroupId
          ? '伴生音频槽（当前无片段）'
          : '拖拽片段到这里', CONTENT_INSET, top + 32);
      }

      let offset = 0;
      for (const clip of track.clips) {
        const x = this.xAtTime(offset);
        const rectWidth = Math.max(2, clipDuration(clip) * this.scale() - 3);
        offset += clipDuration(clip);
        this.drawClip(context, track, clip, x, top + 10, rectWidth, clip.id === this.selectedClipId);
      }

      const handleX = this.container.scrollLeft + 5;
      const isCompanionAudio = track.kind === TrackKind.Audio && Boolean(track.companionGroupId);
      const dragged = this.draggingTrack ? project.findTrack(this.draggingTrack.trackId) : undefined;
      const draggingGroup = this.draggingTrack?.trackId === track.id
        || Boolean(track.companionGroupId && dragged?.companionGroupId === track.companionGroupId);
      context.fillStyle = track.id === this.selectedTrackId || multiSelected
        ? colors.trackSelected
        : track.id === this.activeTrackId ? colors.track : colors.background;
      context.fillRect(this.container.scrollLeft, top, TRACK_HANDLE_WIDTH + 4, ROW_HEIGHT);
      if (isCompanionAudio) {
        context.strokeStyle = draggingGroup ? colors.brand : colors.foregroundMuted;
        context.fillStyle = draggingGroup ? colors.brand : colors.foregroundMuted;
        context.lineWidth = 1.5;
        context.beginPath();
        context.moveTo(handleX + 7, top);
        context.lineTo(handleX + 7, top + ROW_HEIGHT / 2);
        context.lineTo(handleX + 14, top + ROW_HEIGHT / 2);
        context.stroke();
        context.beginPath();
        context.arc(handleX + 14, top + ROW_HEIGHT / 2, 2.5, 0, Math.PI * 2);
        context.fill();
      } else if (track.clips.length > 0 || track.companionGroupId) {
        this.roundedRect(context, handleX, top + 21, 14, 26, 5);
        context.fillStyle = draggingGroup ? colors.brand : colors.track;
        context.fill();
        context.strokeStyle = draggingGroup
          ? colors.selectionForeground
          : colors.foregroundMuted;
        context.lineWidth = 1;
        for (const handleY of [top + 28, top + 34, top + 40]) {
          context.beginPath();
          context.moveTo(handleX + 4, handleY);
          context.lineTo(handleX + 10, handleY);
          context.stroke();
        }
      }

      if (track.bindingId) {
        context.strokeStyle = colors.binding;
        context.lineWidth = 2;
        context.beginPath();
        context.moveTo(handleX + 18, top + 12);
        context.lineTo(handleX + 18, top + ROW_HEIGHT - 12);
        context.stroke();
        context.fillStyle = colors.binding;
        context.beginPath();
        context.arc(handleX + 18, top + ROW_HEIGHT / 2, 3, 0, Math.PI * 2);
        context.fill();
      }

      if (track.id === this.exportPreviewTrackId) {
        context.strokeStyle = colors.brand;
        context.lineWidth = 2;
        context.strokeRect(1, top + 1, Math.max(0, width - 2), ROW_HEIGHT - 2);
      }

      if (this.dropTarget?.trackId === track.id) {
        const dropX = this.xAtTime(
          track.clips.slice(0, this.dropTarget.index).reduce((total, clip) => total + clipDuration(clip), 0),
        );
        context.strokeStyle = colors.brand;
        context.lineWidth = 3;
        context.beginPath();
        context.moveTo(dropX, top + 2);
        context.lineTo(dropX, top + ROW_HEIGHT - 2);
        context.stroke();
        context.fillStyle = colors.brand;
        context.beginPath();
        context.arc(dropX, top + 3, 4, 0, Math.PI * 2);
        context.fill();
      }
    });

    if (this.trackDropIndex !== null) {
      const y = RULER_HEIGHT + this.trackDropIndex * ROW_HEIGHT;
      context.strokeStyle = colors.brand;
      context.lineWidth = 3;
      context.beginPath();
      context.moveTo(this.container.scrollLeft + 3, y);
      context.lineTo(Math.min(width, this.container.scrollLeft + this.container.clientWidth - 3), y);
      context.stroke();
    }

    this.drawRuler(context, width);
    this.drawPlayhead(context, project);
  }

  private drawClip(
    context: CanvasRenderingContext2D,
    track: VideoTrack,
    clip: VideoClip,
    x: number,
    y: number,
    width: number,
    selected: boolean,
  ): void {
    const colors = this.colors;
    const foreground = selected ? colors.selectionForeground : colors.foreground;
    context.globalAlpha = this.dragging?.clipId === clip.id ? 0.45 : 1;
    this.roundedRect(context, x, y, width, CLIP_HEIGHT, 6);
    context.fillStyle = selected ? colors.clipSelected : colors.clip;
    context.fill();
    context.strokeStyle = selected ? colors.clipSelectedBorder : colors.clipBorder;
    context.lineWidth = 1;
    context.stroke();

    if (track.kind === TrackKind.Audio) {
      this.drawWaveform(context, clip, x, y, width, selected);
    }

    if (width > 44) {
      context.save();
      this.roundedRect(context, x, y, width, CLIP_HEIGHT, 6);
      context.clip();
      context.fillStyle = foreground;
      const name = displayName(clip);
      context.fillText(this.truncate(context, name, width - 36), x + 8, y + (track.kind === TrackKind.Audio ? 14 : 18));
      if (track.kind !== TrackKind.Audio) {
        context.fillStyle = selected ? colors.selectionForeground : colors.foregroundMuted;
        context.font = `11px ${getComputedStyle(document.body).fontFamily}`;
        context.fillText(
          `${clip.speed.toFixed(2).replace(/\.?0+$/, '')}× · ${clipDuration(clip).toFixed(2)} 秒`,
          x + 8,
          y + 36,
        );
      }
      context.restore();
      context.font = `12px ${getComputedStyle(document.body).fontFamily}`;
    }
    context.globalAlpha = 1;
  }

  private drawWaveform(
    context: CanvasRenderingContext2D,
    clip: VideoClip,
    x: number,
    y: number,
    width: number,
    selected: boolean,
  ): void {
    const peaks = this.waveforms.get(clip.media.path.toLowerCase());
    if (!peaks || peaks.length === 0 || width < 3) return;
    const center = y + 32;
    const amplitude = 10;
    const bars = Math.max(1, Math.floor(width / 3));
    context.save();
    this.roundedRect(context, x, y, width, CLIP_HEIGHT, 6);
    context.clip();
    context.strokeStyle = selected ? this.colors.selectionForeground : this.colors.waveform;
    context.globalAlpha = selected ? 0.72 : 0.8;
    context.lineWidth = 1.5;
    for (let bar = 0; bar < bars; bar++) {
      const sourceTime = clip.start + (bar / Math.max(1, bars - 1)) * (clip.end - clip.start);
      const index = Math.min(peaks.length - 1, Math.max(0, Math.floor(sourceTime / clip.media.duration * peaks.length)));
      const height = Math.max(1.5, (peaks[index] ?? 0) * amplitude);
      const barX = x + bar * 3 + 1.5;
      context.beginPath();
      context.moveTo(barX, center - height);
      context.lineTo(barX, center + height);
      context.stroke();
    }
    context.restore();
  }

  private truncate(context: CanvasRenderingContext2D, text: string, maxWidth: number): string {
    if (context.measureText(text).width <= maxWidth) return text;
    let result = text;
    while (result.length > 1 && context.measureText(`${result}…`).width > maxWidth) {
      result = result.slice(0, -1);
    }
    return `${result}…`;
  }

  /** 标尺固定在可视区顶部吗？桌面端是冻结标尺；这里用画布顶部对齐，随内容滚动。 */
  private drawRuler(context: CanvasRenderingContext2D, width: number): void {
    const colors = this.colors;
    context.fillStyle = colors.track;
    context.fillRect(0, 0, width, RULER_HEIGHT);
    const scale = this.scale();
    const interval = TIME_INTERVALS.find((value) => value * scale >= 88) ?? Math.max(this.duration / 8, 1);
    const last = this.duration;
    context.font = `11px ${getComputedStyle(document.body).fontFamily}`;
    for (let time = 0; time <= last + 1e-9; time += interval) {
      const x = this.xAtTime(time);
      const totalSeconds = time;
      const hours = Math.floor(totalSeconds / 3600);
      const minutes = Math.floor((totalSeconds % 3600) / 60);
      const seconds = Math.floor(totalSeconds % 60);
      const label =
        time >= 3600
          ? `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`
          : `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}` +
            (interval < 1 ? `.${Math.floor((totalSeconds % 1) * 10)}` : '');
      context.fillStyle = colors.foregroundMuted;
      context.fillText(label, x + 3, 13);
      context.strokeStyle = colors.stroke;
      context.lineWidth = 1;
      context.beginPath();
      context.moveTo(x + 0.5, 22);
      context.lineTo(x + 0.5, 28);
      context.stroke();
    }
  }

  private drawPlayhead(context: CanvasRenderingContext2D, project: EditProject): void {
    const track = project.findTrack(this.activeTrackId);
    if (!track || track.clips.length === 0) return;
    const x = this.xAtTime(Math.min(this.position, trackDuration(track)));
    context.strokeStyle = this.colors.playhead;
    context.lineWidth = 1.5;
    context.beginPath();
    context.moveTo(x, 16);
    context.lineTo(x, this.contentHeight());
    context.stroke();
    context.fillStyle = this.colors.playhead;
    this.roundedRect(context, x - 4, 8, 8, 12, 2);
    context.fill();
  }

  // ---------- 交互 ----------

  private attachEvents(): void {
    this.canvas.addEventListener('pointerdown', (event) => this.onPointerDown(event));
    this.canvas.addEventListener('pointermove', (event) => this.onPointerMove(event));
    this.canvas.addEventListener('pointerup', (event) => this.onPointerUp(event));
    this.canvas.addEventListener('pointercancel', (event) => this.onPointerCancel(event));
    this.canvas.addEventListener('contextmenu', (event) => event.preventDefault());
  }

  private localPoint(event: PointerEvent): { x: number; y: number } {
    const rect = this.canvas.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  private touchPair(): readonly [{ x: number; y: number }, { x: number; y: number }] | null {
    const points = [...this.touchPointers.values()];
    return points.length >= 2 ? [points[0]!, points[1]!] : null;
  }

  private beginTouchGesture(): void {
    const pair = this.touchPair();
    if (!pair) return;
    const distance = Math.hypot(pair[0].x - pair[1].x, pair[0].y - pair[1].y);
    if (distance <= 0) return;
    const rect = this.container.getBoundingClientRect();
    const centerX = (pair[0].x + pair[1].x) / 2 - rect.left;
    this.touchGesture = {
      startDistance: distance,
      startZoom: this.zoom,
      anchorTime: this.timeAtX(this.container.scrollLeft + centerX),
    };
    this.suppressTouchInteraction = true;
    this.seeking = false;
    this.endDrag();
  }

  /** 双指距离控制缩放，双指中心移动控制时间轴窗口。 */
  private updateTouchGesture(): void {
    const gesture = this.touchGesture;
    const pair = this.touchPair();
    if (!gesture || !pair) return;
    const distance = Math.hypot(pair[0].x - pair[1].x, pair[0].y - pair[1].y);
    if (distance <= 0) return;
    const rect = this.container.getBoundingClientRect();
    const centerX = (pair[0].x + pair[1].x) / 2 - rect.left;
    const nextZoom = Math.min(Math.max(gesture.startZoom * distance / gesture.startDistance, 1), 20);
    if (nextZoom !== this.zoom) {
      this.zoom = nextZoom;
      this.resize();
    }
    this.container.scrollLeft = this.xAtTime(gesture.anchorTime) - centerX;
  }

  private onPointerDown(event: PointerEvent): void {
    const project = this.project;
    if (!project || event.button !== 0) return;
    if (event.pointerType === 'touch') {
      this.touchPointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
      this.canvas.setPointerCapture(event.pointerId);
      if (this.touchPointers.size >= 2) {
        this.beginTouchGesture();
        return;
      }
      if (this.suppressTouchInteraction) return;
    }
    const point = this.localPoint(event);

    if (point.y < RULER_HEIGHT) {
      this.seeking = true;
      this.canvas.setPointerCapture(event.pointerId);
      this.callbacks.onSeek(this.activeTrackId, this.timeAtX(point.x));
      return;
    }

    const row = Math.floor((point.y - RULER_HEIGHT) / ROW_HEIGHT);
    if (row < 0 || row >= project.allTracks.length) return;
    const track = project.allTracks[row]!;
    if (this.multiSelectMode) {
      event.preventDefault();
      this.callbacks.onToggleTrack(track.id);
      return;
    }
    const handleRight = this.container.scrollLeft + TRACK_HANDLE_WIDTH;
    if (point.x <= handleRight && (track.clips.length > 0 || Boolean(track.companionGroupId))) {
      this.draggingTrack = { trackId: track.id, started: false, origin: point };
      this.canvas.style.cursor = 'grab';
      this.canvas.setPointerCapture(event.pointerId);
      return;
    }
    const hit = track.clips.find((clip) => {
      const bounds = this.clipBounds(clip.id);
      return bounds ? point.x >= bounds.x && point.x <= bounds.x + bounds.width : false;
    });

    if (hit) {
      this.callbacks.onSelectClip(hit.id, this.timeAtX(point.x));
      this.dragging = { clipId: hit.id, started: false, origin: point };
      this.canvas.setPointerCapture(event.pointerId);
    } else {
      this.callbacks.onSelectTrack(track.id, this.timeAtX(point.x));
    }
  }

  private onPointerMove(event: PointerEvent): void {
    if (event.pointerType === 'touch' && this.touchPointers.has(event.pointerId)) {
      this.touchPointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
      if (this.touchGesture) {
        event.preventDefault();
        this.updateTouchGesture();
        return;
      }
      if (this.suppressTouchInteraction) return;
    }
    const point = this.localPoint(event);
    if (this.seeking) {
      this.callbacks.onSeek(this.activeTrackId, this.timeAtX(point.x));
      return;
    }
    if (this.draggingTrack) {
      if (!this.draggingTrack.started) {
        const dx = Math.abs(point.x - this.draggingTrack.origin.x);
        const dy = Math.abs(point.y - this.draggingTrack.origin.y);
        if (dx < 4 && dy < 4) return;
        this.draggingTrack.started = true;
        this.canvas.style.cursor = 'grabbing';
      }
      this.trackDropIndex = this.trackInsertionAt(point.y);
      this.autoScroll(point);
      this.draw();
      return;
    }
    if (this.dragging) {
      if (!this.dragging.started) {
        const dx = Math.abs(point.x - this.dragging.origin.x);
        const dy = Math.abs(point.y - this.dragging.origin.y);
        if (dx < 4 && dy < 4) return;
        this.dragging.started = true;
      }
      const target = this.insertionAt(point.x, point.y);
      this.dropTarget = target;
      this.autoScroll(point);
      this.draw();
    }
  }

  private onPointerUp(event: PointerEvent): void {
    if (event.pointerType === 'touch') {
      const wasGesture = this.suppressTouchInteraction;
      this.touchPointers.delete(event.pointerId);
      if (this.canvas.hasPointerCapture(event.pointerId)) {
        this.canvas.releasePointerCapture(event.pointerId);
      }
      if (this.touchPointers.size < 2) this.touchGesture = null;
      if (this.touchPointers.size === 0) this.suppressTouchInteraction = false;
      if (wasGesture) return;
    }
    if (this.seeking) {
      this.seeking = false;
      if (this.canvas.hasPointerCapture(event.pointerId)) {
        this.canvas.releasePointerCapture(event.pointerId);
      }
      return;
    }
    if (this.dragging?.started && this.dropTarget) {
      this.callbacks.onMoveClip(this.dragging.clipId, this.dropTarget.trackId, this.dropTarget.index);
    }
    if (this.draggingTrack?.started && this.trackDropIndex !== null) {
      this.callbacks.onMoveTrack(this.draggingTrack.trackId, this.trackDropIndex);
    }
    this.endDrag();
    if (this.canvas.hasPointerCapture(event.pointerId)) {
      this.canvas.releasePointerCapture(event.pointerId);
    }
  }

  private onPointerCancel(event: PointerEvent): void {
    if (event.pointerType === 'touch') {
      this.touchPointers.delete(event.pointerId);
      if (this.touchPointers.size < 2) this.touchGesture = null;
      if (this.touchPointers.size === 0) this.suppressTouchInteraction = false;
    }
    this.seeking = false;
    this.endDrag();
  }

  private endDrag(): void {
    this.dragging = null;
    this.draggingTrack = null;
    this.dropTarget = null;
    this.trackDropIndex = null;
    this.canvas.style.cursor = '';
    this.draw();
  }

  /** 拖到容器边缘时自动滚动，与桌面端 AutoScrollTimeline 行为一致。 */
  private autoScroll(point: { x: number; y: number }): void {
    const margin = 24;
    const { scrollLeft, scrollTop, clientWidth, clientHeight } = this.container;
    if (point.x < margin) this.container.scrollLeft = scrollLeft - margin;
    else if (point.x > clientWidth - margin) this.container.scrollLeft = scrollLeft + margin;
    if (point.y < RULER_HEIGHT + 16) this.container.scrollTop = scrollTop - margin;
    else if (point.y > clientHeight - margin) this.container.scrollTop = scrollTop + margin;
  }
}

export type { ClipPosition, VideoTrack };
