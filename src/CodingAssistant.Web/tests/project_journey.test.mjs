import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
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
let InterviewCompletionNotice;
let ConversationWorkroom;

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
    optimizeDeps: { noDiscovery: true, include: [] },
    root: process.cwd(),
    server: { middlewareMode: true, hmr: false },
    appType: 'custom',
  });
  const journey = await vite.ssrLoadModule('/src/ProjectJourney.tsx');
  ({ JourneyStage, JourneyStatusStrip, restoreJourneyStage, toggleJourneyStage } = journey);
  steps = journey.journeySteps;
  ({ InterviewStudio, InterviewCompletionNotice } =
    await vite.ssrLoadModule('/src/InterviewStudio.tsx'));
  ({ ConversationWorkroom } = await vite.ssrLoadModule('/src/ConversationWorkroom.tsx'));
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

test('after the final 13th answer, discovery has an explicit specification action', () => {
  const html = renderToStaticMarkup(React.createElement(InterviewCompletionNotice, {
    ownerAnswers: 13, hasSpecification: false, onContinue: () => {},
  }));
  assert.match(html, /Your final interview answer is saved/);
  assert.match(html, /No more questions will be asked/);
  assert.match(html, /Continue to specification/);
  assert.match(html, /type="button"/);
  assert.match(html, /role="status"/);
});

test('an already-drafted interview offers review instead of drafting twice', () => {
  const html = renderToStaticMarkup(React.createElement(InterviewCompletionNotice, {
    ownerAnswers: 13, hasSpecification: true, onContinue: () => {},
  }));
  assert.match(html, /Review specification/);
  assert.doesNotMatch(html, /Continue to specification/);
});

test('interviews below the answer limit retain their own composer', () => {
  const html = renderToStaticMarkup(React.createElement(InterviewCompletionNotice, {
    ownerAnswers: 12, hasSpecification: false, onContinue: () => {},
  }));
  assert.equal(html, '');
});

test('turn 25 remains answerable while question 13 is pending', () => {
  const source = readFileSync('src/InterviewStudio.tsx', 'utf8');
  assert.match(source, /const MODEL_DRIVEN_ANSWER_LIMIT = 12;/);
  assert.match(source, /hasPendingFinalQuestion/);
  assert.ok(source.includes('const canAnswerInterview = ownerAnswers < MODEL_DRIVEN_ANSWER_LIMIT ||'));
  assert.match(source, /\{canAnswerInterview && <InterviewAnswerComposer/);
  assert.match(source, /This is the final Product Owner question/);
});

test('interview scroll is bounded to conversation history, not the composer or stage', () => {
  const css = readFileSync('src/mission-control.css', 'utf8');
  assert.match(css, /\.wb-main \.journey-status-strip\s*\{\s*position: static;/);
  assert.match(css, /\.journey-discovery \.journey-stage-body\s*\{\s*max-height: none;\s*overflow: visible;/);
  assert.match(css, /\.journey-discovery \.interview-transcript\s*\{[\s\S]*?overflow-y: auto;/);
  assert.match(css, /\.journey-discovery \.journey-stage-body form\s*\{\s*position: static;/);
  const source = readFileSync('src/InterviewStudio.tsx', 'utf8');
  assert.match(source, /<ConversationWorkroom messages=\{snapshot.messages\} \/>/);
  assert.match(source, /<details className="interview-objective">/);
  assert.match(source, /<InterviewCompletionNotice ownerAnswers=\{ownerAnswers\}/);
});

test('the six-stage project rail uses real buttons for direct navigation', () => {
  const html = renderToStaticMarkup(React.createElement(JourneyStatusStrip, {
    title: task.title,
    steps: [
      { ...steps[0], tone: 'complete', status: 'Interview captured' },
      { ...steps[1], tone: 'attention', status: 'Review specification' },
      ...steps.slice(2),
    ],
    activeId: 'specification', onNavigate: () => {},
  }));
  assert.match(html, /aria-label="Jump to a project stage"/);
  assert.equal((html.match(/class="journey-quick-step /g) ?? []).length, 6);
  assert.match(html, /aria-label="Open Product Discovery: Interview captured"/);
  assert.match(html, /aria-label="Open Specification: Review specification"/);
  assert.match(html, /aria-current="step"/);
  assert.match(html, /aria-controls="journey-panel-specification"/);
  assert.match(html, /journey-quick-current/);
  assert.doesNotMatch(html, /% complete/);
});

test('the latest Product Owner question stays in the foreground with full saved history', () => {
  const messages = [
    { sequence: 1, role: 'assistant', content: 'Who will use this?', createdUtc: '2026-11-21T14:00:00Z' },
    { sequence: 2, role: 'owner', content: 'A small team', createdUtc: '2026-11-21T14:01:00Z' },
    { sequence: 3, role: 'assistant',
      content: 'Choose autonomy for Oddville <script>alert(1)</script>?',
      createdUtc: '2026-11-21T14:02:00Z' },
  ];
  const html = renderToStaticMarkup(React.createElement(ConversationWorkroom, { messages }));
  assert.match(html, /CURRENT PRODUCT OWNER QUESTION/);
  assert.match(html, /Turn 3/);
  assert.match(html, /Choose autonomy for Oddville &lt;script&gt;alert\(1\)&lt;\/script&gt;/);
  assert.doesNotMatch(html, /<script>/);
  assert.match(html, /Earlier conversation/);
  assert.match(html, /2 saved messages/);
  assert.equal((html.match(/class="interview-message"/g) ?? []).length, 2);
  assert.match(html, /Who will use this\?/);
  assert.match(html, /A small team/);
});

test('workroom never fabricates an assistant question from a last saved owner reply', () => {
  const html = renderToStaticMarkup(React.createElement(ConversationWorkroom, {
    messages: [{ sequence: 4, role: 'owner', content: 'Something else',
      createdUtc: '2026-11-21T14:02:00Z' }],
  }));
  assert.match(html, /LAST SAVED OWNER ANSWER/);
  assert.doesNotMatch(html, /CURRENT PRODUCT OWNER QUESTION/);
  assert.doesNotMatch(html, /Earlier conversation/);
});

test('final Product Owner acknowledgement is a handoff, not an unanswered question', () => {
  const html = renderToStaticMarkup(React.createElement(ConversationWorkroom, {
    messages: [
      { sequence: 26, role: 'owner', content: 'Only podcast platform data.',
        createdUtc: '2026-11-22T17:00:00Z' },
      { sequence: 27, role: 'assistant',
        content: 'Your final answer has been saved. Product discovery is complete.',
        createdUtc: '2026-11-22T17:00:01Z' },
    ],
  }));
  assert.match(html, /PRODUCT OWNER HANDOFF/);
  assert.doesNotMatch(html, /CURRENT PRODUCT OWNER QUESTION/);
  assert.match(html, /Your final answer has been saved/);
});

test('UX-2 stylesheet keeps history bounded while composer stays outside it', () => {
  const bootstrap = readFileSync('src/main.tsx', 'utf8');
  const css = readFileSync('src/conversation-workroom.css', 'utf8');
  assert.ok(bootstrap.indexOf("import './conversation-workroom.css'") >
    bootstrap.indexOf("import './agent-presence.css'"));
  assert.match(css, /\.journey-quick-nav/);
  assert.match(css, /\.journey-discovery \.conversation-history \.interview-transcript/);
  assert.match(css, /max-height: min\(49dvh, 550px\)/);
  assert.match(css, /prefers-reduced-motion/);
});
