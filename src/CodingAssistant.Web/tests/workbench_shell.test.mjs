import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let WorkbenchSidebar;
let WorkbenchInspector;
let ProjectOverview;

const task = {
  id: 'a'.repeat(32),
  title: 'Offline Notes',
  objective: 'Build local notes with <unsafe markup> and search.',
  status: 'Active',
  currentVersion: 1,
  createdUtc: '2026-11-20T12:00:00Z',
};
const noop = () => {};

before(async () => {
  vite = await createServer({
    configFile: false,
    root: process.cwd(),
    server: { middlewareMode: true },
    appType: 'custom',
  });
  ({ WorkbenchSidebar, WorkbenchInspector, ProjectOverview } =
    await vite.ssrLoadModule('/src/Workbench.tsx'));
});

after(async () => { if (vite) await vite.close(); });

test('Workbench sidebar exposes only functional navigation and honest agent descriptions', () => {
  const html = renderToStaticMarkup(React.createElement(WorkbenchSidebar, {
    tasks: [task], selectedId: task.id, view: 'studio', disabled: false,
    onSelectTask: noop, onNewProject: noop, onChangeView: noop, onRefresh: noop,
    onDeleteTask: async () => true,
  }));
  assert.match(html, /aria-label="Workspace navigation"/);
  assert.match(html, /Overview/);
  assert.match(html, /Engineering Studio/);
  assert.match(html, /Legacy diagnostics/);
  assert.match(html, /aria-current="page"/);
  assert.match(html, /Offline Notes/);
  assert.match(html, /aria-label="Delete project Offline Notes"/);
  assert.match(html, /aria-expanded="false"/);
  assert.doesNotMatch(html, /Delete permanently/);
  assert.match(html, /MAF · local Ollama \(on request\)/);
  assert.match(html, /Planned — no live agent/);
  assert.doesNotMatch(html, /Verified engineer run/);
});

test('Inspector distinguishes local capability from authorization', () => {
  const html = renderToStaticMarkup(React.createElement(WorkbenchInspector, {
    token: 'f'.repeat(64), view: 'overview', taskView: { task, plan: null }, run: null,
    status: { phase: 'M2-control-plane', model: 'local', executionEnabled: false, localOnly: true },
    onContinue: noop,
  }));
  assert.match(html, /Project inspector/);
  assert.match(html, /Execution/);
  assert.match(html, /Unavailable on this host/);
  assert.match(html, /not a verified build/);
  assert.match(html, /Open Engineering Studio/);
  assert.match(html, /Agent handoff history/);
  assert.match(html, /Loading saved handoffs/);
});

test('Project overview renders its source objective as escaped text, not HTML', () => {
  const html = renderToStaticMarkup(React.createElement(ProjectOverview, {
    token: 'f'.repeat(64), task, onContinue: noop, onDiagnostics: noop,
  }));
  assert.match(html, /Offline Notes/);
  assert.match(html, /&lt;unsafe markup&gt;/);
  assert.doesNotMatch(html, /<unsafe markup>/);
  assert.match(html, /Loading saved project state/);
  assert.match(html, /Continue in Engineering Studio/);
});
