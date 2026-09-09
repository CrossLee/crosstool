import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const entry = fileURLToPath(new URL('./test-windows-workflow.mjs', import.meta.url));
const source = readFileSync(new URL('../../.github/workflows/windows-ci.yml', import.meta.url), 'utf8')
  .replace(/\r\n?/g, '\n');

for (const [name, eol] of [['LF', '\n'], ['CRLF', '\r\n']]) {
  for (const unsafeDefault of [false, true]) {
    test(`${name} file input ${unsafeDefault ? 'rejects default-enabled publication' : 'passes the real workflow contract entry point'}`, () => {
      const directory = mkdtempSync(join(tmpdir(), 'onepaw-workflow-input-'));
      try {
        const fixture = join(directory, 'windows-ci.yml');
        const changed = unsafeDefault ? source.replace('default: false', 'default: true') : source;
        if (unsafeDefault) assert.notEqual(changed, source, 'The negative fixture must change the publication default.');
        writeFileSync(fixture, changed.replace(/\n/g, eol), 'utf8');
        const bytes = readFileSync(fixture, 'utf8');
        assert.equal(bytes.includes('\r\n'), name === 'CRLF', 'Fixture must use its requested line endings.');
        const env = { ...process.env };
        // The child is the CLI under test, not another Node test-runner worker.
        delete env.NODE_TEST_CONTEXT;
        const result = spawnSync(process.execPath, [entry, '--workflow-file', fixture], {
          encoding: 'utf8', timeout: 15000, env,
        });
        assert.ifError(result.error);
        const output = `${result.stdout}\n${result.stderr}`;
        assert.equal(result.signal, null, output);
        assert.equal(result.status, unsafeDefault ? 1 : 0, output);
        assert.match(output, /# tests 11\b/);
        if (unsafeDefault) {
          assert.match(output, /not ok 2 - manual publication is a typed, optional, default-false input/);
          assert.match(output, /# fail 1\b/);
        } else {
          assert.match(output, /# pass 11\b/);
          assert.match(output, /# fail 0\b/);
        }
      } finally {
        // Only the unique fixture directory created by this test is removed.
        rmSync(directory, { recursive: true, force: true });
      }
    });
  }
}
