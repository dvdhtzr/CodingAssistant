import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { createServer } from 'vite';

let server;
let browser;
let page;

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
  await page.goto('http://127.0.0.1:' + address.port + '/tests/browser/fixture.html');
  await page.getByRole('heading', { name: 'Oddville browser fixture' }).waitFor();
});

after(async () => {
  await page?.close();
  await browser?.close();
  await server?.close();
});

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
