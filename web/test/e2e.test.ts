/**
 * 端到端测试：在真实无头 Chromium 里跑完整的导入 → 编辑 → 导出流程。
 *
 * 覆盖单元测试无法触及的部分：
 *   - mp4box 解封装真实 H.264 素材
 *   - WebCodecs 解码 + 编码，Mediabunny 封装
 *   - ffmpeg.wasm 渲染音频并混流
 *   - FFprobe 校验最终 MP4（时长、分辨率、帧数、音轨）
 *
 * 需要 FFmpeg/FFprobe（CLIP_FFMPEG / CLIP_FFPROBE 或 PATH）与 Playwright 的
 * Chromium。缺少依赖时明确跳过并给出原因，不静默通过。
 */

import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { after, before, describe, it } from 'node:test';

import { chromium, type Browser, type Page } from 'playwright';

import {
  buildSite,
  cleanArtifacts,
  extractPcm,
  extractRgbFrameColors,
  extractYuvFrame,
  yuvDelta,
  ensureArtifacts,
  prepareFixtures,
  probeFile,
  repoRoot,
  startStaticServer,
  toolVersion,
  writeArtifact,
  type FixtureFile,
  type StaticServer,
} from './helpers.ts';

/** 与 src/automation.ts 的 AutomationApi 对应的测试侧结构。 */
interface ExportResultShape {
  readonly url: string;
  readonly width: number;
  readonly height: number;
  readonly route: string;
  readonly codec: string | null;
  readonly audioRoute: 'webcodecs' | 'ffmpeg-wasm' | 'none';
  readonly audioCodec: 'aac' | 'opus' | null;
  readonly fellBack?: string;
  readonly byteLength: number;
  readonly progress: readonly number[];
}

interface StateShape {
  readonly sources: readonly {
    readonly path: string;
    readonly duration: number;
    readonly width: number;
    readonly height: number;
    readonly videoStreamIndex: number;
    readonly audioStreamIndex: number | null;
  }[];
  readonly duration: number;
  readonly selectedClipId: string | null;
  readonly activeTrackId: string;
  readonly position: number;
  readonly playing: boolean;
  readonly exportableTracks: number;
  readonly tracks: readonly {
    id: string;
    kind: 'video' | 'audio';
    clips: readonly { id: string; start: number; end: number; speed: number }[];
  }[];
}

let skipReason: string | null = null;
let browser: Browser | null = null;

/**
 * 启动可用的 Chromium。
 *
 * 不同环境安装的构建不一样：开发机上常见的是 chromium-headless-shell，
 * CI 上装的是完整 chromium。依次尝试显式路径、默认构建和 headless shell 通道，
 * 避免因为只装了其中一个而误报失败。
 */
async function launchBrowser(): Promise<Browser> {
  const args = ['--no-sandbox', '--disable-setuid-sandbox'];
  const attempts: { label: string; options: Record<string, unknown> }[] = [];
  const explicit = process.env.CLIP_CHROMIUM;
  if (explicit) attempts.push({ label: `CLIP_CHROMIUM=${explicit}`, options: { executablePath: explicit, args } });
  const bundled = chromium.executablePath();
  if (existsSync(bundled)) attempts.push({ label: bundled, options: { args } });
  attempts.push({ label: 'chromium-headless-shell 通道', options: { channel: 'chromium-headless-shell', args } });
  attempts.push({ label: 'chromium 通道', options: { channel: 'chromium', args } });

  const failures: string[] = [];
  for (const attempt of attempts) {
    try {
      return await chromium.launch(attempt.options);
    } catch (error) {
      failures.push(`${attempt.label}: ${error instanceof Error ? error.message.split('\n')[0] : String(error)}`);
    }
  }
  throw new Error(
    `无法启动 Chromium。请运行 npx playwright install chromium。\n尝试过：\n${failures.join('\n')}`,
  );
}

try {
  toolVersion(process.env.CLIP_FFMPEG ?? 'ffmpeg');
  toolVersion(process.env.CLIP_FFPROBE ?? 'ffprobe');
  // 真启动一次，以准确判断浏览器是否可用（而不是猜测路径）。
  browser = await launchBrowser();
} catch (error) {
  skipReason = error instanceof Error ? error.message : String(error);
}

describe('端到端导出（真实 Chromium + FFprobe）', { skip: skipReason ?? false }, () => {
  let server: StaticServer;
  let suiteBrowser: Browser;
  let fixtures: readonly FixtureFile[];

  before(async () => {
    if (!browser) throw new Error('浏览器未就绪');
    // 复用模块级启动的实例，避免重复拉起进程。
    suiteBrowser = browser;
    cleanArtifacts();
    ensureArtifacts();
    fixtures = prepareFixtures();
    buildSite();
    server = await startStaticServer(join(repoRoot, 'web', 'dist'), true);
  });

  after(async () => {
    await browser?.close();
    browser = null;
    await server?.close();
  });

  interface Session {
    readonly page: Page;
    readonly errors: string[];
  }

  async function openPage(): Promise<Session> {
    const context = await suiteBrowser.newContext();
    const page = await context.newPage();
    const errors: string[] = [];
    page.on('pageerror', (error) => errors.push(`pageerror: ${error.message}`));
    page.on('console', (message) => {
      if (message.type() === 'error') errors.push(`console: ${message.text()}`);
    });
    await page.goto(`${server.origin}/index.html?automation=1`, { waitUntil: 'load' });
    // 等能力探测完成并挂载自动化接口。
    await page.waitForFunction(
      () => Boolean((window as unknown as { __clip?: unknown }).__clip),
      undefined,
      { timeout: 60_000 },
    );
    return { page, errors };
  }

  /** 导入素材并等待模型与导入后的异步波形/能力探测全部就绪。 */
  async function importFixture(page: Page, fixture: FixtureFile): Promise<void> {
    await page.setInputFiles('#file-input', [fixture.path]);
    await page.waitForFunction(
      () => {
        const state = (window as unknown as { __clip: { state: () => StateShape } }).__clip.state();
        const progress = document.getElementById('progress') as HTMLProgressElement | null;
        return state.tracks.some((track) => track.clips.length > 0) && progress?.hidden === true;
      },
      undefined,
      { timeout: 120_000 },
    );
  }

  /** 把导出结果写到磁盘，便于用 FFprobe 校验。 */
  async function saveExport(
    page: Page,
    result: ExportResultShape,
    name: string,
  ): Promise<string> {
    const base64 = await page.evaluate(async (url: string) => {
      const response = await fetch(url);
      const buffer = new Uint8Array(await response.arrayBuffer());
      let binary = '';
      const chunk = 0x8000;
      for (let index = 0; index < buffer.length; index += chunk) {
        binary += String.fromCharCode(...buffer.subarray(index, index + chunk));
      }
      return btoa(binary);
    }, result.url);
    const path = writeArtifact(name, Buffer.from(base64, 'base64'));
    return path;
  }

  it('能力探测识别 WebCodecs 与跨源隔离', async () => {
    const { page, errors } = await openPage();
    const capabilities = await page.evaluate(() => {
      const api = (window as unknown as {
        __clip: { capabilities: () => Record<string, unknown> | null };
      }).__clip.capabilities();
      return {
        capabilities: api,
        isolated: globalThis.crossOriginIsolated === true,
        sharedArrayBuffer: typeof SharedArrayBuffer === 'function',
      };
    });
    writeArtifact('capabilities.json', `${JSON.stringify(capabilities, null, 2)}\n`);
    assert.equal(capabilities.isolated, true, '测试服务器应启用跨源隔离');
    assert.equal(capabilities.sharedArrayBuffer, true, '跨源隔离下 SharedArrayBuffer 应可用');
    assert.ok(capabilities.capabilities, '能力信息应可读取');
    assert.equal(errors.length, 0, `页面不应报错：${errors.join(' | ')}`);
    await page.context().close();
  });

  it('导入横屏素材得到正确的时长与轨道', async () => {
    const { page } = await openPage();
    await importFixture(page, fixtures[0]!);
    const state = await page.evaluate(() =>
      (window as unknown as { __clip: { state: () => StateShape } }).__clip.state(),
    );
    const total = state.tracks
      .filter((track) => track.kind === 'video')
      .flatMap((track) => track.clips)
      .reduce((sum, clip) => sum + (clip.end - clip.start), 0);
    writeArtifact('import-state.json', `${JSON.stringify(state, null, 2)}\n`);
    assert.ok(Math.abs(total - 4) < 0.2, `源时长应约为 4 秒，实际 ${total}`);
    assert.deepEqual(
      state.tracks.filter((track) => track.clips.length > 0).map((track) => track.kind),
      ['video', 'audio'],
      '有声素材应自动生成视频轨和伴生音频槽',
    );
    await page.context().close();
  });

  it('单独导入普通音频时创建空视频轨和已加载的伴生音频轨', async () => {
    const fixture = fixtures.find((item) => item.name === 'tone-3s.m4a');
    assert.ok(fixture, '应准备普通音频回归素材');
    const { page, errors } = await openPage();
    await importFixture(page, fixture);
    const state = await page.evaluate(() =>
      (window as unknown as { __clip: { state: () => StateShape } }).__clip.state(),
    );
    assert.equal(state.sources.length, 1);
    assert.equal(state.sources[0]!.videoStreamIndex, -1);
    assert.equal(state.sources[0]!.audioStreamIndex, 0);
    assert.ok(Math.abs(state.sources[0]!.duration - 3) < 0.1);
    assert.deepEqual(
      state.tracks.slice(0, 2).map((track) => ({ kind: track.kind, clips: track.clips.length })),
      [{ kind: 'video', clips: 0 }, { kind: 'audio', clips: 1 }],
    );
    assert.equal(state.exportableTracks, 0, '空视频轨不能直接导出');
    assert.equal(await page.locator('#audio-preview').isVisible(), true);
    assert.equal(await page.locator('#audio-preview-name').textContent(), fixture.name);
    assert.equal(await page.locator('#export-button').isDisabled(), true);
    assert.equal(errors.length, 0, `普通音频导入期间不应报错：${errors.join(' | ')}`);
    await page.context().close();
  });

  it('右键只选择片段并可设置预设或自定义倍速，左键才改变播放进度', async () => {
    const { page, errors } = await openPage();
    await importFixture(page, fixtures[0]!);
    await page.setInputFiles('#file-input', fixtures[1]!.path);
    await page.waitForFunction(() => {
      const state = (window as unknown as { __clip: { state: () => StateShape } }).__clip.state();
      const progress = document.getElementById('progress') as HTMLProgressElement | null;
      return state.sources.length === 2 && progress?.hidden === true;
    }, undefined, { timeout: 120_000 });

    const initial = await page.evaluate(() =>
      (window as unknown as { __clip: { state: () => StateShape } }).__clip.state());
    const videoTracks = initial.tracks.filter((track) => track.kind === 'video' && track.clips.length > 0);
    assert.ok(videoTracks.length >= 2, '测试需要两个可选择的视频片段');
    const rowCenter = (trackId: string): number => {
      let top = 28;
      for (const track of initial.tracks) {
        // 伴生音频/字幕默认折叠，首屏只为视频轨分配可点击行高。
        if (track.kind !== 'video') continue;
        const height = 68;
        if (track.id === trackId) return top + height / 2;
        top += height;
      }
      throw new Error(`找不到轨道 ${trackId}`);
    };
    const bounds = await page.locator('#timeline').boundingBox();
    assert.ok(bounds, '时间轴没有可点击区域');
    const first = videoTracks[0]!;
    const target = videoTracks[1]!;

    await page.locator('#timeline').click({
      button: 'left',
      position: { x: bounds.width * 0.4, y: rowCenter(first.id) },
    });
    const afterLeft = await page.evaluate(() => ({
      state: (window as unknown as { __clip: { state: () => StateShape } }).__clip.state(),
      source: document.querySelector<HTMLVideoElement>('#preview')?.dataset.source ?? null,
    }));
    assert.equal(afterLeft.state.activeTrackId, first.id);
    assert.equal(afterLeft.state.selectedClipId, first.clips[0]!.id);
    assert.ok(afterLeft.state.position > 0, '左键选择片段应更新播放进度');

    await page.locator('#timeline').click({
      button: 'right',
      position: { x: bounds.width * 0.7, y: rowCenter(target.id) },
    });
    await page.locator('#clip-context-menu').waitFor({ state: 'visible' });
    const afterRight = await page.evaluate(() => ({
      state: (window as unknown as { __clip: { state: () => StateShape } }).__clip.state(),
      source: document.querySelector<HTMLVideoElement>('#preview')?.dataset.source ?? null,
    }));
    assert.equal(afterRight.state.selectedClipId, target.clips[0]!.id, '右键应选中目标片段');
    assert.equal(afterRight.state.activeTrackId, afterLeft.state.activeTrackId, '右键不应切换播放轨道');
    assert.ok(Math.abs(afterRight.state.position - afterLeft.state.position) < 0.000001, '右键不应改变播放进度');
    assert.equal(afterRight.source, afterLeft.source, '右键不应切换正在播放的素材');
    assert.equal(await page.locator('[data-speed="1"]').getAttribute('aria-checked'), 'true');

    await page.click('[data-speed="2"]');
    let changed = await page.evaluate(() =>
      (window as unknown as { __clip: { state: () => StateShape } }).__clip.state());
    assert.equal(changed.tracks.find((track) => track.id === target.id)!.clips[0]!.speed, 2);
    assert.equal(changed.activeTrackId, afterLeft.state.activeTrackId);
    assert.ok(Math.abs(changed.position - afterLeft.state.position) < 0.000001);

    await page.locator('#timeline').click({
      button: 'right',
      position: { x: bounds.width * 0.2, y: rowCenter(target.id) },
    });
    await page.locator('#clip-context-menu').waitFor({ state: 'visible' });
    await page.click('#custom-speed-button');
    await page.locator('#speed-dialog').waitFor({ state: 'visible' });
    await page.fill('#speed-input', '9');
    await page.click('#speed-confirm');
    assert.equal(await page.locator('#speed-dialog').getAttribute('open'), '', '非法倍速不应关闭对话框');
    assert.equal(await page.locator('#speed-validation').isVisible(), true);
    await page.fill('#speed-input', '1.37');
    await page.click('#speed-confirm');
    await page.waitForFunction(() => !document.getElementById('speed-dialog')?.hasAttribute('open'));
    changed = await page.evaluate(() =>
      (window as unknown as { __clip: { state: () => StateShape } }).__clip.state());
    assert.ok(Math.abs(changed.tracks.find((track) => track.id === target.id)!.clips[0]!.speed - 1.37) < 0.000001);
    assert.equal(errors.length, 0, `倍速菜单交互不应报错：${errors.join(' | ')}`);
    await page.context().close();
  });

  it('MKV 素材在浏览器内准备为 MP4 后可预览并导出', async () => {
    const { page, errors } = await openPage();
    await importFixture(page, fixtures[4]!);

    const state = await page.evaluate(() => (window as unknown as {
      __clip: { state: () => StateShape };
    }).__clip.state());
    assert.equal(state.sources.length, 1);
    assert.equal(state.sources[0]!.path, 'landscape-4s-30fps.mkv');
    assert.equal(state.sources[0]!.width, 640);
    assert.equal(state.sources[0]!.height, 360);
    assert.ok(Math.abs(state.sources[0]!.duration - 4) < 0.1);

    await page.waitForFunction(() => {
      const video = document.querySelector<HTMLVideoElement>('#preview');
      return Boolean(video && video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA);
    });
    const result = await page.evaluate(() => (window as unknown as {
      __clip: { exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape> };
    }).__clip.exportToBlob({ route: 'webcodecs-video', width: 320, height: 180 }));
    const path = await saveExport(page, result, 'export-from-mkv.mp4');
    const summary = probeFile(path);
    assert.equal(summary.videoCodec, 'h264');
    assert.equal(summary.hasAudio, true);
    assert.equal(summary.width, 320);
    assert.equal(summary.height, 180);
    assert.ok(Math.abs(summary.duration - 4) < 0.25);
    assert.equal(errors.length, 0, `MKV 导入与导出期间不应报错：${errors.join(' | ')}`);
    await page.context().close();
  });

  it('刷新后要求核对原视频，拒绝错误素材并恢复剪辑位置', async () => {
    const { page, errors } = await openPage();
    await importFixture(page, fixtures[0]!);
    const edited = await page.evaluate(() => {
      const api = (window as unknown as {
        __clip: { seek: (time: number) => void; split: () => boolean; state: () => StateShape };
      }).__clip;
      api.seek(1.5);
      return { split: api.split(), state: api.state() };
    });
    assert.equal(edited.split, true);
    assert.equal(edited.state.tracks.filter((track) => track.kind === 'video').flatMap((track) => track.clips).length, 2);

    await page.waitForFunction(() => {
      const raw = sessionStorage.getItem('clip.edit-session.v1');
      if (!raw) return false;
      const stored = JSON.parse(raw) as { project?: { tracks?: { kind?: string; clips?: unknown[] }[] } };
      return stored.project?.tracks
        ?.filter((track) => track.kind === 'video')
        .reduce((count, track) => count + (track.clips?.length ?? 0), 0) === 2;
    });

    page.once('dialog', (dialog) => void dialog.accept());
    await page.reload({ waitUntil: 'load' });
    await page.waitForFunction(() => Boolean((window as unknown as { __clip?: unknown }).__clip));
    await page.locator('#recovery-dialog').waitFor({ state: 'visible' });
    assert.equal(await page.evaluate(() => document.activeElement?.id), 'recovery-choose',
      '恢复对话框默认应聚焦安全的继续恢复操作');

    await page.click('#recovery-discard');
    await page.locator('#recovery-discard-dialog').waitFor({ state: 'visible' });
    assert.equal(await page.evaluate(() => document.activeElement?.id), 'recovery-discard-cancel',
      '二次确认默认应聚焦继续恢复');
    assert.notEqual(await page.evaluate(() => sessionStorage.getItem('clip.edit-session.v1')), null,
      '打开放弃确认时不应清除恢复数据');
    await page.click('#recovery-discard-cancel');
    await page.waitForFunction(() => !document.getElementById('recovery-discard-dialog')?.hasAttribute('open'));
    assert.equal(await page.locator('#recovery-dialog').getAttribute('open'), '', '取消放弃后应保留恢复对话框');
    assert.equal(await page.evaluate(() => document.activeElement?.id), 'recovery-choose',
      '取消放弃后焦点应回到继续恢复操作');

    await page.setInputFiles('#recovery-file-input', fixtures[1]!.path);
    const rejection = page.locator('#recovery-error:not([hidden])');
    await rejection.waitFor();
    assert.match(await rejection.textContent() ?? '', /不一致/);
    assert.equal(await page.locator('#recovery-dialog').getAttribute('open'), '');

    await page.setInputFiles('#recovery-file-input', fixtures[0]!.path);
    await page.waitForFunction(() => !document.getElementById('recovery-dialog')?.hasAttribute('open'));
    const restored = await page.evaluate(() =>
      (window as unknown as { __clip: { state: () => StateShape } }).__clip.state(),
    );
    assert.equal(restored.tracks.filter((track) => track.kind === 'video').flatMap((track) => track.clips).length, 2);
    assert.ok(Math.abs(restored.position - 1.5) < 0.01, `播放头应恢复到 1.5 秒，实际 ${restored.position}`);
    assert.equal(errors.length, 0, `恢复期间不应报错：${errors.join(' | ')}`);
    await page.context().close();
  });

  it('导出 .clip 文件后可恢复，取消恢复不会覆盖当前项目', async () => {
    const { page, errors } = await openPage();
    await importFixture(page, fixtures[0]!);
    await page.evaluate(() => {
      const api = (window as unknown as { __clip: { seek: (time: number) => void; split: () => boolean } }).__clip;
      api.seek(1.5);
      api.split();
    });

    await page.click('#more-button');
    assert.equal(await page.locator('#more-menu').getAttribute('role'), 'menu');
    assert.deepEqual(
      await page.locator('#more-menu .more-menu-item > span:first-of-type').allTextContents(),
      ['设置', '导入项目', '导出项目'],
    );

    const [download] = await Promise.all([
      page.waitForEvent('download'),
      page.click('#save-project-button'),
    ]);
    const projectPath = await download.path();
    assert.ok(projectPath, '浏览器没有生成 .clip 下载文件');
    const projectDocument = JSON.parse(readFileSync(projectPath, 'utf8')) as {
      format?: string;
      project?: { sources?: unknown[] };
    };
    assert.equal(projectDocument.format, 'clip-project');
    assert.equal(projectDocument.project?.sources?.length, 1);
    assert.match(download.suggestedFilename(), /^Clip-\d{12}\.clip$/);

    await importFixture(page, fixtures[1]!);
    await page.setInputFiles('#project-file-input', projectPath);
    await page.locator('#recovery-dialog').waitFor({ state: 'visible' });
    assert.equal(await page.locator('#recovery-title').textContent(), '导入这份项目？');
    await page.click('#recovery-discard');
    await page.waitForFunction(() => !document.getElementById('recovery-dialog')?.hasAttribute('open'));
    let state = await page.evaluate(() =>
      (window as unknown as { __clip: { state: () => StateShape } }).__clip.state());
    assert.equal(state.sources.length, 2, '取消文件恢复覆盖了当前项目');

    await page.setInputFiles('#project-file-input', projectPath);
    await page.locator('#recovery-dialog').waitFor({ state: 'visible' });
    await page.setInputFiles('#recovery-file-input', fixtures[0]!.path);
    await page.waitForFunction(() => !document.getElementById('recovery-dialog')?.hasAttribute('open'));
    state = await page.evaluate(() =>
      (window as unknown as { __clip: { state: () => StateShape } }).__clip.state());
    assert.equal(state.sources.length, 1);
    assert.equal(state.tracks.filter((track) => track.kind === 'video').flatMap((track) => track.clips).length, 2);
    assert.ok(Math.abs(state.position - 1.5) < 0.01);
    assert.equal(errors.length, 0, `剪辑记录往返期间不应报错：${errors.join(' | ')}`);
    await page.context().close();
  });

  it('放弃恢复需二次确认，确认后才清除存档', async () => {
    const { page } = await openPage();
    await importFixture(page, fixtures[0]!);
    await page.waitForFunction(() => sessionStorage.getItem('clip.edit-session.v1') !== null);

    page.once('dialog', (dialog) => void dialog.accept());
    await page.reload({ waitUntil: 'load' });
    await page.waitForFunction(() => Boolean((window as unknown as { __clip?: unknown }).__clip));
    await page.locator('#recovery-dialog').waitFor({ state: 'visible' });

    await page.click('#recovery-discard');
    await page.locator('#recovery-discard-dialog').waitFor({ state: 'visible' });
    assert.notEqual(await page.evaluate(() => sessionStorage.getItem('clip.edit-session.v1')), null,
      '首次点击放弃不能直接删除存档');

    await page.click('#recovery-discard-confirm');
    await page.waitForFunction(() => !document.getElementById('recovery-dialog')?.hasAttribute('open')
      && !document.getElementById('recovery-discard-dialog')?.hasAttribute('open'));
    assert.equal(await page.evaluate(() => sessionStorage.getItem('clip.edit-session.v1')), null,
      '只有确认放弃后才应清除存档');
    const state = await page.evaluate(() =>
      (window as unknown as { __clip: { state: () => StateShape } }).__clip.state());
    assert.equal(state.sources.length, 0, '确认放弃后应留在空项目');
    await page.context().close();
  });

  it('分割后导出：WebCodecs 路线产出可播放 MP4', async () => {
    const { page, errors } = await openPage();
    await importFixture(page, fixtures[0]!);

    // 播放头移到 1.5 秒后分割，再删除后半段，得到约 1.5 秒的结果。
    const splitResult = await page.evaluate(() => {
      const api = (window as unknown as {
        __clip: {
          seek: (t: number) => void;
          split: () => boolean;
          state: () => StateShape;
        };
      }).__clip;
      api.seek(1.5);
      const ok = api.split();
      return { ok, state: api.state() };
    });
    assert.equal(splitResult.ok, true, '在 1.5 秒处分割应成功');

    // 删除第二段，保留前 1.5 秒。
    const deleted = await page.evaluate(() => {
      const api = (window as unknown as {
        __clip: { state: () => StateShape; deleteClip: (id: string) => boolean };
      }).__clip;
      const state = api.state();
      const clips = state.tracks.filter((track) => track.kind === 'video').flatMap((track) => track.clips);
      const last = clips[clips.length - 1]!;
      return api.deleteClip(last.id);
    });
    assert.equal(deleted, true, '应能删除后半段');

    const result = await page.evaluate(async () => {
      const api = (window as unknown as {
        __clip: { exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape> };
      }).__clip;
      return api.exportToBlob({ route: 'webcodecs-video' });
    });

    writeArtifact('export-webcodecs.json', `${JSON.stringify(result, null, 2)}\n`);
    assert.ok(result.byteLength > 10_000, `导出文件过小：${result.byteLength} 字节`);
    assert.equal(result.route, 'webcodecs-video', `应走 WebCodecs 路线，实际 ${result.route}${result.fellBack ? `（回退原因：${result.fellBack}）` : ''}`);
    assert.ok(result.codec, 'WebCodecs 路线应记录实际使用的 codec');
    assert.ok(result.progress[0]! > 0, '导出开始后应立即离开 0%');
    assert.ok(
      result.progress.every((value, index) => index === 0 || value >= result.progress[index - 1]!),
      `WebCodecs/ffmpeg 分阶段进度不应倒退：${result.progress.join(', ')}`,
    );
    assert.equal(result.progress[result.progress.length - 1], 1, '导出完成时进度应为 100%');

    const path = await saveExport(page, result, 'export-webcodecs.mp4');
    const summary = probeFile(path);
    writeArtifact('export-webcodecs-probe.json', `${JSON.stringify(summary, null, 2)}\n`);

    assert.equal(summary.videoCodec, 'h264', `视频编码应为 h264，实际 ${summary.videoCodec}`);
    assert.equal(summary.width, 640);
    assert.equal(summary.height, 360);
    assert.ok(Math.abs(summary.duration - 1.5) < 0.4, `时长应约为 1.5 秒，实际 ${summary.duration}`);
    assert.equal(summary.hasAudio, true, '导出后应包含音轨');
    assert.equal(summary.audioCodec, result.audioCodec, `音频编码应为 ${result.audioCodec}，实际 ${summary.audioCodec}`);
    assert.ok(summary.frameCount >= 40, `应至少有 40 帧，实际 ${summary.frameCount}`);
    assert.equal(errors.length, 0, `导出期间不应报错：${errors.join(' | ')}`);

    await page.context().close();
  });

  it('WebCodecs 按实际 1440p60 输出提升 H.264 level 而不回退 wasm', async (t) => {
    const { page, errors } = await openPage();
    const supportsH264 = await page.evaluate(() => Boolean((window as unknown as {
      __clip: { capabilities: () => { webCodecsVideo?: boolean } | null };
    }).__clip.capabilities()?.webCodecsVideo));
    if (!supportsH264) {
      await page.context().close();
      t.skip('当前 Chromium 构建未提供 H.264 WebCodecs 编码器');
      return;
    }
    const fixture = fixtures.find((item) => item.name === 'high-rate-1s-60fps.mp4');
    assert.ok(fixture, '应准备 60fps 回归素材');
    await importFixture(page, fixture);

    const result = await page.evaluate(async () => {
      const api = (window as unknown as {
        __clip: { exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape> };
      }).__clip;
      return api.exportToBlob({
        route: 'webcodecs-video',
        width: 2560,
        height: 1440,
      });
    });
    assert.equal(
      result.route,
      'webcodecs-video',
      `1440p60 应继续使用 WebCodecs，实际 ${result.route}${result.fellBack ? `（${result.fellBack}）` : ''}`,
    );
    assert.match(result.codec ?? '', /^avc1\.[0-9a-f]{4}32$/i,
      `1440p60 应使用 H.264 Level 5.1，实际 ${result.codec}`);
    const path = await saveExport(page, result, 'export-webcodecs-1440p60.mp4');
    const summary = probeFile(path);
    assert.equal(summary.width, 2560);
    assert.equal(summary.height, 1440);
    assert.ok(summary.frameCount >= 55, `应输出约 60 帧，实际 ${summary.frameCount}`);
    assert.equal(errors.length, 0, `导出期间不应报错：${errors.join(' | ')}`);

    await page.context().close();
  });

  it('删除后半段后预览与两条导出路线都不泄漏切点后的帧', async () => {
    const { page, errors } = await openPage();
    await importFixture(page, fixtures[3]!);

    const split = await page.evaluate(() => {
      const api = (window as unknown as {
        __clip: { seek: (time: number) => void; split: () => boolean };
      }).__clip;
      api.seek(1);
      return api.split();
    });
    assert.equal(split, true, '应能在红蓝画面的交界处分割');

    // split() 会选中右侧蓝色片段；走真实删除按钮，确保预览状态也被重定位。
    await page.click('#delete-button');
    await page.click('#play-button');
    await page.waitForFunction(
      () => (window as unknown as { __clip: { state: () => StateShape } }).__clip.state().playing,
      undefined,
      { timeout: 10_000 },
    );
    await page.waitForFunction(
      () => !(window as unknown as { __clip: { state: () => StateShape } }).__clip.state().playing,
      undefined,
      { timeout: 10_000 },
    );

    const preview = await page.evaluate(() => {
      const canvas = document.getElementById('preview-frame') as HTMLCanvasElement;
      const context = canvas.getContext('2d');
      if (!context) throw new Error('无法读取预览画布');
      const pixel = context.getImageData(Math.floor(canvas.width / 2), Math.floor(canvas.height / 2), 1, 1).data;
      const state = (window as unknown as { __clip: { state: () => StateShape } }).__clip.state();
      const decoder = document.getElementById('preview') as HTMLVideoElement;
      return { red: pixel[0]!, blue: pixel[2]!, position: state.position, sourceTime: decoder.currentTime };
    });
    assert.ok(preview.red > preview.blue * 3, `停止画面应为保留的红帧，实际 R=${preview.red} B=${preview.blue}`);
    assert.ok(Math.abs(preview.position - 1) < 0.05, `播放头应停在 1 秒切点，实际 ${preview.position}`);
    assert.ok(preview.sourceTime < 1, `解码器也应回到最后一个保留帧，实际源时间 ${preview.sourceTime}`);

    const exports = await page.evaluate(async () => {
      const api = (window as unknown as {
        __clip: { exportToBlob: (request: Record<string, unknown>) => Promise<ExportResultShape> };
      }).__clip;
      return {
        webcodecs: await api.exportToBlob({ route: 'webcodecs-video' }),
        wasm: await api.exportToBlob({ route: 'wasm' }),
      };
    });

    for (const [route, result] of Object.entries(exports)) {
      const path = await saveExport(page, result, `cut-boundary-${route}.mp4`);
      const summary = probeFile(path);
      const colors = extractRgbFrameColors(path);
      assert.ok(Math.abs(summary.duration - 1) < 0.08, `${route} 导出时长应为 1 秒，实际 ${summary.duration}`);
      assert.ok(colors.length >= 3, `${route} 导出没有可检查的视频帧`);
      for (let index = 0; index < colors.length; index += 3) {
        const red = colors[index]!;
        const blue = colors[index + 2]!;
        assert.ok(red > blue * 3, `${route} 第 ${index / 3 + 1} 帧混入了删除的蓝色画面（R=${red} B=${blue}）`);
      }
    }

    assert.equal(errors.length, 0, `预览或导出期间不应报错：${errors.join(' | ')}`);
    await page.context().close();
  });

  it('ffmpeg.wasm 路线导出同类结果（兜底路径可独立成立）', async () => {
    const { page, errors } = await openPage();
    const ffmpegScriptRequests = new Map<string, number>();
    page.on('request', (request) => {
      const pathname = new URL(request.url()).pathname;
      if (/^\/ffmpeg\/core(?:-mt)?\/ffmpeg-core(?:\.worker)?\.js$/.test(pathname)) {
        ffmpegScriptRequests.set(pathname, (ffmpegScriptRequests.get(pathname) ?? 0) + 1);
      }
    });
    await importFixture(page, fixtures[0]!);

    const result = await page.evaluate(async () => {
      const api = (window as unknown as {
        __clip: {
          seek: (t: number) => void;
          split: () => boolean;
          state: () => StateShape;
          deleteClip: (id: string) => boolean;
          exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape>;
        };
      }).__clip;
      api.seek(2);
      api.split();
      const clips = api.state().tracks.filter((track) => track.kind === 'video').flatMap((track) => track.clips);
      api.deleteClip(clips[clips.length - 1]!.id);
      return api.exportToBlob({ route: 'wasm' });
    });

    writeArtifact('export-wasm.json', `${JSON.stringify(result, null, 2)}\n`);
    assert.equal(result.route, 'wasm');
    assert.ok(result.byteLength > 10_000, `导出文件过小：${result.byteLength} 字节`);
    assert.ok(result.progress[0]! > 0, 'ffmpeg.wasm 导出开始后应立即离开 0%');
    assert.ok(
      result.progress.every((value, index) => index === 0 || value >= result.progress[index - 1]!),
      `ffmpeg.wasm 进度不应倒退：${result.progress.join(', ')}`,
    );
    assert.equal(result.progress[result.progress.length - 1], 1, 'ffmpeg.wasm 完成时进度应为 100%');

    const path = await saveExport(page, result, 'export-wasm.mp4');
    const summary = probeFile(path);
    writeArtifact('export-wasm-probe.json', `${JSON.stringify(summary, null, 2)}\n`);

    assert.equal(summary.videoCodec, 'h264');
    assert.equal(summary.width, 640);
    assert.equal(summary.height, 360);
    // 前 2 秒。
    assert.ok(Math.abs(summary.duration - 2) < 0.5, `时长应约为 2 秒，实际 ${summary.duration}`);
    assert.equal(summary.hasAudio, true);
    assert.equal(summary.audioCodec, 'aac');
    assert.ok(ffmpegScriptRequests.size > 0, '应通过 HTTP 预取 ffmpeg 核心脚本');
    for (const [pathname, count] of ffmpegScriptRequests) {
      assert.equal(count, 1, `${pathname} 在单次核心加载中只能请求一次`);
    }
    assert.equal(errors.length, 0, `ffmpeg.wasm 导出期间不应报错：${errors.join(' | ')}`);

    await page.context().close();
  });

  it('竖屏素材按目标尺寸补边并保持比例', async () => {
    const { page } = await openPage();
    await importFixture(page, fixtures[1]!);

    const result = await page.evaluate(async () => {
      const api = (window as unknown as {
        __clip: { exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape> };
      }).__clip;
      // 竖屏素材导出为 640x640，应补黑边而不是拉伸。
      return api.exportToBlob({ route: 'webcodecs-video', width: 640, height: 640 });
    });

    assert.equal(result.width, 640);
    assert.equal(result.height, 640);
    const path = await saveExport(page, result, 'export-portrait-pad.mp4');
    const summary = probeFile(path);
    writeArtifact('export-portrait-pad-probe.json', `${JSON.stringify(summary, null, 2)}\n`);
    assert.equal(summary.width, 640);
    assert.equal(summary.height, 640);
    assert.equal(summary.videoCodec, 'h264');

    await page.context().close();
  });

  it('无声素材导出得到无音轨的 MP4', async () => {
    const { page } = await openPage();
    await importFixture(page, fixtures[2]!);

    const result = await page.evaluate(async () => {
      const api = (window as unknown as {
        __clip: { exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape> };
      }).__clip;
      return api.exportToBlob({ route: 'webcodecs-video' });
    });

    const path = await saveExport(page, result, 'export-silent.mp4');
    const summary = probeFile(path);
    writeArtifact('export-silent-probe.json', `${JSON.stringify(summary, null, 2)}\n`);
    assert.equal(summary.videoCodec, 'h264');
    assert.equal(summary.hasAudio, false, '无声素材不应凭空产生音轨');
    assert.equal(summary.width, 320);
    assert.equal(summary.height, 240);

    await page.context().close();
  });

  it('变速片段导出的时长与倍率一致', async () => {
    const { page } = await openPage();
    await importFixture(page, fixtures[0]!);

    const result = await page.evaluate(async () => {
      const api = (window as unknown as {
        __clip: {
          state: () => StateShape;
          setSpeed: (id: string, speed: number) => boolean;
          exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape>;
        };
      }).__clip;
      const clip = api.state().tracks.flatMap((track) => track.clips)[0]!;
      const changed = api.setSpeed(clip.id, 2);
      return { changed, result: await api.exportToBlob({ route: 'webcodecs-video' }) };
    });

    assert.equal(result.changed, true, '应能设置 2 倍速');
    const path = await saveExport(page, result.result, 'export-speed-2x.mp4');
    const summary = probeFile(path);
    writeArtifact('export-speed-2x-probe.json', `${JSON.stringify(summary, null, 2)}\n`);
    // 4 秒素材 2 倍速 → 约 2 秒。
    assert.ok(
      Math.abs(summary.duration - 2) < 0.4,
      `2 倍速后时长应约为 2 秒，实际 ${summary.duration}`,
    );
    assert.equal(summary.videoCodec, 'h264');

    await page.context().close();
  });

  it('多素材混剪自动补静音并拼接', async () => {
    const { page } = await openPage();
    await importFixture(page, fixtures[0]!);
    await importFixture(page, fixtures[2]!);

    const result = await page.evaluate(async () => {
      const api = (window as unknown as {
        __clip: {
          state: () => StateShape;
          selectTrack: (id: string) => void;
          exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape>;
        };
      }).__clip;
      const state = api.state();
      // 选第一条非空轨道导出，验证单轨导出不混入其他轨道。
      const track = state.tracks.find((item) => item.kind === 'video' && item.clips.length > 0)!;
      api.selectTrack(track.id);
      return api.exportToBlob({ trackId: track.id, route: 'webcodecs-video' });
    });

    const path = await saveExport(page, result, 'export-single-track.mp4');
    const summary = probeFile(path);
    writeArtifact('export-single-track-probe.json', `${JSON.stringify(summary, null, 2)}\n`);
    assert.equal(summary.videoCodec, 'h264');
    // 只导出第一条轨道（4 秒横屏素材）。
    assert.ok(Math.abs(summary.duration - 4) < 0.5, `应只导出 4 秒素材，实际 ${summary.duration}`);

    await page.context().close();
  });

  it('ffmpeg.wasm 分段导出三素材混剪且不同时驻留全部源', async () => {
    const { page } = await openPage();
    await importFixture(page, fixtures[0]!);
    await importFixture(page, fixtures[1]!);
    await importFixture(page, fixtures[2]!);

    const result = await page.evaluate(async () => {
      const api = (window as unknown as {
        __clip: {
          state: () => StateShape;
          moveClip: (clipId: string, trackId: string, index: number) => boolean;
          exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape>;
        };
      }).__clip;
      const tracks = api.state().tracks.filter((track) => track.kind === 'video' && track.clips.length > 0);
      const target = tracks[0]!;
      if (!api.moveClip(tracks[1]!.clips[0]!.id, target.id, 1)) throw new Error('第二份素材移动失败');
      if (!api.moveClip(tracks[2]!.clips[0]!.id, target.id, 2)) throw new Error('第三份素材移动失败');
      return api.exportToBlob({ trackId: target.id, route: 'wasm', width: 320, height: 180 });
    });

    const path = await saveExport(page, result, 'export-wasm-three-sources.mp4');
    const summary = probeFile(path);
    assert.equal(result.route, 'wasm');
    assert.equal(summary.videoCodec, 'h264');
    assert.equal(summary.audioCodec, 'aac');
    assert.equal(summary.width, 320);
    assert.equal(summary.height, 180);
    assert.ok(Math.abs(summary.duration - 9) < 0.5, `三素材总时长应约 9 秒，实际 ${summary.duration}`);
    assert.ok(summary.frameCount >= 255, `应输出约 270 帧，实际 ${summary.frameCount}`);
    await page.context().close();
  });

  it('导出产物可被页面重新解析（往返一致性）', async () => {
    const { page } = await openPage();
    await importFixture(page, fixtures[0]!);

    const result = await page.evaluate(async () => {
      const api = (window as unknown as {
        __clip: { exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape> };
      }).__clip;
      return api.exportToBlob({ route: 'webcodecs-video', width: 320, height: 180 });
    });
    const path = await saveExport(page, result, 'export-roundtrip.mp4');
    const summary = probeFile(path);
    assert.equal(summary.width, 320);
    assert.equal(summary.height, 180);
    // 文件应能被再次当作素材导入（用 FFprobe 已经验证容器有效）。
    const bytes = readFileSync(path).byteLength;
    assert.ok(bytes > 5000, `产物应有实际内容，实际 ${bytes} 字节`);
    await page.context().close();
  });

  it('两条路线的音频均保持正确时长与变速音高', async () => {
    /**
     * 浏览器没有 AAC AudioEncoder 时，快速路线的音频会回退到同一套 FFmpeg 图，
     * 此时可以逐样本比较；支持 AAC 时则验证 WSOLA 后的时长与音高。
     */
    const { page } = await openPage();
    await importFixture(page, fixtures[0]!);

    // 固定为前 2 秒并加 1.5 倍速，让 atempo 路径真正被走到。
    const both = await page.evaluate(async () => {
      const api = (window as unknown as {
        __clip: {
          seek: (t: number) => void;
          split: () => boolean;
          state: () => StateShape;
          deleteClip: (id: string) => boolean;
          setSpeed: (id: string, speed: number) => boolean;
          exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape>;
        };
      }).__clip;
      api.seek(2);
      api.split();
      const clips = api.state().tracks.filter((track) => track.kind === 'video').flatMap((track) => track.clips);
      api.deleteClip(clips[clips.length - 1]!.id);
      const first = api.state().tracks.filter((track) => track.kind === 'video').flatMap((track) => track.clips)[0]!;
      api.setSpeed(first.id, 1.5);
      const webcodecs = await api.exportToBlob({ route: 'webcodecs-video' });
      const wasm = await api.exportToBlob({ route: 'wasm' });
      return { webcodecs, wasm };
    });

    const a = await saveExport(page, both.webcodecs, 'audio-eq-webcodecs.mp4');
    const b = await saveExport(page, both.wasm, 'audio-eq-wasm.mp4');

    // 解码成 PCM 后逐样本比较。
    const pcmA = extractPcm(a);
    const pcmB = extractPcm(b);
    writeArtifact('audio-eq.json', `${JSON.stringify({ samplesA: pcmA.length / 2, samplesB: pcmB.length / 2 }, null, 2)}\n`);

    assert.ok(pcmA.length > 48_000, `WebCodecs 路线音频过短：${pcmA.length} 字节`);
    assert.ok(pcmB.length > 48_000, `ffmpeg.wasm 路线音频过短：${pcmB.length} 字节`);

    // 长度取较短者，容忍编码尾部差异。
    const comparable = Math.min(pcmA.length, pcmB.length);
    let maxDelta = 0;
    let sumDelta = 0;
    for (let index = 0; index < comparable; index += 2) {
      const delta = Math.abs(pcmA.readInt16LE(index) - pcmB.readInt16LE(index));
      maxDelta = Math.max(maxDelta, delta);
      sumDelta += delta;
    }
    const meanDelta = sumDelta / (comparable / 2);
    writeArtifact('audio-eq-delta.json', `${JSON.stringify({ maxDelta, meanDelta }, null, 2)}\n`);

    if (both.webcodecs.audioRoute === 'ffmpeg-wasm') {
      // 同一套滤镜 + 同一个 AAC 编码器，差异应极小（仅取整与浮点顺序）。
      assert.ok(
        meanDelta < 40,
        `两条 FFmpeg 音频路径的平均差异过大：mean=${meanDelta.toFixed(1)} max=${maxDelta}`,
      );
    } else {
      assert.equal(both.webcodecs.audioRoute, 'webcodecs');
      assert.ok(
        Math.abs(pcmA.length - pcmB.length) < 4096,
        `两条路线音频长度差异过大：${pcmA.length} / ${pcmB.length} 字节`,
      );
      let crossings = 0;
      const start = Math.floor(pcmA.length * 0.15 / 2) * 2;
      const end = Math.floor(pcmA.length * 0.85 / 2) * 2;
      for (let offset = start + 2; offset < end; offset += 2) {
        if (pcmA.readInt16LE(offset - 2) < 0 && pcmA.readInt16LE(offset) >= 0) crossings++;
      }
      const frequency = crossings * 48_000 / ((end - start) / 2);
      assert.ok(Math.abs(frequency - 440) < 15, `WebCodecs 变速后音高错误：${frequency.toFixed(1)} Hz`);
    }

    await page.context().close();
  });
  it('明暗主题下界面渲染完整且可交互', async () => {
    /**
     * 界面回归：确认关键区域真的可见。
     * 曾经出现 `.field { display: grid }` 覆盖 `hidden` 属性，导致
     * 「自定义尺寸」在不需要时仍然显示；这类问题只有渲染层面才能发现。
     */
    for (const scheme of ['light', 'dark'] as const) {
      const context = await suiteBrowser.newContext({
        colorScheme: scheme,
        viewport: { width: 1400, height: 900 },
        deviceScaleFactor: 2,
      });
      const page = await context.newPage();
      await page.goto(`${server.origin}/index.html`, { waitUntil: 'load' });
      await page.waitForFunction(
        () => document.getElementById('capability-text')!.textContent!.trim().length > 0,
        undefined,
        { timeout: 60_000 },
      );

      // 空状态：导入按钮与说明应可见，时间轴与导出对话框应隐藏。
      assert.equal(await page.isVisible('#empty-state'), true, `${scheme}: 空状态应可见`);
      assert.equal(await page.isVisible('#timeline-region'), false, `${scheme}: 无素材时应隐藏时间轴`);
      assert.equal(await page.isVisible('#export-dialog'), false, `${scheme}: 导出对话框初始应隐藏`);
      const emptyLayout = await page.evaluate(() => {
        const preview = document.getElementById('preview-region')!;
        const footer = document.querySelector('.status-bar')!.getBoundingClientRect();
        return {
          background: getComputedStyle(preview).backgroundColor,
          footerBottom: footer.bottom,
          viewportBottom: innerHeight,
        };
      });
      assert.equal(emptyLayout.background, 'rgb(255, 255, 255)', `${scheme}: 空预览应保持白色背景`);
      assert.equal(emptyLayout.footerBottom, emptyLayout.viewportBottom, `${scheme}: 空状态 footer 应贴住视口底部`);
      const icons = await page.locator('.ri-icon use').evaluateAll((uses) =>
        uses.map((use) => use.getAttribute('href')),
      );
      assert.ok(icons.length >= 15, `${scheme}: 应使用裁剪后的 RemixIcon 图标集`);
      assert.ok(icons.every((href) => href?.startsWith('./remixicon.svg#ri-')), `${scheme}: 图标应全部来自 RemixIcon sprite`);

      await page.setInputFiles('#file-input', [fixtures[0]!.path, fixtures[1]!.path]);
      // 注意：摘要初始为「0 片段 · 0.00 秒」，因此必须等到时长真的大于 0，
      // 否则会在导入完成前就通过。
      await page.waitForFunction(
        () => {
          const text = document.getElementById('timeline-summary')?.textContent ?? '';
          const seconds = Number(/([\d.]+)\s*秒/.exec(text)?.[1] ?? '0');
          const progress = document.getElementById('progress') as HTMLProgressElement | null;
          return seconds > 0.5 && progress?.hidden === true;
        },
        undefined,
        { timeout: 120_000 },
      );
      assert.equal(await page.isVisible('#timeline-region'), true, `${scheme}: 导入后应显示时间轴`);
      assert.equal(await page.isVisible('#timeline'), true, `${scheme}: 时间轴画布应可见`);
      const editorLayout = await page.evaluate(() => ({
        footerBottom: document.querySelector('.status-bar')!.getBoundingClientRect().bottom,
        viewportBottom: innerHeight,
        documentHeight: document.documentElement.scrollHeight,
      }));
      assert.equal(editorLayout.footerBottom, editorLayout.viewportBottom, `${scheme}: 编辑器 footer 应贴住视口底部`);
      assert.equal(editorLayout.documentHeight, editorLayout.viewportBottom, `${scheme}: 工作区不应撑出视口`);
      await page.waitForSelector('#preview-frame:not([hidden])', { state: 'visible', timeout: 30_000 });

      // 矮视口下时间轴内部收缩，不能把状态栏推出可视区。
      await page.setViewportSize({ width: 1100, height: 420 });
      const compactLayout = await page.evaluate(() => ({
        footerBottom: document.querySelector('.status-bar')!.getBoundingClientRect().bottom,
        timelineHeight: document.getElementById('timeline-region')!.getBoundingClientRect().height,
        viewportBottom: innerHeight,
        documentHeight: document.documentElement.scrollHeight,
      }));
      assert.equal(compactLayout.footerBottom, 420, `${scheme}: 矮视口 footer 应贴住底部`);
      assert.equal(compactLayout.documentHeight, compactLayout.viewportBottom, `${scheme}: 矮视口不应产生页面溢出`);
      assert.ok(compactLayout.timelineHeight < 257, `${scheme}: 时间轴应在矮视口内收缩`);
      await page.setViewportSize({ width: 1400, height: 900 });

      // 预览 100% 是 content-fit；滚轮放大，双击恢复适应。
      await page.hover('#preview-canvas');
      await page.mouse.wheel(0, -120);
      await page.waitForFunction(
        () => Number(document.getElementById('preview-canvas')?.dataset.zoom ?? 1) > 1,
      );
      assert.ok(
        await page.locator('#preview-canvas').evaluate((element) => Number((element as HTMLElement).dataset.zoom) > 1),
        `${scheme}: 预览滚轮应放大画面`,
      );
      await page.dblclick('#preview-canvas');
      assert.equal(
        await page.locator('#preview-canvas').evaluate((element) => Number((element as HTMLElement).dataset.zoom)),
        1,
        `${scheme}: 双击应恢复 content-fit`,
      );

      // 普通滚轮只纵向浏览轨道，不改变缩放或水平位置。
      await page.locator('#timeline-scroll').evaluate((element) => {
        const scroll = element as HTMLElement;
        scroll.style.height = '120px';
        scroll.style.maxHeight = '120px';
        scroll.scrollTop = 0;
      });
      const timelineBox = await page.locator('#timeline-scroll').boundingBox();
      assert.ok(timelineBox, `${scheme}: 应能读取时间轴位置`);
      const timelineBefore = await page.evaluate(() => {
        const scroll = document.getElementById('timeline-scroll')!;
        const canvas = document.getElementById('timeline')!;
        const anchor = scroll.clientWidth * 0.62;
        return {
          anchor,
          clientWidth: scroll.clientWidth,
          canvasWidth: canvas.clientWidth,
          scrollLeft: scroll.scrollLeft,
          scrollTop: scroll.scrollTop,
          hasVerticalOverflow: scroll.scrollHeight > scroll.clientHeight,
          // 与时间轴的 176px 固定轨道标题区和 20px 右侧留白一致。
          normalizedAnchor: (scroll.scrollLeft + anchor - 176) / Math.max(1, canvas.clientWidth - 196),
        };
      });
      const wheelPoint = {
        clientX: timelineBox!.x + timelineBefore.anchor,
        clientY: timelineBox!.y + 40,
      };
      assert.equal(timelineBefore.hasVerticalOverflow, true, `${scheme}: 测试时间轴应具有纵向滚动空间`);
      await page.locator('#timeline-scroll').dispatchEvent('wheel', { ...wheelPoint, deltaY: 120 });
      const timelineVerticallyScrolled = await page.evaluate(() => {
        const scroll = document.getElementById('timeline-scroll')!;
        return {
          canvasWidth: document.getElementById('timeline')!.clientWidth,
          scrollLeft: scroll.scrollLeft,
          scrollTop: scroll.scrollTop,
        };
      });
      assert.equal(timelineVerticallyScrolled.canvasWidth, timelineBefore.canvasWidth,
        `${scheme}: 普通滚轮不应缩放时间轴`);
      assert.equal(timelineVerticallyScrolled.scrollLeft, timelineBefore.scrollLeft,
        `${scheme}: 普通滚轮不应水平移动`);
      assert.ok(timelineVerticallyScrolled.scrollTop > timelineBefore.scrollTop,
        `${scheme}: 普通滚轮应纵向滚动`);

      // Ctrl + 滚轮以指针为锚缩放，不能在旧画布宽度上重复累乘。
      for (let index = 0; index < 8; index++) {
        await page.locator('#timeline-scroll').dispatchEvent('wheel', { ...wheelPoint, deltaY: -120, ctrlKey: true });
      }
      const timelineZoomed = await page.evaluate((anchor) => {
        const scroll = document.getElementById('timeline-scroll')!;
        const canvas = document.getElementById('timeline')!;
        return {
          clientWidth: scroll.clientWidth,
          canvasWidth: canvas.clientWidth,
          scrollLeft: scroll.scrollLeft,
          normalizedAnchor: (scroll.scrollLeft + anchor - 176) / Math.max(1, canvas.clientWidth - 196),
        };
      }, timelineBefore.anchor);
      assert.ok(
        Math.abs(timelineZoomed.canvasWidth / timelineZoomed.clientWidth - Math.pow(1.2, 8)) < 0.01,
        `${scheme}: 连续缩放不应指数累乘旧画布宽度`,
      );
      assert.ok(
        Math.abs(timelineZoomed.normalizedAnchor - timelineBefore.normalizedAnchor) < 0.002,
        `${scheme}: 缩放应保持鼠标下的时间点`,
      );

      // Shift + 滚轮只移动窗口，不改变画布缩放。
      await page.locator('#timeline-scroll').dispatchEvent('wheel', {
        ...wheelPoint,
        deltaY: 120,
        shiftKey: true,
      });
      const timelineShifted = await page.evaluate(() => {
        const scroll = document.getElementById('timeline-scroll')!;
        return {
          canvasWidth: document.getElementById('timeline')!.clientWidth,
          scrollLeft: scroll.scrollLeft,
        };
      });
      assert.equal(timelineShifted.canvasWidth, timelineZoomed.canvasWidth, `${scheme}: Shift + 滚轮不应改变缩放`);
      assert.equal(timelineShifted.scrollLeft - timelineZoomed.scrollLeft, 120, `${scheme}: Shift + 滚轮应横向滚动`);

      // Ctrl + 向下滚动回到适应状态，画布必须重新等于窗口宽度且仍有内容。
      for (let index = 0; index < 30; index++) {
        await page.locator('#timeline-scroll').dispatchEvent('wheel', { ...wheelPoint, deltaY: 120, ctrlKey: true });
      }
      const timelineFitted = await page.evaluate(() => {
        const scroll = document.getElementById('timeline-scroll')!;
        return {
          clientWidth: scroll.clientWidth,
          canvasWidth: document.getElementById('timeline')!.clientWidth,
          scrollLeft: scroll.scrollLeft,
        };
      });
      assert.equal(timelineFitted.canvasWidth, timelineFitted.clientWidth, `${scheme}: 缩小到底应恢复完整时间轴`);
      assert.equal(timelineFitted.scrollLeft, 0, `${scheme}: 适应状态不应停在空白区域`);
      // 摘要显示的是「当前活动轨道」（最后导入的素材），与桌面版一致；
      // 两条轨道各有 1 个片段。
      const summary = await page.textContent('#timeline-summary');
      assert.match(summary ?? '', /1 片段/, `${scheme}: 活动轨道应有 1 个片段，实际 ${summary}`);
      const trackCount = await page.evaluate(
        () => (window as unknown as { __clip?: unknown }).__clip ?? null,
      );
      // 普通访问（无 ?automation=1）不应暴露自动化接口。
      assert.equal(trackCount, null, `${scheme}: 正常访问不应挂载自动化接口`);

      // 导出对话框：默认尺寸与编码器可见，自定义尺寸字段应隐藏。
      await page.click('#export-button');
      await page.waitForSelector('#export-dialog[open]', { timeout: 30_000 });
      assert.equal(await page.isVisible('#dimensions-field'), false, `${scheme}: 未选自定义尺寸时应隐藏宽高输入`);
      assert.equal(await page.isVisible('#encoder'), true, `${scheme}: 应显示编码器选择`);
      const routeText = await page.textContent('#route-callout');
      assert.match(routeText ?? '', /WebCodecs|ffmpeg\.wasm/, `${scheme}: 应说明编码路线`);

      // 切换到自定义尺寸后字段应出现，并且校验生效。
      await page.selectOption('#size', 'custom');
      assert.equal(await page.isVisible('#dimensions-field'), true, `${scheme}: 选自定义尺寸后应显示宽高输入`);
      await page.fill('#width', '721');
      await page.waitForFunction(
        () => !document.getElementById('validation-text')!.hidden,
        undefined,
        { timeout: 10_000 },
      );
      const validation = await page.textContent('#validation-text');
      assert.match(validation ?? '', /偶数/, `${scheme}: 奇数宽高应给出提示，实际 ${validation}`);

      const shot = join(ensureArtifacts(), `ui-${scheme}-editor.png`);
      await page.keyboard.press('Escape');
      await page.screenshot({ path: shot });
      await context.close();
    }
  });

  it('移动端布局、双指手势与导出保存时机正确', async () => {
    const context = await suiteBrowser.newContext({
      viewport: { width: 390, height: 844 },
      deviceScaleFactor: 2,
      isMobile: true,
      hasTouch: true,
    });
    const page = await context.newPage();
    const errors: string[] = [];
    page.on('pageerror', (error) => errors.push(`pageerror: ${error.message}`));
    page.on('console', (message) => {
      if (message.type() === 'error') errors.push(`console: ${message.text()}`);
    });

    // 即使移动浏览器暴露了该 API，也应跳过它，避免系统面板不弹出时卡住导出。
    await page.addInitScript(() => {
      type Probe = { active: boolean; progressHidden: boolean; calls: number };
      const target = window as unknown as {
        __savePickerProbe: Probe | null;
        showSaveFilePicker: (options: unknown) => Promise<unknown>;
      };
      target.__savePickerProbe = null;
      Object.defineProperty(window, 'showSaveFilePicker', {
        configurable: true,
        value: async () => {
          target.__savePickerProbe = {
            active: navigator.userActivation.isActive,
            progressHidden: document.getElementById('progress')?.hidden === true,
            calls: (target.__savePickerProbe?.calls ?? 0) + 1,
          };
          return {
            createWritable: async () => ({
              write: async () => undefined,
              close: async () => undefined,
            }),
          };
        },
      });
    });

    await page.goto(`${server.origin}/index.html`, { waitUntil: 'load' });
    await page.waitForFunction(
      () => document.getElementById('capability-text')!.textContent!.trim().length > 0,
      undefined,
      { timeout: 60_000 },
    );

    const emptyLayout = await page.evaluate(() => ({
      documentWidth: document.documentElement.scrollWidth,
      viewportWidth: innerWidth,
      appWidth: document.getElementById('app')!.getBoundingClientRect().width,
      headerRight: document.querySelector('.command-header')!.getBoundingClientRect().right,
      footerBottom: document.querySelector('.status-bar')!.getBoundingClientRect().bottom,
      viewportBottom: innerHeight,
    }));
    assert.equal(emptyLayout.documentWidth, emptyLayout.viewportWidth, '移动端空状态不应横向溢出');
    assert.equal(emptyLayout.appWidth, emptyLayout.viewportWidth, '工作区应等于移动端视口宽度');
    assert.equal(emptyLayout.headerRight, emptyLayout.viewportWidth, '页头操作不应被裁切');
    assert.equal(emptyLayout.footerBottom, emptyLayout.viewportBottom, '移动端状态栏应贴住底部');

    await page.setInputFiles('#file-input', [fixtures[0]!.path]);
    await page.waitForFunction(
      () => {
        const summary = document.getElementById('timeline-summary')?.textContent ?? '';
        return !document.getElementById('timeline-region')?.hidden && summary.includes('4.00');
      },
      undefined,
      { timeout: 120_000 },
    );
    const editorLayout = await page.evaluate(() => {
      const toolbar = document.querySelector('.timeline-toolbar')!;
      return {
        documentWidth: document.documentElement.scrollWidth,
        viewportWidth: innerWidth,
        toolbarWidth: toolbar.clientWidth,
        toolbarScrollWidth: toolbar.scrollWidth,
        timelineRight: document.getElementById('timeline-region')!.getBoundingClientRect().right,
        timelineCanvasHeight: document.getElementById('timeline')!.clientHeight,
        tapHighlight: getComputedStyle(document.getElementById('timeline')!)
          .getPropertyValue('-webkit-tap-highlight-color'),
      };
    });
    assert.equal(editorLayout.documentWidth, editorLayout.viewportWidth, '导入后也不应撑宽页面');
    assert.equal(editorLayout.timelineRight, editorLayout.viewportWidth, '时间轴应完整收在视口内');
    assert.ok(editorLayout.toolbarScrollWidth > editorLayout.toolbarWidth, '窄屏时操作栏应在内部横向滚动');
    assert.equal(editorLayout.timelineCanvasHeight, 172,
      '伴生轨默认折叠时应只显示两条 68px 视频轨、28px 标尺和 8px 留白');
    assert.match(editorLayout.tapHighlight, /rgba\(0, 0, 0, 0\)|transparent/, '时间轴触摸不应出现蓝色点击层');

    await page.waitForFunction(() => sessionStorage.getItem('clip.edit-session.v1') !== null);
    const savedTracks = await page.evaluate(() => {
      const stored = JSON.parse(sessionStorage.getItem('clip.edit-session.v1')!) as {
        project: { tracks: { id: string; clips: unknown[] }[] };
      };
      return stored.project.tracks.map((track) => ({ id: track.id, clips: track.clips.length }));
    });
    assert.equal(savedTracks.filter((track) => track.clips === 0).length, 1, '存档中也只能有一条空轨');
    assert.equal(savedTracks.at(-1)?.clips, 0, '存档中空轨应位于末尾');

    await page.waitForSelector('#preview-frame:not([hidden])', { state: 'visible', timeout: 30_000 });
    await page.waitForFunction(
      () => {
        const canvas = document.getElementById('preview-frame') as HTMLCanvasElement;
        return canvas.width > 0 && canvas.height > 0;
      },
      undefined,
      { timeout: 30_000 },
    );
    const previewSurface = await page.evaluate(() => {
      const canvas = document.getElementById('preview-frame') as HTMLCanvasElement;
      const decoder = document.getElementById('preview') as HTMLVideoElement;
      const context = canvas.getContext('2d');
      const samples: number[] = [];
      if (context && canvas.width > 0 && canvas.height > 0) {
        for (let row = 1; row <= 3; row++) {
          for (let column = 1; column <= 3; column++) {
            const pixel = context.getImageData(
              Math.floor(canvas.width * column / 4),
              Math.floor(canvas.height * row / 4),
              1,
              1,
            ).data;
            samples.push(pixel[0]! + pixel[1]! + pixel[2]!);
          }
        }
      }
      const decoderBounds = decoder.getBoundingClientRect();
      return {
        canvasHidden: canvas.hidden,
        canvasWidth: canvas.width,
        canvasHeight: canvas.height,
        hasPicture: samples.some((value) => value > 24),
        decoderOpacity: getComputedStyle(decoder).opacity,
        decoderWidth: decoderBounds.width,
        decoderHeight: decoderBounds.height,
        playsInline: decoder.hasAttribute('playsinline'),
        webkitPlaysInline: decoder.hasAttribute('webkit-playsinline'),
        x5PageMode: decoder.getAttribute('x5-video-player-type'),
        pictureInPictureDisabled: decoder.hasAttribute('disablepictureinpicture'),
      };
    });
    assert.equal(previewSurface.canvasHidden, false, '可见预览应由 canvas 承载');
    assert.ok(previewSurface.canvasWidth > 0 && previewSurface.canvasHeight > 0, 'canvas 应按预览区尺寸绘制');
    assert.equal(previewSurface.hasPicture, true, 'canvas 应包含解码后的视频画面');
    assert.equal(previewSurface.decoderOpacity, '0', 'video 解码器不应成为可见预览层');
    assert.ok(previewSurface.decoderWidth <= 2 && previewSurface.decoderHeight <= 2, 'video 解码器应收纳为隐藏尺寸');
    assert.equal(previewSurface.playsInline, true, '应请求标准内联播放');
    assert.equal(previewSurface.webkitPlaysInline, true, '应请求 WebKit 内联播放');
    assert.equal(previewSurface.x5PageMode, 'h5-page', '应请求 X5 使用 H5 页内播放');
    assert.equal(previewSurface.pictureInPictureDisabled, true, '预览解码器不应进入画中画');

    await page.click('#play-button');
    await page.waitForFunction(
      () => (document.getElementById('preview') as HTMLVideoElement).currentTime > 0.1,
      undefined,
      { timeout: 10_000 },
    );
    await page.click('#play-button');

    // 用真实 Chromium 触摸事件验证双指缩放。
    const cdp = await context.newCDPSession(page);
    const previewBox = await page.locator('#preview-canvas').boundingBox();
    assert.ok(previewBox, '应能读取移动端预览区坐标');
    const previewX = previewBox!.x + previewBox!.width / 2;
    const previewY = previewBox!.y + previewBox!.height / 2;
    await cdp.send('Input.dispatchTouchEvent', {
      type: 'touchStart',
      touchPoints: [{ x: previewX - 35, y: previewY, id: 1 }, { x: previewX + 35, y: previewY, id: 2 }],
    });
    await cdp.send('Input.dispatchTouchEvent', {
      type: 'touchMove',
      touchPoints: [{ x: previewX - 75, y: previewY, id: 1 }, { x: previewX + 75, y: previewY, id: 2 }],
    });
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    const previewZoom = Number(await page.locator('#preview-canvas').getAttribute('data-zoom'));
    assert.ok(previewZoom > 2, `双指应放大预览，实际 ${previewZoom}`);

    const timelineBox = await page.locator('#timeline').boundingBox();
    assert.ok(timelineBox, '应能读取移动端时间轴坐标');
    const timelineX = timelineBox!.x + timelineBox!.width / 2;
    const timelineY = timelineBox!.y + Math.min(55, timelineBox!.height / 2);
    await cdp.send('Input.dispatchTouchEvent', {
      type: 'touchStart',
      touchPoints: [{ x: timelineX - 35, y: timelineY, id: 3 }, { x: timelineX + 35, y: timelineY, id: 4 }],
    });
    await cdp.send('Input.dispatchTouchEvent', {
      type: 'touchMove',
      touchPoints: [{ x: timelineX - 80, y: timelineY, id: 3 }, { x: timelineX + 80, y: timelineY, id: 4 }],
    });
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    const timelineGesture = await page.evaluate(() => {
      const canvas = document.getElementById('timeline')!;
      const scroll = document.getElementById('timeline-scroll')!;
      return { canvasWidth: canvas.clientWidth, viewportWidth: scroll.clientWidth, scrollLeft: scroll.scrollLeft };
    });
    assert.ok(timelineGesture.canvasWidth > timelineGesture.viewportWidth * 2, '双指应放大时间轴');
    assert.ok(timelineGesture.scrollLeft > 0, '双指缩放应保持手势中心而移动窗口');

    await page.click('#export-button');
    await page.waitForSelector('#export-dialog[open]');
    const dialogBounds = await page.locator('#export-dialog').evaluate((element) => {
      const bounds = element.getBoundingClientRect();
      return { left: bounds.left, right: bounds.right, bottom: bounds.bottom };
    });
    assert.equal(dialogBounds.left, 0, '移动端导出面板应铺满宽度');
    assert.equal(dialogBounds.right, 390, '移动端导出面板不应超出视口');
    assert.equal(dialogBounds.bottom, 844, '移动端导出面板应贴底');

    await page.click('#export-confirm');
    await page.waitForFunction(
      () => {
        const dialog = document.getElementById('export-dialog') as HTMLDialogElement;
        return !dialog.open && document.getElementById('progress')?.hidden === false;
      },
      undefined,
      { timeout: 10_000 },
    );
    const saveProbe = await page.evaluate(() =>
      (window as unknown as {
        __savePickerProbe: { active: boolean; progressHidden: boolean; calls: number } | null;
      }).__savePickerProbe,
    );
    assert.equal(saveProbe, null, '触控优先设备不应调用可能挂起的保存选择器');

    await page.waitForSelector('#cancel-button:not([hidden])', { timeout: 10_000 });
    await page.click('#cancel-button');
    await page.screenshot({ path: join(ensureArtifacts(), 'ui-mobile-editor.png') });
    assert.equal(errors.length, 0, `移动端页面不应报错：${errors.join(' | ')}`);
    await context.close();
  });

  it('桌面端在用户手势内选择保存位置', async () => {
    const context = await suiteBrowser.newContext({ viewport: { width: 1100, height: 760 } });
    const page = await context.newPage();
    await page.addInitScript(() => {
      type Probe = { active: boolean; progressHidden: boolean; calls: number };
      const target = window as unknown as {
        __savePickerProbe: Probe | null;
        showSaveFilePicker: () => Promise<unknown>;
      };
      target.__savePickerProbe = null;
      Object.defineProperty(window, 'showSaveFilePicker', {
        configurable: true,
        value: async () => {
          target.__savePickerProbe = {
            active: navigator.userActivation.isActive,
            progressHidden: document.getElementById('progress')?.hidden === true,
            calls: (target.__savePickerProbe?.calls ?? 0) + 1,
          };
          return {
            createWritable: async () => ({
              write: async () => undefined,
              close: async () => undefined,
            }),
          };
        },
      });
    });

    await page.goto(`${server.origin}/index.html`, { waitUntil: 'load' });
    await page.setInputFiles('#file-input', [fixtures[0]!.path]);
    await page.waitForFunction(
      () => (document.getElementById('timeline-summary')?.textContent ?? '').includes('4.00'),
      undefined,
      { timeout: 120_000 },
    );
    await page.click('#export-button');
    await page.waitForSelector('#export-dialog[open]');
    await page.click('#export-confirm');
    await page.waitForFunction(
      () => (window as unknown as { __savePickerProbe?: { calls: number } }).__savePickerProbe?.calls === 1,
    );
    const probe = await page.evaluate(() =>
      (window as unknown as {
        __savePickerProbe: { active: boolean; progressHidden: boolean; calls: number };
      }).__savePickerProbe,
    );
    assert.equal(probe.active, true, '桌面保存选择器必须在用户手势激活期内调用');
    assert.equal(probe.progressHidden, true, '桌面端应先选择保存位置，再启动编码');
    assert.equal(probe.calls, 1, '一次导出只应打开一次保存选择器');
    await page.waitForSelector('#cancel-button:not([hidden])', { timeout: 10_000 });
    await page.click('#cancel-button');
    await context.close();
  });

  it('分割后导出整条轨道（多片段、同一素材）', async () => {
    /**
     * 回归测试：同一素材被切成多段后，导出时必须为每个片段重置解码状态。
     * wasm 兜底路线必须逐段编码再拼接，避免大型 filtergraph 同时解码所有片段导致 OOM。
     */
    const { page } = await openPage();
    await importFixture(page, fixtures[0]!);

    const state = await page.evaluate(() => {
      const api = (window as unknown as {
        __clip: { seek: (t: number) => void; split: () => boolean; clips: () => readonly { id: string }[] };
      }).__clip;
      api.seek(1);
      const first = api.split();
      api.seek(2.5);
      const second = api.split();
      return { first, second, count: api.clips().length };
    });
    assert.equal(state.first, true, '第一次分割应成功');
    assert.equal(state.second, true, '第二次分割应成功');
    assert.equal(state.count, 3, `应得到 3 个片段，实际 ${state.count}`);

    // 保留全部三个片段导出（不删除任何片段）。
    const result = await page.evaluate(async () => {
      const api = (window as unknown as {
        __clip: { exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape> };
      }).__clip;
      return api.exportToBlob({ route: 'wasm' });
    });

    const path = await saveExport(page, result, 'export-multisegment.mp4');
    const summary = probeFile(path);
    writeArtifact('export-multisegment-probe.json', `${JSON.stringify(summary, null, 2)}\n`);
    assert.equal(summary.videoCodec, 'h264');
    assert.equal(result.route, 'wasm');
    assert.equal(summary.width, 640);
    assert.equal(summary.height, 360);
    // 三段拼接后应恢复完整时长。
    assert.ok(Math.abs(summary.duration - 4) < 0.4, `三段拼接应约为 4 秒，实际 ${summary.duration}`);
    assert.ok(summary.frameCount >= 110, `应输出约 120 帧，实际 ${summary.frameCount}`);
    assert.equal(summary.hasAudio, true, '应保留音轨');

    await page.context().close();
  });

  it('反复取消导出对话框后编辑与导出仍然正常', async () => {
    // 对话框的监听器与 Promise 在每次打开时重建，取消不应累积状态或卡死。
    const { page, errors } = await openPage();
    await importFixture(page, fixtures[0]!);

    for (let round = 0; round < 3; round++) {
      await page.click('#export-button');
      await page.waitForFunction(
        () => (document.getElementById('export-dialog') as HTMLDialogElement).open,
        undefined,
        { timeout: 15_000 },
      );
      await page.click('#export-cancel');
      await page.waitForFunction(
        () => !(document.getElementById('export-dialog') as HTMLDialogElement).open,
        undefined,
        { timeout: 15_000 },
      );
    }

    const result = await page.evaluate(async () => {
      const api = (window as unknown as {
        __clip: {
          seek: (t: number) => void;
          split: () => boolean;
          clips: () => readonly { id: string }[];
          exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape>;
        };
      }).__clip;
      api.seek(2);
      const splitOk = api.split();
      return { splitOk, count: api.clips().length, exported: await api.exportToBlob({ route: 'webcodecs-video', width: 320, height: 180 }) };
    });

    assert.equal(result.splitOk, true, '取消对话框后应仍能分割');
    assert.equal(result.count, 2, '取消对话框后编辑状态应保持');
    assert.ok(result.exported.byteLength > 1000, '取消对话框后应仍能导出');
    assert.equal(errors.length, 0, `不应有页面错误：${errors.join(' | ')}`);

    await page.context().close();
  });

  it('导出失败后可以再次开始导出', async () => {
    const { page, errors } = await openPage();
    await importFixture(page, fixtures[2]!);

    // 让文件写入失败，覆盖编码完成后失败时的真实 UI 状态恢复路径。
    await page.evaluate(() => {
      Object.defineProperty(window, 'showSaveFilePicker', {
        configurable: true,
        value: async () => ({
          createWritable: async () => ({
            write: async () => { throw new Error('模拟导出文件写入失败'); },
            close: async () => undefined,
          }),
        }),
      });
    });

    await page.click('#export-button');
    await page.waitForSelector('#export-dialog[open]');
    await page.click('#export-confirm');
    await page.waitForFunction(
      () => Array.from(document.querySelectorAll('dialog[open] h2'))
        .some((heading) => heading.textContent === '导出未完成'),
      undefined,
      { timeout: 30_000 },
    );
    await page.getByRole('button', { name: '知道了' }).click();
    await page.waitForFunction(
      () => !(document.getElementById('export-button') as HTMLButtonElement).disabled,
      undefined,
      { timeout: 10_000 },
    );

    await page.click('#export-button');
    await page.waitForSelector('#export-dialog[open]');
    assert.equal(
      await page.locator('#export-confirm').isEnabled(),
      true,
      '失败后重新打开导出对话框时，“开始导出”应恢复可用',
    );
    await page.click('#export-cancel');
    assert.equal(errors.length, 0, `不应有页面错误：${errors.join(' | ')}`);

    await page.context().close();
  });

  it('导出画面在 YUV 采样层面与源一致（两条路线都保真）', async () => {
    /**
     * 保真回归：以原始 YUV 采样为准，而不是解成 RGB 的像素。
     * 注意源的色彩矩阵未标记，解码成 RGB 时会看到约 8/255 的「偏移」，
     * 那是元数据猜测所致；YUV 采样本身应当几乎相同。
     */
    const { page } = await openPage();
    await importFixture(page, fixtures[0]!);

    const routeFiles: { route: string; name: string }[] = [
      { route: 'webcodecs-video', name: 'fidelity-webcodecs.mp4' },
      { route: 'wasm', name: 'fidelity-wasm.mp4' },
    ];

    for (const item of routeFiles) {
      const result = await page.evaluate(
        async (route: string) => {
          const api = (window as unknown as {
            __clip: { exportToBlob: (request?: Record<string, unknown>) => Promise<ExportResultShape> };
          }).__clip;
          return api.exportToBlob({ route });
        },
        item.route,
      );
      const path = await saveExport(page, result, item.name);
      const source = extractYuvFrame(fixtures[0]!.path, 0.5);
      const output = extractYuvFrame(path, 0.5);
      const delta = yuvDelta(source, output);
      writeArtifact(
        `fidelity-${item.route}.json`,
        `${JSON.stringify({ route: item.route, ...delta }, null, 2)}\n`,
      );
      assert.ok(
        delta.mean < 3,
        `${item.route}: YUV 采样与源差异过大（mean=${delta.mean.toFixed(3)} max=${delta.max}）`,
      );
    }

    await page.context().close();
  });
});
