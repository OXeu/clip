/**
 * 端到端测试的工具：生成真实素材、启动静态服务器、驱动无头 Chromium。
 *
 * 目标是在真实浏览器里跑通「导入 → 分割 → 导出」，并验证产出的 MP4 能被
 * FFprobe 解析、时长与分辨率符合预期。这是唯一能同时覆盖 WebCodecs、
 * Mediabunny 与 ffmpeg.wasm 的验证方式。
 */

import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer, type Server } from 'node:http';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const repoRoot = resolve(webRoot, '..');
export const fixtureDir = join(webRoot, 'test', 'fixtures', 'media');
export const artifactDir = join(webRoot, 'test', 'artifacts');

/** 允许通过环境变量指定自带的 FFmpeg/FFprobe。 */
export function resolveFfmpeg(): string {
  return process.env.CLIP_FFMPEG ?? 'ffmpeg';
}
export function resolveFfprobe(): string {
  return process.env.CLIP_FFPROBE ?? 'ffprobe';
}

export function toolVersion(binary: string): string {
  try {
    return execFileSync(binary, ['-version'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] })
      .split('\n')[0]!;
  } catch (error) {
    throw new Error(
      `无法运行 ${binary}：${error instanceof Error ? error.message : String(error)}\n` +
        '请安装 FFmpeg，或通过 CLIP_FFMPEG / CLIP_FFPROBE 指向可执行文件。',
    );
  }
}

function fixtureArgs(
  path: string,
  duration: number,
  hue: number,
  frequency: number,
  size: string,
  rate: number,
): string[] {
  return [
    '-hide_banner', '-loglevel', 'error', '-nostdin', '-y',
    '-f', 'lavfi', '-i', `testsrc2=size=${size}:rate=${rate}:duration=${duration}`,
    '-f', 'lavfi', '-i', `sine=frequency=${frequency}:sample_rate=48000:duration=${duration}`,
    '-filter_complex', `[0:v]hue=h=${hue},format=yuv420p[v]`,
    '-map', '[v]', '-map', '1:a',
    '-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '23', '-pix_fmt', 'yuv420p',
    '-c:a', 'aac', '-b:a', '128k', '-ac', '2',
    '-movflags', '+faststart',
    path,
  ];
}

function silentArgs(path: string, duration: number, size: string, rate: number): string[] {
  return [
    '-hide_banner', '-loglevel', 'error', '-nostdin', '-y',
    '-f', 'lavfi', '-i', `testsrc2=size=${size}:rate=${rate}:duration=${duration}`,
    '-vf', 'hue=h=200,format=yuv420p',
    '-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '23', '-an',
    '-movflags', '+faststart',
    path,
  ];
}

/** 前半纯红、后半纯蓝，用于确认剪辑边界没有泄漏相邻帧。 */
function cutBoundaryArgs(path: string): string[] {
  return [
    '-hide_banner', '-loglevel', 'error', '-nostdin', '-y',
    '-f', 'lavfi', '-i', 'color=c=red:size=320x180:rate=30:duration=1',
    '-f', 'lavfi', '-i', 'color=c=blue:size=320x180:rate=30:duration=1',
    '-filter_complex', '[0:v][1:v]concat=n=2:v=1:a=0,format=yuv420p[v]',
    '-map', '[v]',
    '-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '18', '-pix_fmt', 'yuv420p', '-an',
    '-movflags', '+faststart',
    path,
  ];
}

function matroskaCopyArgs(input: string, output: string): string[] {
  return [
    '-hide_banner', '-loglevel', 'error', '-nostdin', '-y',
    '-i', input,
    '-map', '0:v:0', '-map', '0:a:0?', '-c', 'copy',
    '-f', 'matroska', output,
  ];
}

export interface FixtureFile {
  readonly name: string;
  readonly path: string;
  readonly duration: number;
  readonly width: number;
  readonly height: number;
  readonly hasAudio: boolean;
}

/**
 * 生成测试素材。已存在则跳过，除非 CLIP_REFRESH_FIXTURES=1。
 */
export function prepareFixtures(): readonly FixtureFile[] {
  const binary = resolveFfmpeg();
  mkdirSync(fixtureDir, { recursive: true });

  const specs: { file: FixtureFile; args: string[] }[] = [
    {
      file: { name: 'landscape-4s-30fps.mp4', path: join(fixtureDir, 'landscape-4s-30fps.mp4'), duration: 4, width: 640, height: 360, hasAudio: true },
      args: fixtureArgs(join(fixtureDir, 'landscape-4s-30fps.mp4'), 4, 0, 440, '640x360', 30),
    },
    {
      file: { name: 'portrait-3s-24fps.mp4', path: join(fixtureDir, 'portrait-3s-24fps.mp4'), duration: 3, width: 360, height: 640, hasAudio: true },
      args: fixtureArgs(join(fixtureDir, 'portrait-3s-24fps.mp4'), 3, 90, 660, '360x640', 24),
    },
    {
      file: { name: 'silent-2s-30fps.mp4', path: join(fixtureDir, 'silent-2s-30fps.mp4'), duration: 2, width: 320, height: 240, hasAudio: false },
      args: silentArgs(join(fixtureDir, 'silent-2s-30fps.mp4'), 2, '320x240', 30),
    },
    {
      file: { name: 'cut-boundary-2s-30fps.mp4', path: join(fixtureDir, 'cut-boundary-2s-30fps.mp4'), duration: 2, width: 320, height: 180, hasAudio: false },
      args: cutBoundaryArgs(join(fixtureDir, 'cut-boundary-2s-30fps.mp4')),
    },
    {
      file: { name: 'landscape-4s-30fps.mkv', path: join(fixtureDir, 'landscape-4s-30fps.mkv'), duration: 4, width: 640, height: 360, hasAudio: true },
      args: matroskaCopyArgs(
        join(fixtureDir, 'landscape-4s-30fps.mp4'),
        join(fixtureDir, 'landscape-4s-30fps.mkv'),
      ),
    },
    {
      file: { name: 'high-rate-1s-60fps.mp4', path: join(fixtureDir, 'high-rate-1s-60fps.mp4'), duration: 1, width: 320, height: 180, hasAudio: false },
      args: silentArgs(join(fixtureDir, 'high-rate-1s-60fps.mp4'), 1, '320x180', 60),
    },
  ];

  const refresh = process.env.CLIP_REFRESH_FIXTURES === '1';
  for (const spec of specs) {
    if (refresh || !existsSync(spec.file.path)) {
      execFileSync(binary, spec.args, { stdio: ['ignore', 'ignore', 'pipe'] });
    }
  }
  return specs.map((spec) => spec.file);
}

/** 用 FFprobe 校验产物，返回值直接来自真实解析结果。 */
export interface ProbeSummary {
  readonly duration: number;
  readonly width: number;
  readonly height: number;
  readonly videoCodec: string;
  readonly audioCodec: string | null;
  readonly frameCount: number;
  readonly hasAudio: boolean;
}

export function probeFile(path: string): ProbeSummary {
  const json = execFileSync(
    resolveFfprobe(),
    ['-v', 'error', '-show_streams', '-show_format', '-count_frames', '-of', 'json', path],
    { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 },
  );
  const parsed = JSON.parse(json) as {
    streams: {
      codec_type?: string;
      codec_name?: string;
      width?: number;
      height?: number;
      duration?: string;
      nb_read_frames?: string;
      nb_frames?: string;
    }[];
    format?: { duration?: string };
  };
  const video = parsed.streams.find((s) => s.codec_type === 'video');
  const audio = parsed.streams.find((s) => s.codec_type === 'audio');
  return {
    duration: Number(parsed.format?.duration ?? video?.duration ?? 0),
    width: video?.width ?? 0,
    height: video?.height ?? 0,
    videoCodec: video?.codec_name ?? '',
    audioCodec: audio?.codec_name ?? null,
    frameCount: Number(video?.nb_read_frames ?? video?.nb_frames ?? 0),
    hasAudio: audio !== undefined,
  };
}

const MIME: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.wasm': 'application/wasm',
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
  '.map': 'application/json; charset=utf-8',
};

export interface StaticServer {
  readonly origin: string;
  readonly close: () => Promise<void>;
}

/**
 * 提供 dist 目录，并附带跨源隔离响应头。
 * 这样多线程 ffmpeg.wasm 才能启用，与 web/public/_headers 的生产行为一致。
 */
export async function startStaticServer(root: string, isolate = true): Promise<StaticServer> {
  const server: Server = createServer((request, response) => {
    const url = new URL(request.url ?? '/', 'http://localhost');
    let pathname = decodeURIComponent(url.pathname);
    if (pathname.endsWith('/')) pathname += 'index.html';
    // 阻止路径穿越。
    const target = resolve(join(root, pathname));
    if (!target.startsWith(resolve(root))) {
      response.writeHead(403).end('forbidden');
      return;
    }
    let body: Buffer;
    try {
      body = readFileSync(target);
    } catch {
      response.writeHead(404).end('not found');
      return;
    }
    const headers: Record<string, string> = {
      'content-type': MIME[extname(target)] ?? 'application/octet-stream',
      'cache-control': 'no-store',
    };
    if (isolate) {
      headers['cross-origin-opener-policy'] = 'same-origin';
      headers['cross-origin-embedder-policy'] = 'require-corp';
      headers['cross-origin-resource-policy'] = 'cross-origin';
    }
    response.writeHead(200, headers);
    response.end(body);
  });

  await new Promise<void>((resolveReady) => server.listen(0, '127.0.0.1', resolveReady));
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('无法启动静态服务器。');
  return {
    origin: `http://127.0.0.1:${address.port}`,
    close: () =>
      new Promise<void>((resolveClosed, reject) =>
        server.close((error) => (error ? reject(error) : resolveClosed())),
      ),
  };
}

/** 构建 dist；端到端测试必须针对真实产物而不是源码。 */
export function buildSite(): void {
  try {
    execFileSync('npm', ['run', 'build'], { cwd: webRoot, stdio: ['ignore', 'pipe', 'pipe'] });
  } catch (error) {
    const detail = error as { stdout?: Buffer; stderr?: Buffer; status?: number };
    const stdout = detail.stdout?.toString() ?? '';
    const stderr = detail.stderr?.toString() ?? '';
    throw new Error(
      `构建失败（退出码 ${detail.status ?? '未知'}）。\n${stdout}\n${stderr}`.trim(),
    );
  }
}

export function ensureArtifacts(): string {
  mkdirSync(artifactDir, { recursive: true });
  return artifactDir;
}

export function writeArtifact(name: string, data: Uint8Array | string): string {
  const path = join(ensureArtifacts(), name);
  writeFileSync(path, data);
  return path;
}

export function cleanArtifacts(): void {
  rmSync(artifactDir, { recursive: true, force: true });
}

/**
 * 提取某一时刻的原始 YUV420 采样（不做任何色彩矩阵转换）。
 *
 * 只用 RGB 比较会误判：源文件常常未标记色彩矩阵，播放器只能猜（常见是 BT.601），
 * 而 WebCodecs 输出会明确标记 bt709，于是 RGB 上看起来「偏色」，
 * 实际上 YUV 采样是一致的。要判断编码是否保真，必须看原始采样。
 */
export function extractYuvFrame(path: string, atSeconds: number): Buffer {
  return execFileSync(
    resolveFfmpeg(),
    [
      '-hide_banner', '-loglevel', 'error', '-nostdin',
      '-ss', String(atSeconds), '-i', path, '-frames:v', '1',
      '-pix_fmt', 'yuv420p', '-f', 'rawvideo', '-',
    ],
    { maxBuffer: 256 * 1024 * 1024 },
  );
}

/** 两组 YUV 数据的平均绝对差与最大差。 */
export function yuvDelta(a: Buffer, b: Buffer): { mean: number; max: number } {
  const length = Math.min(a.length, b.length);
  if (length === 0) throw new Error('没有可比较的 YUV 数据。');
  let sum = 0;
  let max = 0;
  for (let index = 0; index < length; index++) {
    const delta = Math.abs(a[index]! - b[index]!);
    sum += delta;
    if (delta > max) max = delta;
  }
  return { mean: sum / length, max };
}

/**
 * 用 FFmpeg 把某个媒体文件的音频解码为 16 位小端 PCM。
 * 用于逐样本比较两条导出路线的音频是否一致。
 */
export function extractPcm(path: string): Buffer {
  return execFileSync(
    resolveFfmpeg(),
    [
      '-hide_banner', '-loglevel', 'error', '-nostdin', '-i', path,
      // 单声道 48 kHz s16le，便于逐样本对比。
      '-vn', '-ac', '1', '-ar', '48000', '-f', 's16le', '-',
    ],
    { maxBuffer: 256 * 1024 * 1024 },
  );
}

/** 每帧缩成一个 RGB 像素，便于检查整段输出是否混入了错误颜色。 */
export function extractRgbFrameColors(path: string): Buffer {
  return execFileSync(
    resolveFfmpeg(),
    [
      '-hide_banner', '-loglevel', 'error', '-nostdin', '-i', path,
      '-an', '-vf', 'scale=1:1', '-pix_fmt', 'rgb24', '-f', 'rawvideo', '-',
    ],
    { maxBuffer: 16 * 1024 * 1024 },
  );
}
