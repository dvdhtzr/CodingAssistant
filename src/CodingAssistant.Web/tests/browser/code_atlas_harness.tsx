import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { CodeAtlasPanel, type AtlasSnapshot } from '../../src/CodeAtlasPanel';
import type { EngineerFile } from '../../src/EngineeringWorkspace';
import '../../src/styles.css';
import '../../src/code-atlas.css';

const runId = 'a'.repeat(32);
const taskId = 'b'.repeat(32);
const baseNodes: AtlasSnapshot['nodes'] = [
  { id: 'r', kind: 'Repository', name: 'Generated source',
    qualifiedName: runId, parentId: null, path: null, line: null },
  { id: 'p', kind: 'Project', name: 'Demo.csproj',
    qualifiedName: 'Demo.csproj', parentId: 'r', path: 'Demo.csproj', line: 1 },
  { id: 'f', kind: 'File', name: 'TaskService.cs',
    qualifiedName: 'TaskService.cs', parentId: 'p', path: 'TaskService.cs', line: 1 },
  { id: 'n', kind: 'Namespace', name: 'Demo',
    qualifiedName: 'Demo', parentId: 'p', path: 'TaskService.cs', line: 1 },
  { id: 'c', kind: 'Class', name: 'TaskService',
    qualifiedName: 'Demo.TaskService', parentId: 'n', path: 'TaskService.cs', line: 2 },
];
const baseEdges: AtlasSnapshot['edges'] = [
  { sourceId: 'r', targetId: 'p', kind: 'Contains' },
  { sourceId: 'p', targetId: 'f', kind: 'Contains' },
  { sourceId: 'p', targetId: 'n', kind: 'Contains' },
  { sourceId: 'n', targetId: 'c', kind: 'Contains' },
  { sourceId: 'f', targetId: 'c', kind: 'Declares' },
];
const initial: AtlasSnapshot = {
  runId, sourceRevision: '1'.repeat(64), coverage: 'SYNTAX_ONLY',
  diagnostics: [], nodes: baseNodes, edges: baseEdges,
};
const next: AtlasSnapshot = {
  ...initial, sourceRevision: '2'.repeat(64),
  nodes: [...baseNodes, {
    id: 'm', kind: 'Method', name: 'CreateTask()',
    qualifiedName: 'Demo.TaskService.CreateTask()', parentId: 'c',
    path: 'TaskService.cs', line: 3,
  }],
  edges: [...baseEdges, { sourceId: 'c', targetId: 'm', kind: 'Contains' }],
};
let response = initial;
const original = window.fetch.bind(window);
window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
  const url = String(input instanceof Request ? input.url : input);
  if (url.endsWith('/atlas'))
    return Promise.resolve(new Response(JSON.stringify(response), {
      status: 200, headers: { 'content-type': 'application/json' },
    }));
  if (url.endsWith('/atlas/evidence')) {
    return Promise.resolve(new Response(JSON.stringify({
      runId, sourceRevision: response.sourceRevision,
      verificationStatus: 'NOT_TESTED',
      qaReviewStatus: 'STATIC_REVIEW_ONLY', qaReviewId: 'q'.repeat(32),
      findings: [{
        id: 'Q1:TaskService.cs:0', fileNodeId: 'f',
        path: 'TaskService.cs', index: 0, severity: 'Major',
        requirement: 'Persistence',
        evidence: 'No persistence implementation is visible in this file',
        recommendation: 'Inspect and independently test storage writes',
        classification: 'UNVERIFIED_QA_HYPOTHESIS',
        qaReviewId: 'q'.repeat(32),
      }],
      repairs: [{
        cycleId: 'c'.repeat(32), baseRunId: runId,
        repairRunId: 'd'.repeat(32), qaReviewId: null,
        state: 'Generated', fileNodeIds: ['f'],
      }],
      diagnostics: [],
    }), { status: 200, headers: { 'content-type': 'application/json' } }));
  }
  if (url.endsWith('/atlas/replay')) {
    const frames = [
      { sequence: 1, workItemId: 'W1',
        sourceRevision: initial.sourceRevision, createdUtc: '2026-11-22T14:00:00Z' },
      { sequence: 2, workItemId: 'W2',
        sourceRevision: next.sourceRevision, createdUtc: '2026-11-22T14:00:01Z' },
    ];
    return Promise.resolve(new Response(JSON.stringify({ runId, frames }), {
      status: 200, headers: { 'content-type': 'application/json' },
    }));
  }
  if (url.endsWith('/atlas/replay/1') || url.endsWith('/atlas/replay/2')) {
    const first = url.endsWith('/1');
    const graph = first ? initial : next;
    return Promise.resolve(new Response(JSON.stringify({
      sequence: first ? 1 : 2,
      workItemId: first ? 'W1' : 'W2',
      sourceRevision: graph.sourceRevision,
      createdUtc: first ? '2026-11-22T14:00:00Z' : '2026-11-22T14:00:01Z',
      graph,
    }), { status: 200, headers: { 'content-type': 'application/json' } }));
  }
  if (url.includes('/atlas/path?')) {
    const request = new URL(url, window.location.href);
    const from = request.searchParams.get('from');
    const to = request.searchParams.get('to');
    const found = from === 'c' && to === 'm' && response === next;
    return Promise.resolve(new Response(JSON.stringify({
      fromId: from, toId: to, found,
      nodeIds: found ? ['c', 'm'] : [],
      edges: found ? [{ sourceId: 'c', targetId: 'm', kind: 'Contains' }] : [],
      visited: found ? 2 : 1, truncated: false,
    }), { status: 200, headers: { 'content-type': 'application/json' } }));
  }
  if (url.includes('/atlas-events?')) {
    const after = Number(new URL(url, window.location.href).searchParams.get('after') ?? 0);
    const committed = response === next;
    const event = {
      runId, sequence: 1, workItemId: 'W2',
      sourceRevision: next.sourceRevision,
      changes: [{ path: 'TaskService.cs', kind: 'Modified', sha256: '2'.repeat(64) }],
      createdUtc: '2026-11-22T14:00:01Z',
    };
    return Promise.resolve(new Response(JSON.stringify({
      runId, lastSequence: committed ? 1 : 0,
      events: committed && after === 0 ? [event] : [],
    }), { status: 200, headers: { 'content-type': 'application/json' } }));
  }
  return original(input, init);
};
function Demo() {
  const [stage, setStage] = useState(0);
  const files: EngineerFile[] = [{
    path: 'TaskService.cs', purpose: 'Service source',
    content: stage ? 'namespace Demo;\nclass TaskService {\n void CreateTask() {}\n}\n'
      : 'namespace Demo;\nclass TaskService {}\n',
    sha256: stage ? '2'.repeat(64) : '1'.repeat(64),
    lastWorkItemId: 'W1', updatedUtc: '2026-11-22T14:00:00Z',
  }];
  return <main style={{ maxWidth: 1180, margin: '20px auto' }}>
    <button type="button" onClick={() => {
      response = next; setStage(1);
    }}>Persist next source revision</button>
    <CodeAtlasPanel token={'f'.repeat(64)} taskId={taskId}
      runId={runId} files={files} running={stage === 0}/>
  </main>;
}
createRoot(document.getElementById('root')!).render(<Demo/>);
