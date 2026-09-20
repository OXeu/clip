/**
 * ffmpeg.wasm 封装。
 *
 * 负责三件事：
 *   1. 选择多线程或单线程核心（取决于页面是否跨源隔离）。
 *   2. 把参数、输入文件、filter 脚本喂给 wasm，并解析 -progress 输出。
 *   3. 把结果读回为 Uint8Array。
 *
 * 为了在 Node 里做端到端验证，本模块不直接依赖 DOM，只依赖传入的 coreURL。
 */

import { FFmpeg } from '@ffmpeg/ffmpeg';
import { fetchFile } from '@ffmpeg/util';

/** 与桌面端一致的进度信息结构。 */
export interface ExportProgress {
  readonly fraction: number;
  readonly message: string;
}

export interface FfmpegCoreUrls {
  readonly coreURL: string;
  readonly wasmURL: string;
  readonly workerURL?: string;
}

export interface FfmpegRunOptions {
  /** 参数列表，不含可执行文件名。 */
  readonly args: readonly string[];
  /** 写入 wasm 文件系统的输入。 */
  readonly inputs: readonly { readonly name: string; readonly data: Uint8Array | Blob }[];
  /** 需要读回的产物文件名。 */
  readonly output: string;
  /** 预期导出总时长（秒），用于换算进度。 */
  readonly duration: number;
  /** -progress 输出的时间基准（微秒）。 */
  readonly onProgress?: (progress: ExportProgress) => void;
  readonly signal?: AbortSignal;
}

export interface FfmpegResult {
  readonly data: Uint8Array;
  readonly log: string;
}

/** 单线程与多线程核心的静态资源位置（由 scripts/copy-ffmpeg-core.mjs 生成）。 */
export function coreUrlsFromBase(base: string, multithreaded: boolean): FfmpegCoreUrls {
  const normalized = base.endsWith('/') ? base : `${base}/`;
  return multithreaded
    ? {
        coreURL: `${normalized}core-mt/ffmpeg-core.js`,
        wasmURL: `${normalized}core-mt/ffmpeg-core.wasm`,
        workerURL: `${normalized}core-mt/ffmpeg-core.worker.js`,
      }
    : {
        coreURL: `${normalized}core/ffmpeg-core.js`,
        wasmURL: `${normalized}core/ffmpeg-core.wasm`,
      };
}

/**
 * 多线程核心是否可用，由一次真实试编码决定，整个页面会话内缓存。
 *
 * 为什么必须试编码而不是看加载是否成功：@ffmpeg/core-mt 会按
 * navigator.hardwareConcurrency 预建 pthread 工作池（默认上限 32），
 * 在核心数很多或受限的容器里，load() 会成功但 exec() 直接挂起。
 * 只信任「能不能真的编码一帧」，与桌面端用试编码判断 NVENC 的思路一致。
 */
let multithreadedVerdict: boolean | null = null;

/** 试编码一帧；超时或非零退出码都视为不可用。 */
async function selfTest(ffmpeg: FFmpeg, timeoutMs: number): Promise<boolean> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<'timeout'>((resolve) => {
    timer = setTimeout(() => resolve('timeout'), timeoutMs);
  });
  try {
    const encode = ffmpeg
      .exec([
        '-hide_banner', '-nostdin', '-loglevel', 'error',
        // 与 NVENC 试编码同理：用一张普通 SDR 画面跑最小可用的真实编码。
        '-f', 'lavfi', '-i', 'color=c=black:s=320x240:r=1',
        '-frames:v', '1', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-f', 'null', '-',
      ])
      .then((code): 'ok' | 'failed' => (code === 0 ? 'ok' : 'failed'))
      .catch((): 'failed' => 'failed');
    const outcome = await Promise.race([encode, timeout]);
    if (outcome === 'ok') return true;
    // 超时说明工作池已死锁，必须终止 worker 才能继续使用。
    ffmpeg.terminate();
    return false;
  } catch {
    try {
      ffmpeg.terminate();
    } catch {
      /* 已经终止。 */
    }
    return false;
  } finally {
    if (timer) clearTimeout(timer);
  }
}

/**
 * 试编码超时。多线程死锁表现为完全无响应（实测 60 秒也不返回），
 * 而正常环境下编码一帧只需几十毫秒，因此可以用很短的超时区分两者。
 */
const SELF_TEST_TIMEOUT_MS = 8_000;
/** 核心约 32 MB；正常网络应在此时间内完成，超时则给出明确错误而不是永久等待。 */
const CORE_LOAD_TIMEOUT_MS = 90_000;

/**
 * Worker 内的动态 import 失败时，浏览器通常只抛出一条没有 HTTP
 * 状态的 TypeError。额外请求一次体积较小的 core JS，把最常见的
 * 「部署遗漏」和「MIME 错误」变成可直接处理的提示。
 */
async function explainCoreLoadFailure(
  error: unknown,
  urls: FfmpegCoreUrls,
  multithreaded: boolean,
): Promise<Error> {
  const mode = multithreaded ? '多线程' : '单线程';
  const original = error instanceof Error ? error.message : String(error);
  try {
    const response = await fetch(urls.coreURL, { cache: 'no-store' });
    if (!response.ok) {
      return new Error(
        `无法加载${mode} ffmpeg.wasm 核心：${urls.coreURL} 返回 HTTP ${response.status}。` +
          '\n请重新部署完整的 dist/ffmpeg 目录。',
      );
    }
    const contentType = response.headers.get('content-type') ?? '';
    await response.body?.cancel();
    if (!/(?:java|ecma)script/i.test(contentType)) {
      return new Error(
        `无法加载${mode} ffmpeg.wasm 核心：${urls.coreURL} 的 Content-Type ` +
          `为 ${contentType || '空'}，需要 JavaScript MIME 类型。`,
      );
    }
  } catch (probeError) {
    return new Error(
      `无法访问${mode} ffmpeg.wasm 核心 ${urls.coreURL}：` +
        (probeError instanceof Error ? probeError.message : String(probeError)),
    );
  }
  return new Error(
    `加载${mode} ffmpeg.wasm 核心失败：${original}\n` +
      `核心：${urls.coreURL}\nWASM：${urls.wasmURL}`,
  );
}

export class FfmpegRunner {
  private ffmpeg: FFmpeg | null = null;
  private loadedMultithreaded: boolean | null = null;
  private logLines: string[] = [];

  get multithreaded(): boolean | null {
    return this.loadedMultithreaded;
  }

  get ready(): boolean {
    return this.ffmpeg !== null;
  }

  /**
   * 加载核心。优先多线程；若加载失败或试编码不通过则回退单线程。
   * 结果在会话内缓存，避免每次导出都重试探编码。
   */
  async load(
    base: string,
    preferMultithreaded: boolean,
    onLog?: (line: string) => void,
    signal?: AbortSignal,
  ): Promise<{ multithreaded: boolean; fellBackFromMultithreaded: boolean }> {
    if (this.ffmpeg) {
      return {
        multithreaded: this.loadedMultithreaded === true,
        fellBackFromMultithreaded: false,
      };
    }

    // 已判定多线程不可用时，直接使用单线程，不再重试。
    const wantMultithreaded = preferMultithreaded && multithreadedVerdict !== false;
    const order = wantMultithreaded ? [true, false] : [false];
    let lastError: unknown = null;
    let sawMultithreadedFailure = false;

    for (const multithreaded of order) {
      if (signal?.aborted) throw new DOMException('已取消导出', 'AbortError');
      const ffmpeg = new FFmpeg();
      ffmpeg.on('log', ({ message }) => {
        this.logLines.push(message);
        // 只保留近期日志，避免长导出占用过多内存。
        if (this.logLines.length > 4000) this.logLines.splice(0, 2000);
        onLog?.(message);
      });
      const loadController = new AbortController();
      let loadTimedOut = false;
      const abortLoad = (): void => loadController.abort();
      const loadTimer = setTimeout(() => {
        loadTimedOut = true;
        loadController.abort();
      }, CORE_LOAD_TIMEOUT_MS);
      signal?.addEventListener('abort', abortLoad, { once: true });
      try {
        const coreUrls = coreUrlsFromBase(base, multithreaded);
        await ffmpeg.load(coreUrls, { signal: loadController.signal });
        // 关键：加载成功不代表能编码，必须真的编一帧。
        const usable = await selfTest(ffmpeg, SELF_TEST_TIMEOUT_MS);
        if (signal?.aborted) {
          ffmpeg.terminate();
          throw new DOMException('已取消导出', 'AbortError');
        }
        if (!usable) {
          if (multithreaded) {
            // 多线程核心在此环境下死锁，记下结论并回退。
            multithreadedVerdict = false;
            sawMultithreadedFailure = true;
            lastError = new Error(
              '多线程 ffmpeg.wasm 核心试编码超时，已回退到单线程核心（速度较慢）。',
            );
            continue;
          }
          throw new Error('ffmpeg.wasm 核心试编码失败，无法使用。');
        }
        if (multithreaded) multithreadedVerdict = true;
        this.ffmpeg = ffmpeg;
        this.loadedMultithreaded = multithreaded;
        return {
          multithreaded,
          fellBackFromMultithreaded: sawMultithreadedFailure && !multithreaded,
        };
      } catch (error) {
        try {
          ffmpeg.terminate();
        } catch {
          /* worker 可能尚未创建。 */
        }
        if (signal?.aborted) throw new DOMException('已取消导出', 'AbortError');
        lastError = loadTimedOut
          ? new Error(
              `加载${multithreaded ? '多线程' : '单线程'} ffmpeg.wasm 核心超时；请检查 ffmpeg 静态资源是否可访问。`,
            )
          : await explainCoreLoadFailure(
              error,
              coreUrlsFromBase(base, multithreaded),
              multithreaded,
            );
        if (multithreaded) {
          sawMultithreadedFailure = true;
          continue;
        }
        throw lastError;
      } finally {
        clearTimeout(loadTimer);
        signal?.removeEventListener('abort', abortLoad);
      }
    }
    throw lastError instanceof Error ? lastError : new Error(String(lastError));
  }

  async run(options: FfmpegRunOptions): Promise<FfmpegResult> {
    const ffmpeg = this.ffmpeg;
    if (!ffmpeg) throw new Error('ffmpeg.wasm 尚未加载。');
    const { args, inputs, output, duration, onProgress, signal } = options;

    const onAbort = (): void => {
      // terminate() 会终止 worker，之后必须重新 load 才能再次使用。
      ffmpeg.terminate();
      this.ffmpeg = null;
      this.loadedMultithreaded = null;
    };
    signal?.addEventListener('abort', onAbort, { once: true });

    let lastFraction = 0;
    const progressHandler = ({ progress, time }: { progress: number; time: number }): void => {
      // ffmpeg.wasm 的 time 以微秒计；progress 在部分版本里不可靠，因此两者取较大值。
      const byTime = duration > 0 && time > 0 ? time / 1_000_000 / duration : 0;
      const fraction = Math.min(Math.max(Math.max(byTime, progress), lastFraction), 0.99);
      lastFraction = fraction;
      onProgress?.({ fraction, message: '正在编码所选轨道…' });
    };
    ffmpeg.on('progress', progressHandler);

    try {
      for (const input of inputs) {
        const data = input.data instanceof Blob ? new Uint8Array(await input.data.arrayBuffer()) : input.data;
        await ffmpeg.writeFile(input.name, data);
      }
      // exec 在失败时返回非零退出码而不抛异常，必须显式检查，
      // 否则编码失败会被误判为成功。
      const exitCode = await ffmpeg.exec([...args]);
      if (exitCode !== 0) {
        const detail = this.logLines.slice(-25).join('\n');
        throw new Error(`ffmpeg.wasm 退出码 ${exitCode}。` + (detail ? `\n\n${detail}` : ''));
      }
      const data = await ffmpeg.readFile(output);
      if (!(data instanceof Uint8Array) || data.byteLength === 0) {
        throw new Error('ffmpeg.wasm 未生成有效的输出文件。');
      }
      lastFraction = 1;
      onProgress?.({ fraction: 1, message: '导出完成' });
      return { data, log: this.logLines.join('\n') };
    } catch (error) {
      if (signal?.aborted) throw new DOMException('已取消导出', 'AbortError');
      const detail = this.logLines.slice(-25).join('\n');
      throw new Error(
        `ffmpeg.wasm 导出失败：${error instanceof Error ? error.message : String(error)}` +
          (detail ? `\n\n${detail}` : ''),
      );
    } finally {
      ffmpeg.off('progress', progressHandler);
      signal?.removeEventListener('abort', onAbort);
      // 清理中间文件，避免多次导出累积占用 wasm 内存。
      for (const input of inputs) {
        try {
          await ffmpeg.deleteFile(input.name);
        } catch {
          /* 文件可能已被 ffmpeg 移除，忽略。 */
        }
      }
    }
  }

  /** 读取一个由单次 run 生成、需要跨调用保留的文件。 */
  async readFile(name: string): Promise<Uint8Array> {
    const ffmpeg = this.ffmpeg;
    if (!ffmpeg) throw new Error('ffmpeg.wasm 尚未加载。');
    const data = await ffmpeg.readFile(name);
    if (!(data instanceof Uint8Array)) throw new Error(`无法读取 ${name}`);
    return data;
  }

  async writeFile(name: string, data: Uint8Array): Promise<void> {
    const ffmpeg = this.ffmpeg;
    if (!ffmpeg) throw new Error('ffmpeg.wasm 尚未加载。');
    await ffmpeg.writeFile(name, data);
  }

  async deleteFile(name: string): Promise<void> {
    const ffmpeg = this.ffmpeg;
    if (!ffmpeg) return;
    try {
      await ffmpeg.deleteFile(name);
    } catch {
      /* 忽略不存在的文件。 */
    }
  }

  async dispose(): Promise<void> {
    if (this.ffmpeg) {
      this.ffmpeg.terminate();
      this.ffmpeg = null;
      this.loadedMultithreaded = null;
    }
    this.logLines = [];
  }
}

export { fetchFile };
