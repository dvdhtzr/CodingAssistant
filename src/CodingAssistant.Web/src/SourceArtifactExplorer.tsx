import { useId, useMemo, useState } from 'react';

export interface SourceArtifactFile {
  path: string;
  purpose: string;
  content: string;
  sha256: string;
  lastWorkItemId?: string;
  updatedUtc?: string;
  createdUtc?: string;
}
export interface SourceTaskReference {
  id: string;
  title: string;
  ordinal: number;
}
export interface SourceFolderNode {
  name: string;
  path: string;
  folders: SourceFolderNode[];
  files: SourceArtifactFile[];
  count: number;
}
const MAX_NUMBERED_LINES = 750;

function pathOrder(left: string, right: string) {
  return left.localeCompare(right, 'en', { sensitivity: 'base', numeric: true });
}

/** Pure presentation tree from already validated, persisted source paths. */
export function buildSourceTree(files: readonly SourceArtifactFile[]): SourceFolderNode {
  const root: SourceFolderNode = {
    name: '', path: '', folders: [], files: [], count: 0,
  };
  for (const file of files) {
    const parts = file.path.split('/');
    let current = root;
    current.count++;
    for (const part of parts.slice(0, -1)) {
      let child = current.folders.find(folder => folder.name === part);
      if (!child) {
        child = {
          name: part, path: current.path ? current.path + '/' + part : part,
          folders: [], files: [], count: 0,
        };
        current.folders.push(child);
      }
      current = child;
      current.count++;
    }
    current.files.push(file);
  }
  function sort(node: SourceFolderNode) {
    node.folders.sort((a, b) => pathOrder(a.name, b.name));
    node.files.sort((a, b) => pathOrder(a.path, b.path));
    node.folders.forEach(sort);
  }
  sort(root);
  return root;
}

export function sourceLanguage(path: string): string {
  const suffix = path.split('.').pop()?.toLowerCase() ?? '';
  return ({
    cs: 'C#', csproj: 'MSBuild', fs: 'F#', ts: 'TypeScript',
    tsx: 'TypeScript JSX', js: 'JavaScript', jsx: 'JavaScript JSX',
    json: 'JSON', md: 'Markdown', css: 'CSS', scss: 'SCSS',
    html: 'HTML', py: 'Python', sql: 'SQL', xml: 'XML',
    yaml: 'YAML', yml: 'YAML', sh: 'Shell', ps1: 'PowerShell',
    go: 'Go', rs: 'Rust', java: 'Java', kt: 'Kotlin',
    toml: 'TOML', dockerfile: 'Dockerfile',
  } as Record<string, string>)[suffix] ?? (path.split('/').pop() === 'Dockerfile'
    ? 'Dockerfile' : 'Plain text');
}

function FolderTree({ folder, selectedPath, onSelect, depth, searching }: {
  folder: SourceFolderNode;
  selectedPath: string;
  onSelect: (path: string) => void;
  depth: number;
  searching: boolean;
}) {
  return <>
    {folder.folders.map(child => <details
      className="source-tree-folder" key={child.path}
      open={searching || selectedPath.startsWith(child.path + '/') ? true : undefined}>
      <summary>
        <span className="source-tree-folder-symbol" aria-hidden="true">▸</span>
        <span className="source-tree-folder-name">{child.name}</span>
        <span className="source-tree-folder-count">{child.count}</span>
      </summary>
      <div className="source-tree-children">
        <FolderTree folder={child} selectedPath={selectedPath} onSelect={onSelect}
          depth={depth + 1} searching={searching}/>
      </div>
    </details>)}
    {folder.files.map(file => <button type="button" key={file.path}
      className={'source-tree-file' + (file.path === selectedPath ? ' source-tree-selected' : '')}
      aria-pressed={file.path === selectedPath}
      title={file.path}
      onClick={() => onSelect(file.path)}>
      <span aria-hidden="true" className="source-tree-file-symbol">≡</span>
      <span className="source-tree-file-name">{file.path.split('/').pop()}</span>
    </button>)}
  </>;
}

/**
 * Actual persisted MAF or legacy file content only. Never evaluates code,
 * fetches arbitrary paths, grants a skill, or claims build/test verification.
 */
export function SourceArtifactExplorer({ files, source, state, runId,
  isCurrent, assignments = [] }: {
  files: readonly SourceArtifactFile[];
  source: 'MAF Software Engineer' | 'Classic generator';
  state: 'Running' | 'Generated' | 'Failed' | 'Interrupted';
  runId: string;
  isCurrent: boolean;
  assignments?: readonly SourceTaskReference[];
}) {
  const searchId = useId();
  const [search, setSearch] = useState('');
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [fullFile, setFullFile] = useState(false);
  const [feedback, setFeedback] = useState('');
  const visible = useMemo(() => files.filter(file => (
    file.path + ' ' + file.purpose
  ).toLocaleLowerCase().includes(search.trim().toLocaleLowerCase())), [files, search]);
  const tree = useMemo(() => buildSourceTree(visible), [visible]);
  const selected = visible.find(file => file.path === selectedPath) ??
    [...visible].sort((a, b) => pathOrder(a.path, b.path))[0];
  const lines = selected?.content.split(/\r?\n/) ?? [];
  const truncated = lines.length > MAX_NUMBERED_LINES && !fullFile;
  const displayed = truncated ? lines.slice(0, MAX_NUMBERED_LINES) : lines;
  const task = assignments.find(item => item.id === selected?.lastWorkItemId);

  async function copy(value: string, label: string) {
    try {
      if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable.');
      await navigator.clipboard.writeText(value);
      setFeedback(label + ' copied.');
    } catch {
      setFeedback('Clipboard unavailable. Select text directly from the preview.');
    }
  }

  return <section className="source-explorer"
    aria-label={source + ' saved source explorer'}>
    <div className="source-explorer-heading">
      <div>
        <span className="wb-kicker">SOURCE & ARTIFACT EXPLORER</span>
        <h3>{source}</h3>
        <p>{files.length} saved file{files.length === 1 ? '' : 's'} ·
          {' '}{state === 'Generated' ? 'Generated source — unverified' :
            'Partial or in-progress source — unverified'}</p>
      </div>
      <span className="source-explorer-state" data-state={state}>
        {state} · NOT TESTED
      </span>
    </div>
    {!isCurrent && <p className="source-explorer-warning" role="status">
      Historical source: current specification, architecture or plan approvals
      differ from the bindings of this run.
    </p>}
    <div className="source-explorer-layout">
      <aside className="source-explorer-tree" aria-label={source + ' file tree'}>
        <div className="source-explorer-search">
          <label htmlFor={searchId}>FIND SAVED FILE</label>
          <input id={searchId} type="search" value={search}
            placeholder="Filename or purpose…" onChange={event => {
              setSearch(event.target.value);
              setFeedback('');
              setFullFile(false);
            }}/>
          {search && <button type="button" className="source-explorer-clear"
            onClick={() => setSearch('')}>Clear search</button>}
        </div>
        <p className="source-explorer-count" role="status">
          {visible.length} of {files.length} saved files
        </p>
        {visible.length ? <nav aria-label={source + ' source files'}
          className="source-tree-nav">
          <FolderTree folder={tree} selectedPath={selected?.path ?? ''}
            onSelect={path => {
              setSelectedPath(path);
              setFullFile(false);
              setFeedback('');
            }} depth={0} searching={Boolean(search.trim())}/>
        </nav> : <p className="source-explorer-empty">
          {files.length ? 'No saved files match your search.' :
            'No source files have been saved for this run.'}</p>}
      </aside>
      <div className="source-explorer-preview">
        {selected ? <>
          <div className="source-preview-toolbar">
            <div className="source-preview-identity">
              <span className="wb-kicker">READ-ONLY SOURCE PREVIEW</span>
              <strong>{selected.path}</strong>
              <small>{sourceLanguage(selected.path)} ·
                {' '}{selected.content.length.toLocaleString()} characters ·
                {' '}{lines.length.toLocaleString()} lines</small>
            </div>
            <div className="source-preview-actions">
              <button type="button" className="outline"
                onClick={() => void copy(selected.path, 'Path')}>Copy path</button>
              <button type="button" className="outline"
                onClick={() => void copy(selected.content, 'Source')}>Copy source</button>
            </div>
          </div>
          <dl className="source-provenance" aria-label="Saved file provenance">
            <div><dt>Purpose</dt><dd>{selected.purpose}</dd></div>
            <div><dt>SHA-256</dt><dd><code>{selected.sha256}</code></dd></div>
            <div><dt>Saved by</dt><dd>{selected.lastWorkItemId ?
              task ? 'Task ' + (task.ordinal + 1) + ': ' + task.title +
                ' (' + selected.lastWorkItemId + ')' :
                'Engineering task ' + selected.lastWorkItemId :
              'Classic draft generator (per-file task not recorded)'}</dd></div>
            {(selected.updatedUtc ?? selected.createdUtc) && <div>
              <dt>Saved at</dt>
              <dd><time dateTime={selected.updatedUtc ?? selected.createdUtc}>
                {new Date((selected.updatedUtc ?? selected.createdUtc)!).toLocaleString()}
              </time></dd>
            </div>}
          </dl>
          {feedback && <p className="source-copy-feedback" role="status">{feedback}</p>}
          {truncated && <div className="source-preview-limit">
            Displaying the first {MAX_NUMBERED_LINES} lines for responsiveness.
            All {lines.length.toLocaleString()} lines remain available.
            <button type="button" className="outline"
              onClick={() => setFullFile(true)}>Show complete file</button>
          </div>}
          <div className="source-code-scroller" role="region"
            aria-label={'Source content: ' + selected.path} tabIndex={0}>
            {fullFile && lines.length > MAX_NUMBERED_LINES ?
              <pre className="source-full-plain">{selected.content}</pre> :
              <ol className="source-code-lines" aria-label="Source code with line numbers">
                {displayed.map((line, index) => <li key={index}>
                  <span aria-hidden="true" className="source-line-number">{index + 1}</span>
                  <code>{line || ' '}</code>
                </li>)}
              </ol>}
          </div>
          {fullFile && lines.length > MAX_NUMBERED_LINES &&
            <button type="button" className="outline source-preview-collapse"
              onClick={() => setFullFile(false)}>Return to numbered preview</button>}
        </> : <div className="source-explorer-no-selection">
          Select a saved file from the tree to inspect its content and provenance.
        </div>}
      </div>
    </div>
    <p className="source-explorer-boundary">
      Run {runId} · Persisted file content only. This viewer does not execute,
      compile, test, validate, or certify source. Use the original ZIP download
      and independent verification workflows for those separate activities.
    </p>
  </section>;
}
