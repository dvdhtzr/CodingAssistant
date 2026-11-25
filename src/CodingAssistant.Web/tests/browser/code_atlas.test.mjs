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
  await page.getByRole('region', { name: 'Code Atlas', exact: true }).waitFor();
});
after(async () => {
  await page?.close();
  await browser?.close();
  await server?.close();
});

test('Code Atlas graph renders real source types and reveals source evidence on click', async () => {
  const atlas = page.getByRole('region', { name: 'Code Atlas', exact: true });
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
  const atlas = page.getByRole('region', { name: 'Code Atlas', exact: true });
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

test('bounded source search jumps to a saved symbol without extra scrolling', async () => {
  const searchPage = await browser.newPage({ viewport: { width: 1420, height: 970 } });
  try {
    await searchPage.goto(page.url());
    const atlas = searchPage.getByRole('region', { name: 'Code Atlas', exact: true });
    await atlas.getByRole('searchbox', { name: 'Find a symbol' }).fill('TaskService');
    const matches = atlas.getByRole('region', {
      name: 'Code Atlas matching symbols',
    });
    await matches.getByText(/matching source symbols/).waitFor();
    assert.ok(await matches.getByRole('button').count() <= 8);
    await matches.getByRole('button', {
      name: /Jump to Class: Demo.TaskService/,
    }).click();
    assert.equal(await matches.count(), 0);
    await atlas.getByRole('complementary', {
      name: 'Selected source entity',
    }).getByRole('heading', { name: 'TaskService' }).waitFor();
    assert.equal(await atlas.getByRole('checkbox', { name: 'Follow build' })
      .isChecked(), false);
  } finally {
    await searchPage.close();
  }
});

test('new persisted source creates method node and honest live indicator', async () => {
  const atlas = page.getByRole('region', { name: 'Code Atlas', exact: true });
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

test('live updates do not move existing nodes or hijack the graph camera', async () => {
  const calmPage = await browser.newPage({ viewport: { width: 1400, height: 980 } });
  try {
    await calmPage.goto(page.url());
    const atlas = calmPage.getByRole('region', { name: 'Code Atlas', exact: true });
    const graph = atlas.getByRole('group', {
      name: 'Interactive Code Atlas graph of saved source',
    });
    const follow = atlas.getByRole('checkbox', { name: 'Follow build' });
    assert.equal(await follow.isChecked(), false);
    await atlas.getByRole('checkbox', { name: 'Methods' }).check();
    const existing = graph.getByRole('button', { name: 'Class: Demo.TaskService' });
    const original = await existing.getAttribute('transform');
    const originalCamera = await graph.locator('g[transform]').first()
      .getAttribute('transform');
    await follow.check();
    await calmPage.getByRole('button', { name: 'Persist next source revision' }).click();
    await graph.getByRole('button', {
      name: 'Method: Demo.TaskService.CreateTask()',
    }).waitFor();
    assert.equal(await existing.getAttribute('transform'), original);
    assert.equal(await graph.locator('g[transform]').first()
      .getAttribute('transform'), originalCamera);
    assert.match(await atlas.getByRole('complementary',
      { name: 'Selected source entity' }).textContent(), /CreateTask/);
  } finally {
    await calmPage.close();
  }
});

test('engineering event refresh preserves the existing Code Atlas canvas', async () => {
  const probePage = await browser.newPage({ viewport: { width: 1400, height: 980 } });
  try {
    await probePage.goto(page.url());
    await probePage.getByRole('button', {
      name: 'Show engineering refresh probe',
    }).click();
    const section = probePage.getByRole('region', {
      name: 'Engineering refresh regression',
    });
    const receipt = section.getByTestId('refresh-receipt');
    const canvas = section.locator('[data-testid="refresh-probe-atlas"] .atlas-canvas');
    await canvas.waitFor();
    await probePage.evaluate(() => {
      window.__originalEngineeringCanvas = document.querySelector(
        '[data-testid="refresh-probe-atlas"] .atlas-canvas');
    });
    const first = Number((await receipt.textContent()).replace('Refresh #', ''));
    await section.getByRole('button', {
      name: 'Refresh engineering snapshot',
    }).click();
    await probePage.waitForFunction(value => {
      const receipt = document.querySelector('[data-testid="refresh-receipt"]');
      return Number((receipt?.textContent ?? '').replace('Refresh #', '')) > value;
    }, first);
    assert.equal(await probePage.evaluate(() =>
      window.__originalEngineeringCanvas?.isConnected &&
      window.__originalEngineeringCanvas === document.querySelector(
        '[data-testid="refresh-probe-atlas"] .atlas-canvas'),
    ), true, 'refresh must not unmount or replace the live graph canvas');
  } finally {
    await probePage.close();
  }
});

test('generation status indicators do not animate regardless of OS preferences', async () => {
  const calmPage = await browser.newPage();
  try {
    await calmPage.goto(page.url());
    const computed = await calmPage.evaluate(() => {
      const classes = ['activity-dot', 'activity-scan', 'ca-stage-live'];
      return classes.map(name => {
        const element = document.createElement('span');
        element.className = name;
        document.body.append(element);
        const animation = getComputedStyle(element).animationName;
        element.remove();
        return animation;
      });
    });
    assert.deepEqual(computed, ['none', 'none', 'none']);
  } finally {
    await calmPage.close();
  }
});

test('adding source keeps the graph viewport height steady, without page scroll anchoring', async () => {
  const steadyPage = await browser.newPage({ viewport: { width: 1400, height: 960 } });
  try {
    await steadyPage.goto(page.url());
    const atlas = steadyPage.getByRole('region', { name: 'Code Atlas', exact: true });
    const viewport = atlas.locator('.atlas-workspace');
    const before = await viewport.boundingBox();
    assert.ok(before && before.height >= 330, 'Atlas must reserve a stable viewing area');
    await steadyPage.getByRole('button', { name: 'Persist next source revision' }).click();
    await atlas.getByText('Recorded source changes (1)').waitFor();
    const after = await viewport.boundingBox();
    assert.ok(after && Math.abs(after.height - before.height) < 2,
      'new saved symbols must not change the graph area height');
    const properties = await steadyPage.evaluate(() => {
      const workspace = document.createElement('main');
      workspace.className = 'wb-main';
      document.body.appendChild(workspace);
      const properties = {
        anchor: getComputedStyle(workspace).overflowAnchor,
        animation: getComputedStyle(workspace).animationName,
      };
      workspace.remove();
      return properties;
    });
    assert.equal(properties.anchor, 'none');
    assert.equal(properties.animation, 'none');
  } finally {
    await steadyPage.close();
  }
});

test('manual graph exploration disables follow-build focus stealing', async () => {
  const atlas = page.getByRole('region', { name: 'Code Atlas', exact: true });
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
    const atlas = manual.getByRole('region', { name: 'Code Atlas', exact: true });
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
    const atlas = tracePage.getByRole('region', { name: 'Code Atlas', exact: true });
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
    const atlas = replayPage.getByRole('region', { name: 'Code Atlas', exact: true });
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


test('historical architecture drift compares pinned frames, without verification claims', async () => {
  const driftPage = await browser.newPage({ viewport: { width: 1420, height: 980 } });
  try {
    await driftPage.goto(page.url());
    const atlas = driftPage.getByRole('region', { name: 'Code Atlas', exact: true });
    const replay = atlas.getByRole('region', { name: 'Code Atlas Build Replay' });
    await replay.getByRole('button', { name: 'Previous' }).click();
    await replay.getByText('Historical work item #2').waitFor();
    await replay.getByRole('button', {
      name: 'Compare architecture with #1',
    }).click();
    const result = replay.getByRole('region', { name: 'Historical architecture drift' });
    await result.getByText(/1 added \/ 0 removed entities/).waitFor();
    assert.match(await result.textContent(), /Added Method.*CreateTask/);
    assert.match(await result.textContent(), /Not build or runtime verification/);
    await replay.getByRole('button', { name: 'Return live' }).click();
    assert.equal(await result.count(), 0);
  } finally {
    await driftPage.close();
  }
});

test('QA overlays show only unverified source-file hypotheses and repair attempts', async () => {
  const qaPage = await browser.newPage({ viewport: { width: 1400, height: 950 } });
  try {
    await qaPage.goto(page.url());
    const atlas = qaPage.getByRole('region', {
      name: 'Code Atlas', exact: true,
    });
    const graph = atlas.getByRole('group', {
      name: 'Interactive Code Atlas graph of saved source',
    });
    await graph.waitFor();
    const fileNode = graph.getByRole('button', {
      name: 'File: TaskService.cs',
    });
    await fileNode.click();
    const inspector = atlas.getByRole('complementary', {
      name: 'Selected source entity',
    });
    await inspector.getByRole('region', { name: 'Static QA findings' }).waitFor();
    const findings = inspector.getByRole('region', {
      name: 'Static QA findings',
    });
    assert.match(await findings.textContent(),
      /hypotheses, not test failures/);
    assert.match(await findings.textContent(),
      /Major: Persistence/);
    assert.match(await findings.textContent(),
      /independently test storage writes/);
    assert.match(await atlas.getByRole('status').allTextContents()
      .then(items => items.join(' ')), /Runtime tests: NOT RUN/);
    const repairs = inspector.getByRole('region', {
      name: 'Related repair attempts',
    });
    assert.match(await repairs.textContent(), /Generated/);
    assert.match(await repairs.textContent(), /unverified/);
    assert.ok(await graph.locator('.atlas-node.atlas-qa-flag').count() > 0);

    // A historical frame must not display current QA findings.
    const replay = atlas.getByRole('region', { name: 'Code Atlas Build Replay' });
    await replay.getByRole('button', { name: 'Previous' }).click();
    await replay.getByText('Historical work item #2').waitFor();
    await graph.getByRole('button', { name: 'File: TaskService.cs' }).click();
    assert.equal(await inspector.getByRole('region', {
      name: 'Static QA findings',
    }).count(), 0);
    assert.equal(await atlas.getByRole('status').filter({
      hasText: 'Runtime tests: NOT RUN',
    }).count(), 0);
  } finally {
    await qaPage.close();
  }
});
