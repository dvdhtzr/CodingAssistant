import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let buildDecisionInbox;
let DecisionInboxView;

const spec = { version: 2, sha256: 'a'.repeat(64), status: 'Approved' };
const approved = topic => ({
  topic, revision: 3, specificationSha256: spec.sha256,
  specificationCurrent: true, status: 'Approved',
});
const baseline = {
  specification: spec, ownerAnswers: 12, readyForSpecification: true,
  decisions: [approved('technology-stack'), approved('architecture-style')],
  plan: { revision: 2, status: 'Approved', current: true },
};
before(async () => {
  vite = await createServer({
    configFile: false,
    optimizeDeps: { noDiscovery: true, include: [] }, root: process.cwd(),
    server: { middlewareMode: true, hmr: false }, appType: 'custom',
  });
  ({ buildDecisionInbox, DecisionInboxView } =
    await vite.ssrLoadModule('/src/OwnerDecisionInbox.tsx'));
});
after(async () => { if (vite) await vite.close(); });

test('a new or partially interviewed project routes to existing product controls', () => {
  assert.deepEqual(buildDecisionInbox({
    specification: null, ownerAnswers: 0,
    readyForSpecification: false, decisions: [], plan: null,
  }).map(x => [x.id, x.stage, x.kind]), [['specification','discovery','next']]);
  assert.deepEqual(buildDecisionInbox({
    specification: null, ownerAnswers: 12,
    readyForSpecification: true, decisions: [], plan: null,
  }).map(x => [x.id, x.stage, x.kind]), [['specification','specification','next']]);
});

test('draft specification is an owner review, not automatically approved', () => {
  const items = buildDecisionInbox({
    ...baseline, specification: { ...spec, status: 'Draft' },
  });
  assert.equal(items.length, 1);
  assert.equal(items[0].kind, 'review');
  assert.equal(items[0].title, 'Review specification v2');
  assert.equal(items[0].stage, 'specification');
});

test('a proposed architecture item requires explicit owner review', () => {
  const items = buildDecisionInbox({
    ...baseline, decisions: [
      { ...approved('technology-stack'), status: 'Proposed', revision: 5 },
      approved('architecture-style'),
    ],
  });
  assert.deepEqual(items.map(x => [x.id, x.kind, x.stage]),
    [['technology-stack','review','architecture']]);
  assert.match(items[0].detail, /explicitly approve or defer/);
});

test('stale decisions are not displayed as valid approvals', () => {
  const items = buildDecisionInbox({
    ...baseline, decisions: [
      { ...approved('technology-stack'), specificationCurrent: false },
      { ...approved('architecture-style'), specificationSha256: 'b'.repeat(64) },
    ],
  });
  assert.equal(items.length, 2);
  assert.ok(items.every(item => item.kind === 'stale'));
  assert.ok(items.every(item => item.stage === 'architecture'));
  assert.doesNotMatch(JSON.stringify(items), /All current approvals/);
});

test('deferred and missing decisions do not falsely unlock implementation', () => {
  const items = buildDecisionInbox({
    ...baseline, decisions: [
      { ...approved('technology-stack'), status: 'Deferred' },
    ],
  });
  assert.deepEqual(items.map(x => [x.id, x.kind]), [
    ['technology-stack','next'], ['architecture-style','next'],
  ]);
});

test('Technical Lead proposal and stale plan are classified separately', () => {
  const proposed = buildDecisionInbox({
    ...baseline, plan: { revision: 4, status: 'Proposed', current: true },
  });
  assert.equal(proposed[0].stage, 'plan');
  assert.equal(proposed[0].kind, 'review');
  assert.equal(proposed[0].title, 'Review implementation plan v4');
  const stale = buildDecisionInbox({
    ...baseline, plan: { revision: 4, status: 'Approved', current: false },
  });
  assert.equal(stale[0].kind, 'stale');
  assert.equal(stale[0].stage, 'plan');
  assert.equal(buildDecisionInbox(baseline).length, 0);
});

test('read-only inbox renders actual stage buttons, not fake approval controls', () => {
  const items = buildDecisionInbox({
    ...baseline, decisions: [
      { ...approved('technology-stack'), status: 'Proposed' },
      { ...approved('architecture-style'), status: 'Proposed' },
    ],
  });
  const html = renderToStaticMarkup(React.createElement(DecisionInboxView, {
    items, onNavigate: () => {},
  }));
  assert.match(html, /Owner decision inbox/);
  assert.match(html, /2 approvals awaiting your review/);
  assert.match(html, /View 1 additional saved action/);
  assert.match(html, /Open Solution Architecture/);
  assert.match(html, /This inbox is read-only navigation/);
  assert.doesNotMatch(html, /onClick="[^"]+"/);
  assert.doesNotMatch(html, /Approve selected alternative/);
});

test('clear inbox explicitly distinguishes approved plans from verified source', () => {
  const html = renderToStaticMarkup(React.createElement(DecisionInboxView, {
    items: buildDecisionInbox(baseline), onNavigate: () => {},
  }));
  assert.match(html, /All current approvals are recorded/);
  assert.match(html, /source is still unverified/);
});
