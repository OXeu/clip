/**
 * Clip 编辑模型的 TypeScript 移植。
 *
 * 与 src/Clip.Core 中的 EditProject / VideoClip / ExportOptions 一一对应，
 * 语义（ripple 删除、跨轨移动、撤销栈、变速、导出选轨）刻意保持一致，
 * 这样网页版与桌面版的剪辑结果不会产生分歧。
 */

export interface MediaInfo {
  readonly path: string;
  readonly duration: number;
  readonly width: number;
  readonly height: number;
  readonly frameRate: number;
  readonly videoStreamIndex: number;
  readonly audioStreamIndex: number | null;
  readonly codec: string;
  readonly videoTimestampOffset: number;
  readonly isHdr: boolean;
}

export const hasAudio = (media: MediaInfo): boolean => media.audioStreamIndex !== null;
export const fileName = (media: MediaInfo): string => media.path.split(/[/\\]/).pop() ?? media.path;

export const ClipKind = {
  Combined: 'combined',
  Video: 'video',
  Audio: 'audio',
} as const;
export type ClipKind = (typeof ClipKind)[keyof typeof ClipKind];

export const TrackKind = {
  Video: 'video',
  Audio: 'audio',
} as const;
export type TrackKind = (typeof TrackKind)[keyof typeof TrackKind];

export interface VideoClip {
  readonly id: string;
  readonly media: MediaInfo;
  readonly start: number;
  readonly end: number;
  readonly speed: number;
  /** 旧项目为 combined；分离导入后用于区分片段的编辑与展示角色。 */
  readonly kind?: ClipKind;
  readonly name?: string;
}

export const MINIMUM_SPEED = 0.1;
export const MAXIMUM_SPEED = 8;

export const clipDuration = (clip: VideoClip): number => (clip.end - clip.start) / clip.speed;
export const displayName = (clip: VideoClip): string => clip.name ?? fileName(clip.media);

export interface VideoTrack {
  readonly id: string;
  readonly name: string;
  readonly isMain: boolean;
  readonly kind?: TrackKind;
  /** 同一 companionGroupId 的视频轨与音频槽构成不可拆散的伴生轨道组。 */
  readonly companionGroupId?: string | null;
  /** 同一非空 bindingId 的轨道共享分割点；删除始终只影响当前片段。 */
  readonly bindingId?: string | null;
  readonly clips: readonly VideoClip[];
}

export interface SeparatedImport {
  readonly videoTrack: VideoTrack;
  readonly audioTrack: VideoTrack;
}

export const trackDuration = (track: VideoTrack): number =>
  track.clips.reduce((total, clip) => total + clipDuration(clip), 0);

export interface ClipPosition {
  readonly trackId: string;
  readonly index: number;
  readonly clip: VideoClip;
  readonly sourceTime: number;
  readonly timelineStart: number;
}

export interface ClipPlayback {
  readonly position: ClipPosition;
  readonly requiresSeek: boolean;
  readonly reachedEnd: boolean;
}

export function validateSpeed(speed: number): void {
  if (!Number.isFinite(speed) || speed < MINIMUM_SPEED || speed > MAXIMUM_SPEED) {
    throw new Error('片段速度必须在 0.1–8 倍之间。');
  }
}

export function validateClip(clip: VideoClip): void {
  validateSpeed(clip.speed);
  const media = clip.media;
  const invalid =
    !Number.isFinite(clip.start) ||
    !Number.isFinite(clip.end) ||
    clip.start < 0 ||
    clip.end > media.duration + 0.001 ||
    clip.end <= clip.start ||
    !Number.isFinite(media.duration) ||
    media.duration <= 0 ||
    !Number.isFinite(media.frameRate) ||
    media.frameRate <= 0 ||
    media.width < 1 ||
    media.height < 1 ||
    media.videoStreamIndex < 0 ||
    (media.audioStreamIndex !== null && media.audioStreamIndex < 0) ||
    media.path.trim() === '';
  if (invalid) throw new Error('片段的素材信息或源视频范围无效。');
}

export function createClip(media: MediaInfo, kind: ClipKind = ClipKind.Combined): VideoClip {
  return {
    id: crypto.randomUUID(),
    media,
    start: 0,
    end: media.duration,
    speed: 1,
    kind,
  };
}

export const sameSource = (a: MediaInfo, b: MediaInfo): boolean =>
  a.path.toLowerCase() === b.path.toLowerCase();

export interface EditProjectSnapshot {
  readonly tracks: readonly VideoTrack[];
  readonly sources: readonly MediaInfo[];
}

type ProjectState = EditProjectSnapshot;

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null;

/**
 * 从 sessionStorage 这类不可信的 JSON 数据恢复项目。
 * 恢复时重新校验所有媒体、片段和 ID，并让片段统一引用 sources 中的媒体对象。
 */
function parseProjectSnapshot(value: unknown): ProjectState {
  if (!isRecord(value) || !Array.isArray(value.tracks) || !Array.isArray(value.sources)) {
    throw new Error('保存的剪辑项目格式无效。');
  }

  const parseMedia = (candidate: unknown): MediaInfo => {
    if (!isRecord(candidate)) throw new Error('保存的素材信息无效。');
    if (
      typeof candidate.path !== 'string'
      || typeof candidate.duration !== 'number'
      || typeof candidate.width !== 'number'
      || typeof candidate.height !== 'number'
      || typeof candidate.frameRate !== 'number'
      || typeof candidate.videoStreamIndex !== 'number'
      || !(candidate.audioStreamIndex === null || typeof candidate.audioStreamIndex === 'number')
      || typeof candidate.codec !== 'string'
      || typeof candidate.videoTimestampOffset !== 'number'
      || typeof candidate.isHdr !== 'boolean'
      || candidate.isHdr
    ) throw new Error('保存的素材信息不完整。');
    const media: MediaInfo = {
      path: candidate.path,
      duration: candidate.duration,
      width: candidate.width,
      height: candidate.height,
      frameRate: candidate.frameRate,
      videoStreamIndex: candidate.videoStreamIndex,
      audioStreamIndex: candidate.audioStreamIndex,
      codec: candidate.codec,
      videoTimestampOffset: candidate.videoTimestampOffset,
      isHdr: candidate.isHdr,
    };
    const probeClip: VideoClip = { id: 'restore-validation', media, start: 0, end: media.duration, speed: 1 };
    validateClip(probeClip);
    return media;
  };

  const sources = value.sources.map(parseMedia);
  const sourcePaths = new Set<string>();
  for (const source of sources) {
    const key = source.path.toLowerCase();
    if (sourcePaths.has(key)) throw new Error('保存的项目包含重复素材。');
    sourcePaths.add(key);
  }

  const trackIds = new Set<string>();
  const clipIds = new Set<string>();
  const tracks = value.tracks.map((candidate): VideoTrack => {
    if (!isRecord(candidate) || !Array.isArray(candidate.clips)) {
      throw new Error('保存的轨道信息无效。');
    }
    const id = candidate.id;
    const name = candidate.name;
    const isMain = candidate.isMain;
    if (typeof id !== 'string' || id.length === 0 || trackIds.has(id) || typeof name !== 'string' || name.length === 0) {
      throw new Error('保存的轨道信息不完整。');
    }
    if (typeof isMain !== 'boolean') {
      throw new Error('保存的主轨道信息无效。');
    }
    trackIds.add(id);
    const kind = candidate.kind === undefined ? TrackKind.Video : candidate.kind;
    const bindingId = candidate.bindingId === undefined ? null : candidate.bindingId;
    const companionGroupId = candidate.companionGroupId === undefined ? null : candidate.companionGroupId;
    if ((kind !== TrackKind.Video && kind !== TrackKind.Audio)
      || !(bindingId === null || (typeof bindingId === 'string' && bindingId.length > 0))
      || !(companionGroupId === null || (typeof companionGroupId === 'string' && companionGroupId.length > 0))) {
      throw new Error('保存的轨道类型或绑定信息无效。');
    }
    const clips = candidate.clips.map((clipCandidate): VideoClip => {
      if (!isRecord(clipCandidate) || !isRecord(clipCandidate.media)) {
        throw new Error('保存的片段信息无效。');
      }
      const path = clipCandidate.media.path;
      const media = typeof path === 'string'
        ? sources.find((source) => source.path.toLowerCase() === path.toLowerCase())
        : undefined;
      if (
        typeof clipCandidate.id !== 'string'
        || typeof clipCandidate.start !== 'number'
        || typeof clipCandidate.end !== 'number'
        || typeof clipCandidate.speed !== 'number'
        || !(clipCandidate.kind === undefined || Object.values(ClipKind).includes(clipCandidate.kind as ClipKind))
        || !(clipCandidate.name === undefined || typeof clipCandidate.name === 'string')
      ) throw new Error('保存的片段信息不完整。');
      const clip: VideoClip = {
        id: clipCandidate.id,
        media: media as MediaInfo,
        start: clipCandidate.start,
        end: clipCandidate.end,
        speed: clipCandidate.speed,
        kind: (clipCandidate.kind as ClipKind | undefined) ?? ClipKind.Combined,
        ...(typeof clipCandidate.name === 'string' ? { name: clipCandidate.name } : {}),
      };
      if (!media || clip.id.length === 0 || clipIds.has(clip.id)) {
        throw new Error('保存的片段引用了无效素材或重复 ID。');
      }
      clipIds.add(clip.id);
      validateClip(clip);
      return clip;
    });
    return { id, name, isMain, kind, companionGroupId, bindingId, clips };
  });

  if (tracks.length === 0 || tracks.filter((track) => track.isMain).length !== 1) {
    throw new Error('保存的项目缺少唯一主轨道。');
  }
  // 兼容上一版分轨存档：相邻且同源的视频/音频轨自动补为伴生组。
  let migratedTracks = [...tracks];
  for (let index = 0; index + 1 < migratedTracks.length; index++) {
    const video = migratedTracks[index]!;
    const audio = migratedTracks[index + 1]!;
    if (video.companionGroupId || audio.companionGroupId
      || video.kind !== TrackKind.Video || audio.kind !== TrackKind.Audio
      || video.clips.length === 0 || audio.clips.length === 0
      || !sameSource(video.clips[0]!.media, audio.clips[0]!.media)) continue;
    const companionGroupId = `restored-${video.id}`;
    migratedTracks[index] = { ...video, companionGroupId };
    migratedTracks[index + 1] = { ...audio, companionGroupId };
    index++;
  }
  const groups = new Map<string, VideoTrack[]>();
  for (const track of migratedTracks) {
    if (!track.companionGroupId) continue;
    const group = groups.get(track.companionGroupId) ?? [];
    group.push(track);
    groups.set(track.companionGroupId, group);
  }
  if ([...groups.values()].some((group) => group.length !== 2
    || group.filter((track) => track.kind === TrackKind.Video).length !== 1
    || group.filter((track) => track.kind === TrackKind.Audio).length !== 1)) {
    throw new Error('保存的项目包含无效的伴生音视频轨道组。');
  }
  return { tracks: migratedTracks, sources };
}

/** 可独立导出的多轨道项目，撤销作用于整个项目。 */
export class EditProject {
  private tracks: VideoTrack[] = [
    { id: crypto.randomUUID(), name: '轨道 1', isMain: true, kind: TrackKind.Video, companionGroupId: null, bindingId: null, clips: [] },
  ];
  private sourceList: MediaInfo[] = [];
  private undoStack: ProjectState[] = [];
  private redoStack: ProjectState[] = [];

  get allTracks(): readonly VideoTrack[] {
    return this.tracks;
  }

  get sources(): readonly MediaInfo[] {
    return this.sourceList;
  }

  get mainTrack(): VideoTrack {
    return this.tracks.find((track) => track.isMain) ?? this.tracks[0]!;
  }

  get duration(): number {
    return this.tracks.reduce((max, track) => Math.max(max, trackDuration(track)), 0);
  }

  get canUndo(): boolean {
    return this.undoStack.length > 0;
  }

  get canRedo(): boolean {
    return this.redoStack.length > 0;
  }

  get exportableTracks(): readonly VideoTrack[] {
    return this.tracks.filter((track) => track.clips.length > 0 && track.kind !== TrackKind.Audio);
  }

  /** 生成适合 JSON 序列化的当前项目快照；撤销/重做历史刻意不持久化。 */
  exportSnapshot(): EditProjectSnapshot {
    return {
      tracks: this.tracks.map((track) => ({
        ...track,
        clips: track.clips.map((clip) => ({ ...clip, media: { ...clip.media } })),
      })),
      sources: this.sourceList.map((source) => ({ ...source })),
    };
  }

  static fromSnapshot(value: unknown): EditProject {
    const state = parseProjectSnapshot(value);
    const project = new EditProject();
    project.tracks = [...state.tracks];
    project.sourceList = [...state.sources];
    project.normalizeTracks();
    project.undoStack = [];
    project.redoStack = [];
    return project;
  }

  import(media: MediaInfo): VideoTrack {
    const clip = createClip(media);
    validateClip(clip);
    if (media.isHdr) throw new Error('暂不支持 HDR 素材，请先转换为 SDR。');
    this.saveUndo();
    if (!this.sourceList.some((source) => sameSource(source, media))) this.sourceList.push(media);
    // 导入始终填充末尾的空轨，然后由 normalizeTracks 补回一条新空轨。
    const empty = this.tracks.find((track) =>
      track.clips.length === 0 && !track.companionGroupId && track.kind !== TrackKind.Audio);
    const trackId = empty?.id ?? crypto.randomUUID();
    if (empty) {
      this.replaceClips(empty.id, [clip]);
    } else {
      this.tracks.push({
        id: trackId,
        name: `轨道 ${this.tracks.length + 1}`,
        isMain: false,
        kind: TrackKind.Video,
        companionGroupId: null,
        bindingId: null,
        clips: [clip],
      });
    }
    this.normalizeTracks();
    return this.findTrack(trackId)!;
  }

  /** 导入一份素材并生成不可拆散的视频轨与伴生音频槽。 */
  importSeparated(media: MediaInfo): SeparatedImport {
    const videoClip = createClip(media, ClipKind.Video);
    validateClip(videoClip);
    if (media.isHdr) throw new Error('暂不支持 HDR 素材，请先转换为 SDR。');
    this.saveUndo();
    if (!this.sourceList.some((source) => sameSource(source, media))) this.sourceList.push(media);
    const companionGroupId = crypto.randomUUID();

    const empty = this.tracks.find((track) =>
      track.clips.length === 0 && !track.companionGroupId && track.kind !== TrackKind.Audio);
    const videoTrackId = empty?.id ?? crypto.randomUUID();
    if (empty) {
      const index = this.tracks.findIndex((track) => track.id === empty.id);
      this.tracks[index] = {
        ...empty,
        name: `视频 ${fileName(media)}`,
        kind: TrackKind.Video,
        companionGroupId,
        bindingId: null,
        clips: [videoClip],
      };
    } else {
      this.tracks.push({
        id: videoTrackId,
        name: `视频 ${fileName(media)}`,
        isMain: false,
        kind: TrackKind.Video,
        companionGroupId,
        bindingId: null,
        clips: [videoClip],
      });
    }

    const audioTrackId = crypto.randomUUID();
    const videoIndex = this.tracks.findIndex((track) => track.id === videoTrackId);
    this.tracks.splice(videoIndex + 1, 0, {
      id: audioTrackId,
      name: hasAudio(media) ? `音频 ${fileName(media)}` : `音频槽 ${fileName(media)}`,
      isMain: false,
      kind: TrackKind.Audio,
      companionGroupId,
      bindingId: null,
      clips: [createClip(media, ClipKind.Audio)],
    });
    this.normalizeTracks();
    return {
      videoTrack: this.findTrack(videoTrackId)!,
      audioTrack: this.findTrack(audioTrackId)!,
    };
  }

  companionTracks(trackId: string): readonly VideoTrack[] {
    const track = this.findTrack(trackId);
    if (!track?.companionGroupId) return track ? [track] : [];
    return this.tracks
      .filter((candidate) => candidate.companionGroupId === track.companionGroupId)
      .sort((left, right) => (left.kind === TrackKind.Video ? -1 : 1) - (right.kind === TrackKind.Video ? -1 : 1));
  }

  bindingTracks(trackId: string): readonly VideoTrack[] {
    const track = this.findTrack(trackId);
    if (!track?.bindingId) return track ? [track] : [];
    return this.tracks.filter((candidate) => candidate.bindingId === track.bindingId);
  }

  /** 分割同步集：显式对齐绑定与每条视频的伴生音频槽做传递闭包。 */
  synchronizedTracks(trackId: string): readonly VideoTrack[] {
    const first = this.findTrack(trackId);
    if (!first) return [];
    const pending = [first];
    const included = new Map<string, VideoTrack>();
    while (pending.length > 0) {
      const current = pending.pop()!;
      if (included.has(current.id)) continue;
      included.set(current.id, current);
      for (const candidate of this.tracks) {
        const companion = current.companionGroupId
          && candidate.companionGroupId === current.companionGroupId;
        const aligned = current.bindingId && candidate.bindingId === current.bindingId;
        if ((companion || aligned) && !included.has(candidate.id)) pending.push(candidate);
      }
    }
    return this.tracks.filter((track) => included.has(track.id));
  }

  /** 用新的绑定替换所选轨道已有绑定；不足两条时不产生历史记录。 */
  bindTracks(trackIds: readonly string[]): string | undefined {
    const ids = [...new Set(trackIds)];
    const selected = ids.map((id) => this.findTrack(id));
    if (ids.length < 2 || selected.some((track) => !track || track.clips.length === 0)) return undefined;
    this.saveUndo();
    const bindingId = crypto.randomUUID();
    const touchedBindings = new Set(selected.map((track) => track!.bindingId).filter(Boolean));
    this.tracks = this.tracks.map((track) => ids.includes(track.id) ? { ...track, bindingId } : track);
    this.clearSingletonBindings(touchedBindings);
    return bindingId;
  }

  unbindTracks(trackIds: readonly string[]): boolean {
    const ids = new Set(trackIds);
    if (![...ids].some((id) => this.findTrack(id)?.bindingId)) return false;
    this.saveUndo();
    const touchedBindings = new Set(
      this.tracks.filter((track) => ids.has(track.id)).map((track) => track.bindingId).filter(Boolean),
    );
    this.tracks = this.tracks.map((track) => ids.has(track.id) ? { ...track, bindingId: null } : track);
    this.clearSingletonBindings(touchedBindings);
    return true;
  }

  findTrack(id: string): VideoTrack | undefined {
    return this.tracks.find((track) => track.id === id);
  }

  findClip(id: string): ClipPosition | undefined {
    for (const track of this.tracks) {
      let offset = 0;
      for (let index = 0; index < track.clips.length; index++) {
        const clip = track.clips[index]!;
        if (clip.id === id) {
          return { trackId: track.id, index, clip, sourceTime: clip.start, timelineStart: offset };
        }
        offset += clipDuration(clip);
      }
    }
    return undefined;
  }

  locate(trackId: string, time: number): ClipPosition | undefined {
    const track = this.findTrack(trackId);
    if (!track || track.clips.length === 0 || !Number.isFinite(time)) return undefined;
    const clamped = Math.min(Math.max(time, 0), trackDuration(track));
    let offset = 0;
    for (let index = 0; index < track.clips.length; index++) {
      const clip = track.clips[index]!;
      if (clamped < offset + clipDuration(clip) - 0.000001 || index === track.clips.length - 1) {
        const sourceTime = Math.min(
          Math.max(clip.start + (clamped - offset) * clip.speed, clip.start),
          clip.end,
        );
        return { trackId, index, clip, sourceTime, timelineStart: offset };
      }
      offset += clipDuration(clip);
    }
    return undefined;
  }

  split(trackId: string, time: number): string | undefined {
    const requested = this.findTrack(trackId);
    if (!requested || requested.clips.length === 0) return undefined;
    // 空的伴生音轨/对齐轨没有可切内容，不应阻止当前轨道分割。
    const tracks = this.synchronizedTracks(trackId).filter((track) => track.clips.length > 0);
    const cuts = tracks.map((track) => {
      const position = this.locate(track.id, time);
      if (!position) return undefined;
      const frame = 1 / position.clip.media.frameRate;
      const cut = Math.round(position.sourceTime / frame) * frame;
      return cut - position.clip.start < frame * 0.5 || position.clip.end - cut < frame * 0.5
        ? undefined
        : { track, position, cut };
    });
    // 伴生音轨和对齐绑定轨采用原子分割，避免某个视角或音轨漏掉切点。
    if (cuts.some((cut) => cut === undefined)) return undefined;
    this.saveUndo();
    let requestedId: string | undefined;
    for (const item of cuts) {
      const { track, position, cut } = item!;
      const clips = [...track.clips];
      const right = { ...position.clip, id: crypto.randomUUID(), start: cut };
      clips[position.index] = { ...position.clip, end: cut };
      clips.splice(position.index + 1, 0, right);
      this.replaceClips(track.id, clips);
      if (track.id === trackId) requestedId = right.id;
    }
    return requestedId;
  }

  move(clipId: string, targetTrackId: string, insertionIndex: number): boolean {
    const source = this.findClip(clipId);
    const target = this.findTrack(targetTrackId);
    if (!source || !target) return false;
    const sourceTrack = this.findTrack(source.trackId)!;
    if (sourceTrack.kind !== target.kind) return false;
    if (insertionIndex < 0 || insertionIndex > target.clips.length) {
      throw new RangeError('insertionIndex');
    }
    if (source.trackId === targetTrackId && (insertionIndex === source.index || insertionIndex === source.index + 1)) {
      return false;
    }
    this.saveUndo();
    const sourceClips = [...this.findTrack(source.trackId)!.clips];
    sourceClips.splice(source.index, 1);
    if (source.trackId === targetTrackId) {
      let index = insertionIndex;
      if (index > source.index) index--;
      sourceClips.splice(index, 0, source.clip);
      this.replaceClips(source.trackId, sourceClips);
    } else {
      const targetClips = [...target.clips];
      targetClips.splice(insertionIndex, 0, source.clip);
      this.replaceClips(source.trackId, sourceClips);
      const targetIndex = this.tracks.findIndex((track) => track.id === targetTrackId);
      this.tracks[targetIndex] = {
        ...this.tracks[targetIndex]!,
        bindingId: null,
        clips: targetClips,
      };
    }
    this.normalizeTracks();
    return true;
  }

  /** 可放置整组伴生轨的位置；末尾普通空轨不参与排序。 */
  trackInsertionBoundaries(): readonly number[] {
    const boundaries = [0];
    const seen = new Set<string>();
    let index = 0;
    while (index < this.tracks.length) {
      const track = this.tracks[index]!;
      if (track.clips.length === 0 && !track.companionGroupId) break;
      if (track.companionGroupId && !seen.has(track.companionGroupId)) {
        seen.add(track.companionGroupId);
        index += this.tracks.filter((candidate) => candidate.companionGroupId === track.companionGroupId).length;
      } else {
        index++;
      }
      boundaries.push(index);
    }
    return boundaries;
  }

  moveTrack(trackId: string, insertionIndex: number): boolean {
    const sourceIndex = this.tracks.findIndex((track) => track.id === trackId);
    if (sourceIndex < 0 || (this.tracks[sourceIndex]!.clips.length === 0
      && !this.tracks[sourceIndex]!.companionGroupId)) return false;
    const contentCount = this.trackInsertionBoundaries().at(-1) ?? 0;
    if (insertionIndex < 0 || insertionIndex > contentCount) throw new RangeError('insertionIndex');
    const targetBoundary = this.trackInsertionBoundaries().reduce((best, boundary) =>
      Math.abs(boundary - insertionIndex) <= Math.abs(best - insertionIndex) ? boundary : best);
    const unit = this.companionTracks(trackId);
    const unitIds = new Set(unit.map((track) => track.id));
    const unitIndexes = this.tracks
      .map((track, index) => unitIds.has(track.id) ? index : -1)
      .filter((index) => index >= 0);
    const unitStart = Math.min(...unitIndexes);
    const unitEnd = Math.max(...unitIndexes) + 1;
    if (targetBoundary >= unitStart && targetBoundary <= unitEnd) return false;
    this.saveUndo();
    const orderedUnit = [
      ...unit.filter((track) => track.kind === TrackKind.Video),
      ...unit.filter((track) => track.kind === TrackKind.Audio),
    ];
    this.tracks = this.tracks.filter((track) => !unitIds.has(track.id));
    const removedBefore = unitIndexes.filter((index) => index < targetBoundary).length;
    this.tracks.splice(targetBoundary - removedBefore, 0, ...orderedUnit);
    return true;
  }

  setSpeed(clipId: string, speed: number): boolean {
    validateSpeed(speed);
    const position = this.findClip(clipId);
    if (!position || position.clip.speed === speed) return false;
    this.saveUndo();
    const clips = [...this.findTrack(position.trackId)!.clips];
    clips[position.index] = { ...position.clip, speed };
    this.replaceClips(position.trackId, clips);
    return true;
  }

  rename(clipId: string, name: string): boolean {
    if (!name.trim()) throw new Error('请输入片段名称。');
    const position = this.findClip(clipId);
    if (!position) return false;
    const trimmed = name.trim();
    if (displayName(position.clip) === trimmed) return false;
    this.saveUndo();
    const clips = [...this.findTrack(position.trackId)!.clips];
    clips[position.index] = { ...position.clip, name: trimmed };
    this.replaceClips(position.trackId, clips);
    return true;
  }

  duplicate(clipId: string): string | undefined {
    const position = this.findClip(clipId);
    if (!position) return undefined;
    this.saveUndo();
    const copy = { ...position.clip, id: crypto.randomUUID() };
    if (clipDuration(copy) < 10) {
      const clips = [...this.findTrack(position.trackId)!.clips];
      clips.splice(position.index + 1, 0, copy);
      this.replaceClips(position.trackId, clips);
    } else {
      const row = this.tracks.findIndex((track) => track.id === position.trackId);
      this.tracks.splice(row + 1, 0, {
        id: crypto.randomUUID(),
        name: `轨道 ${this.tracks.length + 1}`,
        isMain: false,
        kind: this.findTrack(position.trackId)?.kind ?? TrackKind.Video,
        companionGroupId: null,
        bindingId: null,
        clips: [copy],
      });
    }
    this.normalizeTracks();
    return copy.id;
  }

  delete(clipId: string): boolean {
    const position = this.findClip(clipId);
    if (!position) return false;
    this.saveUndo();
    const clips = [...this.findTrack(position.trackId)!.clips];
    clips.splice(position.index, 1);
    this.replaceClips(position.trackId, clips);
    this.normalizeTracks();
    return true;
  }

  /**
   * 跟随源时钟跨过相邻切口而不重新 seek；只有跨过被删除的区间才需要 seek。
   * 与桌面端一致，这是预览能不中断播放的关键。
   */
  advancePlayback(trackId: string, clipId: string, sourceTime: number): ClipPlayback | undefined {
    const track = this.findTrack(trackId);
    let position: ClipPosition | undefined = this.findClip(clipId);
    if (!Number.isFinite(sourceTime) || !track || !position || position.trackId !== trackId) {
      return undefined;
    }
    for (;;) {
      if (sourceTime < position.clip.end) {
        return {
          position: { ...position, sourceTime: Math.max(position.clip.start, sourceTime) },
          requiresSeek: false,
          reachedEnd: false,
        };
      }
      if (position.index === track.clips.length - 1) {
        return {
          position: { ...position, sourceTime: position.clip.end },
          requiresSeek: false,
          reachedEnd: true,
        };
      }
      const next: VideoClip = track.clips[position.index + 1]!;
      const current: ClipPosition = position;
      const mustSeek =
        !sameSource(next.media, position.clip.media) ||
        Math.abs(next.start - position.clip.end) > 0.000001;
      position = {
        trackId,
        index: current.index + 1,
        clip: next,
        sourceTime: next.start,
        timelineStart: current.timelineStart + clipDuration(current.clip),
      };
      if (mustSeek) return { position, requiresSeek: true, reachedEnd: false };
    }
  }

  resolveExportTrack(selectedTrackId: string | null): VideoTrack | undefined {
    if (selectedTrackId !== null) {
      const selected = this.findTrack(selectedTrackId);
      return selected && selected.clips.length > 0 && selected.kind !== TrackKind.Audio ? selected : undefined;
    }
    const tracks = this.exportableTracks;
    return tracks.length === 1 ? tracks[0] : undefined;
  }

  exportClips(trackId: string): readonly VideoClip[] {
    const track = this.findTrack(trackId);
    if (!track) throw new Error('导出轨道不存在。');
    return [...track.clips];
  }

  exportClipsForMain(): readonly VideoClip[] {
    return this.exportClips(this.mainTrack.id);
  }

  undo(): boolean {
    return this.restore(this.undoStack, this.redoStack);
  }

  redo(): boolean {
    return this.restore(this.redoStack, this.undoStack);
  }

  private replaceClips(trackId: string, clips: VideoClip[]): void {
    const index = this.tracks.findIndex((track) => track.id === trackId);
    this.tracks[index] = { ...this.tracks[index]!, clips };
  }

  private clearSingletonBindings(bindingIds: ReadonlySet<string | null | undefined>): void {
    for (const bindingId of bindingIds) {
      if (!bindingId) continue;
      if (this.tracks.filter((track) => track.bindingId === bindingId).length < 2) {
        this.tracks = this.tracks.map((track) => track.bindingId === bindingId ? { ...track, bindingId: null } : track);
      }
    }
  }

  /** 伴生视频/音频相邻排列，最后保留一条用于接收视频片段的普通空轨。 */
  private normalizeTracks(): void {
    const content = this.tracks.filter((track) => track.clips.length > 0 || track.companionGroupId);
    const empty = this.tracks.find((track) => track.clips.length === 0 && !track.companionGroupId);
    if (content.length === 0) {
      const only = empty ?? {
        id: crypto.randomUUID(),
        name: '轨道 1',
        isMain: true,
        kind: TrackKind.Video,
        companionGroupId: null,
        bindingId: null,
        clips: [],
      };
      this.tracks = [{
        ...only,
        name: '轨道 1',
        isMain: true,
        kind: TrackKind.Video,
        companionGroupId: null,
        bindingId: null,
        clips: [],
      }];
      return;
    }

    const main = content.find((track) => track.isMain && track.kind !== TrackKind.Audio)
      ?? content.find((track) => track.kind !== TrackKind.Audio)
      ?? content[0]!;
    const contentTracks: VideoTrack[] = [];
    const seenGroups = new Set<string>();
    for (const track of content) {
      if (!track.companionGroupId) {
        contentTracks.push({ ...track, isMain: track.id === main.id });
        continue;
      }
      if (seenGroups.has(track.companionGroupId)) continue;
      seenGroups.add(track.companionGroupId);
      const group = content.filter((candidate) => candidate.companionGroupId === track.companionGroupId);
      contentTracks.push(
        ...group.filter((candidate) => candidate.kind === TrackKind.Video)
          .map((candidate) => ({ ...candidate, isMain: candidate.id === main.id })),
        ...group.filter((candidate) => candidate.kind === TrackKind.Audio)
          .map((candidate) => ({ ...candidate, isMain: false })),
      );
    }
    const trailingEmpty = empty ?? {
      id: crypto.randomUUID(),
      name: `轨道 ${contentTracks.length + 1}`,
      isMain: false,
      kind: TrackKind.Video,
      companionGroupId: null,
      bindingId: null,
      clips: [],
    };
    this.tracks = [...contentTracks, {
      ...trailingEmpty,
      name: `轨道 ${contentTracks.length + 1}`,
      isMain: false,
      kind: TrackKind.Video,
      companionGroupId: null,
      bindingId: null,
      clips: [],
    }];
    this.clearSingletonBindings(new Set(this.tracks.map((track) => track.bindingId)));
  }

  private snapshot(): ProjectState {
    return { tracks: [...this.tracks], sources: [...this.sourceList] };
  }

  private saveUndo(): void {
    this.undoStack.push(this.snapshot());
    this.redoStack = [];
  }

  private restore(from: ProjectState[], to: ProjectState[]): boolean {
    const state = from.pop();
    if (!state) return false;
    to.push(this.snapshot());
    this.tracks = [...state.tracks];
    this.sourceList = [...state.sources];
    this.normalizeTracks();
    return true;
  }
}

/**
 * 用 const 对象 + 联合类型代替 enum：既可被 tsc 擦除，也能被 Node 的
 * --experimental-strip-types 直接执行，测试无需额外编译步骤。
 */
export const ExportQuality = {
  Original: 'original',
  High: 'high',
  Balanced: 'balanced',
  Compact: 'compact',
} as const;
export type ExportQuality = (typeof ExportQuality)[keyof typeof ExportQuality];

export const VideoEncoder = {
  Nvidia: 'nvidia',
  Software: 'software',
  /** 浏览器 WebCodecs 平台编码（桌面端没有这一项）。 */
  WebCodecs: 'webcodecs',
} as const;
export type VideoEncoder = (typeof VideoEncoder)[keyof typeof VideoEncoder];

export interface ExportOptions {
  readonly quality: ExportQuality;
  readonly width: number | null;
  readonly height: number | null;
  readonly encoder: VideoEncoder;
  readonly hardwareDecode: boolean;
}

export const defaultExportOptions: ExportOptions = {
  quality: ExportQuality.Original,
  width: null,
  height: null,
  encoder: VideoEncoder.WebCodecs,
  hardwareDecode: true,
};

export function qualityValue(quality: ExportQuality): number {
  switch (quality) {
    case ExportQuality.Original:
      return 16;
    case ExportQuality.High:
      return 19;
    case ExportQuality.Balanced:
      return 23;
    case ExportQuality.Compact:
      return 28;
  }
}

export function getDimensions(
  options: Pick<ExportOptions, 'width' | 'height'>,
  media: MediaInfo,
): { width: number; height: number } {
  if ((options.width === null) !== (options.height === null)) {
    throw new Error('宽度和高度必须同时填写。');
  }
  const w = options.width ?? media.width;
  const h = options.height ?? media.height;
  if (w < 2 || h < 2 || w > 7680 || h > 7680) {
    throw new Error('输出宽高必须在 2–7680 像素之间。');
  }
  if (options.width !== null && (w % 2 !== 0 || h % 2 !== 0)) {
    throw new Error('H.264 输出宽高必须为偶数。');
  }
  return { width: w + (w % 2), height: h + (h % 2) };
}
