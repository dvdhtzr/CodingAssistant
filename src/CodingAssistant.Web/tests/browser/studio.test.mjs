import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { createServer } from 'vite';

let vite;
let browser;
let baseUrl;
const id = 'a'.repeat(32), runId = 'b'.repeat(32), reviewId = 'c'.repeat(32);
const sha = 'd'.repeat(64), spec = 'e'.repeat(64), arch = 'f'.repeat(64);
const time = '2026-11-23T12:00:00Z';
const roles = [
  ['ProductOwner', 'Product Owner'], ['SolutionArchitect', 'Solution Architect'],
  ['TechnicalLead', 'Technical Lead'], ['SoftwareEngineer', 'Software Engineer'],
  ['QaEngineer', 'QA Engineer'],
];
const agents = roles.map(([id, role]) => ({
  id, role, goal: 'Record reliable engineering work',
  backstory: 'A fictional specialist', workingStyle: 'Evidence first',
  allowedSkillScopes: [], canExecuteCode: false,
}));
const engineering = {
  job: { id: runId, taskId: id, planRevision: 1, planSha256: sha,
    specificationVersion: 1, specificationSha256: spec, architectureSha256: arch,
    model: 'qwen3-coder:30b', state: 'Generated', phase: 'Finished',
    errorCode: null, createdUtc: time, updatedUtc: time },
  tasks: [{ id: 'item1', ordinal: 0, title: 'Create initial application',
    state: 'Completed', summary: 'Saved source', startedUtc: time, completedUtc: time }],
  files: [{ path: 'index.html', purpose: 'Homepage', content: '<h1>Historic</h1>',
    sha256: sha, lastWorkItemId: 'item1', updatedUtc: time }],
  events: [{ sequence: 1, kind: 'EngineeringTaskCompleted',
    detail: 'Source persisted', createdUtc: time }],
};
const qa = {
  job: { id: reviewId, taskId: id, engineeringRunId: runId,
    sourceSha256: sha, planRevision: 1, planSha256: sha, model: 'qwen3-coder:30b',
    state: 'Running', phase: 'Reviewing', errorCode: null },
  sourceState: 'Generated', reports: [],
  events: [{ sequence: 1, kind: 'QaReviewAdmitted', detail: 'Review started', createdUtc: time }],
};
const repairCycle = {
  cycle: { id: '1'.repeat(32), state: 'Failed', attempt: 6,
    errorCode: 'REPAIR_NO_CHANGE', engineeringRunId: runId, qaReviewId: null },
  engineering: { job: { state: 'Failed', errorCode: 'REPAIR_NO_CHANGE' }, files: [] },
  review: null, staticPreflight: { status: 'NO_ISSUES_DETECTED',
    verificationStatus: 'NOT_VERIFIED', findings: [] },
};
const done = (route, body, status = 200) => route.fulfill({
  status, contentType: 'application/json', body: JSON.stringify(body),
});

function mockApi(page, { architectureOutdated = false,
  specificationStatus = 'Approved', historicalQuality = false } = {}) {
  const selections = [
    { role: 'ProductOwner', model: 'qwen3-coder:30b',
      overridden: false, source: 'Interview selection' },
    { role: 'SolutionArchitect', model: 'qwen3-coder:30b',
      overridden: false, source: 'Inherited interview model' },
    { role: 'TechnicalLead', model: 'qwen3-coder:30b',
      overridden: false, source: 'Inherited interview model' },
    { role: 'SoftwareEngineer', model: 'qwen2.5-coder:14b',
      overridden: false, source: 'Preferred coding model' },
    { role: 'QaEngineer', model: 'qwen2.5-coder:14b',
      overridden: false, source: 'Preferred coding model' },
  ];
  return page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname;
    const method = route.request().method();
    if (method === 'POST' && path.includes('/agent-models/')) {
      const role = path.split('/').at(-1);
      const row = selections.find(item => item.role === role);
      const requested = route.request().postDataJSON()?.model;
      if (!row) return done(route, { error: 'INVALID_AGENT_ROLE' }, 400);
      if (requested !== null && requested !== 'qwen3-coder:30b')
        return done(route, { error: 'MODEL_NOT_INSTALLED' }, 409);
      row.model = requested ?? (role === 'SoftwareEngineer' || role === 'QaEngineer'
        ? 'qwen2.5-coder:14b' : 'qwen3-coder:30b');
      row.overridden = requested !== null;
      row.source = row.overridden ? 'Owner choice' :
        role === 'SoftwareEngineer' || role === 'QaEngineer'
          ? 'Preferred coding model' : 'Inherited interview model';
      return done(route, row);
    }
    if (method === 'POST' && path.endsWith('/architecture-decisions/recommend')) {
      const input = route.request().postDataJSON();
      return done(route, {
        topic: input.topic, revision: input.expectedRevision + 1,
        specificationSha256: spec, specificationCurrent: true,
        recommendedOptionId: 'option-1',
        rationale: 'Current approved requirements assessed',
        options: [
          { id: 'option-1', label: 'Static Web',
            benefit: 'Simple operation', tradeoff: 'Limited dynamic features',
            runtimeProfileId: null },
          { id: 'option-2', label: 'Server Application',
            benefit: 'Dynamic content', tradeoff: 'Higher maintenance',
            runtimeProfileId: null },
        ],
        proposalSha256: arch, status: 'Proposed', selectedOptionId: null,
      });
    }
    if (method !== 'GET') return done(route, { error: 'UNEXPECTED_MUTATION' }, 405);
    if (path === '/api/studio/http-activity') return done(route, {
      events: [
        { sequence: 1, recordedUtc: time, method: 'GET',
          route: '/api/tasks/{id}/architecture-decisions',
          statusCode: 200, durationMs: 114 },
        { sequence: 2, recordedUtc: time, method: 'GET',
          route: '/api/tasks/{id}/engineering-runs/latest',
          statusCode: 200, durationMs: 46 },
      ], lastSequence: 2, truncated: false, capacity: 120,
    });
    if (path.endsWith('/agent-models')) return done(route, { agents: selections });
    if (path === '/api/agents/roster') return done(route, { agents });
    if (path.endsWith('/agent-handoffs')) return done(route, { events: [{
      kind: 'EngineeringHandoff', fromAgent: 'TechnicalLead',
      toAgent: 'SoftwareEngineer', createdUtc: time, current: true,
      specificationVersion: 1, revision: 1, model: 'qwen3-coder:30b',
    }] });
    if (path.endsWith('/engineering-runs/latest'))
      return done(route, { snapshot: engineering });
    if (path.endsWith('/qa-reviews/latest')) return done(route, { snapshot: historicalQuality ?
      { ...qa, job: { ...qa.job, engineeringRunId: '8'.repeat(32), state: 'Failed', errorCode: 'MODEL_OUTPUT_REJECTED' }, sourceState: 'Failed' } : qa });
    if (path.endsWith('/repair-cycles/latest'))
      return done(route, { snapshot: historicalQuality ? { ...repairCycle,
        cycle: { ...repairCycle.cycle, engineeringRunId: '8'.repeat(32) } } : repairCycle });
    if (path.endsWith('/repair-cycles'))
      return done(route, { cycles: Array.from({ length: 6 }, (_, i) => ({
        ...repairCycle.cycle, id: String(i + 1).repeat(32), attempt: i + 1,
      })) });
    if (path.endsWith('/interview')) return done(route, {
      taskId: id, model: 'qwen3-coder:30b', lastSequence: 2, readyForSpecification: true,
      messages: [
        { sequence: 1, role: 'assistant', content: 'What should we build?', createdUtc: time },
        { sequence: 2, role: 'owner', content: 'A local history site.', createdUtc: time },
      ],
      specification: { version: 1, model: 'qwen3-coder:30b', content: 'Build historic',
        sha256: spec, status: specificationStatus, createdUtc: time, approvedUtc: time },
    });
    if (path === '/api/models') return done(route, { models: ['qwen3-coder:30b'] });
    if (path === '/api/runtime-profiles') return done(route, { profiles: [] });
    if (path.endsWith('/architecture-decisions')) return done(route, {
      decisions: ['technology-stack', 'architecture-style'].map(topic => ({
        topic, revision: 1,
        specificationSha256: architectureOutdated ? 'a'.repeat(64) : spec,
        specificationCurrent: !architectureOutdated && specificationStatus === 'Approved',
        recommendedOptionId: 'option-1', rationale: 'Owner-approved project decisions',
        options: [{ id: 'option-1', label: 'Static Web', benefit: 'Local-first',
          tradeoff: 'No executable runtime', runtimeProfileId: null }],
        proposalSha256: arch, status: 'Approved', selectedOptionId: 'option-1',
      })),
    });
    if (path.endsWith('/implementation-plan')) return done(route, {
      schemaVersion: 1,
      plan: { taskId: id, revision: 1,
        specificationVersion: 1, specificationSha256: spec, architectureSha256: arch,
        model: 'qwen3-coder:30b', planSha256: sha, status: 'Approved', current: true,
        effectiveStatus: 'Approved', createdUtc: time, approvedUtc: time,
        outline: { summary: 'Create the historic site',
          tasks: [{ id: 'item1', title: 'Create initial application',
            description: 'Provide a self-contained static page',
            dependsOn: [], acceptanceCriteria: ['Saved source contains index.html'] }],
          testStrategy: ['Independent HTML review'], risks: [] } },
      history: [],
    });
    if (path.endsWith('/draft-build')) return done(route, { available: true, snapshot: null });
    if (path.endsWith('/draft-build/architecture')) return done(route, {
      status: 'READY', readyForSourceGeneration: true, architectureSha256: arch,
      blockingTopics: [], approvedChoices: [],
    });
    console.error('Studio fixture has no GET handler for', method, path);
    return done(route, { error: 'NO_FIXTURE' }, 404);
  });
}
before(async () => {
  vite = await createServer({
    configFile: false, root: process.cwd(),
    server: { host: '127.0.0.1', port: 0, strictPort: false }, appType: 'spa',
  });
  await vite.listen();
  const address = vite.httpServer?.address();
  if (!address || typeof address === 'string') throw Error('No Vite bound address');
  baseUrl = 'http://127.0.0.1:' + address.port;
  browser = await chromium.launch({ headless: true });
});
after(async () => { await browser?.close();await vite?.close(); });

test('new generated source takes priority over historical failed repair and QA', async () => {
  const page = await browser.newPage({ viewport: { width: 1600, height: 900 } });
  try {
    await mockApi(page, { historicalQuality: true });
    await page.goto(baseUrl + '/tests/browser/studio.html');
    const studio = page.getByRole('region', { name: 'AI Engineering Studio' });
    const attention = studio.locator('aside.ca-studio-context');
    await attention.getByText('New source ready for QA').waitFor();
    assert.equal(await attention.getByText('Repair cycle 6 stopped').count(), 0);
    const engineer = studio.locator('[data-agent="SoftwareEngineer"]');
    assert.match(await engineer.textContent(), /Generated/);
    assert.doesNotMatch(await engineer.textContent(), /Needs inspection/);
    await studio.locator('[data-agent="QaEngineer"]').click();
    const canvas = studio.getByRole('region', { name: 'Focused engineering work' });
    await canvas.getByRole('button', { name: 'Ask QA Engineer to inspect generated source' }).waitFor();
    assert.equal(await canvas.getByRole('button', { name: 'Ask QA Engineer to inspect generated source' }).isEnabled(), true);
    assert.match(await canvas.getByLabel('Current QA source').textContent(), /has not been independently reviewed/);
    assert.equal(await canvas.locator('.qa-repair-progress').count(), 0);
    await canvas.getByText('Earlier QA report', { exact: false }).waitFor();
    assert.equal(await canvas.locator('.qa-repair-history').getAttribute('open'), null);
  } finally { await page.close(); }
});

test('studio prioritizes real QA, keeps prior failed repair separate, and exposes source on demand', async () => {
  const page = await browser.newPage({ viewport: { width: 1600, height: 900 } });
  page.on('pageerror', error => console.error('Studio desktop page error:', error.stack));
  page.on('console', message => {
    if (message.type() === 'error') console.error('Studio desktop console:', message.text());
  });
  try {
    await mockApi(page);
    await page.goto(baseUrl + '/tests/browser/studio.html');
    const studio = page.getByRole('region', { name: 'AI Engineering Studio' });
    const team = studio.locator('aside.ca-studio-people');
    const canvas = studio.getByRole('region', { name: 'Focused engineering work' });
    const attention = studio.locator('aside.ca-studio-context');
    assert.equal(await team.getAttribute('role'), 'complementary');
    assert.equal(await attention.getAttribute('role'), 'complementary');
    assert.equal(await attention.getAttribute('aria-label'), 'Owner attention and handoffs');
    await team.getByRole('button', { name: /QA Engineer/ }).waitFor();
    await attention.getByText('Repair cycle 6 stopped').waitFor();
    await canvas.getByRole('heading', { name: 'Independent QA & repair' }).waitFor();
    assert.match(await studio.locator('.ca-studio-run-state').textContent(),
      /QA Engineer reviewing/);
    assert.match(await attention.textContent(), /REPAIR_NO_CHANGE/);
    assert.match(await canvas.textContent(), /Running.*0 file\(s\) inspected/s);
    assert.equal(await canvas.getByRole('button', { name: 'Request a fresh static QA review' })
      .isDisabled(), true);
    const disclos = canvas.locator('details.ca-studio-source-disclosure').first();
    assert.equal(await disclos.getAttribute('open'), null);
    assert.equal(await canvas.getByRole('heading', { name: 'Independent QA & repair' })
      .isVisible(), true);
    await disclos.locator(':scope > summary').click();
    assert.match(await disclos.textContent(), /Generated source/);
    assert.equal(await canvas.getByText('No generated files yet. Start with Source Generation.',
      { exact: true }).count(), 0, 'the separate draft workflow must not hide saved engineer source');
    assert.match(await canvas.textContent(), /Host text preflight:/);
    assert.match(await canvas.textContent(), /No independent findings available/);
    const boxes = [await team.boundingBox(), await canvas.boundingBox(),
      await attention.boundingBox()];
    assert.ok(boxes.every(Boolean));
    assert.ok(boxes[0].x + boxes[0].width <= boxes[1].x + 4 &&
      boxes[1].x + boxes[1].width <= boxes[2].x + 4,
      'desktop team, work and decisions must have separate stable columns');
    const animation = await studio.locator('.ca-studio-run-state').evaluate(el =>
      getComputedStyle(el).animationName);
    assert.equal(animation, 'none');
    await team.getByRole('button', { name: /Software Engineer/ }).click();
    await canvas.getByRole('heading', { name: 'Source Generation' }).waitFor();
    assert.equal(await canvas.locator('#journey-panel-generation').isVisible(), true);
    assert.equal(await canvas.locator('#journey-panel-delivery').isVisible(), false);
    await team.getByRole('button', { name: /QA Engineer/ }).click();
    assert.equal(await canvas.locator('#journey-panel-delivery').isVisible(), true);
    assert.equal(await page.evaluate(() => document.scrollingElement?.scrollTop ?? 0), 0,
      'role navigation must not scroll the whole document');
  } finally { await page.close(); }
});

test('owner can assign independent local models without initiating or modifying agent runs', async () => {
  const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
  const writes = [];
  try {
    await mockApi(page);
    page.on('request', request => {
      if (request.method() === 'POST' &&
          new URL(request.url()).pathname.startsWith('/api/tasks/'))
        writes.push(new URL(request.url()).pathname);
    });
    await page.goto(baseUrl + '/tests/browser/studio.html');
    const show = page.getByRole('button', { name: 'Assign team models' });
    await show.click();
    const controls = page.getByRole('region', {
      name: 'Per-agent local model assignments',
    });
    const qaSelect = controls.getByRole('combobox', { name: 'QA Engineer model' });
    await qaSelect.waitFor();
    const engineerSelect = controls.getByRole('combobox', {
      name: 'Software Engineer model',
    });
    assert.equal(await qaSelect.inputValue(), '__default__');
    assert.equal(await engineerSelect.inputValue(), '__default__');
    await qaSelect.selectOption('qwen3-coder:30b');
    await controls.getByText('QA Engineer will use qwen3-coder:30b').waitFor();
    assert.equal(await qaSelect.inputValue(), 'qwen3-coder:30b');
    assert.equal(await engineerSelect.inputValue(), '__default__',
      'selecting a QA model must not change the Software Engineer model');
    // The running QA model stays pinned to its saved run, while the new-work
    // assignment changes independently in its inspector.
    const qaPresence = page.locator('aside.ca-studio-people')
      .getByRole('button', { name: /QA Engineer/ });
    assert.match(await qaPresence.textContent(), /Running · qwen3-coder:30b/);
    await qaPresence.click();
    await page.locator('.ca-studio-specialist-facts > strong').first()
      .getByText('qwen3-coder:30b', { exact: true }).waitFor();
    assert.deepEqual(writes, ['/api/tasks/' + id + '/agent-models/QaEngineer'],
      'model selection writes only the specific preference; never starts a run');
    await controls.getByRole('button', { name: 'Close models' }).click();
    assert.equal(await controls.count(), 0);
    assert.equal(await page.getByRole('region', {
      name: 'AI Engineering Studio',
    }).count(), 1);
  } finally { await page.close(); }
});

test('roster exposes model and saved status; agent selection opens actual specialist detail', async () => {
  const page = await browser.newPage({ viewport: { width: 1600, height: 900 } });
  try {
    await mockApi(page);
    await page.goto(baseUrl + '/tests/browser/studio.html');
    const roster = page.locator('aside.ca-studio-people');
    const engineer = roster.getByRole('button', { name: /Software Engineer/ });
    const qa = roster.getByRole('button', { name: /QA Engineer/ });
    await qa.waitFor();
    assert.match(await engineer.textContent(), /Next · qwen2.5-coder:14b/);
    assert.match(await qa.textContent(), /Running · qwen3-coder:30b/,
      'running job model must differ from the next-work selection');
    assert.equal(await roster.getByRole('button', { name: 'Models' }).count(), 1,
      'model selection is discoverable directly in the team roster');
    await qa.click();
    const context = page.getByRole('region', { name: 'Selected specialist' });
    await context.getByRole('heading', { name: 'QA Engineer' }).waitFor();
    assert.match(await context.textContent(), /MODEL FOR NEW WORK.*qwen2.5-coder:14b/s);
    assert.match(await context.textContent(), /MODEL PINNED TO ACTIVE WORK.*qwen3-coder:30b/s);
    assert.match(await context.textContent(), /Record reliable engineering work/);
    assert.match(await context.textContent(), /QA source review started/);
    assert.equal(await page.locator('#journey-panel-delivery').isVisible(), true,
      'a roster selection keeps the role stage visible in the central canvas');
    await context.getByText('Working style & background').click();
    assert.match(await context.textContent(), /fictional/i);
    await context.getByRole('button', { name: 'Change model' }).click();
    await page.getByRole('region', {
      name: 'Per-agent local model assignments',
    }).waitFor();
    assert.equal(await page.evaluate(() => document.scrollingElement?.scrollTop ?? 0), 0);
  } finally { await page.close(); }
});

test('activity view shows only actual saved handoffs, source and QA receipts', async () => {
  const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
  const writes = [];
  try {
    await mockApi(page);
    page.on('request', request => {
      if (request.method() === 'POST' &&
          new URL(request.url()).pathname.startsWith('/api/tasks/'))
        writes.push(new URL(request.url()).pathname);
    });
    await page.goto(baseUrl + '/tests/browser/studio.html');
    await page.locator('aside.ca-studio-people')
      .getByRole('button', { name: /QA Engineer/ }).waitFor();
    await page.getByRole('button', { name: 'Activity', exact: true }).click();
    const timeline = page.getByRole('region', { name: 'Recorded team activity' });
    await timeline.getByText('QA source review started').waitFor();
    const receipts = timeline.locator('ol.ca-studio-event-list > li');
    assert.equal(await receipts.count(), 3,
      'one source, one QA, one handoff persisted receipt; no generated dialogue');
    assert.match(await timeline.textContent(), /QA source review started/);
    assert.match(await timeline.textContent(), /Engineer saved a source revision/);
    assert.match(await timeline.textContent(), /Technical Lead → Software Engineer/);
    await receipts.filter({ hasText: 'Technical Lead → Software Engineer' })
      .getByRole('button', { name: 'Inspect work' }).click();
    await page.locator('#journey-panel-plan').waitFor({ state: 'visible' });
    assert.deepEqual(writes, [], 'inspecting saved activity must not mutate source or approvals');
    await page.getByRole('button', { name: /Attention/ }).click();
    await page.getByText('Repair cycle 6 stopped').waitFor();
  } finally { await page.close(); }
});

test('stale architecture shows a clear recovery action and never enables old choices', async () => {
  const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
  const writes = [];
  try {
    await mockApi(page, { architectureOutdated: true });
    page.on('request', request => {
      if (request.method() === 'POST' &&
          new URL(request.url()).pathname.startsWith('/api/tasks/'))
        writes.push({ path: new URL(request.url()).pathname,
          data: request.postDataJSON() });
    });
    await page.goto(baseUrl + '/tests/browser/studio.html');
    const canvas = page.getByRole('region', { name: 'Focused engineering work' });
    await canvas.getByRole('button', { name: /Solution Architecture/ }).first().click();
    await canvas.locator('#journey-panel-architecture').waitFor({ state: 'visible' });
    const recovery = canvas.locator('.architect-recovery');
    await recovery.getByText('2 architecture recommendations need refreshing').waitFor();
    assert.equal(await canvas.locator('fieldset.architect-options').count(), 0,
      'historical options must not look like selectable disabled controls');
    assert.equal(await canvas.locator('input[type="radio"][name^="architecture-"]').count(), 0);
    assert.equal(await canvas.locator('details.architect-archived').count(), 2);
    const stack = canvas.getByRole('button', {
      name: 'Refresh technology stack recommendations',
    });
    assert.equal(await stack.isEnabled(), true,
      'approved spec must offer a working refresh instead of a dead decision');
    // The previous stage may have left the center scrolled far down.
    await canvas.evaluate(el => { el.scrollTop = 340; });
    assert.ok(await canvas.evaluate(el => el.scrollTop) > 0);
    await page.locator('aside.ca-studio-people')
      .getByRole('button', { name: /Solution Architect/ }).click();
    assert.equal(await canvas.evaluate(el => el.scrollTop), 0,
      'choosing a role resets only the center canvas, without jumping the document');
    await stack.click();
    await canvas.getByText('Current approved requirements assessed').waitFor();
    assert.equal(await canvas.locator('fieldset.architect-options').count(), 1);
    assert.equal(await canvas.locator('fieldset.architect-options input[type="radio"]').count(), 2);
    assert.equal(await canvas.locator('details.architect-archived').count(), 1,
      'other stale topic remains historical until separately refreshed');
    assert.deepEqual(writes.map(w => w.path), [
      '/api/tasks/' + id + '/architecture-decisions/recommend',
    ], 'refresh requests a new proposal but never authorizes it');
    assert.equal(writes[0].data.specificationSha256, spec);
    assert.equal(await page.evaluate(() => document.scrollingElement?.scrollTop ?? 0), 0);
  } finally { await page.close(); }
});

test('unapproved specification offers enabled recovery navigation, not disabled architecture buttons', async () => {
  const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
  const writes = [];
  try {
    await mockApi(page, { architectureOutdated: true, specificationStatus: 'Draft' });
    page.on('request', request => {
      if (request.method() === 'POST' &&
          new URL(request.url()).pathname.startsWith('/api/tasks/'))
        writes.push(new URL(request.url()).pathname);
    });
    await page.goto(baseUrl + '/tests/browser/studio.html');
    const canvas = page.getByRole('region', { name: 'Focused engineering work' });
    await canvas.getByRole('button', { name: /Solution Architecture/ }).first().click();
    const recovery = canvas.locator('.architect-recovery');
    await recovery.getByText('The current specification needs approval').waitFor();
    assert.equal(await canvas.locator('fieldset.architect-options').count(), 0);
    await canvas.evaluate(el => { el.scrollTop = 350; });
    const open = recovery.getByRole('button', { name: 'Review current specification' });
    assert.equal(await open.isEnabled(), true);
    await open.click();
    await canvas.locator('#journey-panel-specification').waitFor({ state: 'visible' });
    assert.equal(await canvas.evaluate(el => el.scrollTop), 0,
      'returning to specification presents its current action from the top');
    assert.deepEqual(writes, [], 'navigation never approves old requirements');
  } finally { await page.close(); }
});

test('live activity separates real HTTP summaries from persisted agent work and pauses polling', async () => {
  const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
  let polls = 0;
  const writes = [];
  try {
    await mockApi(page);
    page.on('request', request => {
      const path = new URL(request.url()).pathname;
      if (path === '/api/studio/http-activity') polls++;
      if (request.method() === 'POST' && path.startsWith('/api/tasks/'))
        writes.push(path);
    });
    await page.goto(baseUrl + '/tests/browser/studio.html');
    const strip = page.getByRole('region', { name: 'Live Activity' });
    await strip.getByText('Local API', { exact: true }).waitFor();
    assert.match(await strip.textContent(),
      /GET \/api\/tasks\/\{id\}\/engineering-runs\/latest · 200 · 46 ms/);
    const show = strip.getByRole('button', { name: /Live Activity/ });
    await show.click();
    const traffic = strip.getByRole('region', {
      name: 'Authenticated API request summaries',
    });
    await traffic.getByText('/api/tasks/{id}/architecture-decisions').waitFor();
    assert.equal(await traffic.locator('ol > li').count(), 2);
    assert.match(await traffic.textContent(), /not that an agent finished work/);
    await strip.getByRole('button', { name: 'Agent work' }).click();
    const agent = strip.getByRole('region', { name: 'Persisted agent event summaries' });
    await agent.getByText('QA source review started').waitFor();
    assert.match(await agent.textContent(), /Engineer saved a source revision/);
    assert.match(await agent.textContent(), /Technical Lead → Software Engineer/);
    await strip.getByRole('button', { name: 'System traffic' }).click();
    await strip.getByRole('button', { name: 'Pause feed' }).click();
    const beforePause = polls;
    await page.waitForTimeout(2800);
    assert.equal(polls, beforePause, 'paused activity must stop HTTP requests');
    await strip.getByRole('button', { name: 'Resume feed' }).click();
    await page.waitForTimeout(500);
    assert.ok(polls > beforePause, 'resuming activity reconnects with saved cursor');
    assert.deepEqual(writes, [], 'observing traffic or agent work never mutates a project');
  } finally { await page.close(); }
});

test('narrow Studio has no page-level horizontal scrolling and keeps QA available', async () => {
  const page = await browser.newPage({ viewport: { width: 390, height: 820 },
    reducedMotion: 'reduce' });
  page.on('pageerror', error => console.error('Studio mobile page error:', error.stack));
  page.on('console', message => {
    if (message.type() === 'error') console.error('Studio mobile console:', message.text());
  });
  try {
    await mockApi(page);
    await page.goto(baseUrl + '/tests/browser/studio.html');
    await page.getByRole('heading', { name: 'Independent QA & repair' }).waitFor();
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth) <= 400,
      'narrow Studio must not force horizontal page scrolling');
    const team = page.locator('aside.ca-studio-people');
    await team.getByRole('button', { name: /QA Engineer/ }).waitFor();
    assert.equal(await team.getByRole('button', { name: /QA Engineer/ }).count(), 1);
    const context = page.locator('aside.ca-studio-context');
    const canvas = page.getByRole('region', { name: 'Focused engineering work' });
    const y1 = await canvas.boundingBox(), y2 = await context.boundingBox();
    assert.ok(y1 && y2 && y2.y >= y1.y + y1.height - 4,
      'mobile places attention after focused work');
  } finally { await page.close(); }
});
