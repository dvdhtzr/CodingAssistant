import { before, after, test } from 'node:test';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { createServer } from 'vite';

let server;
let browser;
let page;
before(async () => {
  server = await createServer({
    configFile: false, root: process.cwd(),
    server: { host: '127.0.0.1', port: 0, strictPort: false },
    appType: 'spa',
  });
  await server.listen();
  const addr = server.httpServer?.address();
  if (!addr || typeof addr === 'string') throw new Error('Missing loopback fixture');
  browser = await chromium.launch({ headless: true });
  page = await browser.newPage({ viewport: { width: 1400, height: 980 } });
  await page.goto('http://127.0.0.1:' + addr.port +
    '/tests/browser/code_atlas.html');
  await page.getByRole('region', { name: 'Code Atlas' }).waitFor();
});
after(async () => {
  await page?.close();
  await browser?.close();
  await server?.close();
});

test('Code Atlas graph renders real source types and reveals source evidence on click', async () => {
  const atlas = page.getByRole('region', { name: 'Code Atlas' });
  const graph = atlas.getByRole('group', {
    name: 'Interactive Code Atlas graph of saved source',
  });
  await graph.waitFor();
  const entry = graph.getByRole('button', { name: 'Class: Demo.TaskService' });
  await entry.click();
  const inspector = atlas.getByRole('complementary', {
    name: 'Selected source entity',
  });
  assert.equal(await inspector.getByRole('heading', { name: 'TaskService' })
    .isVisible(), true);
  assert.match(await inspector.textContent(), /TaskService\.cs:2/);
  assert.match(await inspector.textContent(), /class TaskService/);
  assert.match(await inspector.textContent(), /Known relationships/);
  assert.equal(await atlas.getByText('Not compiled, executed, or verified.')
    .isVisible(), true);
});

test('Code Atlas search and mouse zoom are functional, not inert decoration', async () => {
  const atlas = page.getByRole('region', { name: 'Code Atlas' });
  await atlas.getByRole('searchbox', { name: 'Find a symbol' }).fill('Demo.TaskService');
  assert.equal(await atlas.getByRole('complementary',
    { name: 'Selected source entity' }).getByRole('heading',
    { name: 'TaskService' }).isVisible(), true);
  await atlas.getByRole('button', { name: 'Focus relationships' }).click();
  await atlas.getByRole('button', { name: 'Zoom in' }).click();
  const svg = atlas.locator('.atlas-canvas g[transform]').first();
  assert.match(await svg.getAttribute('transform'), /scale\(1\.2\)/);
  await atlas.getByRole('button', { name: 'Reset graph position' }).click();
  assert.match(await svg.getAttribute('transform'), /scale\(1\)/);
});

test('new persisted source creates method node and honest live indicator', async () => {
  const atlas = page.getByRole('region', { name: 'Code Atlas' });
  await atlas.getByRole('button', { name: 'Overview' }).click();
  await atlas.getByRole('checkbox', { name: 'Methods' }).check();
  await atlas.getByRole('checkbox', { name: 'Follow build' }).check();
  await page.getByRole('button', { name: 'Persist next source revision' }).click();
  await atlas.getByRole('group', {
    name: 'Interactive Code Atlas graph of saved source',
  }).getByRole('button', {
    name: 'Method: Demo.TaskService.CreateTask()',
  }).waitFor();
  assert.match(await atlas.textContent(), /1 newly discovered/);
  await atlas.getByText('Recorded source changes (1)').waitFor();
  await atlas.locator('details.atlas-source-history summary').click();
  assert.match(await atlas.getByRole('list', {
    name: 'Committed Code Atlas source events',
  }).textContent(), /#1.*W2.*Modified: TaskService.cs/s);
  assert.match(await atlas.getByRole('complementary',
    { name: 'Selected source entity' }).textContent(), /CreateTask/);
  assert.match(await atlas.textContent(), /Saved snapshot/);
  assert.doesNotMatch(await atlas.textContent(), /VERIFIED BUILD/);
});

test('manual graph exploration disables follow-build focus stealing', async () => {
  const atlas = page.getByRole('region', { name: 'Code Atlas' });
  await atlas.getByRole('checkbox', { name: 'Follow build' }).check();
  await atlas.getByRole('group', {
    name: 'Interactive Code Atlas graph of saved source',
  }).getByRole('button', { name: 'Class: Demo.TaskService' }).click();
  assert.equal(await atlas.getByRole('checkbox', { name: 'Follow build' })
    .isChecked(), false);
});
