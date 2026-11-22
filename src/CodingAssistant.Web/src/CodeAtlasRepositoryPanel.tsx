import { useEffect, useRef, useState } from 'react';
import { api } from './api';
import { CodeAtlasPanel, type AtlasSnapshot } from './CodeAtlasPanel';

export interface AtlasImportedRepository {
  id: string;
  taskId: string;
  repositoryUrl: string;
  commitSha: string;
  sourceRevision: string;
  coverage: string;
  indexedFiles: number;
  excludedFiles: number;
  createdUtc: string;
}
interface ImportedGraph {
  repository: AtlasImportedRepository;
  graph: AtlasSnapshot;
}
interface CommitDriftChange {
  operation: string;
  category: string;
  kind: string;
  identity: string;
}
interface CommitDriftReport {
  repositoryUrl: string;
  fromImportId: string;
  toImportId: string;
  fromCommitSha: string;
  toCommitSha: string;
  fromRevision: string;
  toRevision: string;
  addedEntities: number;
  removedEntities: number;
  addedRelationships: number;
  removedRelationships: number;
  truncated: boolean;
  changes: CommitDriftChange[];
}

/// <summary>
/// Project-scoped, read-only GitHub source import UI. Import requires a literal
/// 40-character commit SHA, not mutable "main", tags, branches or live code
/// execution. Uses the SAME graph renderer as Software Engineer Code Atlas.
/// </summary>
export function CodeAtlasRepositoryPanel({ token, taskId }: {
  token: string;
  taskId: string;
}) {
  const [items, setItems] = useState<AtlasImportedRepository[]>([]);
  const [repoUrl, setRepoUrl] = useState('');
  const [commitSha, setCommitSha] = useState('');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [selectedGraph, setSelectedGraph] = useState<ImportedGraph | null>(null);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [comparisonFrom, setComparisonFrom] = useState('');
  const [drift, setDrift] = useState<CommitDriftReport | null>(null);
  const [driftBusy, setDriftBusy] = useState(false);
  const [driftError, setDriftError] = useState('');
  const comparisonRequest = useRef(0);

  useEffect(() => {
    let closed = false;
    setItems([]);
    setSelectedId(null);
    setSelectedGraph(null);
    setError('');
    comparisonRequest.current++;
    setComparisonFrom('');
    setDrift(null);
    setDriftError('');
    void api<AtlasImportedRepository[]>(token,
      '/api/tasks/' + taskId + '/atlas-imports').then(results => {
      if (!closed) setItems(results);
    }).catch(() => {
      if (!closed) setError('Could not read saved repository imports.');
    });
    return () => { closed = true; };
  }, [token, taskId]);

  async function open(importId: string) {
    if (busy) return;
    setBusy('open');
    setError('');
    try {
      const result = await api<ImportedGraph>(token,
        '/api/tasks/' + taskId + '/atlas-imports/' + importId);
      if (result.repository.id !== importId ||
          result.repository.taskId !== taskId ||
          result.graph.runId !== importId ||
          result.graph.sourceRevision !== result.repository.sourceRevision)
        throw new Error('Imported graph identity does not match its source revision.');
      comparisonRequest.current++;
      setSelectedGraph(result);
      setSelectedId(importId);
      setComparisonFrom('');
      setDrift(null);
      setDriftError('');
    } catch (cause) {
      setError(cause instanceof Error ? cause.message :
        'Could not open the imported repository.');
    } finally {
      setBusy('');
    }
  }

  async function importRepository() {
    if (busy) return;
    if (!/^https:\/\/github\.com\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+(?:\.git)?\/?$/.test(repoUrl) ||
        !/^[a-f0-9]{40}$/.test(commitSha)) {
      setError('Enter a public GitHub repository URL and an exact 40-character commit SHA.');
      return;
    }
    setBusy('import');
    setError('');
    try {
      const result = await api<AtlasImportedRepository>(token,
        '/api/tasks/' + taskId + '/atlas-imports',
        { repositoryUrl: repoUrl, commitSha });
      setItems(previous => [result,
        ...previous.filter(item => item.id !== result.id)]);
      // Wait until POST has durably saved the graph, then open it.
      setBusy('');
      await open(result.id);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message :
        'Import was not completed.');
    } finally { setBusy(''); }
  }

  const comparisons = selectedGraph ? items.filter(item =>
    item.id !== selectedGraph.repository.id &&
    item.repositoryUrl.toLowerCase() ===
      selectedGraph.repository.repositoryUrl.toLowerCase()) : [];
  const starting = comparisons.find(item => item.id === comparisonFrom);

  async function compareCommits() {
    if (!starting || !selectedGraph || busy || driftBusy) return;
    const request = ++comparisonRequest.current;
    const target = selectedGraph.repository;
    setDrift(null);
    setDriftError('');
    setDriftBusy(true);
    try {
      const result = await api<CommitDriftReport>(token,
        '/api/tasks/' + taskId + '/atlas-imports/drift?from=' +
        encodeURIComponent(starting.id) + '&to=' +
        encodeURIComponent(target.id));
      if (request !== comparisonRequest.current) return;
      if (result.fromImportId !== starting.id ||
          result.toImportId !== target.id ||
          result.fromCommitSha !== starting.commitSha ||
          result.toCommitSha !== target.commitSha ||
          result.fromRevision !== starting.sourceRevision ||
          result.toRevision !== target.sourceRevision)
        throw new Error('Commit comparison identity mismatch');
      setDrift(result);
    } catch {
      if (request === comparisonRequest.current)
        setDriftError('Saved commit comparison could not be verified.');
    } finally {
      if (request === comparisonRequest.current) setDriftBusy(false);
    }
  }

  return <section className="atlas-repository-import"
    aria-label="Code Atlas GitHub repository import">
    <div className="atlas-import-heading">
      <strong>Explore existing code</strong>
      <span>Public GitHub · exact commit · read-only</span>
    </div>
    <p>Import an existing public GitHub repository at a specific commit.
      Code Atlas indexes its source without executing builds, scripts or tests.
      Private repositories and mutable branch names are not supported yet.</p>
    <div className="atlas-import-fields">
      <label>GitHub repository URL
        <input type="url" autoComplete="off" spellCheck={false}
          value={repoUrl} onChange={event => setRepoUrl(event.target.value)}
          placeholder="https://github.com/owner/repository" />
      </label>
      <label>Full commit SHA
        <input type="text" autoComplete="off" spellCheck={false}
          value={commitSha}
          onChange={event => setCommitSha(event.target.value.trim().toLowerCase())}
          placeholder="40-character Git commit hash" maxLength={40} />
      </label>
      <button type="button" disabled={!!busy}
        onClick={() => void importRepository()}>
        {busy === 'import' ? 'Indexing repository…' : 'Import source'}
      </button>
    </div>
    {error && <p className="atlas-import-error" role="alert">{error}</p>}
    {items.length > 0 && <div className="atlas-imported-list">
      <strong>Imported repositories</strong>
      <div className="atlas-imported-rows">
        {items.map(item => <button key={item.id} type="button"
          className={item.id === selectedId ? 'atlas-import-selected' : ''}
          disabled={!!busy}
          onClick={() => void open(item.id)}>
          <span>{item.repositoryUrl.replace('https://github.com/', '')}</span>
          <small>{item.commitSha.slice(0, 10)} · {item.indexedFiles} source files
            {item.excludedFiles ? ' · ' + item.excludedFiles + ' excluded' : ''}
          </small>
        </button>)}
      </div>
    </div>}
    {selectedGraph && comparisons.length > 0 && <section
      className="atlas-import-drift" aria-label="Imported commit architecture comparison">
      <strong>Compare imported commits</strong>
      <p>Choose a starting commit from the same repository.
        This compares saved source, not Git history or runtime behavior.</p>
      <div className="atlas-import-drift-controls">
        <label>Starting commit
          <select value={comparisonFrom} onChange={event => {
            comparisonRequest.current++;
            setComparisonFrom(event.target.value);
            setDrift(null);
            setDriftError('');
            setDriftBusy(false);
          }}>
            <option value="">Choose saved commit</option>
            {comparisons.map(item => <option key={item.id} value={item.id}>
              {item.commitSha.slice(0, 12)}
            </option>)}
          </select>
        </label>
        <button type="button" disabled={!starting || !!busy || driftBusy}
          onClick={() => void compareCommits()}>
          {driftBusy ? 'Comparing…' : 'Compare architecture'}
        </button>
      </div>
      {driftError && <p role="alert" className="atlas-import-error">{driftError}</p>}
      {drift && <div role="region" className="atlas-import-drift-result"
        aria-label="Imported architecture drift result">
        <strong>{drift.fromCommitSha.slice(0, 12)} → {drift.toCommitSha.slice(0, 12)}</strong>
        <p>{drift.addedEntities} added / {drift.removedEntities} removed entities
          {' · '}{drift.addedRelationships} added / {drift.removedRelationships} removed relationships</p>
        <p>Static saved-source differences only. Not runtime verification.</p>
        {drift.changes.length > 0 ? <ul>
          {drift.changes.map((change, index) => <li key={index}>
            <strong>{change.operation} {change.kind}</strong>
            <span>{change.identity}</span>
          </li>)}
        </ul> : <p>No architecture changes in indexed source.</p>}
        {drift.truncated && <p>Showing the first {drift.changes.length} changes.
          Totals cover all indexed changes.</p>}
      </div>}
    </section>}
    {selectedGraph && selectedId && <CodeAtlasPanel
      key={selectedId} token={token} taskId={taskId}
      runId={selectedId} files={[]} running={false}
      importedGraph={selectedGraph.graph}/>}
  </section>;
}
