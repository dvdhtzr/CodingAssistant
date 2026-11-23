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

function mockApi(page) {
  return page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname;
    const method = route.request().method();
    if (method !== 'GET') return done(route, { error: 'UNEXPECTED_MUTATION' }, 405);
    if (path === '/api/agents/roster') return done(route, { agents });
    if (path.endsWith('/agent-handoffs')) return done(route, { events: [{
      kind: 'EngineeringHandoff', fromAgent: 'TechnicalLead',
      toAgent: 'SoftwareEngineer', createdUtc: time, current: true,
      specificationVersion: 1, revision: 1, model: 'qwen3-coder:30b',
    }] });
    if (path.endsWith('/engineering-runs/latest'))
      return done(route, { snapshot: engineering });
    if (path.endsWith('/qa-reviews/latest')) return done(route, { snapshot: qa });
    if (path.endsWith('/repair-cycles/latest'))
      return done(route, { snapshot: repairCycle });
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
        sha256: spec, status: 'Approved', createdUtc: time, approvedUtc: time },
    });
    if (path === '/api/models') return done(route, { models: ['qwen3-coder:30b'] });
    if (path === '/api/runtime-profiles') return done(route, { profiles: [] });
    if (path.endsWith('/architecture-decisions')) return done(route, {
      decisions: ['technology-stack', 'architecture-style'].map(topic => ({
        topic, revision: 1, specificationSha256: spec, specificationCurrent: true,
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
    await disclos.locator('summary').click();
    assert.match(await disclos.textContent(), /Generated source/);
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
