/**
 * 编辑模型测试，对应 tests/Clip.Tests/Program.cs 中的核心不变量。
 *
 * 重点是随机编辑的模糊测试：它验证片段不重叠、ID 唯一、locate() 与
 * 轨道偏移一致，是防止网页版与桌面端语义漂移最有效的一条。
 */

import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
  type MediaInfo,
  EditProject,
  TrackKind,
  clipDuration,
  createClip,
  trackDuration,
  validateClip,
} from '../src/model.ts';

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

const near = (actual: number, expected: number, tolerance = 0.001): void => {
  assert.ok(
    Math.abs(actual - expected) <= tolerance,
    `期望 ${expected}，实际 ${actual}`,
  );
};

const assertSingleTrailingEmptyTrack = (project: EditProject): void => {
  const empty = project.allTracks.filter((track) => track.clips.length === 0);
  assert.equal(empty.length, 1, '项目应始终恰好有一条空轨');
  assert.equal(project.allTracks.at(-1)?.id, empty[0]!.id, '空轨应始终位于末尾');
  assert.equal(project.allTracks.filter((track) => track.isMain).length, 1, '只能有一条主轨');
  assert.equal(project.mainTrack.isMain, true, '主轨身份应保持有效');
};

describe('分割与 ripple 删除', () => {
  it('删除中间片段后源时间正确前移', () => {
    const project = new EditProject();
    const track = project.import(media);
    const middle = project.split(track.id, 3);
    assert.ok(middle);
    project.split(track.id, 7);
    assert.ok(project.delete(middle));
    const clips = project.exportClips(track.id);
    near(trackDuration(project.findTrack(track.id)!), 6);
    assert.equal(clips.length, 2);
    // 第二段从源 7 秒开始，现在位于轨道 3 秒处。
    near(project.locate(track.id, 3)!.sourceTime, 7);
    near(project.locate(track.id, 4)!.sourceTime, 8);
  });

  it('边界分割不产生空片段', () => {
    const project = new EditProject();
    const track = project.import(media);
    assert.equal(project.split(track.id, 0), undefined);
    assert.equal(project.split(track.id, 10), undefined);
    assert.equal(project.split(track.id, 0.001), undefined, '小于一帧的分割应被拒绝');
    assert.ok(project.split(track.id, 5));
    assert.equal(project.split(track.id, 5), undefined, '重复分割不应产生片段');
    assert.equal(project.findTrack(track.id)!.clips.length, 2);
  });

  it('播放跨过相邻切口时不重新 seek', () => {
    const project = new EditProject();
    const track = project.import(media);
    const right = project.split(track.id, 5)!;
    const left = project.findTrack(track.id)!.clips[0]!;
    // 第一段播放到终点，下一段紧邻，不需要 seek。
    const playback = project.advancePlayback(track.id, left.id, 5)!;
    assert.equal(playback.position.clip.id, right);
    assert.equal(playback.requiresSeek, false);
    assert.equal(playback.reachedEnd, false);
  });

  it('播放跨过被删除的区间时要求 seek', () => {
    const project = new EditProject();
    const track = project.import(media);
    const middle = project.split(track.id, 3)!;
    project.split(track.id, 7);
    project.delete(middle);
    const left = project.findTrack(track.id)!.clips[0]!;
    const playback = project.advancePlayback(track.id, left.id, 3)!;
    assert.equal(playback.requiresSeek, true, '跨过缺口必须 seek');
    near(playback.position.sourceTime, 7);
  });

  it('播放到轨道末尾时报告结束', () => {
    const project = new EditProject();
    const track = project.import(media);
    const playback = project.advancePlayback(track.id, track.clips[0]!.id, 10)!;
    assert.equal(playback.reachedEnd, true);
  });
});

describe('音视频分轨与对齐绑定', () => {
  it('导入时生成视频轨和紧邻其下的伴生音频槽', () => {
    const project = new EditProject();
    const imported = project.importSeparated(media);
    assert.equal(imported.videoTrack.kind, 'video');
    assert.equal(imported.videoTrack.clips[0]!.kind, 'video');
    assert.equal(imported.audioTrack.kind, 'audio');
    assert.equal(imported.audioTrack.clips[0]!.kind, 'audio');
    assert.equal(imported.videoTrack.companionGroupId, imported.audioTrack.companionGroupId);
    assert.deepEqual(project.allTracks.slice(0, 2).map((track) => track.id),
      [imported.videoTrack.id, imported.audioTrack.id]);
    assert.notEqual(imported.videoTrack.clips[0]!.id, imported.audioTrack.clips[0]!.id);
    assert.equal(project.exportableTracks.length, 1, '音频轨不应出现在视频导出目标中');
  });

  it('无声视频也保留一条伴生音频槽', () => {
    const project = new EditProject();
    const imported = project.importSeparated({ ...media, audioStreamIndex: null });
    assert.equal(imported.audioTrack.kind, TrackKind.Audio);
    assert.equal(imported.audioTrack.companionGroupId, imported.videoTrack.companionGroupId);
  });

  it('空伴生音轨不阻止视频分割，也不会被凭空填充', () => {
    const project = new EditProject();
    const imported = project.importSeparated(media);
    assert.ok(project.delete(imported.audioTrack.clips[0]!.id));
    assert.ok(project.split(imported.videoTrack.id, 4));
    assert.equal(project.findTrack(imported.videoTrack.id)!.clips.length, 2);
    assert.equal(project.findTrack(imported.audioTrack.id)!.clips.length, 0);
  });

  it('非空伴生轨不覆盖切点时仍拒绝原子分割', () => {
    const project = new EditProject();
    const imported = project.importSeparated(media);
    const audioRight = project.split(imported.audioTrack.id, 3);
    assert.ok(audioRight);
    assert.ok(project.delete(audioRight));
    assert.equal(project.split(imported.videoTrack.id, 5), undefined);
  });

  it('删空视频轨后导入新素材不会占用原伴生槽', () => {
    const project = new EditProject();
    const first = project.importSeparated(media);
    const firstGroup = first.videoTrack.companionGroupId;
    assert.ok(project.delete(first.videoTrack.clips[0]!.id));
    const second = project.importSeparated({ ...media, path: 'angle-b.mp4' });
    assert.equal(project.findTrack(first.videoTrack.id)?.companionGroupId, firstGroup);
    assert.equal(project.companionTracks(first.videoTrack.id).length, 2);
    assert.notEqual(second.videoTrack.id, first.videoTrack.id);
    assert.notEqual(second.videoTrack.companionGroupId, firstGroup);
  });

  it('拖动任一伴生轨都会整组排序，音频槽不会脱离视频轨', () => {
    const project = new EditProject();
    const first = project.importSeparated(media);
    const second = project.importSeparated({ ...media, path: 'angle-b.mp4' });
    assert.equal(project.move(first.audioTrack.clips[0]!.id, first.videoTrack.id, 0), false,
      '音频片段不应混入视频轨');
    assert.ok(project.moveTrack(second.audioTrack.id, 0));
    assert.deepEqual(project.allTracks.slice(0, 4).map((track) => track.id),
      [second.videoTrack.id, second.audioTrack.id, first.videoTrack.id, first.audioTrack.id]);
    assert.equal(project.mainTrack.id, first.videoTrack.id, '轨道排序不应改变主轨身份');
    assert.ok(project.undo());
    assert.deepEqual(project.allTracks.slice(0, 4).map((track) => track.id),
      [first.videoTrack.id, first.audioTrack.id, second.videoTrack.id, second.audioTrack.id]);
  });

  it('绑定轨道同步分割，但删除只影响当前片段', () => {
    const project = new EditProject();
    const first = project.importSeparated(media).videoTrack;
    const second = project.importSeparated({ ...media, path: 'angle-b.mp4' }).videoTrack;
    assert.ok(project.bindTracks([first.id, second.id]));
    const right = project.split(first.id, 4);
    assert.ok(right);
    assert.equal(project.findTrack(first.id)!.clips.length, 2);
    assert.equal(project.findTrack(second.id)!.clips.length, 2);
    assert.equal(project.companionTracks(first.id)[1]!.clips.length, 2, '第一视角的伴生音轨应同步分割');
    assert.equal(project.companionTracks(second.id)[1]!.clips.length, 2, '第二视角的伴生音轨应同步分割');
    assert.ok(project.delete(right));
    assert.equal(project.findTrack(first.id)!.clips.length, 1);
    assert.equal(project.findTrack(second.id)!.clips.length, 2, '删除不应传播到绑定轨道');
  });
});

describe('跨轨移动', () => {
  it('把片段移到另一条轨道并从原轨移除', () => {
    const project = new EditProject();
    const first = project.import(media);
    project.import({ ...media, path: 'other.mp4' });
    const clip = first.clips[0]!;
    const target = project.allTracks.at(-1)!;
    assert.equal(target.clips.length, 0);
    assert.ok(project.move(clip.id, target.id, 0));
    assert.equal(project.findTrack(first.id)!.clips.length, 0);
    assert.equal(project.findTrack(target.id)!.clips.length, 1);
    assert.equal(project.findTrack(target.id)!.clips[0]!.id, clip.id);
    assertSingleTrailingEmptyTrack(project);
  });

  it('同轨内移动到自身相邻位置是空操作', () => {
    const project = new EditProject();
    const track = project.import(media);
    const clip = track.clips[0]!;
    assert.equal(project.move(clip.id, track.id, 0), false);
    assert.equal(project.move(clip.id, track.id, 1), false);
  });
});

describe('末尾空轨不变量', () => {
  it('导入会填充现有空轨并只补一条新空轨', () => {
    const project = new EditProject();
    assertSingleTrailingEmptyTrack(project);
    const first = project.import(media);
    assert.equal(first.id, project.mainTrack.id, '首个素材应填充初始主轨');
    assert.equal(project.allTracks.length, 2);
    assertSingleTrailingEmptyTrack(project);
    project.import({ ...media, path: 'other.mp4' });
    assert.equal(project.allTracks.length, 3, '两条内容轨后只应有一条空轨');
    assertSingleTrailingEmptyTrack(project);
  });

  it('删除、跨轨移动、撤销和重做后仍只保留一条空轨', () => {
    const project = new EditProject();
    const first = project.import(media);
    project.import({ ...media, path: 'other.mp4' });
    assert.ok(project.delete(first.clips[0]!.id));
    assertSingleTrailingEmptyTrack(project);
    assert.ok(project.undo());
    assertSingleTrailingEmptyTrack(project);
    assert.ok(project.redo());
    assertSingleTrailingEmptyTrack(project);

    const remaining = project.exportableTracks[0]!.clips[0]!;
    const target = project.allTracks.at(-1)!;
    assert.ok(project.move(remaining.id, target.id, 0));
    assertSingleTrailingEmptyTrack(project);
  });

  it('恢复旧存档时会合并多余空轨', () => {
    const project = new EditProject();
    project.import(media);
    const snapshot = project.exportSnapshot();
    const restored = EditProject.fromSnapshot({
      ...snapshot,
      tracks: [
        ...snapshot.tracks,
        { id: crypto.randomUUID(), name: '多余空轨', isMain: false, clips: [] },
      ],
    });
    assertSingleTrailingEmptyTrack(restored);
  });
});

describe('复制片段', () => {
  it('短于 10 秒复制到原片段之后', () => {
    const project = new EditProject();
    const track = project.import(media);
    project.split(track.id, 4);
    const first = project.findTrack(track.id)!.clips[0]!;
    near(clipDuration(first), 4);
    const copy = project.duplicate(first.id)!;
    const clips = project.findTrack(track.id)!.clips;
    assert.equal(clips.length, 3);
    assert.equal(clips[1]!.id, copy);
    assert.equal(project.allTracks.length, 2, '不应新建轨道');
  });

  it('10 秒及以上复制到下方新轨道', () => {
    const project = new EditProject();
    const track = project.import(media);
    const clip = track.clips[0]!;
    near(clipDuration(clip), 10);
    project.duplicate(clip.id);
    assert.equal(project.allTracks.length, 3);
    assert.equal(project.findTrack(track.id)!.clips.length, 1, '原轨不应增加片段');
  });
});

describe('变速', () => {
  it('拒绝非法速度且不改变项目', () => {
    const project = new EditProject();
    const track = project.import(media);
    const id = track.clips[0]!.id;
    for (const speed of [0, -1, Number.NaN, Number.POSITIVE_INFINITY, 0.01, 8.1]) {
      assert.throws(() => project.setSpeed(id, speed), `应拒绝速度 ${speed}`);
      near(trackDuration(project.findTrack(track.id)!), 10);
    }
  });

  it('速度改变轨道时长与定位', () => {
    const project = new EditProject();
    const track = project.import(media);
    const id = track.clips[0]!.id;
    assert.ok(project.setSpeed(id, 2));
    near(trackDuration(project.findTrack(track.id)!), 5);
    near(project.locate(track.id, 2.5)!.sourceTime, 5);
  });
});

describe('撤销与重做', () => {
  it('撤销回到上一步，重做再次应用', () => {
    const project = new EditProject();
    const track = project.import(media);
    const original = track.clips[0]!;
    project.split(track.id, 5);
    assert.equal(project.findTrack(track.id)!.clips.length, 2);
    assert.ok(project.undo());
    assert.equal(project.findTrack(track.id)!.clips.length, 1);
    assert.equal(project.findTrack(track.id)!.clips[0]!.id, original.id);
    assert.ok(project.redo());
    assert.equal(project.findTrack(track.id)!.clips.length, 2);
  });

  it('新操作清空重做栈', () => {
    const project = new EditProject();
    const track = project.import(media);
    project.split(track.id, 5);
    project.undo();
    assert.equal(project.canRedo, true);
    project.split(track.id, 2);
    assert.equal(project.canRedo, false);
  });

  it('撤销空项目返回 false', () => {
    assert.equal(new EditProject().undo(), false);
  });

  it('导入可被撤销', () => {
    const project = new EditProject();
    project.import(media);
    assert.equal(project.sources.length, 1);
    assert.ok(project.undo());
    assert.equal(project.sources.length, 0);
    assert.equal(project.allTracks.length, 1);
  });
});

describe('项目快照恢复', () => {
  it('保留轨道、剪辑、命名和变速，但从干净的撤销栈继续', () => {
    const original = new EditProject();
    const track = original.import(media);
    const right = original.split(track.id, 4)!;
    original.rename(right, '结尾');
    original.setSpeed(right, 2);

    const restored = EditProject.fromSnapshot(JSON.parse(JSON.stringify(original.exportSnapshot())));
    assert.deepEqual(restored.exportSnapshot(), original.exportSnapshot());
    assert.equal(restored.findClip(right)?.clip.name, '结尾');
    assert.equal(restored.findClip(right)?.clip.speed, 2);
    assert.equal(restored.canUndo, false);
    assert.equal(restored.canRedo, false);
  });

  it('拒绝损坏、越界或重复 ID 的存档', () => {
    const original = new EditProject();
    const track = original.import(media);
    const snapshot = original.exportSnapshot();
    assert.throws(() => EditProject.fromSnapshot({ tracks: [], sources: [] }));
    assert.throws(() => EditProject.fromSnapshot({
      ...snapshot,
      tracks: snapshot.tracks.map((lane) => ({
        ...lane,
        clips: lane.clips.map((clip) => ({ ...clip, end: media.duration + 1 })),
      })),
    }));
    assert.throws(() => EditProject.fromSnapshot({
      ...snapshot,
      tracks: [snapshot.tracks[0], { ...track, id: snapshot.tracks[0]!.id }],
    }));
  });
});

describe('导出轨道解析', () => {
  it('只有一条非空轨道时默认导出它', () => {
    const project = new EditProject();
    const track = project.import(media);
    assert.equal(project.resolveExportTrack(null)?.id, track.id);
  });

  it('多条非空轨道时必须显式选择', () => {
    const project = new EditProject();
    project.import(media);
    const second = project.import({ ...media, path: 'other.mp4' });
    assert.equal(project.resolveExportTrack(null), undefined);
    assert.equal(project.resolveExportTrack(second.id)?.id, second.id);
  });

  it('选中的空轨道不是导出目标', () => {
    const project = new EditProject();
    project.import(media);
    assert.equal(project.resolveExportTrack(project.allTracks.at(-1)!.id), undefined);
  });

  it('导出快照不随后续编辑变化', () => {
    const project = new EditProject();
    const track = project.import(media);
    const snapshot = project.exportClips(track.id);
    project.split(track.id, 5);
    assert.equal(snapshot.length, 1, '快照被后续编辑改动');
    assert.equal(project.exportClips(track.id).length, 2);
  });
});

describe('校验', () => {
  it('拒绝已删除轨道或越界范围', () => {
    // createClip 只构造，不校验；与桌面端一致，校验发生在 import/validateClip。
    assert.throws(() => validateClip(createClip({ ...media, duration: 0 })));
    assert.throws(() => validateClip({ ...createClip(media), end: 20 }));
    assert.throws(() => validateClip({ ...createClip(media), speed: 0 }));
    assert.throws(() => validateClip({ ...createClip(media), start: -1 }));
    assert.throws(() => validateClip({ ...createClip(media), end: 0 }));
  });

  it('拒绝 HDR 素材', () => {
    const project = new EditProject();
    assert.throws(() => project.import({ ...media, isHdr: true }));
  });
});

describe('随机编辑保持不变量', () => {
  it('500 次随机操作后片段不重叠且 ID 唯一', () => {
    // 自带确定性 PRNG，保证失败可复现（桌面端用 Random(28)）。
    let seed = 28;
    const random = (): number => {
      seed = (seed * 1103515245 + 12345) & 0x7fffffff;
      return seed / 0x7fffffff;
    };
    const pick = <T>(items: readonly T[]): T => items[Math.floor(random() * items.length)]!;

    const project = new EditProject();
    project.import(media);
    project.import({ ...media, path: 'other.mp4' });

    for (let iteration = 0; iteration < 500; iteration++) {
      const all = project.allTracks.flatMap((track) => track.clips);
      const track = pick(project.allTracks);
      const clip = all.length > 0 ? pick(all) : undefined;
      switch (Math.floor(random() * 8)) {
        case 0:
          project.split(track.id, random() * trackDuration(track));
          break;
        case 1:
          if (clip) project.move(clip.id, track.id, Math.floor(random() * (track.clips.length + 1)));
          break;
        case 2:
          if (clip) project.setSpeed(clip.id, pick([0.25, 0.5, 1, 1.25, 2, 4]));
          break;
        case 3:
          if (clip) project.delete(clip.id);
          break;
        case 4:
          project.undo();
          break;
        case 5:
          project.redo();
          break;
        case 6:
          if (clip) project.duplicate(clip.id);
          break;
        case 7:
          if (clip) project.rename(clip.id, `镜头 ${iteration}`);
          break;
      }

      const after = project.allTracks.flatMap((t) => t.clips);
      assert.equal(
        new Set(after.map((c) => c.id)).size,
        after.length,
        `第 ${iteration} 次操作后出现重复片段 ID`,
      );
      for (const lane of project.allTracks) {
        let offset = 0;
        for (const segment of lane.clips) {
          validateClip(segment);
          const middle = offset + clipDuration(segment) / 2;
          near(project.locate(lane.id, middle)!.sourceTime, (segment.start + segment.end) / 2, 0.002);
          offset += clipDuration(segment);
        }
        near(trackDuration(lane), offset);
      }
      assertSingleTrailingEmptyTrack(project);
    }
  });
});
