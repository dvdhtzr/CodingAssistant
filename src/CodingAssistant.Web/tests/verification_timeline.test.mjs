import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let VerificationTimelineView;
let summarizeVerificationEvidence;
const sha = 'a'.repeat(64);
const when = '2026-11-21T15:00:00Z';
function fixture() {
  return {
    plan: { revision: 2, planSha256: sha, status: 'Approved', current: true },
    profile: { id: 'static-web', version: '0.1.0',
      qualificationStatus: 'UNQUALIFIED', executableViaRegistry: false,
      requiredIndependentTests: ['Linked asset integrity', 'Chromium mouse and keyboard behavior'] },
    registryExecutionAvailable: false,
    engineering: {
      job: { id: 'e'.repeat(32), planRevision: 2, planSha256: sha,
        state: 'Generated', model: 'qwen3.5:9b', errorCode: null },
      files: [{ path: 'index.html' }], tasks: [{ state: 'Completed' }],
      events: [{ sequence: 1, kind: 'EngineeringRunAdmitted',
        detail: 'Approved plan accepted for source-only tasks', createdUtc: when },
        { sequence: 2, kind: 'SourceFileSaved',
          detail: 'Saved index.html', createdUtc: '2026-11-21T15:02:00Z' }],
    },
    qa: { job: { id: 'f'.repeat(32), engineeringRunId: 'e'.repeat(32),
      planSha256: sha, sourceSha256: 'b'.repeat(64),
      state: 'Reviewed', model: 'qwen3.5:9b', errorCode: null },
      sourceState: 'Generated',
      reports: [{ path: 'index.html', reviewedUtc: when }],
      events: [{ sequence: 1, kind: 'QaReviewAdmitted',
        detail: 'Static review only', createdUtc: '2026-11-21T15:03:00Z' }] },
  };
}
before(async () => {
  vite = await createServer({ configFile: false, root: process.cwd(),
    server: { middlewareMode: true }, appType: 'custom' });
  ({ VerificationTimelineView, summarizeVerificationEvidence } =
    await vite.ssrLoadModule('/src/VerificationTimeline.tsx'));
});
after(async () => { await vite?.close(); });

test('chronological ledger preserves host timestamps and independent sequences', () => {
  const data = summarizeVerificationEvidence(fixture());
  assert.equal(data.sourceCurrent, true);
  assert.equal(data.qaCurrent, true);
  assert.equal(data.sourceStatus, 'GENERATED · NOT TESTED');
  assert.deepEqual(data.events.map(e => e.kind),
    ['EngineeringRunAdmitted', 'SourceFileSaved', 'QaReviewAdmitted']);
  assert.equal(data.events[0].createdUtc, when);
  assert.equal(data.events[2].source, 'QA static review');
});
test('a model static QA review never upgrades a generated ZIP to VERIFIED', () => {
  const html = renderToStaticMarkup(React.createElement(
    VerificationTimelineView, { evidence: fixture() }));
  assert.match(html, /GENERATED · NOT TESTED/);
  assert.match(html, /Not qualified \/ blocked/);
  assert.match(html, /Build, test, and browser outcomes: NOT TESTED/);
  assert.match(html, /UNQUALIFIED/);
  assert.match(html, /source-only tasks/);
  assert.match(html, /Saved index.html/);
  assert.match(html, /2026-11-21T15:03:00Z/);
  assert.match(html, /Security and task-scope gates #76 and #92 remain open/);
  assert.doesNotMatch(html, /PASSED 12 tests|Successfully verified|Run verification/);
});
test('no source or QA records cannot become an inferred test result', () => {
  const empty = fixture();
  empty.engineering = null;
  empty.qa = null;
  const view = summarizeVerificationEvidence(empty);
  assert.equal(view.sourceStatus, 'NO SOURCE RUN');
  assert.equal(view.events.length, 0);
  const html = renderToStaticMarkup(React.createElement(
    VerificationTimelineView, { evidence: empty }));
  assert.match(html, /No MAF engineering attempt saved/);
  assert.match(html, /No static QA attempt saved/);
  assert.match(html, /No engineering or QA events have been recorded/);
});
test('stale plan and mismatched QA are visibly historical, never current', () => {
  const old = fixture();
  old.plan = { ...old.plan, planSha256: 'c'.repeat(64) };
  let state = summarizeVerificationEvidence(old);
  assert.equal(state.sourceCurrent, false);
  assert.equal(state.qaCurrent, false);
  assert.equal(state.sourceStatus, 'HISTORICAL · NOT TESTED');
  const mismatched = fixture();
  mismatched.qa.job.engineeringRunId = 'd'.repeat(32);
  state = summarizeVerificationEvidence(mismatched);
  assert.equal(state.sourceCurrent, true);
  assert.equal(state.qaCurrent, false);
  assert.equal(state.events.find(e => e.source === 'QA static review').current, false);
});
test('failed source and interrupted QA remain NOT TESTED with saved events', () => {
  const data = fixture();
  data.engineering.job.state = 'Failed';
  data.engineering.job.errorCode = 'MODEL_OUTPUT_REJECTED';
  data.qa.job.state = 'Interrupted';
  const html = renderToStaticMarkup(React.createElement(
    VerificationTimelineView, { evidence: data }));
  assert.match(html, /PARTIAL SOURCE · NOT TESTED/);
  assert.match(html, /MODEL_OUTPUT_REJECTED/);
  assert.match(html, /Interrupted/);
  assert.doesNotMatch(html, /Verified build/);
});
test('untrusted persisted event details are escaped, never interpreted as markup', () => {
  const data = fixture();
  data.engineering.events[0].detail = '<img src=x onerror=alert(1)>';
  const html = renderToStaticMarkup(React.createElement(
    VerificationTimelineView, { evidence: data }));
  assert.match(html, /&lt;img src=x onerror=alert\(1\)&gt;/);
  assert.doesNotMatch(html, /<img src=x/);
});
test('UX-6 styles and deterministic tests are registered in normal CI', () => {
  const main = readFileSync('src/main.tsx', 'utf8');
  const packageFile = JSON.parse(readFileSync('package.json', 'utf8'));
  const css = readFileSync('src/verification-timeline.css', 'utf8');
  assert.ok(main.indexOf("import './verification-timeline.css'") >
    main.indexOf("import './team-workroom.css'"));
  assert.match(packageFile.scripts['test:workbench'], /verification_timeline.test.mjs/);
  assert.match(css, /prefers-reduced-motion/);
  assert.match(css, /verification-events/);
});
