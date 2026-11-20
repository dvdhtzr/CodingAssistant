import { useEffect, useState } from 'react';
import { api, type TaskItem } from './api';

interface SourceFile { path: string; content: string; sha256: string; }
interface SourceEvent { sequence: number; kind: string; createdUtc: string; }
interface DraftBuild {
  id: string;
  taskId: string;
  specificationVersion: number;
  specificationHash: string;
  model: string;
  state: 'Queued' | 'Generating' | 'Validating' | 'Ready' |
         'Failed' | 'Interrupted' | 'Superseded';
  errorCode: string | null;
  createdUtc: string;
  events: SourceEvent[];
  files: SourceFile[];
}
interface LatestBuild { draft: DraftBuild | null; }

const activeStates = new Set(['Queued', 'Generating', 'Validating']);
const stages: Record<string, string> = {
  Queued: 'Queued locally — no model request has been sent.',
  ModelRequestStarted: 'The approved specification was sent to local Ollama.',
  ModelResponseReceived: 'The local model returned proposed files.',
  DraftFilesSaved: 'Proposed source files were validated and saved.',
  SpecificationChanged: 'The approved specification changed. Discarding stale output.',
  Interrupted: 'The application stopped. No model request will restart automatically.',
};

function safeError(code: string | null): string {
  if (code === 'MODEL_NOT_INSTALLED') return 'Selected local Ollama model is not installed.';
  if (code === 'LOCAL_MODEL_UNAVAILABLE') return 'Local model unavailable or timed out.';
  if (code === 'DRAFT_OUTPUT_REJECTED') return 'Model response did not meet the bounded source-file contract.';
  if (code === 'SPECIFICATION_CONFLICT') return 'The specification changed; approve the new version.';
  if (code === 'HOST_RESTARTED' || code === 'HOST_STOPPED') return 'The host stopped before completion.';
  return code ? 'Draft stopped: ' + code : 'Draft unavailable.';
}

export function ProjectDraftPanel({ token, task, specification }: {
  token: string;
  task: TaskItem;
  specification: {
    version: number;
    sha256: string;
    status: 'Draft' | 'Approved' | 'Superseded';
  };
}) {
  const [draft, setDraft] = useState<DraftBuild | null>(null);
  const [busy, setBusy] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const [error, setError] = useState('');
  const [clock, setClock] = useState(Date.now());

  useEffect(() => {
    let stopped = false;
    setDraft(null);
    setError('');
    void api<LatestBuild>(token, `/api/tasks/${task.id}/project-draft`)
      .then(result => { if (!stopped) setDraft(result.draft); })
      .catch(() => { if (!stopped) setError('Saved source draft unavailable.'); });
    return () => { stopped = true; };
  }, [token, task.id]);

  useEffect(() => {
    if (!draft || !activeStates.has(draft.state)) return;
    let stopped = false;
    const id = draft.id;
    const timer = window.setInterval(() => {
      setClock(Date.now());
      void api<DraftBuild>(token, `/api/project-drafts/${id}`)
        .then(value => { if (!stopped) setDraft(value); })
        .catch(() => { if (!stopped) setError('Connection lost; saved progress can be reopened.'); });
    }, 2500);
    return () => { stopped = true; window.clearInterval(timer); };
  }, [token, draft?.id, draft?.state]);

  async function start() {
    if (busy || specification.status !== 'Approved') return;
    setBusy(true);
    setError('');
    try {
      const response = await api<DraftBuild>(
        token, `/api/tasks/${task.id}/project-draft`, {
          specificationVersion: specification.version,
          specificationHash: specification.sha256,
        });
      setDraft(response);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to create source draft.');
    } finally {
      setBusy(false);
    }
  }

  async function downloadAll() {
    if (!draft || draft.state !== 'Ready' || downloading) return;
    setDownloading(true);
    setError('');
    try {
      const response = await fetch(`/api/project-drafts/${draft.id}/zip`, {
        method: 'GET',
        cache: 'no-store',
        credentials: 'omit',
        referrerPolicy: 'no-referrer',
        headers: { Authorization: `Bearer ${token}` },
      });
      if (!response.ok) throw new Error('Verified ZIP download unavailable.');
      const file = await response.blob();
      const href = URL.createObjectURL(file);
      const link = document.createElement('a');
      link.href = href;
      link.download = `codingassistant-draft-${draft.id.slice(0, 8)}.zip`;
      link.click();
      window.setTimeout(() => URL.revokeObjectURL(href), 2000);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Download failed.');
    } finally {
      setDownloading(false);
    }
  }

  const current = draft && draft.specificationHash === specification.sha256 &&
    draft.specificationVersion === specification.version;
  const running = draft !== null && activeStates.has(draft.state);
  const seconds = draft ? Math.max(0, Math.floor(
    (clock - Date.parse(draft.createdUtc)) / 1000)) : 0;

  return <section className="build-draft">
    <h2>3. Generate and review source files</h2>
    <p className="hint">The local model will use your approved specification to propose a
      small, project-specific set of source files. You can watch real stages,
      inspect the output, and export a ZIP. <strong>This is a source draft:
      no build commands, tests, or generated programs are executed.</strong>
      No existing repository is modified.</p>
    <div className="actions">
      <button type="button" className="primary"
        disabled={busy || running || specification.status !== 'Approved'}
        onClick={() => void start()}>
        {running ? 'Local source generation in progress' :
          draft ? 'Create another source draft' : 'Generate project source draft'}
      </button>
      {draft?.state === 'Ready' &&
        <button type="button" onClick={() => void downloadAll()} disabled={downloading}>
          {downloading ? 'Preparing ZIP…' : 'Download all source files (.zip)'}
        </button>}
    </div>

    {error && <div className="alert" role="alert">{error}</div>}

    {draft && <div className="project-draft-result">
      <div className="actions">
        <span className="pill">Source draft: {draft.state}</span>
        <span className="pill">{draft.model}</span>
        {!current && <span className="pill">Earlier specification version</span>}
      </div>
      {running && <div className="activity" role="status">
        <div className="activity-heading">
          <span className="activity-dot" aria-hidden="true" />
          <strong>{draft.state === 'Generating' ? 'Waiting for local model response' :
            draft.state === 'Validating' ? 'Checking generated source paths and size' :
            'Queued for local source generation'}</strong>
          <span className="pill">{seconds}s</span>
        </div>
        <div className="activity-track" aria-hidden="true">
          <div className="activity-scan" />
        </div>
        <p className="hint">The animation and clock do not prove model activity;
          only events below confirm completed stages.</p>
      </div>}
      {draft.state === 'Ready' && <p className="success">
        ✓ {draft.files.length} source file(s) saved as reviewable text.
        Compilation and tests have NOT been performed.
      </p>}
      {['Failed', 'Interrupted', 'Superseded'].includes(draft.state) &&
        <p className="activity-warning">{safeError(draft.errorCode)}</p>}
      <h3>Actual build stages</h3>
      <ol className="events" aria-label="Project source generation events">
        {draft.events.map(event => <li key={event.sequence}>
          <span className="number">#{event.sequence}</span>
          <span><strong>{stages[event.kind] ?? event.kind}</strong>
            <small className="trace-note">{new Date(event.createdUtc).toLocaleTimeString()}</small>
          </span>
        </li>)}
      </ol>
      {draft.files.length > 0 && <>
        <h3>Generated files</h3>
        {draft.files.map(file => <details key={file.path} className="source-file">
          <summary>{file.path} · {file.content.length} characters</summary>
          <pre className="verified-patch">{file.content}</pre>
        </details>)}
      </>}
    </div>}
    <p className="hint">Full autonomous engineering with safe filesystem writes,
      real compilers, tests and live per-tool trace remains the next gated
      milestone. This feature does not substitute a file-drafting call for
      the qualified OpenClaw/ACPX/OpenCode agent.</p>
  </section>;
}
