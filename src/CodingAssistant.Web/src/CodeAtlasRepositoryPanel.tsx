import { useEffect, useState } from 'react';
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

  useEffect(() => {
    let closed = false;
    setItems([]);
    setSelectedId(null);
    setSelectedGraph(null);
    setError('');
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
      setSelectedGraph(result);
      setSelectedId(importId);
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
    {selectedGraph && selectedId && <CodeAtlasPanel
      key={selectedId} token={token} taskId={taskId}
      runId={selectedId} files={[]} running={false}
      importedGraph={selectedGraph.graph}/>}
  </section>;
}
