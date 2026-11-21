// Windows-first, real Chromium behavior regression on TRUSTED fixtures only.
// This test does NOT run user/model-generated source or qualify Docker isolation.
import { before, after, test } from 'node:test';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

let server;
let browser;
let origin;
const fixture = 'tests/browser/fixtures/calculation-journal/';

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
    throw new Error('Calculator fixture must bind to IPv4 loopback only.');
  origin = 'http://127.0.0.1:' + address.port + '/';
  browser = await chromium.launch({ headless: true });
});
after(async () => {
  await browser?.close();
  await server?.close();
});

async function withFixture(file, verify) {
  const context = await browser.newContext();
  try {
    const page = await context.newPage();
    await page.goto(origin + fixture + file);
    await page.getByRole('heading', { name: /Calculation Journal/ }).waitFor();
    await verify(page);
  } finally {
    await context.close();
  }
}
async function result(page) {
  return (await page.getByRole('status', { name: 'Result' }).count()) > 0 ?
    page.getByRole('status', { name: 'Result' }).textContent() :
    page.locator('output#display').textContent();
}

test('frozen broken calculator looks rendered but is genuinely inert', async () => {
  await withFixture('broken.html', async page => {
    assert.equal(await page.locator('script[src]').count(), 0,
      'original failure must keep unlinked JavaScript');
    assert.equal(await page.locator('script').count(), 1,
      'original failure includes placeholder-only inline script');
    await page.getByRole('button', { name: '2' }).click();
    await page.getByRole('button', { name: '=' }).click();
    assert.equal(await result(page), '0',
      'negative-control buttons do not alter the display');
    assert.equal(await page.locator('button.number, #equals').count(), 0,
      'frozen broken script selectors do not match the HTML');
  });
});

test('working reference: mouse 2 + 3 = 5', async () => {
  await withFixture('working.html', async page => {
    await page.getByRole('button', { name: '2' }).click();
    await page.getByRole('button', { name: 'Add' }).click();
    await page.getByRole('button', { name: '3' }).click();
    await page.getByRole('button', { name: 'Equals' }).click();
    assert.equal(await result(page), '5');
  });
});

test('working reference: keyboard 12.5 / 2 = 6.25', async () => {
  await withFixture('working.html', async page => {
    await page.keyboard.type('12.5/2');
    await page.keyboard.press('Enter');
    assert.equal(await result(page), '6.25');
  });
});

test('working reference: decimal 0.1 + 0.2 = 0.3', async () => {
  await withFixture('working.html', async page => {
    await page.keyboard.type('0.1+0.2');
    await page.keyboard.press('Enter');
    assert.equal(await result(page), '0.3');
  });
});

test('working reference: keyboard Backspace and divide-by-zero', async () => {
  await withFixture('working.html', async page => {
    await page.keyboard.type('19');
    await page.keyboard.press('Backspace');
    assert.equal(await result(page), '1');
    await page.keyboard.type('2');
    assert.equal(await result(page), '12');
    await page.getByRole('button', { name: 'Clear' }).click();
    await page.keyboard.type('9/0');
    await page.keyboard.press('Enter');
    assert.equal(await result(page), 'Cannot divide by zero');
    await page.getByRole('button', { name: 'Clear' }).click();
    assert.equal(await result(page), '0');
  });
});

test('working reference: Back recall and History selection', async () => {
  await withFixture('working.html', async page => {
    await page.keyboard.type('8+2');
    await page.keyboard.press('Enter');
    assert.equal(await result(page), '10');
    await page.getByRole('button', { name: 'Clear' }).click();
    await page.getByRole('button', { name: 'Back recall' }).click();
    assert.equal(await result(page), '10');
    await page.getByRole('button', { name: 'Clear' }).click();
    await page.keyboard.type('7*3');
    await page.keyboard.press('Enter');
    assert.equal(await result(page), '21');
    await page.getByRole('button', { name: 'History' }).click();
    const history = page.getByRole('region', { name: 'Calculation history' });
    assert.equal(await history.isVisible(), true);
    assert.equal(await history.locator('li').count(), 2);
    await history.getByRole('button', { name: '8 + 2 = 10' }).click();
    assert.equal(await result(page), '10');
  });
});

test('working reference: saved history survives reload and browser restart', async () => {
  const userDataDir = await mkdtemp(join(tmpdir(), 'ca-calc-browser-'));
  let first;
  let second;
  try {
    first = await chromium.launchPersistentContext(userDataDir, {
      headless: true, viewport: { width: 1000, height: 760 },
    });
    const page = first.pages()[0] ?? await first.newPage();
    await page.goto(origin + fixture + 'working.html');
    await page.keyboard.type('2+3');
    await page.keyboard.press('Enter');
    assert.equal(await result(page), '5');
    await page.reload();
    await page.getByRole('button', { name: 'History' }).click();
    assert.equal(await page.locator('#history-list li').count(), 1);
    assert.match(await page.locator('#history-list').textContent(), /2 \+ 3 = 5/);
    await first.close();
    first = null;

    // A genuinely new Chromium process, not a copied storageState object.
    second = await chromium.launchPersistentContext(userDataDir, {
      headless: true, viewport: { width: 1000, height: 760 },
    });
    const reopened = second.pages()[0] ?? await second.newPage();
    await reopened.goto(origin + fixture + 'working.html');
    await reopened.getByRole('button', { name: 'History' }).click();
    assert.equal(await reopened.locator('#history-list li').count(), 1);
    await reopened.getByRole('button', { name: '2 + 3 = 5' }).click();
    assert.equal(await result(reopened), '5');
  } finally {
    await first?.close();
    await second?.close();
    await rm(userDataDir, { recursive: true, force: true,
      maxRetries: 5, retryDelay: 250 });
  }
});
