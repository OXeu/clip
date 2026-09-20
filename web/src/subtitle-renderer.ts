import {
  type SubtitleCue,
  type SubtitleRegion,
  SubtitleVerticalAlignment,
  type VideoTrack,
  subtitleCues,
} from './model.ts';

export interface SubtitleRenderTrack {
  readonly cues: readonly SubtitleCue[];
  readonly region: SubtitleRegion;
}

export interface SubtitleOverlayImage {
  readonly name: string;
  readonly start: number;
  readonly end: number;
  readonly data: Blob;
}

export function subtitleRenderTrack(track: VideoTrack | undefined): SubtitleRenderTrack | null {
  return track?.subtitleRegion
    ? { cues: subtitleCues(track), region: track.subtitleRegion }
    : null;
}

function wrappedLines(
  context: OffscreenCanvasRenderingContext2D | CanvasRenderingContext2D,
  text: string,
  maxWidth: number,
): string[] {
  const lines: string[] = [];
  for (const paragraph of text.replace(/\r/g, '').split('\n')) {
    if (!paragraph) {
      lines.push('');
      continue;
    }
    let line = '';
    for (const character of paragraph) {
      const candidate = line + character;
      if (line && context.measureText(candidate).width > maxWidth) {
        lines.push(line.trimEnd());
        line = character.trimStart();
      } else {
        line = candidate;
      }
    }
    lines.push(line);
  }
  return lines.length > 0 ? lines : [''];
}

export function drawSubtitleText(
  context: OffscreenCanvasRenderingContext2D | CanvasRenderingContext2D,
  width: number,
  height: number,
  cue: SubtitleCue,
  region: SubtitleRegion,
): void {
  const x = region.x * width;
  const y = region.y * height;
  const boxWidth = region.width * width;
  const boxHeight = region.height * height;
  const fontSize = Math.max(16, Math.round(height * 0.046));
  const lineHeight = Math.round(fontSize * 1.28);
  const horizontalPadding = Math.max(6, Math.round(fontSize * 0.35));
  context.save();
  context.font = `650 ${fontSize}px system-ui, sans-serif`;
  context.textAlign = 'center';
  context.textBaseline = 'top';
  context.lineJoin = 'round';
  const lines = wrappedLines(context, cue.text, Math.max(1, boxWidth - horizontalPadding * 2));
  const textHeight = lines.length * lineHeight;
  const top = region.alignment === SubtitleVerticalAlignment.Top
    ? y
    : region.alignment === SubtitleVerticalAlignment.Center
      ? y + (boxHeight - textHeight) / 2
      : y + boxHeight - textHeight;
  context.strokeStyle = 'rgba(0, 0, 0, 0.92)';
  context.fillStyle = '#ffffff';
  context.lineWidth = Math.max(3, fontSize * 0.14);
  for (let index = 0; index < lines.length; index++) {
    const lineY = top + index * lineHeight;
    context.strokeText(lines[index]!, x + boxWidth / 2, lineY, boxWidth - horizontalPadding * 2);
    context.fillText(lines[index]!, x + boxWidth / 2, lineY, boxWidth - horizontalPadding * 2);
  }
  context.restore();
}

export function drawSubtitleTracksAtTime(
  context: OffscreenCanvasRenderingContext2D | CanvasRenderingContext2D,
  width: number,
  height: number,
  tracks: readonly SubtitleRenderTrack[] | null | undefined,
  time: number,
): void {
  for (const track of tracks ?? []) {
    const cue = track.cues.find((candidate) => time >= candidate.start && time < candidate.end);
    if (cue) drawSubtitleText(context, width, height, cue, track.region);
  }
}

export async function createSubtitleOverlayImages(
  tracks: readonly SubtitleRenderTrack[] | null | undefined,
  width: number,
  height: number,
): Promise<readonly SubtitleOverlayImage[]> {
  const result: SubtitleOverlayImage[] = [];
  for (const track of tracks ?? []) {
    for (const cue of track.cues) {
      const canvas = new OffscreenCanvas(width, height);
      const context = canvas.getContext('2d');
      if (!context) throw new Error('无法创建字幕图层。');
      context.clearRect(0, 0, width, height);
      drawSubtitleText(context, width, height, cue, track.region);
      result.push({
        name: `subtitle-${String(result.length).padStart(4, '0')}.png`,
        start: cue.start,
        end: cue.end,
        data: await canvas.convertToBlob({ type: 'image/png' }),
      });
    }
  }
  return result;
}
