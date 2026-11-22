import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let commandActivity;
let commandAgents;
let CommandActivityView;
const time1 = '2026-11-22T12:01:00Z';
const time2 = '2026-11-22T12:02:00Z';
const agent = (id, role) => ({
  id, role, goal: 'Goal', workingStyle: 'Evidence-led', backstory: 'Fictional',
  allowedSkillScopes: [], canExecuteCode: false,
});
const task = {
  id: 'a'.repeat(32), title: 'Example build', objective: 'Implement saved requirements',
  status: 'Active', currentVersion: 1, createdUtc: time1,
};
const evidence = () => ({
  agents: [
    agent('ProductOwner', 'Product Owner'),
    agent('SoftwareEngineer', 'Software Engineer'),
    agent('QaEngineer', 'QA Engineer'),
  ],
  handoffs: [
    { kind: 'EngineeringTaskAssigned', fromAgent: 'TechnicalLead',
      toAgent: 'SoftwareEngineer', createdUtc: time1, current: true,
      workItemId: 'W1', specificationVersion: 1, revision: 1, model: 'local' },
  ],
  engineering: {
    job: { id: 'b'.repeat(32), state: 'Running', planSha256: 'c'.repeat(64) },
    tasks: [{ id: 'W1', ordinal: 0, title: 'Implement project', state: 'Running' }],
    events: [{ sequence: 7, kind: 'EngineeringTaskStarted', detail: 'Work item W1',
      createdUtc: time2 }],
    files: [],
  },
  qa: null,
  decisions: {
    specification: { version: 1, status: 'Approved', sha256: 'd'.repeat(64) },
    ownerAnswers: 3, readyForSpecification: true,
    decisions: [
      { topic: 'technology-stack', revision: 1, status: 'Approved',
        specificationCurrent: true, specificationSha256: 'd'.repeat(64) },
      { topic: 'architecture-style', revision: 1, status: 'Approved',
        specificationCurrent: true, specificationSha256: 'd'.repeat(64) },
    ],
    plan: { revision: 1, current: true, status: 'Approved' },
  },
});
before(async () => {
  vite = await createServer({ configFile: false, root: process.cwd(),
    server: { middlewareMode: true }, appType: 'custom' });
  ({ commandActivity, commandAgents, CommandActivityView } =
    await vite.ssrLoadModule('/src/CommandActivity.tsx'));
});
after(async () => { if (vite) await vite.close(); });

test('agent status is host-backed, not fabricated presence', () => {
  const data = evidence();
  const items = commandAgents(data);
  assert.equal(items.find(a => a.agent.id === 'SoftwareEngineer').active, true);
  assert.equal(items.find(a => a.agent.id === 'ProductOwner').active, false);
  assert.match(items.find(a => a.agent.id === 'ProductOwner').status, /No work recorded/);
  data.engineering = null;
  assert.equal(commandAgents(data).some(item => item.active), false);
});
test('activity uses timestamps, source-scoped event IDs and no invented events', () => {
  const entries = commandActivity(evidence());
  assert.equal(entries.length, 2);
  assert.equal(entries[0].label, 'EngineeringTaskStarted');
  assert.equal(entries[0].destination, 'work');
  assert.notEqual(entries[0].id, entries[1].id);
  const empty = evidence();
  empty.engineering = null;
  empty.handoffs = [];
  assert.deepEqual(commandActivity(empty), []);
});
test('Command gives a next action but no permission or fabricated verification', () => {
  const html = renderToStaticMarkup(React.createElement(CommandActivityView, {
    task, evidence: evidence(), onNavigate: () => {},
  }));
  assert.match(html, /Agent Activity Board/);
  assert.match(html, /Recent activity/);
  assert.match(html, /Inspect ongoing engineering/);
  assert.match(html, /Recorded running/);
  assert.match(html, /Work item W1/);
  assert.match(html, /Legacy diagnostics/);
  assert.doesNotMatch(html, /Authorize this exact M1 plan|Approval granted|Verified build/);
});
test('draft specification takes precedence over generating-source suggestion', () => {
  const data = evidence();
  data.decisions.specification = { version: 2, status: 'Draft',
    sha256: 'e'.repeat(64) };
  const html = renderToStaticMarkup(React.createElement(CommandActivityView, {
    task, evidence: data, onNavigate: () => {},
  }));
  assert.match(html, /Review specification v2/);
});
