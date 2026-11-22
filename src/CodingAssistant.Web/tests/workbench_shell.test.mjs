import { after, before, test } from 'node:test';
import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let WorkbenchSidebar;
let WorkbenchInspector;
let ProjectOverview;
let TeamDestination;
let QualityDestination;
let SkillsDestination;
let HistoryDestination;

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
    optimizeDeps: { noDiscovery: true, include: [] },
    root: process.cwd(),
    server: { middlewareMode: true, hmr: false },
    appType: 'custom',
  });
  ({ WorkbenchSidebar, WorkbenchInspector, ProjectOverview } =
    await vite.ssrLoadModule('/src/Workbench.tsx'));
  ({ TeamDestination, QualityDestination, SkillsDestination, HistoryDestination } =
    await vite.ssrLoadModule('/src/MissionDestinations.tsx'));
});

after(async () => { if (vite) await vite.close(); });

test('Workbench sidebar exposes only functional navigation and honest agent descriptions', () => {
  const html = renderToStaticMarkup(React.createElement(WorkbenchSidebar, {
    tasks: [task], selectedId: task.id, view: 'work', disabled: false,
    onSelectTask: noop, onNewProject: noop, onChangeView: noop, onRefresh: noop,
    onDeleteTask: async () => true,
  }));
  assert.match(html, /aria-label="Workspace navigation"/);
  for (const destination of ['Command', 'Team', 'Work', 'Quality', 'Skills', 'History']) {
    assert.match(html, new RegExp('>' + destination + '</button>'));
  }
  assert.doesNotMatch(html, />Legacy diagnostics<\/button>/);
  assert.match(html, /aria-current="page"/);
  assert.match(html, /PROJECT SWITCHER/);
  assert.match(html, /<details class="wb-projects wb-project-switcher">/);
  assert.doesNotMatch(html, /<details class="wb-projects wb-project-switcher" open/);
  assert.match(html, /<details class="wb-team">/);
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
    token: 'f'.repeat(64), view: 'command', taskView: { task, plan: null }, run: null,
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

test('Team, Quality, Skills and History expose honest destinations without automatic authorization', () => {
  const common = { token: 'f'.repeat(64), taskId: task.id, onWork: noop };
  const team = renderToStaticMarkup(React.createElement(TeamDestination, common));
  const quality = renderToStaticMarkup(React.createElement(QualityDestination, common));
  const skills = renderToStaticMarkup(React.createElement(SkillsDestination, common));
  const history = renderToStaticMarkup(React.createElement(HistoryDestination, common));
  assert.match(team, /Engineering team/);
  assert.match(quality, /Loading saved QA evidence/);
  assert.match(quality, /static inspection never verifies compilation or execution/);
  assert.match(skills, /No installed registry or assignment controls/);
  assert.match(history, /Loading recorded activity/);
  assert.doesNotMatch(team + quality + skills + history, /Authorize this exact M1 plan/);
});

test('Work focuses on one stage and keeps approval and QA workflows available on demand', () => {
  const work = readFileSync('src/InterviewStudio.tsx', 'utf8');
  const delivery = readFileSync('src/ProjectDraftPanel.tsx', 'utf8');
  const css = readFileSync('src/mission-control.css', 'utf8');
  assert.match(work, /journey-shell--focused/);
  assert.match(work, /WORK \/ CURRENT STEP/);
  assert.match(work, /Choose a stage/);
  assert.match(work, /Show all stages/);
  assert.match(work, /Project progress & technical history/);
  assert.match(work, /<OwnerDecisionInbox /);
  assert.match(work, /<VerificationTimeline /);
  assert.match(delivery, /QA review, findings & repair history/);
  assert.match(delivery, /<QaReviewPanel /);
  assert.match(delivery, /<EngineeringDeliveryPanel /);
  assert.match(css, /journey-shell--focused > \.journey-stage:not\(\.journey-open\)/);
  assert.match(css, /\.mc-work-qa-details/);
  assert.doesNotMatch(work, /setExpanded\(null\)/);
});

test('current Mission Control surfaces omit obsolete generation and diagnostics labels', () => {
  const command = readFileSync('src/CommandActivity.tsx', 'utf8');
  const work = readFileSync('src/ProjectDraftPanel.tsx', 'utf8');
  const source = readFileSync('src/SourceArtifactExplorer.tsx', 'utf8');
  const timeline = readFileSync('src/VerificationTimeline.tsx', 'utf8');
  const app = readFileSync('src/App.tsx', 'utf8');
  assert.doesNotMatch(command, /Legacy diagnostics|Classic generator/);
  assert.match(work, /<summary>Additional source-generation options<\/summary>/);
  assert.match(work, /open=\{running \|\| !!job\?\.errorCode\}/);
  assert.match(work, /source="Saved draft"/);
  assert.doesNotMatch(work, /Classic source generator|Legacy specification-only draft|agent pilot contract/);
  assert.match(source, /Saved source \(per-file task not recorded\)/);
  assert.doesNotMatch(timeline, /legacy C# pilot/i);
  assert.doesNotMatch(app, /Legacy diagnostics|Legacy single-file HTML preview/);
});
