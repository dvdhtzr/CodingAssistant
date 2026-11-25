// Windows-first, real Chromium behavior regression on TRUSTED fixtures only.
// This test does NOT run user/model-generated source or qualify Docker isolation.
import { before, after, test } from 'node:test';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { CALCULATION_CASES,evaluateCalculationJournal } from '../../../../tools/m4/calculation_journal_contract.mjs';

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
    // Vite may inject its own development client while serving the trusted
    // fixture. Assert only the APPLICATION script is absent, not the server's.
    const sources = await page.locator('script[src]').evaluateAll(elements =>
      elements.map(element => element.getAttribute('src')));
    assert.ok(!sources.some(source => source?.includes('broken.js')),
      'original application JavaScript must remain unlinked');
    const inlineScripts = await page.locator('script:not([src])').allTextContents();
    assert.ok(inlineScripts.some(source =>
      source.includes('TODO connect calculator handlers')),
      'original failure includes placeholder-only inline application script');
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

// AET-8 trusted CI only: reuse the exact same outside-page evaluator on
// positive and negative inputs. No application-supplied receipt is trusted.
function trustedDriver(file,spoof=false){
  return async()=>{
    const dir=await mkdtemp(join(tmpdir(),'ca-aet8-calc-'));
    let context;
    const url=origin+fixture+file;
    async function start(){
      context=await chromium.launchPersistentContext(dir,{headless:true,viewport:{width:1000,height:760}});
      if(spoof)await context.route('**/working.js',route=>route.fulfill({
        contentType:'application/javascript',
        body:'document.body.dataset.verified="true";window.verification={status:"VERIFIED",passed:7};'
      }));
      const page=context.pages()[0]??await context.newPage();
      page.setDefaultTimeout(3000);
      await page.goto(url);
      return page;
    }
    async function close(){
      try{await context?.close();}
      finally{await rm(dir,{recursive:true,force:true,maxRetries:5,retryDelay:250});}
    }
    try{
      const page=await start();
      return {page,close,restart:async()=>{await context.close();context=null;return await start();}};
    }catch(e){await close();throw e;}
  };
}
test('AET-8 host-controlled functional evaluator accepts working trusted application',async()=>{
  const report=await evaluateCalculationJournal(trustedDriver('working.html'));
  assert.deepEqual(Object.keys(report.checks),CALCULATION_CASES);
  assert.equal(report.passed,7,JSON.stringify(report));
  assert.equal(report.required,7);
  assert.equal(report.generatedArtifactVerified,false);
  assert.equal(report.runtimeQualified,false);
});
test('AET-8 identical evaluator rejects inert and forged success applications',async()=>{
  // The spoof uses the real working markup but replaces the application
  // script with a frozen false success claim. No generated source is loaded.
  for(const driver of [trustedDriver('broken.html'),trustedDriver('working.html',true)]){
    const report=await evaluateCalculationJournal(driver);
    assert.deepEqual(Object.keys(report.checks),CALCULATION_CASES);
    assert.equal(report.passed,0,JSON.stringify(report));
    assert.equal(report.generatedArtifactVerified,false);
    assert.equal(report.runtimeQualified,false);
  }
});
