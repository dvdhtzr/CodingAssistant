import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { createServer } from 'vite';

let server;
let browser;
let page;
let fixtureBaseUrl;

before(async () => {
  server = await createServer({
    configFile: false,
    root: process.cwd(),
    server: { host: '127.0.0.1', port: 0, strictPort: false },
    appType: 'spa',
  });
  await server.listen();
  const address = server.httpServer?.address();
  if (!address || typeof address === 'string')
    throw new Error('Vite browser fixture failed to bind on IPv4 loopback.');
  browser = await chromium.launch({ headless: true });
  page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
  fixtureBaseUrl = 'http://127.0.0.1:' + address.port;
  await page.goto(fixtureBaseUrl + '/tests/browser/fixture.html');
  await page.getByRole('heading', { name: 'Oddville browser fixture' }).waitFor();
});

after(async () => {
  await page?.close();
  await browser?.close();
  await server?.close();
});

test('Team Console is a compact stable agent workspace with contextual inspection', async () => {
  const consolePage = await browser.newPage({
    viewport: { width: 1440, height: 900 },
    reducedMotion: 'reduce',
  });
  try {
    await consolePage.goto(fixtureBaseUrl +
      '/tests/browser/fixture.html?compact-command-acceptance');
    const work = consolePage.getByRole('region', { name: 'Team Console' });
    await work.getByRole('heading', { name: 'Agent Activity Board' }).waitFor();
    assert.equal(await work.locator('.ca-node').count(), 5);
    assert.match(await work.getByRole('region', {
      name: 'Agent collaboration board',
    }).textContent(), /TechnicalLead.*SoftwareEngineer/s);
    const floor = await work.locator('.ca-console-floor').boundingBox();
    const side = await work.locator('.ca-console-side').boundingBox();
    const whole = await work.boundingBox();
    assert.ok(floor && side && whole && side.x > floor.x + floor.width - 3 &&
      Math.abs(side.y - floor.y) < 5,
    'agent floor and owner attention must be adjacent columns on desktop');
    assert.ok(whole.height < 1000, 'Command should not be an unbounded long panel');
    assert.equal(await consolePage.locator('.ca-console-crew .ca-node.ca-running')
      .count(), 1);
    const animation = await consolePage.locator(
      '.ca-console-crew .ca-node.ca-running .ca-orb').evaluate(el =>
      getComputedStyle(el, '::before').animationName);
    assert.equal(animation, 'none', 'active agent indication must not pulse');
    const inspector = work.getByRole('button', {
      name: /Inspect Software Engineer: Source run: Running/,
    });
    await inspector.click();
    await consolePage.getByRole('dialog').waitFor();
    await consolePage.keyboard.press('Escape');
    assert.equal(await consolePage.getByRole('dialog').count(), 0);
    await consolePage.setViewportSize({ width: 390, height: 820 });
    const mobileFloor = await work.locator('.ca-console-floor').boundingBox();
    const mobileSide = await work.locator('.ca-console-side').boundingBox();
    assert.ok(mobileFloor && mobileSide &&
      mobileSide.y >= mobileFloor.y + mobileFloor.height - 4,
    'narrow screen stacks the attention panel below agent board');
    assert.ok(await consolePage.evaluate(() => document.documentElement.scrollWidth) <= 400,
      'compact console must not create horizontal document scrolling');
  } finally {
    await consolePage.close();
  }
});

for (const outcome of ['success', 'failure']) {
  test('completed 27-message interview specification button ' + outcome, async () => {
    const acceptancePage = await browser.newPage({
      viewport: { width: 1440, height: 900 },
    });
    let draftRequests = 0;
    let requestedDraftModel = '';
    const now = '2026-11-22T17:00:00Z';
    const messages = Array.from({ length: 27 }, (_, i) => ({
      sequence: i + 1,
      role: i % 2 ? 'owner' : 'assistant',
      content: i === 26 ? 'Your final answer has been saved. Product discovery is complete.'
        : i % 2 ? 'Owner decision ' + ((i + 1) / 2) + ': use podcast platform audience data.'
          : 'Which podcast metrics do you need?',
      createdUtc: now, choice: null,
    }));
    const snapshot = {
      taskId: 'a'.repeat(32), model: 'qwen3:30b', lastSequence: 27,
      readyForSpecification: true, messages, specification: null,
    };
    const respond = (route, status, body) => route.fulfill({
      status, contentType: 'application/json', body: JSON.stringify(body),
    });
    try {
      await acceptancePage.route('**/api/**', async route => {
        const url = new URL(route.request().url()).pathname;
        const method = route.request().method();
        if (method === 'GET' && url.endsWith('/interview'))
          return respond(route, 200, snapshot);
        if (method === 'GET' && url === '/api/models')
          return respond(route, 200, { models: ['qwen3:30b', 'qwen3-coder:30b'] });
        if (method === 'POST' && url.endsWith('/interview/specification')) {
          draftRequests++;
          requestedDraftModel = route.request().postDataJSON().model;
          if (outcome === 'failure')
            return respond(route, 400, { error: 'INVALID_INPUT' });
          return respond(route, 200, {
            ...snapshot, specification: {
              version: 1, model: 'qwen3-coder:30b',
              content: '# Podcast analytics specification\\n' +
                'The owner requests aggregated demographic data from podcast ' +
                'platforms for selected shows. Accept a set of shows, display ' +
                'the combined audience results, and preserve the limits of ' +
                'available data. Define transparent acceptance criteria.',
              sha256: 'b'.repeat(64), status: 'Draft',
              createdUtc: now, approvedUtc: null,
            },
          });
        }
        return respond(route, 404, { error: 'NOT_FOUND' });
      });
      await acceptancePage.goto(fixtureBaseUrl +
        '/tests/browser/fixture.html?specification-acceptance');
      const completion = acceptancePage.getByRole('button', {
        name: 'Continue to specification',
      });
      await completion.waitFor();
      await completion.click();
      const active = acceptancePage.locator('#journey-panel-specification');
      const modelSelect = active.getByLabel('Local model for this specification');
      await modelSelect.waitFor();
      assert.equal(await modelSelect.inputValue(), 'qwen3:30b');
      await active.getByText('This Qwen 3 model can continue reasoning').waitFor();
      await modelSelect.selectOption('qwen3-coder:30b');
      assert.equal(await active.getByText('This Qwen 3 model can continue reasoning').count(), 0);
      await active.getByRole('button', { name: 'Draft detailed specification' })
        .click();
      if (outcome === 'failure') {
        const alert = active.getByRole('alert');
        await alert.waitFor();
        assert.match(await alert.textContent(), /saved interview could not be processed/);
      } else {
        await active.getByText('Specification v1').waitFor();
        await active.getByText('Drafted with qwen3-coder:30b').waitFor();
        assert.equal(await active.getByRole('checkbox', {
          name: /reviewed this specification/,
        }).isChecked(), false);
        assert.equal(await active.getByRole('button', {
          name: 'Approve this specification',
        }).isDisabled(), true);
      }
      assert.equal(draftRequests, 1,
        'visible action must send exactly one specification request');
      assert.equal(requestedDraftModel, 'qwen3-coder:30b',
        'the API receives only the explicitly selected installed drafting model');
    } finally {
      await acceptancePage.close();
    }
  });
}

test('newest saved Product Owner question stays visible and the full prior history is accessible', async () => {
  const workroom = page.locator('.conversation-workroom');
  assert.equal(await workroom.getByText('How should Oddville residents remember each other?', {
    exact: false,
  }).count(), 1);
  assert.equal(await page.locator('.conversation-current').isVisible(), true);
  const details = page.locator('details.conversation-history');
  assert.equal(await details.getAttribute('open'), null);
  assert.equal(await details.locator('li').count(), 12);
  await details.locator('summary').click();
  assert.equal(await details.locator('ol').isVisible(), true);
  const bounds = await details.locator('ol').evaluate(el => ({
    height: el.clientHeight, scrollHeight: el.scrollHeight,
  }));
  assert.ok(bounds.scrollHeight > bounds.height, 'long history should scroll independently');
  await details.locator('summary').click();
  assert.equal(await details.locator('ol').isVisible(), false);
});

test('journey navigation does not lose unsent checkbox and Something else feedback', async () => {
  const checkbox = page.getByRole('checkbox', { name: /Persistent memories/ });
  await checkbox.check();
  await page.getByPlaceholder('Type your own answer…').fill('Remember grudges for two days');
  const spec = page.getByRole('button', { name: /^Open Specification:/ });
  await spec.focus();
  await page.keyboard.press('Enter');
  assert.equal(await page.locator('#journey-panel-specification').isVisible(), true);
  assert.equal(await page.locator('#journey-panel-discovery').isVisible(), false);
  assert.equal(await spec.getAttribute('aria-current'), 'step');

  await page.getByRole('button', { name: /^Open Product Discovery:/ }).click();
  assert.equal(await page.locator('#journey-panel-discovery').isVisible(), true);
  assert.equal(await checkbox.isChecked(), true);
  assert.equal(await page.getByPlaceholder('Type your own answer…').inputValue(),
    'Remember grudges for two days');
  await page.getByRole('button', { name: /^Send answer/ }).click();
  const answer = await page.getByRole('status', { name: 'Last submitted answer' }).textContent();
  // Output is only the local fixture's preview; it is not saved to a product API.
  assert.match(answer ?? '', /Selected options: Persistent memories/);
  assert.match(answer ?? '', /Something else: Remember grudges for two days/);
});

test('all six stage controls are keyboard-focusable and link to real panels', async () => {
  const nav = page.getByRole('navigation', { name: 'Jump to a project stage' });
  const buttons = nav.getByRole('button');
  assert.equal(await buttons.count(), 6);
  const ids = ['discovery', 'specification', 'architecture', 'plan', 'generation', 'delivery'];
  for (let i = 0; i < ids.length; i++) {
    assert.equal(await buttons.nth(i).getAttribute('aria-controls'),
      'journey-panel-' + ids[i]);
  }
});

test('owner inbox action navigates to original architecture review stage without approval', async () => {
  const inbox = page.getByRole('region', { name: 'Owner decision inbox' });
  assert.match(await inbox.textContent(), /Review technology stack v1/);
  await inbox.getByRole('button', { name: 'Open Solution Architecture' }).click();
  assert.equal(await page.locator('#journey-panel-architecture').isVisible(), true);
  assert.equal(await page.getByRole('button', { name: /^Open Solution Architecture:/ })
    .getAttribute('aria-current'), 'step');
  assert.match(await inbox.textContent(), /read-only navigation/);
  assert.equal(await inbox.getByRole('button', { name: /Approve/i }).count(), 0);
});

test('source explorer browses nested persisted files and preserves selection through search', async () => {
  const explorer = page.getByRole('region', { name: 'MAF Software Engineer saved source explorer' });
  const fileSearch = explorer.getByRole('searchbox', { name: 'FIND SAVED FILE' });
  assert.equal(await explorer.getByText('4 of 4 saved files').isVisible(), true);
  assert.equal(await explorer.getByRole('region', { name: /Source content: README.md/ }).isVisible(), true);
  await fileSearch.fill('resident');
  assert.equal(await explorer.getByText('1 of 4 saved files').isVisible(), true);
  const resident = explorer.getByRole('button', { name: 'Resident.cs' });
  await resident.click();
  assert.equal(await explorer.getByRole('region',
    { name: 'Source content: src/Models/Resident.cs' }).isVisible(), true);
  assert.match(await explorer.textContent(), /Task 1: Build residents \(T1\)/);
  assert.match(await explorer.textContent(), /Resident domain/);
  assert.match(await explorer.textContent(), /bbbbbbbbbbbb/);
  await explorer.getByRole('button', { name: 'Clear search' }).click();
  assert.equal(await resident.getAttribute('aria-pressed'), 'true');
  assert.equal(await explorer.getByText('4 of 4 saved files').isVisible(), true);
});

test('source preview escapes untrusted source and supports full large-file inspection', async () => {
  const explorer = page.getByRole('region', { name: 'MAF Software Engineer saved source explorer' });
  const fileSearch = explorer.getByRole('searchbox', { name: 'FIND SAVED FILE' });
  await fileSearch.fill('overview.tsx');
  await explorer.getByRole('button', { name: 'Overview.tsx' }).click();
  const preview = explorer.getByRole('region', { name: 'Source content: src/Views/Overview.tsx' });
  assert.match(await preview.textContent(), /<script>alert\(1\)<\/script>/);
  assert.equal(await explorer.locator('script').count(), 0);
  await fileSearch.fill('longpayload');
  await explorer.getByRole('button', { name: 'LongPayload.ts' }).click();
  assert.equal(await explorer.getByRole('button', { name: 'Show complete file' }).isVisible(), true);
  assert.match(await explorer.textContent(), /Displaying the first 750 lines/);
  assert.equal(await explorer.getByText('const n809 = 809;').count(), 0);
  await explorer.getByRole('button', { name: 'Show complete file' }).click();
  assert.match(await explorer.getByRole('region',
    { name: 'Source content: src/LongPayload.ts' }).textContent(), /const n809 = 809;/);
  await explorer.getByRole('button', { name: 'Return to numbered preview' }).click();
  assert.equal(await explorer.getByText('const n809 = 809;').count(), 0);
});

test('MAF team workroom changes specialist without changing owner approval state', async () => {
  const team = page.getByRole('region', { name: 'Project agent team workroom' });
  const roster = team.getByRole('group', { name: 'Select an engineering specialist' });
  assert.equal(await roster.getByRole('button').count(), 5);
  assert.equal(await roster.getByRole('button', { name: /Product Owner/ })
    .getAttribute('aria-pressed'), 'true');
  assert.match(await team.textContent(), /SpecificationHandoff/);
  assert.match(await team.textContent(), /Historical binding/);
  assert.match(await team.textContent(), /product-discovery/);
  assert.match(await team.textContent(), /not enabled/);

  await roster.getByRole('button', { name: /Software Engineer/ }).click();
  assert.equal(await roster.getByRole('button', { name: /Software Engineer/ })
    .getAttribute('aria-pressed'), 'true');
  assert.match(await team.textContent(), /1 of 2 saved tasks completed/);
  assert.match(await team.textContent(), /Build residents/);
  assert.match(await team.textContent(), /Source saved/);
  assert.match(await team.textContent(), /Engineering run: Generated/);
  assert.match(await team.textContent(), /source-implementation/);
  assert.match(await team.textContent(), /not enabled/);

  await team.getByRole('button', { name: 'Open Software Engineer stage' }).click();
  assert.equal(await page.locator('#journey-panel-generation').isVisible(), true);
  assert.equal(await page.getByRole('button', { name: /^Open Source Generation:/ })
    .getAttribute('aria-current'), 'step');
  assert.equal(await team.getByRole('button', { name: /Approve/i }).count(), 0);
});

test('QA role shows independent static report as historical, not verified runtime', async () => {
  const team = page.getByRole('region', { name: 'Project agent team workroom' });
  await team.getByRole('button', { name: /QA Engineer/ }).click();
  assert.match(await team.textContent(), /Static review Reviewed/);
  assert.match(await team.textContent(), /Historical QA review/);
  assert.match(await team.textContent(), /No build, test, or VERIFIED claim/);
  assert.match(await team.textContent(), /static-code-review/);
  assert.equal(await team.getByRole('button', { name: 'Open Artifacts & Delivery' }).count(), 1);
});

test('UX-6 renders stored source and static QA activity without invented verification', async () => {
  const ledger = page.getByRole('region',
    { name: 'Qualified execution and verification timeline' });
  assert.equal(await ledger.isVisible(), true);
  assert.match(await ledger.textContent(), /GENERATED · NOT TESTED/);
  assert.match(await ledger.textContent(), /UNQUALIFIED/);
  assert.match(await ledger.textContent(), /SourceFileSaved/);
  assert.match(await ledger.textContent(), /QaReviewAdmitted/);
  assert.match(await ledger.textContent(), /Build, test, and browser outcomes: NOT TESTED/);
  assert.equal(await ledger.getByRole('list', {
    name: 'Recorded engineering and QA evidence events' }).locator('li').count(), 2);
  assert.equal(await ledger.getByRole('button', { name: /Run verification/i }).count(), 0);
  assert.match(await ledger.textContent(), /historical binding/);
});

test('UX-6B source preflight exposes concrete defects without executable controls', async () => {
  const preflight = page.getByRole('region', { name: 'Static Web source preflight' });
  assert.equal(await preflight.isVisible(), true);
  assert.match(await preflight.textContent(), /NO_LINKED_JAVASCRIPT/);
  assert.match(await preflight.textContent(), /GENERATED_NOT_TESTED|not execute JavaScript/);
  assert.equal(await preflight.getByRole('button').count(), 0);
  assert.match(await preflight.textContent(), /runtime evaluator remains unqualified/);
});


test('M4.6 real Chromium shows candidate scope separately from unqualified execution', async () => {
  const scope = page.locator('.verification-scope[data-state="candidate"]');
  assert.equal(await scope.isVisible(), true);
  assert.match(await scope.textContent(), /Scope candidate/);
  assert.match(await scope.textContent(), /Generated · not tested/);
  assert.match(await scope.textContent(), /Execution DISABLED/);
  assert.match(await scope.textContent(), /untrusted Static Web executor remains unqualified/);
  assert.equal(await scope.locator('button').count(), 0);
  const manifest = scope.locator('details.verification-scope-manifest');
  assert.equal(await manifest.getAttribute('open'), null);
  await manifest.locator('summary').click();
  assert.match(await manifest.textContent(), /index\.html/);
  assert.match(await manifest.textContent(), /app\.js/);
  await manifest.locator('summary').click();
  assert.equal(await manifest.getAttribute('open'), null);
  const fingerprints=scope.locator('details.verification-scope-binding');
  await fingerprints.locator('summary').click();
  assert.match(await fingerprints.textContent(), /fingerprints, not authorization tokens/);
});
test('M4.6 real Chromium keeps rejected scope visible without any run action', async () => {
  const scope = page.locator('.verification-scope[data-state="blocked"]');
  assert.equal(await scope.isVisible(), true);
  assert.match(await scope.textContent(), /Needs attention/);
  assert.match(await scope.textContent(), /MISSING ROOT INDEX HTML/);
  assert.match(await scope.textContent(), /SOURCE FILE HASH MISMATCH/);
  assert.match(await scope.textContent(), /needs a root index\.html/);
  assert.equal(await scope.getByRole('button').count(), 0);
  assert.doesNotMatch(await scope.textContent(), /Scope candidate/);
});
