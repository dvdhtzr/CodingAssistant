import { before, after, test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'vite';

let vite;
let studioAgentPresence;
let studioRecordedActivity;
before(async () => {
  vite = await createServer({
    configFile: false, optimizeDeps: { noDiscovery: true, include: [] },
    root: process.cwd(), server: { middlewareMode: true, hmr: false },
    appType: 'custom',
  });
  ({ studioAgentPresence, studioRecordedActivity } =
    await vite.ssrLoadModule('/src/StudioPresence.ts'));
});
after(async () => { await vite?.close(); });
const agents = ['ProductOwner', 'SolutionArchitect', 'TechnicalLead',
  'SoftwareEngineer', 'QaEngineer'].map(id => ({
  id, role: id.replace(/([a-z])([A-Z])/g, '$1 $2'),
  goal: 'Inspect saved evidence', workingStyle: 'Evidence first',
  backstory: 'Fictional specialist', allowedSkillScopes: [], canExecuteCode: false,
}));
const models = agents.map(agent => ({
  role: agent.id, model: agent.id === 'SoftwareEngineer' ||
    agent.id === 'QaEngineer' ? 'qwen2.5-coder:14b' : 'qwen3-coder:30b',
  overridden: false, source: 'Default',
}));
const base = {
  agents, handoffs: [{
    kind: 'EngineeringTasksAssigned', fromAgent: 'TechnicalLead',
    toAgent: 'SoftwareEngineer', createdUtc: '2026-11-23T09:00:00Z',
    current: false, specificationVersion: 1, revision: 1,
    model: 'qwen3-coder:30b', workItemId: 'build',
  }],
  engineering: {
    job: { id: 'run1', state: 'Generated', planSha256: 'plan',
      model: 'qwen3-coder:30b' },
    tasks: [{ id: 'build', ordinal: 0, title: 'Implement UI', state: 'Completed' }],
    files: [], events: [{ sequence: 1, kind: 'EngineeringTaskCompleted',
      detail: 'Saved source', createdUtc: '2026-11-23T09:01:00Z' }],
  },
  qa: {
    job: { id: 'qa1', state: 'Reviewed', engineeringRunId: 'run1',
      planSha256: 'plan', model: 'qwen2.5-coder:14b' },
    sourceState: 'Generated', reports: [{ path: 'app.js' }],
    events: [{ sequence: 1, kind: 'QaStaticReportSaved',
      detail: 'Inspected app.js', createdUtc: '2026-11-23T09:02:00Z' }],
  },
  repair: { cycle: { state: 'Failed', attempt: 6, errorCode: 'REPAIR_NO_CHANGE' } },
};

test('only persisted running jobs create active model-aware specialist status', () => {
  const roles = studioAgentPresence(base, models);
  assert.equal(roles.length, 5);
  assert.equal(roles.filter(r => r.active).length, 0);
  assert.equal(roles.find(r => r.agent.id === 'TechnicalLead').state, 'recorded');
  assert.equal(roles.find(r => r.agent.id === 'ProductOwner').state, 'unstarted');
  const engineer = roles.find(r => r.agent.id === 'SoftwareEngineer');
  assert.equal(engineer.state, 'stopped', 'failed repair is recorded separately from current work');
  assert.equal(engineer.modelForNextRun, 'qwen2.5-coder:14b');
  assert.equal(engineer.modelForCurrentRun, null);
  assert.equal(engineer.assignment, '1 of 1 assigned task(s) saved');
  const qa = roles.find(r => r.agent.id === 'QaEngineer');
  assert.equal(qa.state, 'recorded');
  assert.equal(qa.assignment, '1 saved file report(s)');
  assert.equal(qa.modelForNextRun, 'qwen2.5-coder:14b');
});

test('running QA displays the model pinned to the job instead of its next-work preference', () => {
  const snapshot = {
    ...base, repair: null,
    qa: { ...base.qa, job: { ...base.qa.job,
      state: 'Running', model: 'qwen3-coder:30b' } },
  };
  const roles = studioAgentPresence(snapshot, models);
  const qa = roles.find(r => r.agent.id === 'QaEngineer');
  assert.equal(qa.active, true);
  assert.equal(qa.state, 'running');
  assert.equal(qa.modelForCurrentRun, 'qwen3-coder:30b');
  assert.equal(qa.modelForNextRun, 'qwen2.5-coder:14b');
  assert.equal(roles.find(r => r.agent.id === 'SoftwareEngineer').active, false);
});

test('timeline uses saved UTC receipts and retains historical handoff labels', () => {
  const events = studioRecordedActivity(base);
  assert.equal(events.length, 3);
  assert.deepEqual(events.map(e => e.at), [
    '2026-11-23T09:02:00Z', '2026-11-23T09:01:00Z',
    '2026-11-23T09:00:00Z',
  ]);
  assert.equal(events[0].stage, 'delivery');
  assert.match(events[0].title, /QA saved an independent review/);
  assert.equal(events[1].stage, 'generation');
  assert.match(events[1].title, /Engineer saved a source revision/);
  assert.equal(events[2].historical, true);
  assert.deepEqual(events[2].roleIds, ['TechnicalLead','SoftwareEngineer']);
  assert.match(events[2].title, /Technical Lead → Software Engineer/);
  assert.equal(studioRecordedActivity({ ...base, engineering: null, qa: null,
    handoffs: [] }).length, 0);
  assert.equal(studioRecordedActivity(null).length, 0);
});

test('failed or interrupted snapshots alone are not invented activity receipts', () => {
  const failures = {
    ...base,
    handoffs: [], engineering: { ...base.engineering, events: [] },
    qa: { ...base.qa, events: [] },
    repair: { cycle: { state: 'Failed', attempt: 9, errorCode: 'REPAIR_NO_CHANGE' } },
  };
  assert.deepEqual(studioRecordedActivity(failures), []);
});
