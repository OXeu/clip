/**
 * 一致性测试：网页版生成的 filter graph 必须与桌面端 C# 实现逐字相同。
 *
 * 基准 web/test/fixtures/conformance.json 由 tools/Clip.Conformance 用
 * src/Clip.Core 里真正的 ExportService 生成，不是手抄常量。
 * 重新生成：
 *   dotnet run --project tools/Clip.Conformance -c Release -- web/test/fixtures/conformance.json
 */

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';

import {
  buildAudioFilter,
  buildFilter,
  buildTempoFilter,
} from '../src/filtergraph.ts';
import {
  type ExportOptions,
  type MediaInfo,
  type VideoClip,
  defaultExportOptions,
  getDimensions,
} from '../src/model.ts';

interface ConformanceCase {
  readonly name: string;
  readonly filter: string;
  readonly dimensions: { readonly width: number; readonly height: number };
}

interface ConformanceFixture {
  readonly tempo: Record<string, string>;
  readonly cases: readonly ConformanceCase[];
}

const fixture = JSON.parse(
  readFileSync(fileURLToPath(new URL('./fixtures/conformance.json', import.meta.url)), 'utf8'),
) as ConformanceFixture;

const media: MediaInfo = {
  path: 'source.mp4',
  duration: 10,
  width: 1920,
  height: 1080,
  frameRate: 30,
  videoStreamIndex: 0,
  audioStreamIndex: 1,
  codec: 'h264',
  videoTimestampOffset: 0,
  isHdr: false,
};

const portrait: MediaInfo = { ...media, path: 'portrait.mp4', width: 1080, height: 1920, frameRate: 24 };
const silent: MediaInfo = { ...media, audioStreamIndex: null };
const indexed: MediaInfo = { ...media, videoStreamIndex: 2, audioStreamIndex: 3 };
const offset: MediaInfo = { ...media, videoTimestampOffset: 1.5 };

const CLIP_ID = '00000000-0000-0000-0000-000000000001';

/** 与 tools/Clip.Conformance/Program.cs 的 Clip 帮助函数一致。 */
function clip(source: MediaInfo, start: number, end: number, speed = 1): VideoClip {
  return { id: CLIP_ID, media: source, start, end, speed };
}

const opts = (over: Partial<ExportOptions> = {}): ExportOptions => ({ ...defaultExportOptions, ...over });

function segments(source: MediaInfo, ranges: readonly (readonly [number, number])[], options: ExportOptions): string {
  return buildFilter(ranges.map(([start, end]) => clip(source, start, end)), options);
}

/** 用例顺序与 Clip.Conformance 中追加的顺序一致。 */
const cases: readonly { name: string; build: () => string; dimensions: () => { width: number; height: number } }[] = [
  {
    name: 'single full clip',
    build: () => segments(media, [[0, 10]], opts()),
    dimensions: () => getDimensions(opts(), media),
  },
  {
    name: 'trimmed interval',
    build: () => segments(media, [[1.25, 5.5]], opts()),
    dimensions: () => getDimensions(opts(), media),
  },
  {
    name: 'two retained intervals',
    build: () => segments(indexed, [[0, 2], [5, 10]], opts()),
    dimensions: () => getDimensions(opts(), media),
  },
  {
    name: 'silent source',
    build: () => segments(silent, [[0, 5]], opts()),
    dimensions: () => getDimensions(opts(), silent),
  },
  {
    name: 'custom portrait dimensions',
    build: () => segments(media, [[0, 5]], opts({ width: 720, height: 1280 })),
    dimensions: () => getDimensions(opts({ width: 720, height: 1280 }), media),
  },
  {
    name: 'nonstandard stream indexes',
    build: () => segments(indexed, [[1, 4]], opts()),
    dimensions: () => getDimensions(opts(), indexed),
  },
  {
    name: 'timestamp offset',
    build: () => segments(offset, [[0, 3]], opts()),
    dimensions: () => getDimensions(opts(), offset),
  },
  {
    name: 'speed 2x',
    build: () => buildFilter([clip(media, 0, 10, 2)], opts()),
    dimensions: () => getDimensions(opts(), media),
  },
  {
    name: 'speed 0.5x',
    build: () => buildFilter([clip(media, 0, 10, 0.5)], opts()),
    dimensions: () => getDimensions(opts(), media),
  },
  {
    name: 'speed 8x',
    build: () => buildFilter([clip(media, 0, 10, 8)], opts()),
    dimensions: () => getDimensions(opts(), media),
  },
  {
    name: 'speed 0.1x',
    build: () => buildFilter([clip(media, 0, 10, 0.1)], opts()),
    dimensions: () => getDimensions(opts(), media),
  },
  {
    name: 'speed 1.37x',
    build: () => buildFilter([clip(media, 0, 10, 1.37)], opts()),
    dimensions: () => getDimensions(opts(), media),
  },
  {
    name: 'mixed sources and speeds',
    build: () => buildFilter([clip(silent, 0, 10, 2), clip(portrait, 0, 10, 0.5)], opts()),
    dimensions: () => getDimensions(opts(), silent),
  },
  {
    name: 'mixed sources with audio',
    build: () => buildFilter([clip(media, 0, 4, 2), clip(portrait, 2, 9.5, 0.5)], opts({ width: 1280, height: 720 })),
    dimensions: () => getDimensions(opts({ width: 1280, height: 720 }), media),
  },
  {
    name: 'crossfade-free three cuts',
    build: () => buildFilter([clip(media, 0, 3), clip(media, 3, 6), clip(media, 6, 10)], opts()),
    dimensions: () => getDimensions(opts(), media),
  },
  {
    name: 'custom size on portrait source',
    build: () => buildFilter([clip(portrait, 1, 8)], opts({ width: 640, height: 640 })),
    dimensions: () => getDimensions(opts({ width: 640, height: 640 }), portrait),
  },
];

describe('filter graph 与桌面端 C# 实现一致', () => {
  it('基准覆盖全部用例', () => {
    assert.equal(fixture.cases.length, cases.length);
  });

  for (const [index, testCase] of cases.entries()) {
    it(testCase.name, () => {
      const expected = fixture.cases[index];
      assert.ok(expected, `基准缺少用例 ${testCase.name}`);
      assert.equal(expected.name, testCase.name, '基准顺序与用例顺序不一致');
      assert.equal(testCase.build(), expected.filter);
      assert.deepEqual(testCase.dimensions(), {
        width: expected.dimensions.width,
        height: expected.dimensions.height,
      });
    });
  }
});

describe('tempo 分级与桌面端一致', () => {
  for (const [speed, expected] of Object.entries(fixture.tempo)) {
    it(`${speed}x`, () => {
      assert.equal(buildTempoFilter(Number(speed)), expected);
    });
  }

  it('每级都落在 0.5–2，且乘积等于目标速度', () => {
    for (const speed of [0.1, 0.25, 0.5, 0.75, 1, 1.37, 2, 3, 4, 8]) {
      const stages = buildTempoFilter(speed)
        .split(',')
        .map((stage) => Number(stage.split('=')[1]));
      assert.ok(stages.every((s) => s >= 0.5 && s <= 2), `${speed}x 存在跳采样的分级`);
      const product = stages.reduce((a, b) => a * b, 1);
      assert.ok(Math.abs(product - speed) < 1e-9, `${speed}x 分级乘积为 ${product}`);
    }
  });
});

describe('音频半边 filter graph', () => {
  it('无声素材补静音并保持 concat 段数', () => {
    const graph = buildAudioFilter([clip(silent, 0, 5), clip(media, 0, 5)]);
    assert.match(graph, /anullsrc=r=48000:cl=stereo,atrim=duration=5/);
    assert.match(graph, /concat=n=2:v=0:a=1\[audio\]$/);
  });

  it('源下标从 1 开始，因为 0 号输入是 WebCodecs 的 video.mp4', () => {
    const graph = buildAudioFilter([clip(media, 0, 5)]);
    assert.match(graph, /^\[1:1\]/, '应引用第二个输入');
    assert.doesNotMatch(graph, /^\[0:/m);
  });

  it('与完整图中的音频段一致', () => {
    const clips = [clip(media, 0, 4, 2), clip(portrait, 2, 9.5, 0.5)];
    const full = buildFilter(clips, opts());
    const audio = buildAudioFilter(clips);
    // 每个 [as..] 到 [a..] 的音频片段必须在两张图里完全相同。
    const chains = [...audio.matchAll(/\[as(\d+)\](.+?)\[a\1\]/g)].map((m) => m[2]!);
    assert.ok(chains.length === 2, '未找到音频链');
    for (const chain of chains) assert.ok(full.includes(chain), `完整图缺少音频链：${chain}`);
  });

  it('全程无声时不引用任何输入', () => {
    const graph = buildAudioFilter([clip(silent, 0, 5)]);
    assert.doesNotMatch(graph, /\[\d+:\d+\]/);
    assert.match(graph, /anullsrc/);
  });
});

describe('参数校验', () => {
  it('拒绝奇数自定义宽高', () => {
    assert.throws(() => getDimensions({ width: 721, height: 1280 }, media));
  });

  it('拒绝空轨道', () => {
    assert.throws(() => buildFilter([], opts()));
  });

  it('拒绝超出范围的速度', () => {
    assert.throws(() => buildTempoFilter(0));
    assert.throws(() => buildTempoFilter(8.1));
  });

  it('拒绝 HDR 素材', () => {
    assert.throws(() => buildFilter([clip({ ...media, isHdr: true }, 0, 5)], opts()));
  });

  it('拒绝只填写一边的尺寸', () => {
    assert.throws(() => getDimensions({ width: 720, height: null }, media));
  });
});
