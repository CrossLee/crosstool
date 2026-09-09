// Packaging-only export of the user-approved raster artwork. No drawing,
// cropping, background removal, or recoloring is performed. macOS sips does
// the required pixel-size/format conversions; ICO entries are only repacked.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const windowsRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const source = path.resolve(windowsRoot, '../Resources/Brand/OnePaw-AppIcon.png');
const output = path.join(windowsRoot, 'src/Crosio.Windows.App/Assets');
const expectedSourceSha256 = '632a1d63d4b47f0062dc8893853f2d832f1da27948c6d27cb44b37e26dd775dd';
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
if (process.platform !== 'darwin') throw new Error('This packaging export uses macOS sips. Windows builds use the checked-in exports.');
if (sha256(readFileSync(source)) !== expectedSourceSha256) throw new Error('The approved source changed; confirm new artwork before updating the source hash.');
mkdirSync(output, { recursive: true });
const temporary = mkdtempSync(path.join(tmpdir(), 'onepaw-icon-export-'));
const assets = [];
function record(name, pixels, format) {
  assets.push({ name, pixels, format, sha256: sha256(readFileSync(path.join(output, name))) });
}
function exportImage(name, size, format = 'png', destination = output) {
  const target = path.join(destination, name);
  execFileSync('/usr/bin/sips', ['-z', String(size), String(size), '-s', 'format', format, source, '--out', target], { stdio: 'ignore' });
  return target;
}
try {
  for (const [name, base] of [['Square44x44Logo', 44], ['Square150x150Logo', 150], ['StoreLogo', 50]]) {
    for (const scale of [100, 125, 150, 200, 400]) {
      const pixels = Math.ceil(base * scale / 100);
      const file = `${name}.scale-${scale}.png`;
      exportImage(file, pixels);
      record(file, pixels, 'png');
    }
  }
  const targetSizes = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];
  for (const size of targetSizes) {
    const name = `Square44x44Logo.targetsize-${size}.png`;
    exportImage(name, size);
    record(name, size, 'png');
    for (const theme of ['unplated', 'lightunplated']) {
      const variant = `Square44x44Logo.targetsize-${size}_altform-${theme}.png`;
      copyFileSync(path.join(output, name), path.join(output, variant));
      record(variant, size, 'png');
    }
  }
  const icoSizes = [16, 24, 32, 48, 256];
  const entries = icoSizes.map(size => {
    const bytes = readFileSync(exportImage(`${size}.ico`, size, 'ico', temporary));
    if (bytes.readUInt16LE(2) !== 1 || bytes.readUInt16LE(4) !== 1) throw new Error(`Unexpected sips ICO container for ${size}.`);
    const descriptor = Buffer.from(bytes.subarray(6, 22));
    const offset = descriptor.readUInt32LE(12);
    const length = descriptor.readUInt32LE(8);
    return { descriptor, payload: bytes.subarray(offset, offset + length) };
  });
  const header = Buffer.alloc(6 + entries.length * 16);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(entries.length, 4);
  let offset = header.length;
  for (const [index, entry] of entries.entries()) {
    entry.descriptor.writeUInt32LE(offset, 12);
    entry.descriptor.copy(header, 6 + index * 16);
    offset += entry.payload.length;
  }
  writeFileSync(path.join(output, 'OnePaw.ico'), Buffer.concat([header, ...entries.map(entry => entry.payload)]));
  record('OnePaw.ico', icoSizes, 'ico');
  writeFileSync(path.join(output, 'IconAssets.json'), JSON.stringify({
    artwork: 'OnePaw-AppIcon-v2-cat — user-approved reaching kitten, 2026-09-09',
    source: 'Resources/Brand/OnePaw-AppIcon.png',
    sourceSha256: expectedSourceSha256,
    conversion: 'macOS sips size/format export only; no artwork edits; ICO entries repacked without modifying bitmap payloads',
    assets,
  }, null, 2) + '\n');
  console.log(`Exported ${assets.length} icon assets from the unchanged approved image.`);
} finally {
  rmSync(temporary, { recursive: true, force: true });
}
