import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let SourceArtifactExplorer;
let buildSourceTree;
let sourceLanguage;
const files = [
  { path: 'src/Models/Resident.cs', purpose: 'Resident domain model',
    content: 'public class Resident { }\n', sha256: 'a'.repeat(64),
    lastWorkItemId: 'T1', updatedUtc: '2026-11-21T14:30:00Z' },
  { path: 'src/Views/Overview.tsx', purpose: 'Town overview',
    content: 'export const title = "<script>danger</script>";\n',
    sha256: 'b'.repeat(64), lastWorkItemId: 'T2',
    updatedUtc: '2026-11-21T14:32:00Z' },
  { path: 'README.md', purpose: 'Setup and usage guide',
    content: '# Oddville\nLocal first', sha256: 'c'.repeat(64),
    lastWorkItemId: 'T1', updatedUtc: '2026-11-21T14:28:00Z' },
];

before(async () => {
  vite = await createServer({
    configFile: false, root: process.cwd(),
    server: { middlewareMode: true }, appType: 'custom',
  });
  ({ SourceArtifactExplorer, buildSourceTree, sourceLanguage } =
    await vite.ssrLoadModule('/src/SourceArtifactExplorer.tsx'));
});
after(async () => { if (vite) await vite.close(); });

test('file hierarchy preserves stored paths, sorts folders, and counts real saved files', () => {
  const tree = buildSourceTree(files);
  assert.equal(tree.count, 3);
  assert.deepEqual(tree.folders.map(folder => folder.name), ['src']);
  assert.deepEqual(tree.files.map(file => file.path), ['README.md']);
  assert.equal(tree.folders[0].count, 2);
  assert.deepEqual(tree.folders[0].folders.map(folder => folder.name),
    ['Models', 'Views']);
  assert.equal(tree.folders[0].folders[0].files[0].path, 'src/Models/Resident.cs');
  assert.equal(tree.folders[0].folders[1].files[0].path, 'src/Views/Overview.tsx');
});

test('real file format labels are inferred only from actual saved paths', () => {
  assert.equal(sourceLanguage('src/Models/Resident.cs'), 'C#');
  assert.equal(sourceLanguage('src/Views/Overview.tsx'), 'TypeScript JSX');
  assert.equal(sourceLanguage('README.md'), 'Markdown');
  assert.equal(sourceLanguage('Dockerfile'), 'Dockerfile');
  assert.equal(sourceLanguage('strange.unknown'), 'Plain text');
});

test('MAF source viewer shows stored SHA, task provenance and line numbers; never executes code', () => {
  const markup = renderToStaticMarkup(React.createElement(SourceArtifactExplorer, {
    files, source: 'MAF Software Engineer', state: 'Generated',
    runId: 'd'.repeat(32), isCurrent: true,
    assignments: [
      { id: 'T1', ordinal: 0, title: 'Domain model' },
      { id: 'T2', ordinal: 1, title: 'UI composition' },
    ],
  }));
  assert.match(markup, /SOURCE &amp; ARTIFACT EXPLORER/);
  assert.match(markup, /MAF Software Engineer/);
  assert.match(markup, /3 saved files/);
  assert.match(markup, /Generated · NOT TESTED/);
  assert.match(markup, /aria-label="MAF Software Engineer source files"/);
  assert.match(markup, /aria-label="Source code with line numbers"/);
  assert.match(markup, /README.md/);
  assert.match(markup, /Setup and usage guide/);
  assert.match(markup, /Task 1: Domain model \(T1\)/);
  assert.match(markup, /SHA-256/);
  assert.match(markup, /2026-11-21T14:28:00Z/);
  assert.match(markup, /Copy source/);
  assert.match(markup, /does not execute/);
  assert.doesNotMatch(markup, /Verified build/);
});

test('legacy source has honest provenance and partial source warnings', () => {
  const markup = renderToStaticMarkup(React.createElement(SourceArtifactExplorer, {
    files: [{ path: 'app/Program.cs', purpose: 'Entry point',
      content: 'Console.WriteLine("Hi");', sha256: 'e'.repeat(64),
      createdUtc: '2026-11-21T14:30:00Z' }],
    source: 'Classic generator', state: 'Failed',
    runId: 'e'.repeat(32), isCurrent: false,
  }));
  assert.match(markup, /Classic generator/);
  assert.match(markup, /Failed · NOT TESTED/);
  assert.match(markup, /Historical source/);
  assert.match(markup, /per-file task not recorded/);
  assert.match(markup, /Partial or in-progress source — unverified/);
});

test('untrusted file contents render as literal escaped source text', () => {
  const markup = renderToStaticMarkup(React.createElement(SourceArtifactExplorer, {
    files: [files[1]], source: 'MAF Software Engineer', state: 'Generated',
    runId: 'a'.repeat(32), isCurrent: true,
  }));
  assert.match(markup, /&lt;script&gt;danger&lt;\/script&gt;/);
  assert.doesNotMatch(markup, /<script>/);
  assert.match(markup, /src\/Views\/Overview.tsx/);
});

test('long files retain complete copyable data but start with a bounded numbered preview', () => {
  const content = Array.from({ length: 805 }, (_, i) => 'const n' + i + ' = ' + i + ';')
    .join('\n');
  const markup = renderToStaticMarkup(React.createElement(SourceArtifactExplorer, {
    files: [{ path: 'big.ts', purpose: 'Long file', content, sha256: 'f'.repeat(64) }],
    source: 'Classic generator', state: 'Generated',
    runId: 'b'.repeat(32), isCurrent: true,
  }));
  assert.match(markup, /Displaying the first 750 lines/);
  assert.match(markup, /Show complete file/);
  assert.match(markup, /const n749/);
  assert.doesNotMatch(markup, /const n804/);
});

test('an empty source manifest remains an empty state, not fake generated files', () => {
  const markup = renderToStaticMarkup(React.createElement(SourceArtifactExplorer, {
    files: [], source: 'MAF Software Engineer', state: 'Running',
    runId: 'f'.repeat(32), isCurrent: true,
  }));
  assert.match(markup, /No source files have been saved/);
  assert.match(markup, /0 of 0 saved files/);
  assert.doesNotMatch(markup, /aria-label="Source code with line numbers"/);
});

test('UX-4 styles are registered after previous Mission Control styles', () => {
  const main = readFileSync('src/main.tsx', 'utf8');
  const css = readFileSync('src/source-explorer.css', 'utf8');
  assert.ok(main.indexOf("import './source-explorer.css'") >
    main.indexOf("import './owner-decision-inbox.css'"));
  assert.match(css, /source-explorer-layout/);
  assert.match(css, /source-code-lines/);
  assert.match(css, /prefers-reduced-motion/);
});
