const test = require('node:test');
const assert = require('node:assert/strict');
const { mkdtemp, writeFile, rm } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { createHash } = require('node:crypto');
const { parseTag, readBundle, publishRelease } = require('./publish-release.cjs');

const sha = 'a'.repeat(40);
const hash = bytes => `sha256:${createHash('sha256').update(bytes).digest('hex')}`;

async function fixture(t, options = {}) {
  const directory = await mkdtemp(join(tmpdir(), 'clip-release-test-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const bytes = Buffer.from('verified package fixture');
  const packageName = 'Clip-win-x64.zip';
  await writeFile(join(directory, packageName), bytes);
  await writeFile(join(directory, `${packageName}.sha256`), `${hash(bytes).slice(7)}  ${packageName}\n`);
  await writeFile(join(directory, `${packageName}.manifest.json`), JSON.stringify({
    archive: packageName, length: bytes.length, sha256: hash(bytes).slice(7), files: []
  }));
  const context = { repo: { owner: 'fixture', repo: 'clip' }, sha, eventName: 'push', ref: 'refs/tags/v1.2.3', runId: 123 };
  const state = { release: options.release, assets: options.assets ?? [], mutations: [], generated: [], comparisons: [], tagChecks: 0 };
  const commit = { sha, parents: [{ sha: 'b'.repeat(40) }], commit: { message: 'fix: preserve export selection\n\nDetails' } };
  const api = {
    getCommit: async () => ({ data: { sha: (options.wrongTag || (options.moveTag && state.tagChecks++ > 0)) ? 'b'.repeat(40) : sha } }),
    listReleases: async () => ({ data: [...(options.previous ?? []), ...(state.release ? [state.release] : [])] }),
    generateReleaseNotes: async args => { state.generated.push(args); return { data: { body: '## What changed\n\nGenerated GitHub notes.' } }; },
    compareCommitsWithBasehead: async args => { state.comparisons.push(args); return { data: { commits: [commit] } }; },
    listCommits: async () => ({ data: [commit] }),
    createRelease: async args => {
      state.mutations.push(['create', args]);
      state.release = { ...args, id: 42, html_url: 'https://github.com/fixture/clip/releases/tag/' + args.tag_name };
      return { data: state.release };
    },
    getRelease: async () => ({ data: { ...state.release, ...(options.externallyPublished ? { draft: false } : {}) } }),
    listReleaseAssets: async () => ({ data: [...state.assets] }),
    deleteReleaseAsset: async args => {
      state.mutations.push(['delete', args]);
      state.assets = state.assets.filter(asset => asset.id !== args.asset_id);
    },
    uploadReleaseAsset: async args => {
      state.mutations.push(['upload', args.name]);
      if (options.uploadFailure === args.name) throw new Error('Simulated upload failure');
      const asset = { id: state.assets.length + 100, name: args.name, size: args.data.length,
        digest: options.corruptUpload ? 'sha256:incorrect' : hash(args.data), state: 'uploaded' };
      state.assets.push(asset);
      return { data: asset };
    },
    updateRelease: async args => {
      state.mutations.push(['publish', args]);
      Object.assign(state.release, args);
      return { data: state.release };
    }
  };
  const github = { rest: { repos: api }, paginate: async (method, args) => (await method(args)).data };
  return { state, context, directory, run: () => publishRelease({ github, context, directory, core: { info() {} } }) };
}

test('tag validation accepts stable and prerelease versions and rejects invalid or unsafe refs', () => {
  assert.equal(parseTag('v1.2.3').prerelease, false);
  assert.equal(parseTag('v1.2.3-rc.1').version, '1.2.3-rc.1');
  assert.equal(parseTag('v1.2.3-beta.0').prerelease, true);
  for (const tag of ['main', '1.2.3', 'v01.2.3', 'v1.2', 'v1.2.3-', 'v1.2.3-rc..1', 'v1.2.3-01', 'v1.2.3;echo unsafe']) {
    assert.throws(() => parseTag(tag), /Release tag/);
  }
});

test('stable release is drafted, uploaded, verified, then published with notes for PRs and direct commits', async t => {
  const f = await fixture(t, { previous: [{ tag_name: 'v1.2.2', draft: false, prerelease: false }] });
  await f.run();
  assert.deepEqual(f.state.mutations.map(([action]) => action), ['create', 'upload', 'upload', 'upload', 'publish']);
  assert.equal(f.state.mutations[0][1].draft, true);
  assert.equal(f.state.generated[0].previous_tag_name, 'v1.2.2');
  assert.equal(f.state.comparisons[0].basehead, 'v1.2.2...v1.2.3');
  assert.match(f.state.release.body, /Generated GitHub notes/);
  assert.match(f.state.release.body, /fix: preserve export selection/);
  assert.match(f.state.release.body, /SHA-256/);
  assert.equal(f.state.release.draft, false);
  assert.equal(f.state.release.prerelease, false);
  assert.equal(f.state.release.make_latest, 'legacy');
});

test('prereleases are never marked latest and first releases include commit history', async t => {
  const f = await fixture(t);
  f.context.ref = 'refs/tags/v1.2.3-rc.1';
  await f.run();
  assert.equal(f.state.release.prerelease, true);
  assert.equal(f.state.release.make_latest, 'false');
  assert.equal(f.state.generated[0].previous_tag_name, undefined);
  assert.match(f.state.release.body, /preserve export selection/);
});

test('branch, pull-request and nonversion events cannot publish', async t => {
  for (const override of [{ ref: 'refs/heads/master' }, { eventName: 'pull_request' }, { ref: 'refs/tags/notes' }]) {
    const f = await fixture(t);
    Object.assign(f.context, override);
    await assert.rejects(f.run);
    assert.deepEqual(f.state.mutations, []);
  }
});

test('tampered or incomplete bundles fail before creating a release', async t => {
  const f = await fixture(t);
  await writeFile(join(f.directory, 'Clip-win-x64.zip'), 'changed bytes');
  await assert.rejects(f.run, /do not agree/);
  assert.deepEqual(f.state.mutations, []);
  await rm(join(f.directory, 'Clip-win-x64.zip.sha256'));
  await assert.rejects(f.run, /ENOENT/);
  assert.deepEqual(f.state.mutations, []);
});

test('an already published release is left untouched on reruns', async t => {
  const original = { id: 42, tag_name: 'v1.2.3', draft: false, body: 'Maintainer notes', html_url: 'existing' };
  const f = await fixture(t, { release: { ...original } });
  assert.equal(await f.run(), 'existing');
  assert.deepEqual(f.state.mutations, []);
  assert.deepEqual(f.state.release, original);
});

test('draft retries retain notes and good assets, replacing only incomplete expected assets', async t => {
  const f = await fixture(t, { release: { id: 42, tag_name: 'v1.2.3', draft: true, body: 'Reviewed notes', target_commitish: sha } });
  const files = await readBundle(f.directory);
  f.state.assets.push({ id: 1, name: files[0].name, size: files[0].size, digest: files[0].digest, state: 'uploaded' },
    { id: 2, name: files[1].name, state: 'starter' }, { id: 3, name: 'extra.txt', state: 'uploaded' });
  await f.run();
  assert.deepEqual(f.state.mutations.map(([action]) => action), ['delete', 'upload', 'upload', 'publish']);
  assert.equal(f.state.mutations[0][1].asset_id, 2);
  assert.equal(f.state.release.body, 'Reviewed notes');
  assert.ok(f.state.assets.some(asset => asset.id === 3));
});

test('failed uploads keep the release private and can be resumed', async t => {
  const options = { uploadFailure: 'Clip-win-x64.zip.sha256' };
  const f = await fixture(t, options);
  await assert.rejects(f.run, /Simulated upload failure/);
  assert.equal(f.state.release.draft, true);
  assert.ok(!f.state.mutations.some(([action]) => action === 'publish'));
  options.uploadFailure = undefined;
  await f.run();
  assert.equal(f.state.release.draft, false);
  assert.equal(f.state.mutations.filter(([action, name]) => action === 'upload' && name === 'Clip-win-x64.zip').length, 1);
});

test('server-side digest mismatches prevent publication', async t => {
  const f = await fixture(t, { corruptUpload: true });
  await assert.rejects(f.run, /asset failed verification/);
  assert.equal(f.state.release.draft, true);
});

test('tag changes and drafts for other commits cannot publish the wrong binary', async t => {
  for (const options of [{ wrongTag: true }, { moveTag: true },
    { release: { id: 42, tag_name: 'v1.2.3', draft: true, target_commitish: 'b'.repeat(40) } }]) {
    const f = await fixture(t, options);
    await assert.rejects(f.run, /tested commit|different commit/);
    assert.ok(!f.state.mutations.some(([action]) => action === 'publish'));
  }
});

test('a draft published externally is not modified by an in-flight retry', async t => {
  const f = await fixture(t, { externallyPublished: true });
  await assert.rejects(f.run, /published while/);
  assert.deepEqual(f.state.mutations.map(([action]) => action), ['create']);
});
