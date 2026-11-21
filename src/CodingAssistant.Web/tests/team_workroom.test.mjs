import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let TeamWorkroomView;
let summarizeTeamEvidence;
let roster;
const specSha = 'a'.repeat(64);
const runSha = 'b'.repeat(64);
const now = '2026-11-21T15:00:00Z';
const identity = (id, role, scopes = []) => ({
  id, role, goal: 'Goal of ' + role, backstory: 'Fictional ' + role,
  workingStyle: 'Evidence-based ' + role,
  allowedSkillScopes: scopes, canExecuteCode: false,
});
const evidence = () => ({
  agents: [
    identity('ProductOwner', 'Product Owner', ['product-discovery']),
    identity('SolutionArchitect', 'Solution Architect', ['architecture-review']),
    identity('TechnicalLead', 'Technical Lead', ['task-decomposition']),
    identity('SoftwareEngineer', 'Software Engineer', ['source-implementation']),
    identity('QaEngineer', 'QA Engineer', ['static-code-review']),
  ],
  events: [
    {
      kind: 'SpecificationHandoff', fromAgent: 'ProductOwner',
      toAgent: 'SolutionArchitect', current: false,
      specificationVersion: 1, revision: 0, createdUtc: now, model: 'qwen3.5:9b',
    },
    {
      kind: 'PlanApproved', fromAgent: 'HumanOwner',
      toAgent: 'SoftwareEngineer', current: true,
      specificationVersion: 2, revision: 1, createdUtc: now, model: 'qwen3.5:9b',
      planSha256: runSha,
    },
    {
      kind: 'EngineeringTaskAssigned', fromAgent: 'TechnicalLead',
      toAgent: 'SoftwareEngineer', current: true,
      specificationVersion: 2, revision: 1, createdUtc: now,
      workItemId: 'W1', model: 'qwen3.5:9b',
    },
    {
      kind: 'QaReviewRequested', fromAgent: 'SoftwareEngineer',
      toAgent: 'QaEngineer', current: true,
      specificationVersion: 2, revision: 1, createdUtc: now,
      model: 'qwen3.5:9b',
    },
  ],
  engineering: {
    job: {
      id: 'c'.repeat(32), state: 'Running', planRevision: 1,
      planSha256: runSha, model: 'qwen3.5:9b',
    },
    tasks: [
      { id: 'W1', ordinal: 0, title: 'Build residents', state: 'Completed',
        startedUtc: now, completedUtc: now, summary: 'Saved source' },
      { id: 'W2', ordinal: 1, title: 'Build UI', state: 'Pending',
        startedUtc: null, completedUtc: null, summary: null },
    ],
    events: [], files: [],
  },
  qa: {
    job: {
      id: 'd'.repeat(32), state: 'Reviewed',
      engineeringRunId: 'd'.repeat(32), planSha256: runSha,
    },
    sourceState: 'Generated',
    reports: [{ path: 'src/Resident.cs', reviewedUtc: now }],
  },
});

before(async () => {
  vite = await createServer({
    configFile: false, root: process.cwd(),
    server: { middlewareMode: true }, appType: 'custom',
  });
  ({ TeamWorkroomView, summarizeTeamEvidence } =
    await vite.ssrLoadModule('/src/TeamWorkroom.tsx'));
});
after(async () => { if (vite) await vite.close(); });

test('host roster stays distinct from observed events and synthetic presence', () => {
  const list = summarizeTeamEvidence(evidence());
  assert.equal(list.length, 5);
  assert.deepEqual(list.map(x => x.agent.id),
    ['ProductOwner','SolutionArchitect','TechnicalLead','SoftwareEngineer','QaEngineer']);
  assert.deepEqual(list.map(x => x.recordedHandoffs), [1, 1, 1, 3, 1]);
  assert.equal(list[0].historicalHandoffs, 1);
  assert.equal(list[0].reportedState, 'Recorded handoffs');
  assert.equal(list[3].reportedState, 'Engineering run: Running');
  assert.equal(list[3].taskCount, 2);
  assert.equal(list[3].completedTasks, 1);
  assert.equal(list[4].reportedState, 'QA static review: Reviewed');
});

test('no handoffs or jobs does not invent working/idle status', () => {
  const empty = evidence();
  empty.events = [];
  empty.engineering = null;
  empty.qa = null;
  const summary = summarizeTeamEvidence(empty);
  assert.ok(summary.every(item => item.recordedHandoffs === 0));
  assert.ok(summary.every(item => item.reportedState === 'No saved handoffs yet'));
});

test('agent profiles show allowed skill scopes as not installed or executable', () => {
  const html = renderToStaticMarkup(React.createElement(TeamWorkroomView, {
    evidence: evidence(), onNavigate: () => {},
  }));
  assert.match(html, /aria-label="Project agent team workroom"/);
  assert.match(html, /5 roles · 4 saved handoffs/);
  assert.equal((html.match(/class="team-workroom-member(?: team-member-selected)?"/g) ?? []).length, 5);
  assert.match(html, /aria-pressed="true"/);
  assert.match(html, /Product Owner/);
  assert.match(html, /Professional backstory \(fictional\)/);
  assert.match(html, /Future skill scopes/);
  assert.match(html, /product-discovery/);
  assert.match(html, /not enabled/);
  assert.match(html, /No SKILL.md package is installed/);
  assert.match(html, /Executable tools: Not authorized/);
  assert.match(html, /No synthetic model activity/);
  assert.doesNotMatch(html, /Install skill|Run skill|Execute source/);
});

test('handoff list marks historical spec binding and retains host timestamps', () => {
  const html = renderToStaticMarkup(React.createElement(TeamWorkroomView, {
    evidence: evidence(), onNavigate: () => {},
  }));
  assert.match(html, /SpecificationHandoff/);
  assert.match(html, /Historical binding/);
  assert.match(html, /2026-11-21T15:00:00Z/);
  assert.match(html, /1 historical/);
});

test('team workroom includes no automatic approval or code-execution capability', () => {
  const html = renderToStaticMarkup(React.createElement(TeamWorkroomView, {
    evidence: evidence(), onNavigate: () => {},
  }));
  assert.match(html, /No role can approve its own work/);
  assert.match(html, /not independently verified/);
  assert.doesNotMatch(html, /type="submit"/);
  assert.doesNotMatch(html, /Approval granted/);
});

test('workroom stylesheet is imported after prior UX slices', () => {
  const main = readFileSync('src/main.tsx', 'utf8');
  const style = readFileSync('src/team-workroom.css', 'utf8');
  assert.ok(main.indexOf("import './team-workroom.css'") >
    main.indexOf("import './source-explorer.css'"));
  assert.match(style, /team-workroom-roster/);
  assert.match(style, /data-agent="QaEngineer"/);
  assert.match(style, /prefers-reduced-motion/);
});
