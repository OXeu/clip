/**
 * 把 ffmpeg.wasm 核心与 worker 复制到 public/ffmpeg，随站点一起静态托管。
 *
 * 刻意不使用运行时 CDN：静态站点应当自包含，避免第三方可用性与版本漂移，
 * 也便于内容安全策略收紧。核心体积较大（约 32 MB），由托管方按需开启压缩。
 *
 * 用法：node scripts/copy-ffmpeg-core.mjs
 */

import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = join(webRoot, 'public', 'ffmpeg');

/** 核心包 -> 目标子目录。单线程核心始终需要；多线程核心在跨源隔离下更佳。 */
const bundles = [
  { package: '@ffmpeg/core', folder: 'core', files: ['ffmpeg-core.js', 'ffmpeg-core.wasm'] },
  {
    package: '@ffmpeg/core-mt',
    folder: 'core-mt',
    files: ['ffmpeg-core.js', 'ffmpeg-core.wasm', 'ffmpeg-core.worker.js'],
  },
];

function human(bytes) {
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

rmSync(output, { recursive: true, force: true });
mkdirSync(output, { recursive: true });

let total = 0;
for (const bundle of bundles) {
  const source = join(webRoot, 'node_modules', ...bundle.package.split('/'), 'dist', 'esm');
  if (!existsSync(source)) {
    throw new Error(`缺少 ${bundle.package}，请先运行 npm install`);
  }
  const target = join(output, bundle.folder);
  mkdirSync(target, { recursive: true });
  for (const file of bundle.files) {
    const from = join(source, file);
    if (!existsSync(from)) throw new Error(`${bundle.package} 中找不到 ${file}`);
    cpSync(from, join(target, file));
    const size = statSync(from).size;
    total += size;
    console.log(`  ${bundle.folder}/${file}  ${human(size)}`);
  }
}

// 记录版本，便于确认线上核心与 package.json 一致。
const version = JSON.parse(
  readFileSync(join(webRoot, 'node_modules', '@ffmpeg', 'core', 'package.json'), 'utf8'),
).version;
writeFileSync(
  join(output, 'version.json'),
  `${JSON.stringify({ package: '@ffmpeg/core', version }, null, 2)}\n`,
);

console.log(`ffmpeg 核心已就绪：${output}（@ffmpeg/core ${version}，合计 ${human(total)}）`);
