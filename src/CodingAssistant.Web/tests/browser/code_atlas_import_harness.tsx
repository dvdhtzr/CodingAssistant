import { createRoot } from 'react-dom/client';
import { CodeAtlasRepositoryPanel } from '../../src/CodeAtlasRepositoryPanel';
import type { AtlasSnapshot } from '../../src/CodeAtlasPanel';
import '../../src/styles.css';
import '../../src/code-atlas.css';

const taskId = '1'.repeat(32);
const importId = '2'.repeat(32);
const commit = 'a'.repeat(40);
const source = 'namespace Demo;\npublic class Order {\n public void Place() {}\n}\n';
const graph: AtlasSnapshot = {
  runId: importId, sourceRevision: 'b'.repeat(64),
  coverage: 'STATIC_SEMANTIC', diagnostics: [],
  nodes: [
    { id: 'root', kind: 'Repository', name: 'example/sample',
      qualifiedName: importId, parentId: null, path: null, line: null },
    { id: 'folder', kind: 'Folder', name: 'src',
      qualifiedName: 'src/', parentId: 'root', path: null, line: null },
    { id: 'file', kind: 'File', name: 'Order.cs',
      qualifiedName: 'src/Order.cs', parentId: 'folder',
      path: 'src/Order.cs', line: 1 },
    { id: 'ns', kind: 'Namespace', name: 'Demo',
      qualifiedName: 'Demo', parentId: 'root',
      path: 'src/Order.cs', line: 1 },
    { id: 'type', kind: 'Class', name: 'Order',
      qualifiedName: 'Demo.Order', parentId: 'ns',
      path: 'src/Order.cs', line: 2 },
    { id: 'method', kind: 'Method', name: 'Place()',
      qualifiedName: 'Demo.Order.Place()', parentId: 'type',
      path: 'src/Order.cs', line: 3 },
  ],
  edges: [
    { sourceId: 'root', targetId: 'folder', kind: 'Contains' },
    { sourceId: 'folder', targetId: 'file', kind: 'Contains' },
    { sourceId: 'root', targetId: 'ns', kind: 'Contains' },
    { sourceId: 'ns', targetId: 'type', kind: 'Contains' },
    { sourceId: 'type', targetId: 'method', kind: 'Contains' },
  ],
};
const repository = {
  id: importId, taskId, repositoryUrl: 'https://github.com/example/sample',
  commitSha: commit, sourceRevision: graph.sourceRevision,
  coverage: graph.coverage, indexedFiles: 1,
  excludedFiles: 0, createdUtc: '2026-11-22T00:00:00Z',
};
let saved = false;
const original = window.fetch.bind(window);
window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
  const url = String(input instanceof Request ? input.url : input);
  if (url.endsWith('/atlas-imports') && (!init?.method || init.method === 'GET'))
    return Promise.resolve(new Response(JSON.stringify(saved ? [repository] : []), {
      status: 200, headers: { 'content-type': 'application/json' },
    }));
  if (url.endsWith('/atlas-imports') && init?.method === 'POST') {
    saved = true;
    return Promise.resolve(new Response(JSON.stringify(repository), {
      status: 200, headers: { 'content-type': 'application/json' },
    }));
  }
  if (url.endsWith('/atlas-imports/' + importId))
    return Promise.resolve(new Response(JSON.stringify({ repository, graph }), {
      status: 200, headers: { 'content-type': 'application/json' },
    }));
  if (url.includes('/atlas-imports/' + importId + '/file?'))
    return Promise.resolve(new Response(JSON.stringify({
      path: 'src/Order.cs', sha256: '9'.repeat(64), content: source,
    }), { status: 200, headers: { 'content-type': 'application/json' } }));
  if (url.includes('/atlas-imports/' + importId + '/path?')) {
    const query = new URL(url, location.href).searchParams;
    const from = query.get('from'), to = query.get('to');
    return Promise.resolve(new Response(JSON.stringify({
      fromId: from, toId: to, found: from === 'type' && to === 'method',
      nodeIds: ['type', 'method'],
      edges: [{ sourceId: 'type', targetId: 'method', kind: 'Contains' }],
      visited: 2, truncated: false,
    }), { status: 200, headers: { 'content-type': 'application/json' } }));
  }
  return original(input, init);
};

createRoot(document.getElementById('root')!).render(
  <CodeAtlasRepositoryPanel token={'f'.repeat(64)} taskId={taskId}/>
);
