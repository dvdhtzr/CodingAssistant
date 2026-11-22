import { useEffect, useMemo, useRef, useState } from 'react';
import { api } from './api';
import type { EngineerFile } from './EngineeringWorkspace';

export interface AtlasNode {
  id: string;
  kind: string;
  name: string;
  qualifiedName: string;
  parentId: string | null;
  path: string | null;
  line: number | null;
}
export interface AtlasEdge {
  sourceId: string;
  targetId: string;
  kind: string;
}
export interface AtlasSnapshot {
  runId: string;
  sourceRevision: string;
  coverage: string;
  nodes: AtlasNode[];
  edges: AtlasEdge[];
  diagnostics: string[];
}
export interface AtlasPosition { x: number; y: number; depth: number; }
export interface AtlasView {
  nodes: AtlasNode[];
  edges: AtlasEdge[];
  positions: Record<string, AtlasPosition>;
  total: number;
  limited: boolean;
}

const MAX_VISIBLE = 50;
const SKIP_METHODS = new Set(['Method', 'Constructor']);
const GRAPH_RADIUS = [0, 120, 230, 325, 390];
const KIND_WEIGHT: Record<string, number> = {
  Repository: 0, Project: 1, Namespace: 2, Folder: 3,
  Class: 4, Interface: 5, Struct: 6, Enum: 7,
  File: 8, Constructor: 9, Method: 10,
};

function graphSort(a: AtlasNode, b: AtlasNode): number {
  return (KIND_WEIGHT[a.kind] ?? 99) - (KIND_WEIGHT[b.kind] ?? 99) ||
    a.qualifiedName.localeCompare(b.qualifiedName);
}

// Pure, deterministic projection. All visible symbols come from persisted
// source analysis; the browser never generates inferred relationships.
export function projectAtlas(snapshot: AtlasSnapshot, focusId: string | null,
  methods: boolean, query = ''): AtlasView {
  const allowed = snapshot.nodes.filter(node => methods ||
    !SKIP_METHODS.has(node.kind));
  const byId = new Map(allowed.map(node => [node.id, node]));
  const contains = snapshot.edges.filter(edge =>
    edge.kind === 'Contains' && byId.has(edge.sourceId) &&
    byId.has(edge.targetId));
  const children = new Map<string, AtlasNode[]>();
  for (const edge of contains) {
    const group = children.get(edge.sourceId) ?? [];
    group.push(byId.get(edge.targetId)!);
    children.set(edge.sourceId, group);
  }
  for (const group of children.values()) group.sort(graphSort);
  const root = allowed.find(node => node.kind === 'Repository') ??
    allowed.slice().sort(graphSort)[0];
  const searched = query.trim().toLowerCase();
  const match = searched ? allowed.find(node =>
    (node.qualifiedName + ' ' + node.name).toLowerCase().includes(searched)) : null;
  const focus = (focusId && byId.get(focusId)) || match || root;
  if (!focus) return { nodes: [], edges: [], positions: {}, total: 0, limited: false };

  const visible = new Map<string, AtlasNode>();
  const depth = new Map<string, number>();
  const queue: AtlasNode[] = [];
  function insert(node: AtlasNode, level: number) {
    if (visible.has(node.id) || visible.size >= MAX_VISIBLE) return;
    visible.set(node.id, node);
    depth.set(node.id, level);
    queue.push(node);
  }
  insert(focus, 0);
  // Focused views retain the containing lineage for navigational context.
  if (focus.id !== root?.id) {
    let current: AtlasNode | undefined = focus;
    let limit = 0;
    while (current?.parentId && limit++ < 8) {
      const parent: AtlasNode | undefined = byId.get(current.parentId);
      if (!parent) break;
      insert(parent, Math.min(limit, 4));
      current = parent;
    }
  }
  for (let i = 0; i < queue.length && visible.size < MAX_VISIBLE; i++) {
    const next = queue[i];
    const level = depth.get(next.id) ?? 0;
    if (level >= 4) continue;
    for (const child of children.get(next.id) ?? []) insert(child, level + 1);
  }
  // When looking at a selected method/type, include its actual callers,
  // declarations, or other known direct relationships without inventing edges.
  if (focusId || match) {
    for (const edge of snapshot.edges) {
      if (edge.sourceId === focus.id && byId.has(edge.targetId))
        insert(byId.get(edge.targetId)!, 1);
      if (edge.targetId === focus.id && byId.has(edge.sourceId))
        insert(byId.get(edge.sourceId)!, 1);
    }
  }

  const groupByDepth = new Map<number, AtlasNode[]>();
  for (const node of visible.values()) {
    const level = depth.get(node.id) ?? 0;
    const list = groupByDepth.get(level) ?? [];
    list.push(node);
    groupByDepth.set(level, list);
  }
  const positions: Record<string, AtlasPosition> = {};
  for (const [level, group] of groupByDepth) {
    group.sort(graphSort);
    const radius = GRAPH_RADIUS[Math.min(level, GRAPH_RADIUS.length - 1)];
    for (let i = 0; i < group.length; i++) {
      const angle = -Math.PI / 2 + (2 * Math.PI * i / group.length) +
        level * 0.19;
      positions[group[i].id] = {
        x: level === 0 ? 0 : Math.cos(angle) * radius,
        y: level === 0 ? 0 : Math.sin(angle) * radius,
        depth: level,
      };
    }
  }
  const resultEdges = snapshot.edges.filter(edge =>
    visible.has(edge.sourceId) && visible.has(edge.targetId) &&
    (edge.kind === 'Contains' || (focusId || match) &&
      (edge.sourceId === focus.id || edge.targetId === focus.id)));
  return {
    nodes: [...visible.values()],
    edges: resultEdges,
    positions,
    total: allowed.length,
    limited: visible.size < allowed.length,
  };
}

function relatedEdges(snapshot: AtlasSnapshot, id: string): AtlasEdge[] {
  return snapshot.edges.filter(edge =>
    edge.sourceId === id || edge.targetId === id);
}

function selectedExcerpt(node: AtlasNode | undefined,
  files: readonly EngineerFile[]): string {
  if (!node?.path) return '';
  const file = files.find(f => f.path === node.path);
  if (!file) return '';
  const lines = file.content.split(/\r?\n/);
  const start = Math.max(0, (node.line ?? 1) - 4);
  return lines.slice(start, start + 10).map((line, i) =>
    String(start + i + 1).padStart(4, ' ') + '  ' + line).join('\n');
}

export function CodeAtlasPanel({ token, taskId, runId, files, running }: {
  token: string;
  taskId: string;
  runId: string;
  files: readonly EngineerFile[];
  running: boolean;
}) {
  const [snapshot, setSnapshot] = useState<AtlasSnapshot | null>(null);
  const [error, setError] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  const [focus, setFocus] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [showMethods, setShowMethods] = useState(false);
  const [followBuild, setFollowBuild] = useState(true);
  const [zoom, setZoom] = useState(1);
  const [offset, setOffset] = useState({ x: 0, y: 0 });
  const pointer = useRef<{ x: number; y: number } | null>(null);
  const revision = useRef('');
  const lastFileSet = useRef('');
  const fileSignature = files.map(f => f.path + ':' + f.sha256).sort().join('|');

  useEffect(() => {
    setSnapshot(null);
    setError('');
    setSelected(null);
    setFocus(null);
    revision.current = '';
    lastFileSet.current = '';
    return () => { active = false; };
  }, [runId]);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const value = await api<AtlasSnapshot>(token,
          '/api/tasks/' + taskId + '/engineering-runs/' + runId + '/atlas');
        if (cancelled) return;
        const existing = revision.current;
        const previousSet = lastFileSet.current;
        const nextSet = value.nodes.filter(n => n.kind === 'File')
          .map(n => n.path).sort().join('|');
        revision.current = value.sourceRevision;
        lastFileSet.current = nextSet;
        setSnapshot(value);
        setError('');
        if (followBuild && existing && existing !== value.sourceRevision &&
          previousSet !== nextSet) {
          // A newly persisted file is real evidence of progress. Don't shift
          // the viewport in manual exploration mode.
          const lastAdded = value.nodes.find(n => n.kind === 'File' &&
            !!n.path && !previousSet.split('|').includes(n.path));
          if (lastAdded) { setSelected(lastAdded.id); setFocus(lastAdded.id); }
        }
      } catch {
        if (!cancelled) setError('Code Atlas could not load this saved source graph.');
      }
    }
    void load();
    // EngineeringWorkspace already polls accepted source every 3 seconds.
    // A new persisted file signature drives this projection refresh.
    return () => { cancelled = true; };
  }, [token, taskId, runId, fileSignature, followBuild]);

  const view = useMemo(() => snapshot ?
    projectAtlas(snapshot, focus, showMethods, query) : null,
    [snapshot, focus, showMethods, query]);
  const node = snapshot?.nodes.find(n => n.id === selected);
  const links = node && snapshot ? relatedEdges(snapshot, node.id) : [];
  const names = new Map(snapshot?.nodes.map(n => [n.id, n.name]) ?? []);
  const excerpt = selectedExcerpt(node, files);

  function reset() {
    setSelected(null); setFocus(null); setQuery('');
    setZoom(1); setOffset({ x: 0, y: 0 });
  }

  return <section className="atlas-panel" aria-label="Code Atlas">
    <header className="atlas-heading">
      <div>
        <span className="wb-kicker">CODE ATLAS / SOURCE CONSTELLATION</span>
        <h3>Watch the architecture take shape</h3>
        <p>Source-derived structure from saved Software Engineer files.
          Not compiled, executed, or verified.</p>
      </div>
      <span className="atlas-live-indicator" data-running={running}>
        <span aria-hidden="true" />{running ? 'Observing source' : 'Saved snapshot'}
      </span>
    </header>
    <div className="atlas-controls">
      <label className="atlas-search">Find a symbol
        <input type="search" value={query}
          onChange={event => { setQuery(event.target.value); setFocus(null); }}
          placeholder="Class, method, namespace…" />
      </label>
      <label className="atlas-toggle"><input type="checkbox"
        checked={showMethods} onChange={e => setShowMethods(e.target.checked)} />
        Methods
      </label>
      <label className="atlas-toggle"><input type="checkbox"
        checked={followBuild} onChange={e => setFollowBuild(e.target.checked)} />
        Follow build
      </label>
      <button type="button" className="outline" onClick={reset}>Overview</button>
    </div>
    {error && <p role="alert" className="atlas-error">{error}</p>}
    {!snapshot && !error && <p role="status" className="atlas-empty">
      Reading saved source structure…</p>}
    {snapshot && view && <div className="atlas-workspace">
      <div className="atlas-canvas-wrap">
        {view.nodes.length === 1 && snapshot.nodes.length === 1
          ? <p className="atlas-empty">No source files have been saved yet.
              The graph will grow after each completed engineering assignment.</p>
          : <>
            <svg className="atlas-canvas" viewBox="-480 -300 960 600"
              role="img" aria-label="Interactive Code Atlas graph of saved source"
              onPointerDown={e => {
                if (e.target !== e.currentTarget) return;
                pointer.current = { x: e.clientX, y: e.clientY };
                e.currentTarget.setPointerCapture(e.pointerId);
              }}
              onPointerMove={e => {
                if (!pointer.current) return;
                const rect = e.currentTarget.getBoundingClientRect();
                const factor = 960 / Math.max(rect.width, 1);
                setOffset(p => ({
                  x: p.x + (e.clientX - pointer.current!.x) * factor,
                  y: p.y + (e.clientY - pointer.current!.y) * factor,
                }));
                pointer.current = { x: e.clientX, y: e.clientY };
              }}
              onPointerUp={() => { pointer.current = null; }}
              onPointerCancel={() => { pointer.current = null; }}>
              <defs>
                <radialGradient id="atlas-halo">
                  <stop offset="0%" stopColor="#3bc7b7" stopOpacity=".2"/>
                  <stop offset="100%" stopColor="#3bc7b7" stopOpacity="0"/>
                </radialGradient>
              </defs>
              <circle cx="0" cy="0" r="270" fill="url(#atlas-halo)"
                pointerEvents="none" />
              <g transform={'translate(' + offset.x + ' ' + offset.y +
                  ') scale(' + zoom + ')'}>
                {view.edges.map(edge => {
                  const from = view.positions[edge.sourceId];
                  const to = view.positions[edge.targetId];
                  if (!from || !to) return null;
                  const relevant = selected &&
                    (edge.sourceId === selected || edge.targetId === selected);
                  return <line key={edge.kind + edge.sourceId + edge.targetId}
                    x1={from.x} y1={from.y} x2={to.x} y2={to.y}
                    className={'atlas-edge' + (relevant ? ' atlas-related' : '')}/>;
                })}
                {view.nodes.map(current => {
                  const at = view.positions[current.id];
                  const chosen = selected === current.id;
                  const relevant = selected && links.some(edge =>
                    edge.sourceId === current.id || edge.targetId === current.id);
                  const radius = current.kind === 'Repository' ? 29 :
                    current.kind === 'Project' ? 23 :
                    current.kind === 'Namespace' ? 18 : 12;
                  return <g key={current.id} tabIndex={0} role="button"
                    aria-label={current.kind + ': ' + current.qualifiedName}
                    aria-pressed={chosen} className={'atlas-node atlas-kind-' +
                      current.kind.toLowerCase() +
                      (chosen ? ' atlas-selected' : '') +
                      (relevant ? ' atlas-connected' : '')}
                    onClick={() => setSelected(current.id)}
                    onKeyDown={event => {
                      if (event.key === 'Enter' || event.key === ' ') {
                        event.preventDefault();
                        setSelected(current.id);
                      }
                    }} transform={'translate(' + at.x + ' ' + at.y + ')'}>
                    <circle r={radius + (chosen ? 8 : 5)}
                      className="atlas-node-halo" />
                    <circle r={radius} className="atlas-node-core" />
                    <text textAnchor="middle" y={radius + 17}
                      className="atlas-node-label">{current.name.length > 21
                        ? current.name.slice(0, 20) + '…' : current.name}</text>
                  </g>;
                })}
              </g>
            </svg>
            <div className="atlas-zoom" aria-label="Graph zoom">
              <button type="button" aria-label="Zoom in"
                onClick={() => setZoom(z => Math.min(2.4, z * 1.2))}>+</button>
              <button type="button" aria-label="Zoom out"
                onClick={() => setZoom(z => Math.max(.45, z / 1.2))}>−</button>
              <button type="button" aria-label="Reset graph position"
                onClick={() => { setZoom(1); setOffset({ x: 0, y: 0 }); }}>⌖</button>
            </div>
          </>}
        <div className="atlas-graph-footer">
          <span>{view.nodes.length} of {view.total} symbols
            {' · '}{view.edges.length} visible links</span>
          <span>{snapshot.coverage === 'SYNTAX_ONLY' ? 'Syntax indexed' :
            snapshot.coverage === 'PARTIAL_SYNTAX' ? 'Partial C# syntax' :
              snapshot.coverage}</span>
        </div>
      </div>
      <aside className="atlas-details" aria-label="Selected source entity">
        {node ? <>
          <span className="wb-kicker">{node.kind.toUpperCase()}</span>
          <h4>{node.name}</h4>
          <p className="atlas-qualified">{node.qualifiedName}</p>
          <dl>
            <dt>Source</dt>
            <dd>{node.path ?? 'Logical group'}
              {node.line ? ':' + node.line : ''}</dd>
            <dt>Known relationships</dt>
            <dd>{links.length}</dd>
          </dl>
          <button type="button" className="outline"
            onClick={() => { setFocus(node.id); setQuery(''); }}>
            Focus relationships
          </button>
          <div className="atlas-relationships">
            {links.slice(0, 16).map(edge => {
              const other = edge.sourceId === node.id ?
                edge.targetId : edge.sourceId;
              return <button type="button" key={edge.kind + ':' + edge.sourceId +
                ':' + edge.targetId} onClick={() => setSelected(other)}>
                <span>{edge.kind}</span> {names.get(other) ?? 'Unknown symbol'}
              </button>;
            })}
            {links.length === 0 && <p>No indexed relationships yet.</p>}
          </div>
          {excerpt && <pre className="atlas-excerpt"
            aria-label="Source excerpt">{excerpt}</pre>}
        </> : <>
          <span className="wb-kicker">EXPLORE</span>
          <h4>Your code, connected</h4>
          <p>Select a node to inspect its source, relationships, and location.
            Choose Focus relationships to explore its neighborhood.</p>
          <p>Use the search field to find a symbol, or enable Methods for
            a more detailed source map.</p>
        </>}
      </aside>
    </div>}
    {snapshot && snapshot.diagnostics.length > 0 &&
      <p className="atlas-coverage-note" role="status">
        Some source could not be completely indexed. The graph contains
        only relationships supported by the current analysis.</p>}
  </section>;
}
