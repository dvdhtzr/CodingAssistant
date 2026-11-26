import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let commandActivity;
let commandAgents;
let commandJourney;
let CommandActivityView;
let CommandDrawer;
let buildCommandDecisionQueue;
let DecisionQueueView;
let collaborationEvidence;
let workAgentStatuses;
let WorkTeamStrip;
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
  vite = await createServer({ configFile: false,
    optimizeDeps: { noDiscovery: true, include: [] }, root: process.cwd(),
    server: { middlewareMode: true, hmr: false }, appType: 'custom' });
  ({ commandActivity, commandAgents, commandJourney, CommandActivityView } =
    await vite.ssrLoadModule('/src/CommandActivity.tsx'));
  ({ CommandDrawer } = await vite.ssrLoadModule('/src/CommandDrawer.tsx'));
  ({ collaborationEvidence } = await vite.ssrLoadModule('/src/MissionDestinations.tsx'));
  ({ workAgentStatuses, WorkTeamStrip } =
    await vite.ssrLoadModule('/src/WorkTeamStrip.tsx'));
  ({ buildCommandDecisionQueue, DecisionQueueView } =
    await vite.ssrLoadModule('/src/DecisionQueue.tsx'));
});
after(async () => { if (vite) await vite.close(); });

test('agent collaboration trail requires saved exact-source investigation and QA receipts', () => {
  assert.deepEqual(collaborationEvidence(null), {
    inspections: 0, repairState: null, qaState: null, qaReceived: false,
  });
  const data = {
    engineering: {
      job: { id: 'a'.repeat(32), state: 'Generated',
        planSha256: 'b'.repeat(64) },
      events: [
        { sequence: 2, kind: 'EngineerAtlasInspected',
          detail: 'Host used atlas.find_symbols', createdUtc: time1 },
        { sequence: 3, kind: 'EngineeringTaskCompleted',
          detail: 'Saved source', createdUtc: time2 },
      ],
    },
    repair: { cycle: { engineeringRunId: 'a'.repeat(32),
      qaReviewId: 'c'.repeat(32), state: 'QaReviewed', attempt: 1 } },
    qa: {
      job: { id: 'c'.repeat(32), state: 'Reviewed',
        engineeringRunId: 'a'.repeat(32), planSha256: 'b'.repeat(64) },
      events: [{ sequence: 2, kind: 'QaReceivedEngineerInspectionEvidence',
        detail: 'Host-backed receipt', createdUtc: time2 }],
      reports: [],
    },
    handoffs: [],
  };
  assert.deepEqual(collaborationEvidence(data), {
    inspections: 1, repairState: 'QaReviewed',
    qaState: 'Reviewed', qaReceived: true,
  });
  data.qa.job.engineeringRunId = 'old-run-id';
  data.repair.cycle.engineeringRunId = 'old-run-id';
  assert.deepEqual(collaborationEvidence(data), {
    inspections: 1, repairState: null, qaState: null, qaReceived: false,
  });
  // An unrelated or old approval hash must not look like current QA.
  data.qa.job.engineeringRunId = data.engineering.job.id;
  data.qa.job.planSha256 = 'd'.repeat(64);
  assert.equal(collaborationEvidence(data).qaReceived, false);
});

test('compact Work agent strip reports only saved running roles and repair handoff', () => {
  const data = evidence();
  const status = workAgentStatuses({ ...data, repair: null });
  assert.equal(status.find(x => x.agent.id === 'SoftwareEngineer').active, true);
  assert.equal(status.find(x => x.agent.id === 'QaEngineer').active, false);
  data.engineering.job.state = 'Generated';
  const repair = { cycle: { state: 'QaRunning', attempt: 4, engineeringRunId: data.engineering.job.id } };
  const duringQa = workAgentStatuses({ ...data, repair });
  assert.equal(duringQa.find(x => x.agent.id === 'SoftwareEngineer').active, false);
  assert.equal(duringQa.find(x => x.agent.id === 'QaEngineer').active, true);
  assert.equal(duringQa.find(x => x.agent.id === 'ProductOwner').active, false);
  repair.cycle.engineeringRunId = 'old-run';
  assert.equal(workAgentStatuses({ ...data, repair }).some(x => x.active), false);
  const html = renderToStaticMarkup(React.createElement(WorkTeamStrip, {
    token: 'f'.repeat(64), taskId: task.id, onOpen: () => {},
  }));
  assert.match(html, /Agent collaboration status/);
  assert.match(html, /Open Team Console/);
  assert.doesNotMatch(html, /MISSION CONTROL|Mission Control/);
});

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
  assert.match(html, /Team Console/);
  assert.match(html, /Handoffs/);
  assert.match(html, /TechnicalLead.*SoftwareEngineer/s);
  assert.doesNotMatch(html, /MISSION STATE|ACTIVE MISSION|NEXT HANDOFF · SOFTWARE ENGINEER/);
  assert.match(html, /Inspect ongoing engineering/);
  assert.match(html, /Recorded running/);
  assert.match(html, /Work item W1/);
  assert.doesNotMatch(html, /Legacy diagnostics|Classic generator|Developer tools/);
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

test('agent and event controls offer contextual inspection instead of navigating immediately', () => {
  const html = renderToStaticMarkup(React.createElement(CommandActivityView, {
    task, evidence: evidence(), onNavigate: () => {},
  }));
  assert.match(html, /aria-haspopup="dialog"/);
  assert.match(html, /Inspect Software Engineer: Source run: Running/);
  assert.doesNotMatch(html, /role="dialog"/); // Closed until explicitly requested.
});
test('agent drawer shows real identity, permissions and relevant persisted handoffs', () => {
  const data = evidence();
  const html = renderToStaticMarkup(React.createElement(CommandDrawer, {
    projectTitle: task.title,
    selection: { kind: 'agent', id: 'SoftwareEngineer' },
    agents: commandAgents(data), events: commandActivity(data), evidence: data,
    onClose: () => {}, onNavigate: () => {},
  }));
  assert.match(html, /role="dialog"/);
  assert.match(html, /aria-modal="true"/);
  assert.match(html, /Close contextual drawer/);
  assert.match(html, /Example build/);
  assert.match(html, /EngineeringTaskAssigned/);
  assert.match(html, /No executable capability declared/);
  assert.match(html, /Open in Team/);
  assert.doesNotMatch(html, /Approve plan|Authorize repair|Run source/);
});
test('event drawer preserves exact persisted evidence and navigates to owner workspace', () => {
  const data = evidence();
  const event = commandActivity(data)[0];
  const html = renderToStaticMarkup(React.createElement(CommandDrawer, {
    projectTitle: task.title,
    selection: { kind: 'event', id: event.id },
    agents: commandAgents(data), events: commandActivity(data), evidence: data,
    onClose: () => {}, onNavigate: () => {},
  }));
  assert.match(html, /EngineeringTaskStarted/);
  assert.match(html, /2026-11-22T12:02:00Z/);
  assert.match(html, /Work item W1/);
  assert.match(html, /Open in Work/);
  assert.match(html, /Opening a workspace never authorizes work|Inspection only/);
});
test('a missing record is not replaced by a plausible record from another project', () => {
  const data = evidence();
  const html = renderToStaticMarkup(React.createElement(CommandDrawer, {
    projectTitle: task.title,
    selection: { kind: 'event', id: 'not-from-this-project' },
    agents: commandAgents(data), events: commandActivity(data), evidence: data,
    onClose: () => {}, onNavigate: () => {},
  }));
  assert.match(html, /This selection is no longer in the current project evidence/);
  assert.doesNotMatch(html, /Work item W1/);
});

test('current owner approvals rank ahead of QA hypotheses', () => {
  const data = evidence();
  data.decisions.specification = { version: 2, status: 'Draft', sha256: 'e'.repeat(64) };
  data.qa = {
    job: { id: 'f'.repeat(32), state: 'Reviewed',
      engineeringRunId: data.engineering.job.id, planSha256: data.engineering.job.planSha256 },
    reports: [{ path: 'App.cs', reviewedUtc: time2,
      assessment: { findings: [{ severity: 'Major', requirement: 'Potential bug' }] } }],
  };
  const queue = buildCommandDecisionQueue(data);
  assert.equal(queue[0].category, 'approval');
  assert.equal(queue[0].title, 'Review specification v2');
  assert.equal(queue.filter(item => item.category === 'qa-review').length, 1);
});
test('stale QA findings are not claimed as current decisions', () => {
  const data = evidence();
  data.qa = {
    job: { id: 'f'.repeat(32), state: 'Reviewed',
      engineeringRunId: 'e'.repeat(32), planSha256: data.engineering.job.planSha256 },
    reports: [{ path: 'App.cs', reviewedUtc: time2,
      assessment: { findings: [{ severity: 'Critical', requirement: 'Historical' }] } }],
  };
  assert.equal(buildCommandDecisionQueue(data).some(item =>
    item.category === 'qa-review'), false);
  data.qa.job.engineeringRunId = data.engineering.job.id;
  data.decisions.plan.current = false;
  assert.equal(buildCommandDecisionQueue(data).some(item =>
    item.category === 'qa-review'), false);
});
test('current static findings require review but do not automatically authorize repair', () => {
  const data = evidence();
  data.qa = {
    job: { id: 'f'.repeat(32), state: 'Reviewed',
      engineeringRunId: data.engineering.job.id, planSha256: data.engineering.job.planSha256 },
    reports: [{ path: 'App.cs', reviewedUtc: time2,
      assessment: { findings: [{ severity: 'Major', requirement: 'Check behavior' }] } }],
  };
  const queue = buildCommandDecisionQueue(data);
  const qa = queue.find(item => item.category === 'qa-review');
  assert.ok(qa);
  assert.equal(qa.requiresApproval, false);
  assert.match(qa.binding, /plan SHA-256/);
  const markup = renderToStaticMarkup(React.createElement(DecisionQueueView, {
    items: queue, onInspect: () => {},
  }));
  assert.match(markup, /Decision Queue/);
  assert.match(markup, /aria-haspopup="dialog"/);
  assert.doesNotMatch(markup, /Authorize repair|Approve current plan/);
  const details = renderToStaticMarkup(React.createElement(CommandDrawer, {
    projectTitle: task.title,
    selection: { kind: 'decision', id: qa.id }, decisions: queue,
    agents: commandAgents(data), events: commandActivity(data), evidence: data,
    onClose: () => {}, onNavigate: () => {},
  }));
  assert.match(details, /role="dialog"/);
  assert.match(details, /Source \/ approval binding/);
  assert.match(details, /Open in Quality/);
  assert.match(details, /Inspection only/);
});
test('failed source run is surfaced as an inspection blocker, not approval', () => {
  const data = evidence();
  data.engineering.job.state = 'Failed';
  const blocked = buildCommandDecisionQueue(data).find(item => item.category === 'blocked');
  assert.ok(blocked);
  assert.equal(blocked.requiresApproval, false);
});


test('incomplete server source appears as Software Engineer work, not a completed build', () => {
  const data = evidence();
  data.engineering.job.state = 'Generated';
  data.engineering.tasks[0].state = 'Completed';
  data.sourceReadiness = {
    runId: data.engineering.job.id, sourceState: 'Generated',
    sourceSha256: 'f'.repeat(64), projectKind: 'NODE_SERVER',
    runtimeProfileId: 'node-server', status: 'INCOMPLETE',
    verificationStatus: 'GENERATED_NOT_TESTED', executionAvailable: false,
    findings: [{ code: 'MISSING_PACKAGE_MANIFEST', severity: 'Error',
      path: 'package.json', detail: 'No package manifest' }],
  };
  const stages = commandJourney(data);
  assert.equal(stages.find(item => item.id === 'build').state, 'attention');
  assert.equal(stages.find(item => item.id === 'verify').state, 'todo');
  const html = renderToStaticMarkup(React.createElement(CommandActivityView, {
    task, evidence: data, onNavigate: () => {},
  }));
  assert.match(html, /Engineering needs attention/);
  assert.match(html, /Open engineering work order/);
  assert.match(html, /Source needs attention:/);
  assert.match(html, /blocking completeness issue/);
  assert.match(html, /Inspect source/);
  assert.match(html, /Agent Activity Board/);
  assert.doesNotMatch(html, /Verified application|build passed|Tests passed/);
});

test('completed static QA is never a verified build stage', () => {
  const data = evidence();
  data.engineering.job.state = 'Generated';
  data.engineering.tasks[0].state = 'Completed';
  data.qa = {
    job: { id: 'f'.repeat(32), state: 'Reviewed',
      engineeringRunId: data.engineering.job.id,
      planSha256: data.engineering.job.planSha256 },
    reports: [],
  };
  const stages = commandJourney(data);
  assert.equal(stages.find(item => item.id === 'build').state, 'attention');
  assert.equal(stages.find(item => item.id === 'verify').state, 'attention');
  assert.equal(stages.find(item => item.id === 'verify').hint, 'Static review only');
});
