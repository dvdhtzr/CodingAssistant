import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

let vite;
let Composer;
let formatAnswer;
const noop = () => {};

before(async () => {
  vite = await createServer({
    configFile: false, root: process.cwd(),
    server: { middlewareMode: true }, appType: 'custom',
  });
  ({ InterviewAnswerComposer: Composer, formatInterviewAnswer: formatAnswer } =
    await vite.ssrLoadModule('/src/InterviewAnswerComposer.tsx'));
});
after(async () => { if (vite) await vite.close(); });

test('single-answer suggestions are semantic radio buttons with an accessible alternative', () => {
  const html = renderToStaticMarkup(React.createElement(Composer, {
    choice: { kind: 'single', options: ['Individual', 'Team', '<script>'] },
    busy: false, onAnswer: noop,
  }));
  assert.equal((html.match(/type="radio"/g) ?? []).length, 3);
  assert.match(html, /Choose one answer, or write your own/);
  assert.match(html, /Something else/);
  assert.match(html, /Type your own answer/);
  assert.match(html, /&lt;script&gt;/);
  assert.doesNotMatch(html, /<script>/);
  assert.match(html, /disabled=""/);
});

test('multiple-choice suggestions use independent checkboxes, never false approvals', () => {
  const html = renderToStaticMarkup(React.createElement(Composer, {
    choice: { kind: 'multiple', options: ['Offline use', 'Export', 'Local models'] },
    busy: false, onAnswer: noop,
  }));
  assert.equal((html.match(/type="checkbox"/g) ?? []).length, 3);
  assert.match(html, /Choose all that apply/);
  assert.match(html, /Nothing is approved automatically/);
});

test('old and unstructured interview questions keep a working free-text composer', () => {
  const html = renderToStaticMarkup(React.createElement(Composer, {
    choice: null, busy: false, onAnswer: noop,
  }));
  assert.match(html, /textarea/);
  assert.match(html, /Your answer or additional requirements/);
  assert.doesNotMatch(html, /type="radio"/);
  assert.doesNotMatch(html, /type="checkbox"/);
});

test('choice answers remain plain, reviewable owner text for the existing API', () => {
  assert.equal(formatAnswer(
    { kind: 'single', options: ['One', 'Two'] }, ['Two'], '', ''),
    'Selected option: Two');
  assert.equal(formatAnswer(
    { kind: 'multiple', options: ['A', 'B', 'C'] }, ['A', 'C'],
    'and accessibility', ''),
    'Selected options: A; C\nSomething else: and accessibility');
  assert.equal(formatAnswer(
    { kind: 'single', options: ['A', 'B'] }, [], 'My own approach', ''),
    'Something else: My own approach');
  assert.equal(formatAnswer(null, [], '', '  Custom answer  '),
    'Custom answer');
});
