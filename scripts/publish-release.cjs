const { readFile } = require('node:fs/promises');
const { resolve } = require('node:path');
const { createHash } = require('node:crypto');

const packageName = 'Clip-win-x64.zip';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

function parseTag(tag) {
  const match = /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?$/.exec(tag ?? '');
  if (!match || (match[4] && match[4].split('.').some(part =>
    !part || (/^\d+$/.test(part) && part.length > 1 && part.startsWith('0'))))) {
    throw new Error('Release tag must be vMAJOR.MINOR.PATCH, optionally followed by -rc.1 or another prerelease suffix.');
  }
  return { tag, version: tag.slice(1), numbers: match.slice(1, 4).map(BigInt), prerelease: Boolean(match[4]) };
}

function compareVersions(left, right) {
  for (let i = 0; i < 3; i++) {
    if (left.numbers[i] !== right.numbers[i]) return left.numbers[i] > right.numbers[i] ? 1 : -1;
  }
  return 0;
}

async function readBundle(directory) {
  if (!directory) throw new Error('RELEASE_DIRECTORY is required.');
  const files = await Promise.all([packageName, `${packageName}.sha256`, `${packageName}.manifest.json`].map(async name => {
    const data = await readFile(resolve(directory, name));
    return { name, data, size: data.length, digest: `sha256:${hash(data)}` };
  }));
  const manifest = JSON.parse(files[2].data.toString('utf8'));
  const checksum = files[1].data.toString('utf8').trim().split(/\s+/);
  if (manifest.archive !== packageName || manifest.length !== files[0].size ||
      `sha256:${manifest.sha256}` !== files[0].digest || checksum.length !== 2 ||
      checksum[0] !== manifest.sha256 || checksum[1] !== packageName) {
    throw new Error('Windows ZIP, checksum, and manifest do not agree.');
  }
  return files;
}

function verifyAssets(files, assets) {
  for (const file of files) {
    const matching = assets.filter(asset => asset.name === file.name);
    if (matching.length !== 1 || matching[0].state !== 'uploaded' ||
        matching[0].size !== file.size || matching[0].digest !== file.digest) {
      throw new Error(`Release asset failed verification: ${file.name}`);
    }
  }
}

async function publishRelease({ github, context, core, directory = process.env.RELEASE_DIRECTORY }) {
  if (context.eventName !== 'push' || !context.ref.startsWith('refs/tags/')) {
    throw new Error('Release publication requires a tag push.');
  }
  const version = parseTag(context.ref.slice('refs/tags/'.length));
  const files = await readBundle(directory);
  const repo = context.repo;
  const api = github.rest.repos;
  async function verifyTag() {
    const { data: commit } = await api.getCommit({ ...repo, ref: version.tag });
    if (commit.sha !== context.sha) throw new Error('Release tag no longer points to the tested commit.');
  }
  await verifyTag();
  const releases = await github.paginate(api.listReleases, { ...repo, per_page: 100 });
  let release = releases.find(item => item.tag_name === version.tag);
  if (release && !release.draft) {
    core.info(`${version.tag} is already published; leaving its notes and assets unchanged.`);
    return release.html_url;
  }
  if (release && /^[0-9a-f]{40}$/i.test(release.target_commitish) && release.target_commitish !== context.sha) {
    throw new Error('Existing release draft belongs to a different commit.');
  }
  if (!release) {
    const previous = releases.filter(item => !item.draft && !item.prerelease).flatMap(item => {
      try {
        const parsed = parseTag(item.tag_name);
        return compareVersions(parsed, version) < 0 ? [{ ...item, parsed }] : [];
      } catch { return []; }
    }).sort((a, b) => compareVersions(b.parsed, a.parsed))[0];
    const { data: notes } = await api.generateReleaseNotes({
      ...repo, tag_name: version.tag, target_commitish: context.sha,
      ...(previous ? { previous_tag_name: previous.tag_name } : {})
    });
    // Direct commits are common in this repository; include them even when there are no merged PRs.
    const commits = previous
      ? (await api.compareCommitsWithBasehead({ ...repo, basehead: `${previous.tag_name}...${version.tag}`, per_page: 100 })).data.commits
      : (await api.listCommits({ ...repo, sha: context.sha, per_page: 100 })).data;
    const base = `https://github.com/${repo.owner}/${repo.repo}`;
    const log = commits.filter(commit => commit.parents.length < 2).map(commit => {
      const title = commit.commit.message.split('\n')[0].replace(/[\\`*_{}\[\]<>]/g, '\\$&');
      return `- ${title} ([${commit.sha.slice(0, 7)}](${base}/commit/${commit.sha}))`;
    }).join('\n');
    const body = [
      `下载 **${packageName}**，完整解压后运行 \`Clip.exe\`。支持 Windows 10 / 11 x64，内置 .NET 和 FFmpeg。`,
      '同时提供 SHA-256 校验文件和包内文件清单；Source code 归档不是可直接运行的应用。',
      notes.body,
      log ? `<details>\n<summary>提交记录（最多 100 条）</summary>\n\n${log}\n\n</details>` : '',
      `安装包 SHA-256：\`${files[0].digest.slice(7)}\``,
      `[Windows 构建与验证](${base}/actions/runs/${context.runId}) · [使用指南](${base}/blob/${version.tag}/docs/usage.md) · [许可说明](${base}/blob/${version.tag}/THIRD-PARTY-NOTICES.md)`
    ].filter(Boolean).join('\n\n');
    ({ data: release } = await api.createRelease({
      ...repo, tag_name: version.tag, target_commitish: context.sha, name: `Clip ${version.tag}`,
      body, draft: true, prerelease: version.prerelease
    }));
  }

  const releaseArgs = { ...repo, release_id: release.id };
  const assets = await github.paginate(api.listReleaseAssets, { ...releaseArgs, per_page: 100 });
  for (const file of files) {
    const existing = assets.find(asset => asset.name === file.name);
    if (existing?.state === 'uploaded' && existing.size === file.size && existing.digest === file.digest) continue;
    // A retry may replace partial uploads in a draft, but never overwrite a published release.
    const { data: current } = await api.getRelease(releaseArgs);
    if (!current.draft) throw new Error('Release was published while assets were being prepared.');
    if (existing) await api.deleteReleaseAsset({ ...repo, asset_id: existing.id });
    await api.uploadReleaseAsset({ ...releaseArgs, name: file.name, data: file.data,
      headers: { 'content-type': file.name.endsWith('.zip') ? 'application/zip' : 'application/octet-stream',
        'content-length': file.size } });
  }
  verifyAssets(files, await github.paginate(api.listReleaseAssets, { ...releaseArgs, per_page: 100 }));
  await verifyTag();
  if (!(await api.getRelease(releaseArgs)).data.draft) throw new Error('Release was published by another process.');
  const { data: published } = await api.updateRelease({
    ...releaseArgs, draft: false, prerelease: version.prerelease,
    make_latest: version.prerelease ? 'false' : 'legacy'
  });
  core.info(`Published ${published.html_url}`);
  return published.html_url;
}

module.exports = { parseTag, readBundle, verifyAssets, publishRelease };
