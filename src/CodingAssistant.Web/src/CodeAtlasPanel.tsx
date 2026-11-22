import { useEffect, useMemo, useRef, useState } from 'react';
import { api } from './api';
import * as signalR from '@microsoft/signalr';
import type { EngineerFile } from './EngineeringWorkspace';

export interface AtlasImportedFile {
  path: string;
  sha256: string;
  content: string;
}
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
  evidencePath?: string | null;
  evidenceLine?: number | null;
  resolution?: string;
}
export interface AtlasSnapshot {
  runId: string;
  sourceRevision: string;
  coverage: string;
  nodes: AtlasNode[];
  edges: AtlasEdge[];
  diagnostics: string[];
}
export interface AtlasSourceChange {
  path: string;
  kind: 'Added' | 'Modified' | 'Removed';
  sha256: string;
}
export interface AtlasGraphDelta {
  addedNodeIds: string[];
  updatedNodeIds: string[];
  removedNodeIds: string[];
  addedEdgeKeys: string[];
  removedEdgeKeys: string[];
}
export interface AtlasSourceEvent {
  runId: string;
  sequence: number;
  workItemId: string;
  sourceRevision: string;
  changes: AtlasSourceChange[];
  createdUtc: string;
  graphDelta?: AtlasGraphDelta | null;
}
export interface AtlasSourceEventPage {
  runId: string;
  lastSequence: number;
  events: AtlasSourceEvent[];
}
export interface AtlasQaFindingMarker {
  id: string;
  fileNodeId: string;
  path: string;
  index: number;
  severity: string;
  requirement: string;
  evidence: string;
  recommendation: string;
  classification: string;
  qaReviewId: string;
}
export interface AtlasRepairMarker {
  cycleId: string;
  baseRunId: string;
  repairRunId: string;
  state: string;
  qaReviewId: string | null;
  fileNodeIds: string[];
}
export interface AtlasEvidenceOverlay {
  runId: string;
  sourceRevision: string;
  verificationStatus: string;
  qaReviewStatus: string;
  qaReviewId: string | null;
  findings: AtlasQaFindingMarker[];
  repairs: AtlasRepairMarker[];
  diagnostics: string[];
}
export interface AtlasPosition { x: number; y: number; depth: number; }
export interface AtlasReplaySummary {
  sequence: number;
  workItemId: string;
  sourceRevision: string;
  createdUtc: string;
}
export interface AtlasReplayManifest {
  runId: string;
  frames: AtlasReplaySummary[];
}
export interface AtlasReplayFrame extends AtlasReplaySummary {
  graph: AtlasSnapshot;
}
export interface AtlasPathResult {
  fromId: string;
  toId: string;
  found: boolean;
  nodeIds: string[];
  edges: AtlasEdge[];
  visited: number;
  truncated: boolean;
}
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

export function CodeAtlasPanel({ token, taskId, runId, files, running,
  onSourceChanged, importedGraph }: {
  token: string;
  taskId: string;
  runId: string;
  files: readonly EngineerFile[];
  running: boolean;
  onSourceChanged?: () => void;
  importedGraph?: AtlasSnapshot;
}) {
  const [snapshot, setSnapshot] = useState<AtlasSnapshot | null>(null);
  const [error, setError] = useState('');
  const [importedFile, setImportedFile] = useState<AtlasImportedFile | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [focus, setFocus] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [showMethods, setShowMethods] = useState(false);
  const [relationshipDirection, setRelationshipDirection] = useState<
    'all' | 'incoming' | 'outgoing'>('all');
  const [followBuild, setFollowBuild] = useState(true);
  const [recentIds, setRecentIds] = useState<string[]>([]);
  const [traceFrom, setTraceFrom] = useState<string | null>(null);
  const [traceResult, setTraceResult] = useState<AtlasPathResult | null>(null);
  const [traceError, setTraceError] = useState('');
  const [traceBusy, setTraceBusy] = useState(false);
  const [sourceEvents, setSourceEvents] = useState<AtlasSourceEvent[]>([]);
  const [replayStatus, setReplayStatus] = useState('');
  const [evidence, setEvidence] = useState<AtlasEvidenceOverlay | null>(null);
  const [replayFrames, setReplayFrames] = useState<AtlasReplaySummary[]>([]);
  const [replaySequence, setReplaySequence] = useState<number | null>(null);
  const [replayGraph, setReplayGraph] = useState<AtlasSnapshot | null>(null);
  const [replayBusy, setReplayBusy] = useState(false);
  const [replayError, setReplayError] = useState('');
  const replayRequest = useRef(0);
  const [zoom, setZoom] = useState(1);
  const [offset, setOffset] = useState({ x: 0, y: 0 });
  const pointer = useRef<{ x: number; y: number } | null>(null);
  const revision = useRef('');
  const lastNodeIds = useRef<string[]>([]);
  const fileSignature = files.map(f => f.path + ':' + f.sha256).sort().join('|');

  useEffect(() => {
    setSnapshot(null);
    setError('');
    setImportedFile(null);
    setSelected(null);
    setFocus(null);
    revision.current = '';
    lastNodeIds.current = [];
    setRecentIds([]);
    setTraceFrom(null);
    setTraceResult(null);
    setTraceError('');
    setSourceEvents([]);
    setReplayStatus('');
    setEvidence(null);
    setReplayFrames([]);
    setReplaySequence(null);
    setReplayGraph(null);
    setReplayError('');
    replayRequest.current++;
  }, [runId]);

  useEffect(() => {
    if (importedGraph) {
      setSnapshot(importedGraph);
      setError('');
      return;
    }
    let cancelled = false;
    async function load() {
      try {
        const value = await api<AtlasSnapshot>(token,
          '/api/tasks/' + taskId + '/engineering-runs/' + runId + '/atlas');
        if (cancelled) return;
        const existing = revision.current;
        const previousIds = new Set(lastNodeIds.current);
        const added = existing && existing !== value.sourceRevision
          ? value.nodes.filter(n => !previousIds.has(n.id))
          : [];
        revision.current = value.sourceRevision;
        lastNodeIds.current = value.nodes.map(n => n.id);
        setSnapshot(value);
        setError('');
        if (existing && existing !== value.sourceRevision) {
          setTraceResult(null);
          setTraceError('');
          setRecentIds(added.map(n => n.id));
          if (followBuild && added.length) {
            // Follow a node that the current abstraction level can actually
            // display. Otherwise, focus its known containing type.
            const eligible = added.filter(n =>
              showMethods || !SKIP_METHODS.has(n.kind));
            const ranked = [...eligible].sort((a, b) => {
              const importance = (kind: string) =>
                kind === 'Method' || kind === 'Constructor' ? 0 :
                kind === 'Class' || kind === 'Interface' ? 1 : 2;
              return importance(a.kind) - importance(b.kind);
            });
            const next = ranked[0] ?? value.nodes.find(n =>
              n.id === added[0].parentId) ?? null;
            if (next) {
              setSelected(next.id);
              setFocus(next.id);
            }
          }
        }
      } catch {
        if (!cancelled) setError('Code Atlas could not load this saved source graph.');
      }
    }
    void load();
    // EngineeringWorkspace already polls accepted source every 3 seconds.
    // A new persisted file signature drives this projection refresh.
    return () => { cancelled = true; };
  }, [token, taskId, runId, fileSignature, followBuild, showMethods, importedGraph]);


  // Subscribe first, then replay durable events to close the subscribe/read
  // race. Signals contain only revision metadata, never untrusted source.
  // Polling remains a bounded fallback when SignalR is unavailable.
  useEffect(() => {
    if (importedGraph) return;
    let closed = false;
    let lastSequence = 0;
    let syncing = false;
    let again = false;
    const base = '/api/tasks/' + taskId + '/engineering-runs/' + runId;
    async function reconcile() {
      if (syncing) { again = true; return; }
      syncing = true;
      let changed = false;
      try {
        for (let page = 0; page < 20; page++) {
          const result = await api<AtlasSourceEventPage>(
            token, base + '/atlas-events?after=' + lastSequence);
          if (closed) return;
          if (result.runId !== runId)
            throw new Error('Atlas source scope changed');
          for (const event of result.events) {
            if (event.sequence <= lastSequence) continue;
            if (event.sequence !== lastSequence + 1) {
              // Never silently misorder or invent a missing change.
              throw new Error('Atlas source history has a sequence gap');
            }
            lastSequence = event.sequence;
            changed = true;
          }
          if (result.events.length) {
            setSourceEvents(previous => {
              const known = new Set(previous.map(event => event.sequence));
              return [...previous, ...result.events.filter(event =>
                !known.has(event.sequence))].slice(-24);
            });
          }
          if (lastSequence >= result.lastSequence) break;
          if (result.events.length === 0)
            throw new Error('Atlas source history could not advance');
        }
        setReplayStatus('');
        if (changed) onSourceChanged?.();
      } catch {
        if (!closed) setReplayStatus(
          'Atlas event connection is recovering; saved source remains available.');
      } finally {
        syncing = false;
        if (again && !closed) {
          again = false;
          void reconcile();
        }
      }
    }
    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/api/progress', {
        transport: signalR.HttpTransportType.LongPolling,
        accessTokenFactory: () => token,
      })
      .withAutomaticReconnect([0, 2000, 10000])
      .build();
    connection.on('AtlasUpdated', (hint: { runId: string }) => {
      if (hint.runId === runId) void reconcile();
    });
    connection.onreconnected(async () => {
      try {
        await connection.invoke('WatchAtlas', taskId, runId);
        if (!closed) await reconcile();
      } catch {
        if (!closed) setReplayStatus('Atlas notifications unavailable; using replay.');
      }
    });
    void connection.start().then(async () => {
      if (closed) return;
      await connection.invoke('WatchAtlas', taskId, runId);
      await reconcile();
    }).catch(() => {
      if (!closed) void reconcile();
    });
    const timer = window.setInterval(() => void reconcile(), 3000);
    return () => {
      closed = true;
      window.clearInterval(timer);
      void connection.stop();
    };
  }, [token, taskId, runId, onSourceChanged, importedGraph]);

  useEffect(() => {
    if (importedGraph) return;
    let cancelled = false;
    async function loadFrames() {
      try {
        const manifest = await api<AtlasReplayManifest>(token,
          '/api/tasks/' + taskId + '/engineering-runs/' + runId +
          '/atlas/replay');
        if (!cancelled && manifest.runId === runId)
          setReplayFrames(manifest.frames.slice(-64));
      } catch {
        if (!cancelled) setReplayError(
          'Historical graph checkpoints could not be loaded.');
      }
    }
    void loadFrames();
    return () => { cancelled = true; };
  }, [token, taskId, runId, fileSignature, importedGraph]);

  // QA can complete after source generation. Poll the existing owner-private,
  // project-bound evidence projection without altering agent permissions.
  useEffect(() => {
    if (importedGraph) return;
    let cancelled = false;
    async function loadEvidence() {
      try {
        const result = await api<AtlasEvidenceOverlay>(token,
          '/api/tasks/' + taskId + '/engineering-runs/' + runId +
          '/atlas/evidence');
        if (!cancelled && result.runId === runId) setEvidence(result);
      } catch {
        // Atlas itself remains usable if an earlier version lacks overlays.
        if (!cancelled) setEvidence(null);
      }
    }
    void loadEvidence();
    const timer = window.setInterval(() => void loadEvidence(), 5000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [token, taskId, runId, fileSignature, importedGraph]);

  async function selectReplay(sequence: number | null) {
    const request = ++replayRequest.current;
    setReplayError('');
    setTraceFrom(null);
    setTraceResult(null);
    setSelected(null);
    setFocus(null);
    setQuery('');
    if (sequence === null) {
      setReplaySequence(null);
      setReplayGraph(null);
      setReplayBusy(false);
      return;
    }
    setReplayBusy(true);
    try {
      const frame = await api<AtlasReplayFrame>(token,
        '/api/tasks/' + taskId + '/engineering-runs/' + runId +
        '/atlas/replay/' + sequence);
      if (request !== replayRequest.current) return;
      if (frame.sequence !== sequence || frame.graph.runId !== runId ||
          frame.graph.sourceRevision !== frame.sourceRevision)
        throw new Error('Historical source revision mismatch');
      setReplaySequence(sequence);
      setReplayGraph(frame.graph);
    } catch {
      if (request === replayRequest.current)
        setReplayError('This historical graph checkpoint is unavailable.');
    } finally {
      if (request === replayRequest.current) setReplayBusy(false);
    }
  }
  const displayGraph = replayGraph ?? snapshot;
  const replayIndex = replaySequence === null ? replayFrames.length :
    replayFrames.findIndex(frame => frame.sequence === replaySequence);
  const replayStep = replayIndex < 0 ? replayFrames.length : replayIndex;

  const view = useMemo(() => displayGraph ?
    projectAtlas(displayGraph, focus, showMethods, query) : null,
    [displayGraph, focus, showMethods, query]);
  const node = displayGraph?.nodes.find(n => n.id === selected);
  const allLinks = node && displayGraph ?
    relatedEdges(displayGraph, node.id) : [];
  const links = allLinks.filter(edge => relationshipDirection === 'all' ||
    (relationshipDirection === 'incoming' ? edge.targetId === node?.id :
      edge.sourceId === node?.id));
  const names = new Map(displayGraph?.nodes.map(n => [n.id, n.name]) ?? []);
  // Current source text must NEVER be mislabeled as an old revision's content.
  const excerpt = replaySequence === null
    ? selectedExcerpt(node, importedFile && importedFile.path === node?.path
      ? [{ ...importedFile, purpose: 'Imported source',
           lastWorkItemId: '', updatedUtc: '' }] : files)
    : '';
  // Large imported source texts are never bulk-delivered with the graph.
  // Fetch only the selected file, scoped to this exact imported commit.
  useEffect(() => {
    if (!importedGraph || !node?.path) {
      setImportedFile(null);
      return;
    }
    let cancelled = false;
    setImportedFile(null);
    void api<AtlasImportedFile>(token,
      '/api/tasks/' + taskId + '/atlas-imports/' + runId +
      '/file?path=' + encodeURIComponent(node.path)).then(file => {
        if (!cancelled && file.path === node.path)
          setImportedFile(file);
      }).catch(() => {
        if (!cancelled) setImportedFile(null);
      });
    return () => { cancelled = true; };
  }, [token, taskId, runId, importedGraph, node?.path]);
  const currentEvidence = replaySequence === null && evidence?.runId === runId &&
    evidence?.sourceRevision === snapshot?.sourceRevision ? evidence : null;
  // These are FILE-level QA hypotheses, never claims about a particular method.
  const selectedPath = node?.path;
  const nodeFindings = selectedPath && currentEvidence ? currentEvidence.findings
    .filter(item => item.path === selectedPath) : [];
  const nodeRepairs = selectedPath && currentEvidence ? currentEvidence.repairs
    .filter(repair => repair.fileNodeIds.some(id =>
      displayGraph?.nodes.find(file => file.id === id)?.path === selectedPath)) : [];
  const findingNodeIds = new Set(currentEvidence?.findings.map(f =>
    f.fileNodeId) ?? []);
  const currentChange = replaySequence === null &&
    sourceEvents.at(-1)?.sourceRevision === snapshot?.sourceRevision
    ? sourceEvents.at(-1) : null;
  const modifiedPaths = new Set(currentChange?.changes
    .filter(change => change.kind === 'Modified')
    .map(change => change.path) ?? []);

  async function tracePath(to: string) {
    if (!traceFrom || traceBusy) return;
    setTraceBusy(true);
    setTraceError('');
    setTraceResult(null);
    try {
      const path = await api<AtlasPathResult>(token,
        '/api/tasks/' + taskId +
        (importedGraph ? '/atlas-imports/' : '/engineering-runs/') +
        runId + '/path?from=' + encodeURIComponent(traceFrom) +
        '&to=' + encodeURIComponent(to) + '&maxHops=6');
      if (path.fromId !== traceFrom || path.toId !== to)
        throw new Error('Path identity mismatch');
      setTraceResult(path);
    } catch {
      setTraceError('Could not trace this source dependency path.');
    } finally { setTraceBusy(false); }
  }

  function reset() {
    setSelected(null); setFocus(null); setQuery('');
    setTraceFrom(null); setTraceResult(null); setTraceError('');
    void selectReplay(null);
    setZoom(1); setOffset({ x: 0, y: 0 });
  }

  return <section className="atlas-panel" aria-label="Code Atlas">
    <header className="atlas-heading">
      <div>
        <span className="wb-kicker">CODE ATLAS / SOURCE CONSTELLATION</span>
        <h3>{importedGraph ? 'Explore the imported architecture' :
          'Watch the architecture take shape'}</h3>
        <p>{importedGraph ?
          'Commit-pinned GitHub source, indexed read-only. Not built, executed, or verified.' :
          'Source-derived structure from saved Software Engineer files. Not compiled, executed, or verified.'}</p>
      </div>
      <span className="atlas-live-indicator" data-running={running && !importedGraph}>
        <span aria-hidden="true" />{importedGraph ? 'Imported commit' :
          running ? 'Observing source' : 'Saved snapshot'}
      </span>
    </header>
    <div className="atlas-controls">
      <label className="atlas-search">Find a symbol
        <input type="search" value={query}
          onChange={event => {
            const search = event.target.value;
            setQuery(search); setFocus(null); setFollowBuild(false);
            const found = search.trim() ? displayGraph?.nodes.find(item =>
              (item.qualifiedName + ' ' + item.name).toLowerCase()
                .includes(search.trim().toLowerCase())) : null;
            setSelected(found?.id ?? null);
          }}
          placeholder="Class, method, namespace…" />
      </label>
      <label className="atlas-toggle"><input type="checkbox"
        checked={showMethods} onChange={e => setShowMethods(e.target.checked)} />
        Methods
      </label>
      {!importedGraph && <label className="atlas-toggle"><input type="checkbox"
        checked={followBuild} onChange={e => setFollowBuild(e.target.checked)} />
        Follow build
      </label>}
      <button type="button" className="outline" onClick={reset}>Overview</button>
    </div>
    {error && <p role="alert" className="atlas-error">{error}</p>}
    {!snapshot && !error && <p role="status" className="atlas-empty">
      {importedGraph ? 'Reading imported source structure…' :
        'Reading saved source structure…'}</p>}
    {displayGraph && view && <div className="atlas-workspace">
      <div className="atlas-canvas-wrap">
        {view.nodes.length === 1 && displayGraph.nodes.length === 1
          ? <p className="atlas-empty">No source files have been saved yet.
              The graph will grow after each completed engineering assignment.</p>
          : <>
            <svg className="atlas-canvas" viewBox="-480 -300 960 600"
              role="group" aria-label="Interactive Code Atlas graph of saved source"
              onPointerDown={e => {
                if (e.target !== e.currentTarget) return;
                setFollowBuild(false);
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
              onWheel={e => {
                if (e.ctrlKey || e.altKey) return;
                e.preventDefault();
                setFollowBuild(false);
                setZoom(current => Math.max(.45, Math.min(2.4,
                  current * (e.deltaY > 0 ? .91 : 1.09))));
              }}
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
                  const traced = traceResult?.edges.some(pathEdge =>
                    pathEdge.kind === edge.kind &&
                    pathEdge.sourceId === edge.sourceId &&
                    pathEdge.targetId === edge.targetId);
                  return <line key={edge.kind + edge.sourceId + edge.targetId}
                    x1={from.x} y1={from.y} x2={to.x} y2={to.y}
                    className={'atlas-edge' + (relevant ? ' atlas-related' : '') +
                      (traced ? ' atlas-traced' : '')}/>;
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
                      (current.kind === 'File' && findingNodeIds.has(current.id)
                        ? ' atlas-qa-flag' : '') +
                      (relevant ? ' atlas-connected' : '') +
                      (replaySequence === null &&
                        recentIds.includes(current.id) ? ' atlas-new' : '') +
                      (replaySequence === null && current.path &&
                        modifiedPaths.has(current.path) &&
                        !recentIds.includes(current.id) ? ' atlas-modified' : '')}
                    onClick={() => { setSelected(current.id); setFollowBuild(false); }}
                    onKeyDown={event => {
                      if (event.key === 'Enter' || event.key === ' ') {
                        event.preventDefault();
                        setSelected(current.id);
                        setFollowBuild(false);
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
          <span>{recentIds.length ? recentIds.length + ' newly discovered · ' : ''}{view.nodes.length} of {view.total} symbols
            {' · '}{view.edges.length} visible links</span>
          <span>{replaySequence !== null ? 'Historical frame · ' : ''}
            {displayGraph.coverage === 'STATIC_SEMANTIC' ? 'Static C# relationships' :
            displayGraph.coverage === 'PARTIAL_SEMANTIC' ? 'Partial static relationships' :
            displayGraph.coverage === 'SYNTAX_ONLY' ? 'Syntax indexed' :
            displayGraph.coverage === 'PARTIAL_SYNTAX' ? 'Partial C# syntax' :
              displayGraph.coverage}</span>
        </div>
      </div>
      <aside className="atlas-details" aria-label="Selected source entity">
        {node ? <>
          <span className="wb-kicker">{node.kind.toUpperCase()}</span>
          <h4>{node.name}</h4>
          <p className="atlas-qualified">{node.qualifiedName}</p>
          {node.path && modifiedPaths.has(node.path) &&
            <p className="atlas-change-label">Updated in saved source revision</p>}
          <dl>
            <dt>Source</dt>
            <dd>{node.path ?? 'Logical group'}
              {node.line ? ':' + node.line : ''}</dd>
            <dt>Known relationships</dt>
            <dd>{links.length}</dd>
          </dl>
          <button type="button" className="outline"
            onClick={() => { setFocus(node.id); setQuery(''); setFollowBuild(false); }}>
            Focus relationships
          </button>
          {replaySequence !== null && <p className="atlas-path-hint">
            Historical graph only. Current source excerpts and live path
            tracing are disabled for this older revision.</p>}
          {replaySequence === null && <div className="atlas-path-actions">
            <button type="button" className="outline"
              aria-pressed={traceFrom === node.id}
              onClick={() => { setTraceFrom(node.id); setTraceResult(null); setTraceError(''); }}>
              {traceFrom === node.id ? 'Path start selected' : 'Set path start'}
            </button>
            {traceFrom && traceFrom !== node.id && <button type="button"
              className="outline" disabled={traceBusy}
              onClick={() => void tracePath(node.id)}>
              {traceBusy ? 'Tracing…' : 'Trace path to this node'}
            </button>}
          </div>}
          {traceFrom && replaySequence === null &&
            <p className="atlas-path-hint">Path start: {
            names.get(traceFrom) ?? 'Selected source'}</p>}
          {traceError && <p role="alert" className="atlas-error">{traceError}</p>}
          {traceResult && <section className="atlas-path-result"
            aria-label="Dependency path result">
            <strong>{traceResult.found ? 'Directed path found' :
              traceResult.truncated ? 'Search limit reached' : 'No path found within six hops'}</strong>
            {traceResult.found && <ol>{traceResult.edges.map((edge, index) =>
              <li key={index}>{names.get(edge.sourceId) ?? 'Unknown'}
                {' → '}{edge.kind}{' → '}{names.get(edge.targetId) ?? 'Unknown'}
                {edge.evidencePath && <small> {edge.evidencePath}:{edge.evidenceLine}</small>}
              </li>)}</ol>}
          </section>}
          {replaySequence === null && nodeFindings.length > 0 &&
            <section className="atlas-qa-evidence" aria-label="Static QA findings">
              <strong>Static QA · hypotheses, not test failures</strong>
              <p>Findings refer to this source file, not necessarily the selected symbol.</p>
              {nodeFindings.slice(0, 6).map(finding =>
                <article key={finding.id}>
                  <b>{finding.severity}: {finding.requirement}</b>
                  <p>{finding.evidence}</p>
                  <small>Suggested check: {finding.recommendation}</small>
                </article>)}
            </section>}
          {replaySequence === null && nodeRepairs.length > 0 &&
            <section className="atlas-repair-evidence" aria-label="Related repair attempts">
              <strong>Related repair attempts · unverified</strong>
              {nodeRepairs.slice(0, 3).map(repair =>
                <p key={repair.cycleId}>Repair {repair.cycleId.slice(0, 8)}:
                  {' '}{repair.state}. Independent QA is separate.</p>)}
            </section>}
          <div className="atlas-relation-filters" role="group"
            aria-label="Relationship direction">
            {(['all', 'outgoing', 'incoming'] as const).map(direction =>
              <button key={direction} type="button"
                aria-pressed={relationshipDirection === direction}
                onClick={() => setRelationshipDirection(direction)}>
                {direction === 'all' ? 'All' :
                  direction === 'outgoing' ? 'Uses →' : '← Used by'}
              </button>)}
          </div>
          <div className="atlas-relationships">
            {links.slice(0, 16).map(edge => {
              const other = edge.sourceId === node.id ?
                edge.targetId : edge.sourceId;
              return <button type="button" key={edge.kind + ':' + edge.sourceId +
                ':' + edge.targetId} onClick={() => { setSelected(other); setFollowBuild(false); }}>
                <span>{edge.sourceId === node.id ? '→' : '←'} {edge.kind}</span>
                {' '}{names.get(other) ?? 'Unknown symbol'}
                {edge.resolution === 'ResolvedStatic' &&
                  <small className="atlas-evidence">
                    {edge.evidencePath}:{edge.evidenceLine} · Static reference
                  </small>}
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
    {currentEvidence && (currentEvidence.qaReviewStatus !== 'NOT_REVIEWED' ||
      currentEvidence.repairs.length > 0) &&
      <div className="atlas-qa-summary" role="status">
        <strong>QA evidence</strong>
        <span>{currentEvidence.findings.length} static finding(s)</span>
        <span>{currentEvidence.repairs.length} related repair(s)</span>
        <span>Runtime tests: NOT RUN</span>
      </div>}
    {replayFrames.length > 0 && <section className="atlas-replay"
      aria-label="Code Atlas Build Replay">
      <div className="atlas-replay-heading">
        <strong>Build Replay</strong>
        <span>{replaySequence === null ? 'Live saved source' :
          'Historical work item #' + replaySequence}</span>
      </div>
      <div className="atlas-replay-controls">
        <button type="button" disabled={replayBusy || replayStep <= 0}
          onClick={() => void selectReplay(
            replayFrames[replayStep - 1].sequence)}>Previous</button>
        <input type="range" min={0} max={replayFrames.length} step={1}
          aria-label="Build Replay timeline"
          value={replayStep} disabled={replayBusy}
          onChange={event => {
            const index = Number(event.target.value);
            void selectReplay(index === replayFrames.length ?
              null : replayFrames[index].sequence);
          }}/>
        <button type="button" disabled={replayBusy ||
            replayStep >= replayFrames.length}
          onClick={() => void selectReplay(
            replayStep + 1 === replayFrames.length ? null :
            replayFrames[replayStep + 1].sequence)}>Next</button>
        <button type="button" className="outline"
          disabled={replayBusy || replaySequence === null}
          onClick={() => void selectReplay(null)}>Return live</button>
      </div>
      {replayError && <p role="alert" className="atlas-error">{replayError}</p>}
      {replaySequence !== null && <p className="atlas-replay-caption">
        Snapshot recorded for {replayFrames.find(frame =>
          frame.sequence === replaySequence)?.workItemId ?? 'saved work'}.
        Historical node identities and source locations are preserved.
        Source contents are not replayed.
      </p>}
    </section>}
    {sourceEvents.length > 0 && <details className="atlas-source-history">
      <summary>Recorded source changes ({sourceEvents.length})</summary>
      <ol aria-label="Committed Code Atlas source events">
        {sourceEvents.slice().reverse().slice(0, 8).map(event => <li key={event.sequence}>
          <strong>#{event.sequence} · {event.workItemId}</strong>
          <span>{event.changes.map(change =>
            change.kind + ': ' + change.path).join(' · ')}</span>
          {event.graphDelta && <span className="atlas-event-symbols">
            {' · '}{event.graphDelta.addedNodeIds.length} new symbols
            {' · '}{event.graphDelta.updatedNodeIds.length} updated
            {' · '}{event.graphDelta.removedNodeIds.length} removed
          </span>}
        </li>)}
      </ol>
    </details>}
    {replayStatus && <p className="atlas-coverage-note" role="status">{replayStatus}</p>}
    {snapshot && snapshot.diagnostics.length > 0 &&
      <p className="atlas-coverage-note" role="status">
        Some source could not be completely indexed. The graph contains
        only relationships supported by the current analysis.</p>}
  </section>;
}
