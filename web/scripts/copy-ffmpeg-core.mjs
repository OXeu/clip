/**
 * 把 ffmpeg.wasm 核心与 worker 复制到 public/ffmpeg，随站点一起静态托管。
 *
 * 刻意不使用运行时 CDN：静态站点应当自包含，避免第三方可用性与版本漂移，
 * 也便于内容安全策略收紧。核心体积较大（约 32 MB），会拆成静态托管可接受的分片。
 *
 * 用法：node scripts/copy-ffmpeg-core.mjs
 */

import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = join(webRoot, 'public', 'ffmpeg');
// Cloudflare Workers Static Assets 单文件上限为 25 MiB。留出足够余量，
// 由浏览器在加载核心时把这些分片重新组合成 application/wasm Blob。
const WASM_PART_BYTES = 16 * 1024 * 1024;

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

function copyWasmAsParts(from, target, file) {
  const bytes = readFileSync(from);
  const parts = [];
  for (let offset = 0, index = 0; offset < bytes.length; offset += WASM_PART_BYTES, index += 1) {
    const partFile = `${file}.part-${String(index).padStart(3, '0')}`;
    const part = bytes.subarray(offset, Math.min(offset + WASM_PART_BYTES, bytes.length));
    writeFileSync(join(target, partFile), part);
    parts.push({ file: partFile, size: part.length });
  }
  writeFileSync(
    join(target, `${file}.json`),
    `${JSON.stringify({ format: 'split-wasm', version: 1, size: bytes.length, parts }, null, 2)}\n`,
  );
  return parts;
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
    const size = statSync(from).size;
    total += size;
    if (file.endsWith('.wasm')) {
      const parts = copyWasmAsParts(from, target, file);
      console.log(`  ${bundle.folder}/${file}  ${human(size)} -> ${parts.length} 个分片`);
    } else {
      cpSync(from, join(target, file));
      console.log(`  ${bundle.folder}/${file}  ${human(size)}`);
    }
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
