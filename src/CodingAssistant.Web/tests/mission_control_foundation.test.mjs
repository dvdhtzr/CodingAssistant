import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let WorkbenchSidebar;
let WorkbenchInspector;
let ProjectOverview;

const task = {
  id: 'a'.repeat(32),
  title: 'Research <Lab>',
  objective: 'Local-first data dashboard with <script> text.',
  status: 'Active',
  currentVersion: 1,
  createdUtc: '2026-11-21T12:00:00Z',
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

test('searchable saved projects retain real selection and destructive confirmation controls', () => {
  const markup = renderToStaticMarkup(React.createElement(WorkbenchSidebar, {
    tasks: [task], selectedId: task.id, view: 'overview', disabled: false,
    onSelectTask: noop, onNewProject: noop, onChangeView: noop,
    onRefresh: noop, onDeleteTask: async () => true,
  }));
  assert.match(markup, /Search saved projects/);
  assert.match(markup, /value=""/);
  assert.match(markup, /SAVED PROJECTS/);
  assert.match(markup, /Engineering Studio/);
  assert.match(markup, /aria-current="page"/);
  assert.match(markup, /Research &lt;Lab&gt;/);
  assert.match(markup, /Delete project Research/);
  assert.match(markup, /ENGINEERING TEAM/);
  assert.doesNotMatch(markup, /Delete permanently/);
});

test('collapsed inspector is hidden, but its real history and roster remain mounted', () => {
  const markup = renderToStaticMarkup(React.createElement(WorkbenchInspector, {
    token: 'f'.repeat(64), view: 'overview',
    taskView: { task, plan: null }, run: null,
    status: { phase: 'local', model: 'local', executionEnabled: false, localOnly: true },
    onContinue: noop, collapsed: true,
  }));
  assert.match(markup, /aria-label="Project inspector" hidden=""/);
  assert.match(markup, /Recorded handoffs/);
  assert.match(markup, /Agent identities &amp; backstories/);
  assert.match(markup, /Execution/);
  assert.match(markup, /not a verified build/);
});

test('mission overview keeps user text escaped and labels evidence boundaries', () => {
  const markup = renderToStaticMarkup(React.createElement(ProjectOverview, {
    token: 'f'.repeat(64), task, onContinue: noop, onDiagnostics: noop,
  }));
  assert.match(markup, /MISSION CONTROL \/ PROJECT OVERVIEW/);
  assert.match(markup, /Research &lt;Lab&gt;/);
  assert.match(markup, /&lt;script&gt; text/);
  assert.doesNotMatch(markup, /<script>/);
  assert.match(markup, /Loading saved project state/);
  assert.match(markup, /Source is not a verified application/);
  assert.match(markup, /Continue in Engineering Studio/);
});

test('design CSS is imported after legacy styles without changing API contracts', () => {
  const bootstrap = readFileSync('src/main.tsx', 'utf8');
  const stylesheet = readFileSync('src/mission-control.css', 'utf8');
  assert.ok(bootstrap.indexOf("import './mission-control.css'") >
    bootstrap.indexOf("import './journey.css'"));
  assert.match(stylesheet, /\.wb-layout--inspector-closed/);
  assert.match(stylesheet, /\.wb-project-search-label/);
  assert.match(stylesheet, /prefers-reduced-motion/);
});
