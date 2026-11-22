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


test('manual exploration retains the chosen node while later source appears', async () => {
  const manual = await browser.newPage({ viewport: { width: 1340, height: 900 } });
  try {
    await manual.goto(page.url());
    const atlas = manual.getByRole('region', { name: 'Code Atlas' });
    const graph = atlas.getByRole('group', {
      name: 'Interactive Code Atlas graph of saved source',
    });
    await graph.getByRole('button', { name: 'Class: Demo.TaskService' }).click();
    assert.equal(await atlas.getByRole('checkbox', { name: 'Follow build' })
      .isChecked(), false);
    await manual.getByRole('button', { name: 'Persist next source revision' }).click();
    await atlas.getByText('Recorded source changes (1)').waitFor();
    assert.equal(await atlas.getByRole('complementary',
      { name: 'Selected source entity' }).getByRole('heading',
      { name: 'TaskService' }).isVisible(), true);
    assert.equal(await atlas.getByRole('checkbox', { name: 'Follow build' })
      .isChecked(), false);
    await graph.locator('.atlas-node.atlas-modified').first().waitFor();
  } finally {
    await manual.close();
  }
});


test('real browser traces a bounded path between two indexed symbols', async () => {
  const tracePage = await browser.newPage({ viewport: { width: 1400, height: 900 } });
  try {
    await tracePage.goto(page.url());
    const atlas = tracePage.getByRole('region', { name: 'Code Atlas' });
    const graph = atlas.getByRole('group', {
      name: 'Interactive Code Atlas graph of saved source',
    });
    await graph.getByRole('button', { name: 'Class: Demo.TaskService' }).click();
    const inspector = atlas.getByRole('complementary', {
      name: 'Selected source entity',
    });
    await inspector.getByRole('button', { name: 'Set path start' }).click();
    await atlas.getByRole('checkbox', { name: 'Methods' }).check();
    await tracePage.getByRole('button', {
      name: 'Persist next source revision',
    }).click();
    await graph.getByRole('button', {
      name: 'Method: Demo.TaskService.CreateTask()',
    }).click();
    await inspector.getByRole('button', { name: 'Trace path to this node' }).click();
    const pathResult = inspector.getByRole('region', {
      name: 'Dependency path result',
    });
    await pathResult.getByText('Directed path found').waitFor();
    assert.match(await pathResult.textContent(),
      /TaskService.*Contains.*CreateTask/);
    assert.ok(await graph.locator('line.atlas-traced').count() > 0);
  } finally {
    await tracePage.close();
  }
});


test('Build Replay shows historical graph without substituting current source contents', async () => {
  const replayPage = await browser.newPage({ viewport: { width: 1420, height: 980 } });
  try {
    await replayPage.goto(page.url());
    const atlas = replayPage.getByRole('region', { name: 'Code Atlas' });
    const graph = atlas.getByRole('group', {
      name: 'Interactive Code Atlas graph of saved source',
    });
    const replay = atlas.getByRole('region', { name: 'Code Atlas Build Replay' });
    await replay.getByText('Build Replay', { exact: true }).waitFor();

    await atlas.getByRole('checkbox', { name: 'Methods' }).check();
    await replayPage.getByRole('button', {
      name: 'Persist next source revision',
    }).click();
    await graph.getByRole('button', {
      name: 'Method: Demo.TaskService.CreateTask()',
    }).waitFor();
    // Move from live to the most recent historical checkpoint, then back
    // to the first work item before the method was written.
    await replay.getByRole('button', { name: 'Previous' }).click();
    await replay.getByText('Historical work item #2').waitFor();
    await replay.getByRole('button', { name: 'Previous' }).click();
    await replay.getByText('Historical work item #1').waitFor();
    assert.equal(await graph.getByRole('button', {
      name: 'Method: Demo.TaskService.CreateTask()',
    }).count(), 0);

    await graph.getByRole('button', { name: 'Class: Demo.TaskService' }).click();
    const inspector = atlas.getByRole('complementary', {
      name: 'Selected source entity',
    });
    assert.match(await inspector.textContent(),
      /Historical graph only/);
    assert.equal(await inspector.locator('.atlas-excerpt').count(), 0);
    assert.equal(await inspector.getByRole('button', {
      name: 'Set path start',
    }).count(), 0);

    await replay.getByRole('button', { name: 'Next' }).click();
    await replay.getByText('Historical work item #2').waitFor();
    await graph.getByRole('button', {
      name: 'Method: Demo.TaskService.CreateTask()',
    }).waitFor();

    await replay.getByRole('button', { name: 'Return live' }).click();
    await replay.getByText('Live saved source').waitFor();
    await graph.getByRole('button', { name: 'Class: Demo.TaskService' }).click();
    assert.match(await inspector.textContent(), /CreateTask/);
    assert.equal(await inspector.getByRole('button', {
      name: 'Set path start',
    }).isVisible(), true);
  } finally {
    await replayPage.close();
  }
});
