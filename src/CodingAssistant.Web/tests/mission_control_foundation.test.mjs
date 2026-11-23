import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let WorkbenchSidebar;
let ProjectSwitcher;
let WorkbenchInspector;
let ProjectOverview;
let AgentRosterCards;
let EngineeringGenerationPanel;

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
    optimizeDeps: { noDiscovery: true, include: [] },
    root: process.cwd(),
    server: { middlewareMode: true, hmr: false },
    appType: 'custom',
  });
  ({ WorkbenchSidebar, ProjectSwitcher, WorkbenchInspector, ProjectOverview } =
    await vite.ssrLoadModule('/src/Workbench.tsx'));
  ({ AgentRosterCards } = await vite.ssrLoadModule('/src/AgentIdentityPanel.tsx'));
  ({ EngineeringGenerationPanel } =
    await vite.ssrLoadModule('/src/EngineeringWorkspace.tsx'));
});
after(async () => { if (vite) await vite.close(); });

test('rail navigation and the project switcher retain selection and guarded deletion', () => {
  const nav = renderToStaticMarkup(React.createElement(WorkbenchSidebar, {
    selectedId: task.id, view: 'command', disabled: false,
    onNewProject: noop, onChangeView: noop,
  }));
  for (const name of ['Command', 'Team', 'Work', 'Quality', 'Skills', 'History']) {
    assert.match(nav, new RegExp('>' + name + '</button>'));
  }
  assert.match(nav, /aria-current="page"/);

  const projects = renderToStaticMarkup(React.createElement(ProjectSwitcher, {
    tasks: [task], selectedId: task.id, disabled: false,
    onSelectTask: noop, onRefresh: noop, onDeleteTask: async () => true,
  }));
  assert.match(projects, /Search saved projects/);
  assert.match(projects, /value=""/);
  assert.match(projects, /SAVED PROJECTS/);
  assert.match(projects, /PROJECT SWITCHER/);
  assert.match(projects, /Research &lt;Lab&gt;/);
  assert.match(projects, /Delete project Research/);
  assert.doesNotMatch(projects, /Delete permanently/);
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


test('actual MAF roster cards show independently defined goals and fictional identities', () => {
  const html = renderToStaticMarkup(React.createElement(AgentRosterCards, {
    agents: [{
      id: 'ProductOwner', role: 'Product Owner',
      goal: 'Protect the owner acceptance criteria',
      workingStyle: 'Asks focused questions', backstory: 'Fictional product veteran',
      allowedSkillScopes: ['acceptance-criteria'], canExecuteCode: false,
    }, {
      id: 'QaEngineer', role: 'QA Engineer',
      goal: 'Record defects without claiming tests ran',
      workingStyle: 'Evidence first', backstory: 'Fictional skeptical tester',
      allowedSkillScopes: ['static-review'], canExecuteCode: false,
    }],
  }));
  assert.match(html, /Product Owner/);
  assert.match(html, /QA Engineer/);
  assert.match(html, /Fictional product veteran/);
  assert.match(html, /FUTURE SKILL SCOPES \(NOT ENABLED\)/);
  assert.match(html, /Executable tools/);
  assert.match(html, /Not authorized/);
  assert.equal((html.match(/<summary/g) ?? []).length, 2);
  assert.doesNotMatch(html, /Verified build/);
});

test('engineering task board communicates saved task and file evidence without fabricated progress', () => {
  const html = renderToStaticMarkup(React.createElement(EngineeringGenerationPanel, {
    legacyRunning: false,
    engine: {
      snapshot: {
        job: { state: 'Running', phase: 'EngineeringTask', planRevision: 2,
          model: 'qwen3.5:9b', errorCode: null },
        tasks: [
          { id: 'task-a', ordinal: 0, title: 'Build data model',
            state: 'Completed', summary: 'Source saved', startedUtc: null,
            completedUtc: '2026-11-21T14:00:00Z' },
          { id: 'task-b', ordinal: 1, title: 'Build dashboard',
            state: 'Running', summary: null, startedUtc: '2026-11-21T14:01:00Z',
            completedUtc: null },
        ],
        files: [{ path: 'Model.cs', lastWorkItemId: 'task-a' }],
        events: [],
      },
      plan: { status: 'Approved', current: true },
      error: '', busy: '', start: noop, cancel: noop, refresh: noop, download: noop,
    },
  }));
  assert.match(html, /Implementation workstream/);
  assert.match(html, /1 of 2 completed/);
  assert.match(html, /Recorded active assignment: Build dashboard/);
  assert.match(html, /data-task-state="Completed"/);
  assert.match(html, /data-task-state="Running"/);
  assert.match(html, /Assignment 1 · 1 source file\(s\) last saved by this assignment/);
  assert.match(html, /Source saved/);
  assert.doesNotMatch(html, /% complete/);
  assert.match(html, /not built, executed or independently tested/);
});

test('failed task names are separated from file counts and show exact persisted code', () => {
  const html = renderToStaticMarkup(React.createElement(EngineeringGenerationPanel, {
    legacyRunning: false,
    engine: {
      snapshot: {
        job: { state: 'Failed', phase: 'Failed', planRevision: 1,
          model: 'qwen3-coder:30b', errorCode: 'MODEL_OUTPUT_REJECTED' },
        tasks: [
          { id: 'final-step', ordinal: 5, title: 'Launch Preparation and Testing',
            state: 'Failed', summary: null, startedUtc: '2026-11-23T10:47:14Z',
            completedUtc: null },
        ],
        files: [], events: [{ sequence: 3, kind: 'EngineeringRunFailed',
          detail: 'Stopped safely: MODEL_OUTPUT_REJECTED' }],
      },
      plan: { status: 'Approved', current: true },
      error: '', busy: '', start: noop, cancel: noop, refresh: noop, download: noop,
    },
  }));
  assert.match(html, /Assignment 6 · 0 source file\(s\) last saved by this assignment/);
  assert.match(html, /Launch Preparation and Testing/);
  assert.match(html, /Reason code: <code>MODEL_OUTPUT_REJECTED<\/code>/);
  assert.match(html, /Earlier completed source remains saved/);
  assert.doesNotMatch(html, /Task 60 saved/);
});

test('failed run offers scoped resume and clearly distinguishes restarting from task one', () => {
  const priorRun = {
    id: 'a'.repeat(32), taskId: 'b'.repeat(32), state: 'Failed',
    phase: 'Failed', planRevision: 2, planSha256: 'c'.repeat(64),
    model: 'qwen3-coder:30b', errorCode: 'MODEL_OUTPUT_REJECTED',
  };
  const html = renderToStaticMarkup(React.createElement(EngineeringGenerationPanel, {
    legacyRunning: false,
    engine: {
      snapshot: {
        job: priorRun,
        tasks: [
          { id: 'first', ordinal: 0, title: 'Website foundation', state: 'Completed',
            summary: 'Saved source', startedUtc: null, completedUtc: '2026-11-23T10:00:00Z' },
          { id: 'final', ordinal: 1, title: 'Launch preparation', state: 'Failed',
            summary: null, startedUtc: '2026-11-23T10:47:14Z', completedUtc: null },
        ],
        files: [{ path: 'index.html', lastWorkItemId: 'first' }], events: [],
      },
      plan: { revision: 2, planSha256: priorRun.planSha256,
        status: 'Approved', current: true },
      error: '', busy: '', start: noop, resume: noop, cancel: noop,
      refresh: noop, download: noop,
    },
  }));
  assert.match(html, /Resume remaining 1 assignment\(s\)/);
  assert.match(html, /Start a separate new run from task 1/);
  assert.match(html, /MODEL_OUTPUT_REJECTED/);
  assert.match(html, /Assignment 2 · 0 source file\(s\)/);
});

test('agent presence stylesheet is loaded after the base Mission Control design', () => {
  const bootstrap = readFileSync('src/main.tsx', 'utf8');
  const css = readFileSync('src/agent-presence.css', 'utf8');
  assert.ok(bootstrap.indexOf("import './agent-presence.css'") >
    bootstrap.indexOf("import './mission-control.css'"));
  assert.match(css, /prefers-reduced-motion/);
  assert.match(css, /data-task-state="Failed"/);
  assert.match(css, /data-agent="QaEngineer"/);
});
