import { before, after, test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';
import { readFileSync } from 'node:fs';

let vite;
let projectAtlas;
let CodeAtlasPanel;
const nodes = [
  { id: 'root', kind: 'Repository', name: 'Source',
    qualifiedName: 'run', parentId: null, path: null, line: null },
  { id: 'project', kind: 'Project', name: 'Demo',
    qualifiedName: 'Demo.csproj', parentId: 'root', path: 'Demo.csproj', line: 1 },
  { id: 'ns', kind: 'Namespace', name: 'Demo.Domain',
    qualifiedName: 'Demo.Domain', parentId: 'project', path: 'Model.cs', line: 1 },
  { id: 'class', kind: 'Class', name: 'Model',
    qualifiedName: 'Demo.Domain.Model', parentId: 'ns', path: 'Model.cs', line: 2 },
  { id: 'method', kind: 'Method', name: 'Save()',
    qualifiedName: 'Demo.Domain.Model.Save()', parentId: 'class', path: 'Model.cs', line: 3 },
  { id: 'interface', kind: 'Interface', name: 'IModel',
    qualifiedName: 'Demo.Domain.IModel', parentId: 'ns', path: 'IModel.cs', line: 1 },
  { id: 'file', kind: 'File', name: 'Model.cs',
    qualifiedName: 'Model.cs', parentId: 'project', path: 'Model.cs', line: 1 },
];
const edges = [
  ['root', 'project'], ['project', 'ns'], ['ns', 'class'],
  ['ns', 'interface'], ['class', 'method'], ['project', 'file'],
].map(([sourceId, targetId]) => ({ sourceId, targetId, kind: 'Contains' }))
  .concat([{ sourceId: 'file', targetId: 'class', kind: 'Declares' }]);
const snapshot = {
  runId: 'f'.repeat(32), sourceRevision: 'a'.repeat(64),
  coverage: 'SYNTAX_ONLY', diagnostics: [], nodes, edges,
};

before(async () => {
  vite = await createServer({
    configFile: false, optimizeDeps: { noDiscovery: true, include: [] },
    root: process.cwd(), server: { middlewareMode: true, hmr: false },
    appType: 'custom',
  });
  ({ projectAtlas, CodeAtlasPanel } = await vite.ssrLoadModule('/src/CodeAtlasPanel.tsx'));
});
after(async () => { if (vite) await vite.close(); });

test('Atlas overview comes exclusively from provided source graph', () => {
  const result = projectAtlas(snapshot, null, false);
  assert.equal(result.nodes.length, nodes.length - 1);
  assert.ok(result.nodes.some(n => n.kind === 'Class' && n.name === 'Model'));
  assert.ok(!result.nodes.some(n => n.kind === 'Method'));
  assert.ok(result.edges.every(e => e.kind === 'Contains'));
  assert.equal(result.positions.root.depth, 0);
  assert.ok(Object.values(result.positions).every(p =>
    Number.isFinite(p.x) && Number.isFinite(p.y)));
});

test('Atlas method view remains deterministic and source based', () => {
  const first = projectAtlas(snapshot, null, true);
  const second = projectAtlas(snapshot, null, true);
  assert.deepEqual(first, second);
  assert.ok(first.nodes.find(n => n.kind === 'Method' && n.name === 'Save()'));
  assert.ok(first.edges.some(e => e.targetId === 'method'));
});

test('focusing on a type keeps its namespace lineage and method', () => {
  const focused = projectAtlas(snapshot, 'class', true);
  assert.ok(focused.nodes.some(n => n.id === 'class'));
  assert.ok(focused.nodes.some(n => n.id === 'ns'));
  assert.ok(focused.nodes.some(n => n.id === 'method'));
});

test('searching focuses matching real code and never invents a node', () => {
  const filtered = projectAtlas(snapshot, null, false, 'IModel');
  assert.ok(filtered.nodes.some(n => n.id === 'interface'));
  assert.ok(filtered.nodes.every(n => nodes.some(real => real.id === n.id)));
  const unknown = projectAtlas(snapshot, null, false, 'does-not-exist');
  assert.ok(!unknown.nodes.some(n => n.name === 'does-not-exist'));
});

test('the source graph is bounded and does not render a huge hairball', () => {
  const largeNodes = [{ ...nodes[0] }];
  const largeEdges = [];
  for (let i = 0; i < 350; i++) {
    largeNodes.push({ id: 'n' + i, kind: 'File', name: 'File' + i,
      qualifiedName: 'f' + i, parentId: 'root', path: 'f' + i, line: 1 });
    largeEdges.push({ sourceId: 'root', targetId: 'n' + i, kind: 'Contains' });
  }
  const limited = projectAtlas({
    ...snapshot, nodes: largeNodes, edges: largeEdges,
  }, null, true);
  assert.ok(limited.nodes.length <= 50);
  assert.ok(limited.limited);
  assert.ok(limited.edges.every(edge =>
    limited.positions[edge.sourceId] && limited.positions[edge.targetId]));
});

test('Code Atlas initial state is honest and does not fake a generated graph', () => {
  const html = renderToStaticMarkup(React.createElement(CodeAtlasPanel, {
    token: 'a'.repeat(64), taskId: 'b'.repeat(32),
    runId: 'c'.repeat(32), files: [], running: true,
  }));
  assert.match(html, /CODE ATLAS \/ SOURCE CONSTELLATION/);
  assert.match(html, /Observing source/);
  assert.match(html, /Reading saved source structure/);
  assert.match(html, /Not compiled, executed, or verified/);
  assert.doesNotMatch(html, /Verified build/);
  assert.doesNotMatch(html, /<script>/);
});

test('Atlas is integrated into the engineering screen without a new competing workflow', () => {
  const code = readFileSync('src/EngineeringWorkspace.tsx', 'utf8');
  const css = readFileSync('src/code-atlas.css', 'utf8');
  const imports = readFileSync('src/main.tsx', 'utf8');
  assert.match(code, /<CodeAtlasPanel token=\{engine.token\}/);
  assert.match(css, /atlas-workspace/);
  assert.match(css, /prefers-reduced-motion/);
  assert.match(imports, /code-atlas\.css/);
});
