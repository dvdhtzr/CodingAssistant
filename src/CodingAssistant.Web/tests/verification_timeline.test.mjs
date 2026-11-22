import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let VerificationTimelineView;
let summarizeVerificationEvidence;
let VerificationScopeView;
let scopeMatchesRun;
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
  ({ VerificationScopeView, scopeMatchesRun } =
    await vite.ssrLoadModule('/src/VerificationScope.tsx'));
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

test('static preflight findings show source hash and never certify code', () => {
  const data = fixture();
  data.preflight = {
    runId: 'e'.repeat(32), sourceState: 'Generated', sourceSha256: 'b'.repeat(64),
    status: 'POTENTIAL_ISSUES', verificationStatus: 'GENERATED_NOT_TESTED',
    htmlFileCount: 1, referencedAssets: 0,
    findings: [
      { code: 'NO_LINKED_JAVASCRIPT', severity: 'Warning',
        path: 'calculator.html', detail: 'JavaScript not linked' },
      { code: 'SELECTOR_NOT_DECLARED', severity: 'Warning',
        path: 'calculator.js', detail: '<script>Not an HTML element</script>' },
    ],
  };
  const html = renderToStaticMarkup(React.createElement(
    VerificationTimelineView, { evidence: data }));
  assert.match(html, /Static Web source preflight/);
  assert.match(html, /POTENTIAL ISSUES/);
  assert.match(html, /NO_LINKED_JAVASCRIPT/);
  assert.match(html, /calculator\.html/);
  assert.match(html, /GENERATED · NOT TESTED/);
  assert.match(html, /&lt;script&gt;Not an HTML element&lt;\/script&gt;/);
  assert.doesNotMatch(html, /<script>Not an HTML element<\/script>/);
  assert.doesNotMatch(html, /Source verified/);
});
test('no detected static findings do not imply browser checks ran', () => {
  const data = fixture();
  data.preflight = {
    runId: 'e'.repeat(32), sourceState: 'Generated', sourceSha256: 'b'.repeat(64),
    status: 'NO_ISSUES_DETECTED', verificationStatus: 'GENERATED_NOT_TESTED',
    htmlFileCount: 1, referencedAssets: 2, findings: [],
  };
  const html = renderToStaticMarkup(React.createElement(
    VerificationTimelineView, { evidence: data }));
  assert.match(html, /JavaScript syntax, application behavior and browser tests are NOT TESTED/);
  assert.match(html, /independent runtime evaluator remains unqualified/);
});


const runForScope = Object.freeze({
  id:'e'.repeat(32),taskId:'1'.repeat(32),planRevision:2,
  planSha256:sha,specificationVersion:3,
  specificationSha256:'b'.repeat(64),architectureSha256:'c'.repeat(64),
  model:'qwen3.5:9b',state:'Generated',
});
function goodScope() {
  return {
    schemaVersion:1,taskId:runForScope.taskId,
    engineeringRunId:runForScope.id,planRevision:runForScope.planRevision,
    planSha256:runForScope.planSha256,
    specificationVersion:runForScope.specificationVersion,
    specificationSha256:runForScope.specificationSha256,
    architectureSha256:runForScope.architectureSha256,
    model:runForScope.model,sourceFingerprint:'d'.repeat(64),
    scopeSha256:'f'.repeat(64),status:'SCOPE_CANDIDATE',
    executionEnabled:false,verificationStatus:'GENERATED_NOT_TESTED',
    blockers:[],files:[{path:'index.html',sha256:'e'.repeat(64),utf8Bytes:120},
      {path:'app.js',sha256:'c'.repeat(64),utf8Bytes:91}],
  };
}
test('M4.6 candidate displays authenticated scope but never implies executable tests',()=>{
  const scope=goodScope();
  assert.equal(scopeMatchesRun(scope,runForScope),true);
  const html=renderToStaticMarkup(React.createElement(
    VerificationScopeView,{scope}));
  assert.match(html,/Scope candidate/);
  assert.match(html,/Bound files/);
  assert.match(html,/Execution <strong>DISABLED/);
  assert.match(html,/GENERATED_NOT_TESTED|Generated · not tested/);
  assert.match(html,/source fingerprint and scope digest/);
  assert.match(html,/index\.html/);
  assert.match(html,/app\.js/);
  assert.match(html,/untrusted Static Web executor remains unqualified/);
  assert.doesNotMatch(html,/Run verification|Successfully verified|PASSED 16\/16/);
  assert.doesNotMatch(html,/<button/);
});
test('M4.6 blocked scope shows human-readable blockers and no scope approval',()=>{
  const scope={...goodScope(),status:'BLOCKED',scopeSha256:null,
    sourceFingerprint:null,blockers:['MISSING_ROOT_INDEX_HTML','APPROVED_PLAN_MISMATCH'],
    files:[]};
  assert.equal(scopeMatchesRun(scope,runForScope),true);
  const html=renderToStaticMarkup(React.createElement(
    VerificationScopeView,{scope}));
  assert.match(html,/Needs attention/);
  assert.match(html,/needs a root index\.html/);
  assert.match(html,/approved Technical Lead plan has changed/);
  assert.doesNotMatch(html,/Scope candidate/);
  assert.doesNotMatch(html,/<button/);
});
test('M4.6 mismatch, forged authorization or altered manifest never display as ready',()=>{
  const scope=goodScope();
  for(const wrong of [
    {...scope,engineeringRunId:'0'.repeat(32)},
    {...scope,taskId:'0'.repeat(32)},
    {...scope,planSha256:'0'.repeat(64)},
    {...scope,specificationSha256:'0'.repeat(64)},
    {...scope,architectureSha256:'0'.repeat(64)},
    {...scope,model:'unknown:latest'},
    {...scope,executionEnabled:true},
    {...scope,verificationStatus:'VERIFIED'},
    {...scope,scopeSha256:null},
    {...scope,sourceFingerprint:null},
    {...scope,files:[{path:'../private.js',sha256:'a'.repeat(64),utf8Bytes:10}]},
    {...scope,files:[{path:'index.html',sha256:'a'.repeat(64),utf8Bytes:-1}]},
    {...scope,blockers:['SOURCE_NOT_COMPLETE']},
  ])assert.equal(scopeMatchesRun(wrong,runForScope),false,JSON.stringify(wrong));
  assert.equal(scopeMatchesRun(scope,{...runForScope,state:'Running'}),false);
  assert.equal(scopeMatchesRun(scope,runForScope,[{path:'index.html',sha256:'0'.repeat(64)}]),
    false);
  assert.equal(scopeMatchesRun(scope,runForScope,scope.files.map(file=>({
    path:file.path,sha256:file.sha256,
  }))),true);
  const html=renderToStaticMarkup(React.createElement(
    VerificationScopeView,{scope:null,error:'Scope binding mismatch'}));
  assert.match(html,/Not assessed/);
  assert.match(html,/No eligibility can be inferred/);
  assert.doesNotMatch(html,/Scope candidate/);
});
test('M4.6 verification timeline shows scope blockers with independent status',()=>{
  const data=fixture();
  data.scope={...goodScope(),status:'BLOCKED',scopeSha256:null,
    sourceFingerprint:null,files:[],blockers:['STATIC_SOURCE_PREFLIGHT_HAS_FINDINGS']};
  const html=renderToStaticMarkup(React.createElement(
    VerificationTimelineView,{evidence:data}));
  assert.match(html,/Verification readiness/);
  assert.match(html,/Static checks found issues/);
  assert.match(html,/Build, test, and browser outcomes: NOT TESTED/);
  assert.doesNotMatch(html,/Run verification/);
});
test('M4.6 UI is registered for normal CI and does not fetch an execution URL',()=>{
  const code=readFileSync('src/VerificationScope.tsx','utf8');
  const css=readFileSync('src/verification-timeline.css','utf8');
  const delivery=readFileSync('src/EngineeringWorkspace.tsx','utf8');
  const timeline=readFileSync('src/VerificationTimeline.tsx','utf8');
  assert.match(code,/\/verification-scope/);
  assert.doesNotMatch(code,/\/execute|\/run-verification|\/build-test/);
  assert.match(delivery,/EngineeringVerificationScope/);
  assert.match(timeline,/scopeMatchesRun/);
  assert.match(css,/verification-scope-blockers/);
  assert.match(css,/max-width:760px/);
  assert.match(css,/focus-visible/);
});
