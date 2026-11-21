import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let JourneyStage;
let JourneyStatusStrip;
let restoreJourneyStage;
let toggleJourneyStage;
let steps;
let InterviewStudio;

const task = {
  id: 'a'.repeat(32),
  title: 'Long-running Project',
  objective: 'Build a local-first tool with real approvals.',
  status: 'Active',
  currentVersion: 1,
  createdUtc: '2026-11-20T12:00:00Z',
};

before(async () => {
  vite = await createServer({
    configFile: false,
    root: process.cwd(),
    server: { middlewareMode: true },
    appType: 'custom',
  });
  const journey = await vite.ssrLoadModule('/src/ProjectJourney.tsx');
  ({ JourneyStage, JourneyStatusStrip, restoreJourneyStage, toggleJourneyStage } = journey);
  steps = journey.journeySteps;
  ({ InterviewStudio } = await vite.ssrLoadModule('/src/InterviewStudio.tsx'));
});

after(async () => { if (vite) await vite.close(); });

test('the six stages have stable identities, order and genuine process labels', () => {
  assert.deepEqual(steps.map(step => step.id),
    ['discovery', 'specification', 'architecture', 'plan', 'generation', 'delivery']);
  assert.equal(steps[0].title, 'Product Discovery');
  assert.equal(steps[3].title, 'Implementation Plan');
  assert.equal(steps[5].title, 'Artifacts & Delivery');
});

test('closed stage keeps its mounted child and removes it from keyboard navigation', () => {
  const html = renderToStaticMarkup(React.createElement(JourneyStage, {
    step: steps[1], index: 1, open: false, onOpen: () => {},
    children: React.createElement('input', {
      'aria-label': 'Unsent specification note', defaultValue: 'Unsent text',
    }),
  }));
  assert.match(html, /aria-expanded="false"/);
  assert.match(html, /id="journey-panel-specification"/);
  assert.match(html, /hidden=""/);
  assert.match(html, /Unsent specification note/);
  assert.match(html, /Unsent text/);
});

test('open stage provides an actual labelled region and one expandable button', () => {
  const html = renderToStaticMarkup(React.createElement(JourneyStage, {
    step: { ...steps[2], tone: 'attention', status: '1 approval needed' },
    index: 2, open: true, onOpen: () => {},
    children: React.createElement('p', null, 'Review choices'),
  }));
  assert.match(html, /aria-expanded="true"/);
  assert.match(html, /role="region"/);
  assert.match(html, /aria-labelledby="journey-label-architecture"/);
  assert.match(html, /journey-attention/);
  assert.match(html, /1 approval needed/);
  assert.doesNotMatch(html, /hidden=""/);
});

test('progress uses colored state segments, not imaginary percentages', () => {
  const html = renderToStaticMarkup(React.createElement(JourneyStatusStrip, {
    title: task.title,
    steps: [
      { ...steps[0], tone: 'complete', status: 'Interview captured' },
      { ...steps[1], tone: 'attention', status: 'Review required' },
      ...steps.slice(2),
    ],
    activeId: 'specification',
  }));
  assert.match(html, /Stage 2/);
  assert.match(html, /1 of 6 stages completed/);
  assert.match(html, /1 requiring review/);
  assert.match(html, /journey-progress-segment journey-complete/);
  assert.doesNotMatch(html, /% complete/);
});

test('interview renders all six accordion headers with exactly one stage open', () => {
  const html = renderToStaticMarkup(React.createElement(InterviewStudio, {
    token: 'f'.repeat(64), task,
  }));
  assert.equal((html.match(/class="journey-stage journey-/g) ?? []).length, 6);
  assert.equal((html.match(/aria-expanded="true"/g) ?? []).length, 1);
  assert.equal((html.match(/aria-expanded="false"/g) ?? []).length, 5);
  for (const id of ['discovery', 'specification', 'architecture', 'plan', 'generation', 'delivery']) {
    assert.match(html, new RegExp('id="journey-panel-' + id + '"'));
  }
  assert.match(html, /Loading your saved conversation/);
});

test('saved work restores to Technical Lead before source generation', () => {
  assert.equal(restoreJourneyStage(null, 'pending', 'pending', 'pending', 'pending'),
    'discovery');
  assert.equal(restoreJourneyStage('Draft', 'pending', 'pending', 'pending', 'pending'),
    'specification');
  assert.equal(restoreJourneyStage('Approved', 'active', 'pending', 'pending', 'pending'),
    'architecture');
  assert.equal(restoreJourneyStage('Approved', 'complete', 'attention', 'pending', 'pending'),
    'plan');
  assert.equal(restoreJourneyStage('Approved', 'complete', 'complete', 'pending', 'pending'),
    'generation');
  assert.equal(restoreJourneyStage('Approved', 'complete', 'complete', 'complete', 'complete'),
    'delivery');
  assert.equal(restoreJourneyStage('Approved', 'blocked', 'pending', 'pending', 'attention'),
    'architecture');
});

test('repeated header press collapses all panels, while the next opens one', () => {
  assert.equal(toggleJourneyStage('discovery', 'discovery'), null);
  assert.equal(toggleJourneyStage(null, 'specification'), 'specification');
  assert.equal(toggleJourneyStage('specification', 'architecture'), 'architecture');
  assert.equal(toggleJourneyStage('architecture', 'architecture'), null);
  assert.equal(toggleJourneyStage(null, 'architecture'), 'architecture');
});

test('progress strip remains understandable with no expanded stage', () => {
  const html = renderToStaticMarkup(React.createElement(JourneyStatusStrip, {
    title: task.title, steps, activeId: null,
  }));
  assert.match(html, /All stages collapsed/);
  assert.match(html, /No stage open/);
  assert.doesNotMatch(html, /Stage 0/);
});

test('Technical Lead stage has a mounted, accessible plan-review region', () => {
  const html = renderToStaticMarkup(React.createElement(InterviewStudio, {
    token: 'f'.repeat(64), task,
  }));
  assert.match(html, /id="journey-panel-plan"/);
  assert.match(html, /Technical Lead/);
  assert.match(html, /Implementation Plan/);
  assert.match(html, /aria-expanded="false"/);
});
