import { before, after, test } from 'node:test';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { createServer } from 'vite';

let server;
let browser;
let page;
let href;
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
    throw new Error('Missing trusted browser fixture');
  href = 'http://127.0.0.1:' + address.port +
    '/tests/browser/code_atlas_import.html';
  browser = await chromium.launch({ headless: true });
  page = await browser.newPage({ viewport: { width: 1420, height: 980 } });
  await page.goto(href);
  await page.getByRole('region', {
    name: 'Code Atlas GitHub repository import',
  }).waitFor();
});
after(async () => {
  await page?.close();
  await browser?.close();
  await server?.close();
});

test('only a commit-pinned public GitHub source is accepted by import UI', async () => {
  const section = page.getByRole('region', {
    name: 'Code Atlas GitHub repository import',
  });
  await section.getByLabel('GitHub repository URL').fill(
    'https://github.com/example/sample');
  await section.getByLabel('Full commit SHA').fill('main');
  await section.getByRole('button', { name: 'Import source' }).click();
  assert.match(await section.getByRole('alert').textContent(),
    /exact 40-character commit SHA/);
  assert.equal(await section.getByRole('button', {
    name: 'Open repository',
  }).count(), 0);
});

test('imported repo opens the actual Code Atlas graph and source inspector', async () => {
  const section = page.getByRole('region', {
    name: 'Code Atlas GitHub repository import',
  });
  await section.getByLabel('Full commit SHA').fill('a'.repeat(40));
  await section.getByRole('button', { name: 'Import source' }).click();
  const atlas = section.getByRole('region', {
    name: 'Code Atlas', exact: true,
  });
  await atlas.getByText('Explore the imported architecture').waitFor();
  assert.match(await atlas.textContent(), /Commit-pinned GitHub source/);
  const graph = atlas.getByRole('group', {
    name: 'Interactive Code Atlas graph of saved source',
  });
  await graph.getByRole('button', { name: 'Class: Demo.Order' }).click();
  const inspector = atlas.getByRole('complementary', {
    name: 'Selected source entity',
  });
  await inspector.getByRole('heading', { name: 'Order' }).waitFor();
  await inspector.getByText('src/Order.cs:2').waitFor();
  await inspector.getByText(/class Order/).waitFor();
  assert.equal(await atlas.getByRole('checkbox', {
    name: 'Follow build',
  }).count(), 0);
  assert.equal(await atlas.getByRole('region', {
    name: 'Code Atlas Build Replay',
  }).count(), 0);
});

test('imported method dependency trace stays inside the selected commit', async () => {
  const section = page.getByRole('region', {
    name: 'Code Atlas GitHub repository import',
  });
  const atlas = section.getByRole('region', {
    name: 'Code Atlas', exact: true,
  });
  const graph = atlas.getByRole('group', {
    name: 'Interactive Code Atlas graph of saved source',
  });
  await graph.getByRole('button', { name: 'Class: Demo.Order' }).click();
  const inspector = atlas.getByRole('complementary', {
    name: 'Selected source entity',
  });
  await inspector.getByRole('button', { name: 'Set path start' }).click();
  await atlas.getByRole('checkbox', { name: 'Methods' }).check();
  await graph.getByRole('button', {
    name: 'Method: Demo.Order.Place()',
  }).click();
  await inspector.getByRole('button', {
    name: 'Trace path to this node',
  }).click();
  const result = inspector.getByRole('region', {
    name: 'Dependency path result',
  });
  await result.getByText('Directed path found').waitFor();
  assert.match(await result.textContent(), /Order.*Contains.*Place/);
  assert.ok(await graph.locator('line.atlas-traced').count() > 0);
});

test('imported repository can be reopened after view refresh', async () => {
  await page.reload();
  const section = page.getByRole('region', {
    name: 'Code Atlas GitHub repository import',
  });
  await section.getByRole('button', {
    name: /example\/sample/,
  }).click();
  const atlas = section.getByRole('region', {
    name: 'Code Atlas', exact: true,
  });
  await atlas.getByRole('group', {
    name: 'Interactive Code Atlas graph of saved source',
  }).getByRole('button', {
    name: 'Class: Demo.Order',
  }).waitFor();
  assert.match(await atlas.textContent(), /STATIC_SEMANTIC|Static C# relationships/);
});
