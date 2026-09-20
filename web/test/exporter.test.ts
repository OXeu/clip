import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { FrameQueue, muxerFrameRate } from '../src/exporter.ts';

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
