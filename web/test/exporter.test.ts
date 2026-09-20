import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
  buildWasmConcatArguments,
  FrameQueue,
  mapWasmMedia,
  isNonMonotonicDtsError,
  muxerFrameRate,
  preferredWebCodecsAudioCodec,
  videoMuxerReadinessError,
  wasmConcatManifest,
  webCodecsVideoConfig,
} from '../src/exporter.ts';
import type { MediaInfo, VideoClip } from '../src/model.ts';

const fakeFrame = (): VideoFrame => ({ close() {} }) as VideoFrame;

describe('mp4-muxer 帧率配置', () => {
  it('整数帧率可直接作为容器 timescale', () => {
    assert.equal(muxerFrameRate(24), 24);
    assert.equal(muxerFrameRate(60), 60);
  });

  it('非整数帧率省略配置并保留真实 chunk 时间戳', () => {
    assert.equal(muxerFrameRate(32.10813374436803), undefined);
    assert.equal(muxerFrameRate(30_000 / 1_001), undefined);
  });

  it('非法帧率不会传给 mp4-muxer', () => {
    assert.equal(muxerFrameRate(0), undefined);
    assert.equal(muxerFrameRate(Number.NaN), undefined);
  });
});

describe('mp4-muxer 视频元数据', () => {
  it('编码器无输出时不进入 finalize', () => {
    assert.match(videoMuxerReadinessError(0, false) ?? '', /没有输出/);
  });

  it('缺少 decoderConfig 时不进入 finalize', () => {
    assert.match(videoMuxerReadinessError(1, false) ?? '', /decoderConfig/);
  });

  it('有编码块与 decoderConfig 时可安全封装', () => {
    assert.equal(videoMuxerReadinessError(1, true), null);
  });
});

describe('WebCodecs 视频时间戳', () => {
  it('使用 realtime 模式防止硬件编码器输出 B 帧重排', () => {
    const config = webCodecsVideoConfig('avc1.640028', 1920, 1080, 8_000_000, 60);
    assert.equal(config.latencyMode, 'realtime');
    assert.equal(config.framerate, 60);
  });

  it('识别 mp4-muxer 的 DTS 回退错误以触发安全回退', () => {
    assert.equal(isNonMonotonicDtsError(
      new Error('Timestamps must be monotonically increasing (DTS went from 33361.999 to 16681).'),
    ), true);
    assert.equal(isNonMonotonicDtsError(new Error('decoderConfig is null')), false);
  });
});

describe('WebCodecs 解码背压', () => {
  it('消费到低水位后唤醒等待中的样本投喂', async () => {
    const queue = new FrameQueue(2);
    queue.push(fakeFrame());
    queue.push(fakeFrame());

    let released = false;
    const producer = queue.readyForMore().then(() => {
      released = true;
    });
    // 让 readyForMore() 确实进入等待态。
    await Promise.resolve();
    assert.equal(released, false);

    const frame = await queue.next();
    frame?.close();
    await producer;
    assert.equal(released, true, '消费帧后生产方应立即恢复');
    queue.stop();
  });
});

describe('WebCodecs 音轨选择', () => {
  it('无声项目即使浏览器支持音频编码也不创建音轨', () => {
    assert.equal(preferredWebCodecsAudioCodec(false, {
      aacEncoder: true,
      opusEncoder: true,
    }), null);
  });

  it('有声音时优先 AAC，其次 Opus', () => {
    assert.equal(preferredWebCodecsAudioCodec(true, {
      aacEncoder: true,
      opusEncoder: true,
    }), 'aac');
    assert.equal(preferredWebCodecsAudioCodec(true, {
      aacEncoder: false,
      opusEncoder: true,
    }), 'opus');
  });
});

describe('ffmpeg.wasm 素材映射', () => {
  it('清洗后 basename 相同的三份素材仍保留三个唯一输入', () => {
    const media = (path: string, stream: number): MediaInfo => ({
      path,
      duration: 40,
      width: 2560,
      height: 1440,
      frameRate: 60,
      videoStreamIndex: 0,
      audioStreamIndex: stream,
      codec: 'h264',
      videoTimestampOffset: 0,
      isHdr: false,
    });
    const sources = [
      media('camera-a/素材?.mp4', 1),
      media('camera-b/素材*.mp4', 1),
      media('camera-c/素材:.mp4', 1),
    ];
    const clip = (source: MediaInfo, index: number): VideoClip => ({
      id: `clip-${index}`,
      media: source,
      start: index,
      end: index + 1,
      speed: 1,
    });
    const mapping = mapWasmMedia([
      clip(sources[0]!, 0),
      clip(sources[1]!, 1),
      clip(sources[2]!, 2),
      clip(sources[0]!, 3),
      clip(sources[2]!, 4),
      clip(sources[1]!, 5),
    ]);

    assert.equal(mapping.sources.length, 3);
    assert.equal(new Set(mapping.sources.map((source) => source.name)).size, 3);
    assert.equal(new Set(mapping.clips.map((item) => item.media.path)).size, 3);
    assert.deepEqual(
      mapping.clips.map((item) => item.media.path),
      [0, 1, 2, 0, 2, 1].map((index) => mapping.sources[index]!.name),
    );
  });

  it('分段清单按顺序拼接并保留音视频映射', () => {
    const segments = ['clip-segment-0000.mp4', 'clip-segment-0001.mp4', 'clip-segment-0002.mp4'];
    assert.equal(
      wasmConcatManifest(segments),
      "file 'clip-segment-0000.mp4'\nfile 'clip-segment-0001.mp4'\nfile 'clip-segment-0002.mp4'\n",
    );
    const args = buildWasmConcatArguments('clip-segments.txt', 'output.mp4', true);
    assert.deepEqual(args.slice(args.indexOf('-map'), args.indexOf('-c')), [
      '-map', '0:v:0', '-map', '0:a:0',
    ]);
    assert.ok(args.includes('copy'));
  });
});
