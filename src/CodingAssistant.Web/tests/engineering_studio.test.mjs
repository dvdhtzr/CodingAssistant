import { before, after, test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let EngineeringStudio;
let studioAttention;

before(async () => {
  vite = await createServer({
    configFile: false, optimizeDeps: { noDiscovery: true, include: [] },
    root: process.cwd(), server: { middlewareMode: true, hmr: false },
    appType: 'custom',
  });
  ({ EngineeringStudio, studioAttention } =
    await vite.ssrLoadModule('/src/EngineeringStudio.tsx'));
});
after(async () => { await vite?.close(); });

const task = {
  id: 'a'.repeat(32), title: 'Historic', objective: 'Build a local app',
  status: 'Active', currentVersion: 1, createdUtc: '2026-11-23T12:00:00Z',
};
const noop = () => {};

test('Engineering Studio makes team, focused work and attention distinct accessible regions', () => {
  const html = renderToStaticMarkup(React.createElement(EngineeringStudio, {
    task, token: 'f'.repeat(64), onTeam: noop,
  }));
  assert.match(html, /aria-label="AI Engineering Studio"/);
  assert.match(html, /aria-label="Engineering team"/);
  assert.match(html, /aria-label="Focused engineering work"/);
  assert.match(html, /aria-label="Owner attention and handoffs"/);
  assert.match(html, /Your team at work/);
  assert.match(html, /No active agent run recorded/);
  assert.match(html, /RECORDED WORK ONLY/);
  assert.match(html, /PROJECT WORK|WORK \/ CURRENT STEP/);
  assert.doesNotMatch(html, /Agent collaboration board/);
});

test('owner attention is source- and plan-bound; failed repair is not mistaken for current QA', () => {
  const saved = {
    agents: [], handoffs: [],
    engineering: { job: { id: 'run-1', planSha256: 'hash-1',
      state: 'Generated', errorCode: null }, tasks: [], files: [], events: [] },
    qa: { job: { engineeringRunId: 'run-1', planSha256: 'hash-1',
      state: 'Reviewed' },
      reports: [{ path: 'app.js', reviewedUtc: '2026-11-23T12:00:00Z',
        assessment: { findings: [{ severity: 'Major', requirement: 'Fix interaction' }] } }] },
    repair: { cycle: { state: 'Failed', attempt: 6, errorCode: 'REPAIR_NO_CHANGE' } },
  };
  const focus = studioAttention(saved, null);
  assert.equal(focus[0].stage, 'delivery');
  assert.match(focus[0].title, /potential QA finding/);
  assert.equal(focus[1].title, 'Repair cycle 6 stopped');
  assert.equal(focus[1].detail, 'REPAIR_NO_CHANGE');
  saved.qa.job.planSha256 = 'stale-hash';
  const stale = studioAttention(saved, null);
  assert.equal(stale.length, 1);
  assert.equal(stale[0].title, 'Repair cycle 6 stopped');
});

test('decision lane navigates to original approval forms and never approves by itself', () => {
  const decisions = {
    specification: { version: 3, sha256: 'c'.repeat(64), status: 'Draft' },
    ownerAnswers: 8, readyForSpecification: true, decisions: [], plan: null,
  };
  const attention = studioAttention(null, decisions);
  assert.equal(attention.length, 1);
  assert.equal(attention[0].stage, 'specification');
  assert.match(attention[0].title, /Review specification v3/);
  const studio = readFileSync('src/EngineeringStudio.tsx', 'utf8');
  assert.doesNotMatch(studio, /POST|method: 'POST'|\/approve|\/repair-proposals/);
  assert.match(studio, /buildDecisionInbox/);
  assert.match(studio, /studioAgentPresence/);
  assert.match(studio, /studioRecordedActivity/);
});

test('stage navigation preserves focused controls without automating scroll or adding privilege', () => {
  const interview = readFileSync('src/InterviewStudio.tsx', 'utf8');
  const project = readFileSync('src/ProjectDraftPanel.tsx', 'utf8');
  const css = readFileSync('src/engineering-studio.css', 'utf8');
  const app = readFileSync('src/App.tsx', 'utf8');
  const main = readFileSync('src/main.tsx', 'utf8');
  assert.match(interview, /studioCanvas \? 'mc-work-focus ca-studio-stagebar'/);
  assert.match(interview, /if \(studioCanvas\) return;/);
  assert.match(interview, /aria-label="Project work stages"/);
  assert.match(project, /Independent QA & repair/);
  assert.match(project, /Saved engineering source, Code Atlas and exports/);
  assert.match(project, /<QaReviewPanel token=\{token\} taskId=\{taskId\} engineering=\{engineer\}/);
  assert.match(app, /<EngineeringStudio key=\{taskView.task.id\}/);
  assert.match(main, /import '.\/engineering-studio.css'/);
  assert.match(css, /animation: none !important/);
  assert.match(css, /overflow-anchor: none/);
  assert.match(css, /@media \(max-width: 800px\)/);
});
