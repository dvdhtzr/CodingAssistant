import { useEffect, useState } from 'react';
import { api } from './api';
import { TaskBuildPanel } from './TaskBuildPanel';

interface SpecContract {
  version: number;
  sha256: string;
  status: 'Draft' | 'Approved' | 'Superseded';
}
interface DraftJob {
  id: string;
  specificationVersion: number;
  specificationSha256: string;
  model: string;
  state: 'Running' | 'Generated' | 'Failed' | 'Interrupted';
  phase: string;
  errorCode: string | null;
  createdUtc: string;
  updatedUtc: string;
}
interface DraftEvent {
  sequence: number;
  kind: string;
  detail: string;
  createdUtc: string;
}
interface DraftFile {
  path: string;
  purpose: string;
  content: string;
  sha256: string;
  createdUtc: string;
}
interface DraftSnapshot {
  job: DraftJob;
  events: DraftEvent[];
  files: DraftFile[];
  architecture: { sha256: string; choices: {
    topic: string; label: string; selectedOptionId: string;
  }[] } | null;
}
interface ArchitectureReadiness {
  status: 'READY' | 'INCOMPLETE' | 'LEGACY_UNBOUND' | 'SPECIFICATION_NOT_APPROVED';
  readyForSourceGeneration: boolean;
  architectureSha256: string | null;
  blockingTopics: string[];
  approvedChoices: { topic: string; label: string }[];
}
interface DraftResponse {
  available: boolean;
  snapshot: DraftSnapshot | null;
}

export function ProjectDraftPanel({ token, taskId, spec }: {
  token: string;
  taskId: string;
  spec: SpecContract | null;
}) {
  const [result, setResult] = useState<DraftSnapshot | null>(null);
  const [architecture, setArchitecture] = useState<ArchitectureReadiness | null>(null);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [clock, setClock] = useState(Date.now());

  useEffect(() => {
    let closed = false;
    setResult(null);
    setArchitecture(null);
    setError('');
    async function sync() {
      try {
        const [response, readiness] = await Promise.all([
          api<DraftResponse>(token, `/api/tasks/${taskId}/draft-build`),
          api<ArchitectureReadiness>(token,
            `/api/tasks/${taskId}/draft-build/architecture`),
        ]);
        if (!closed) {
          setResult(response.snapshot);
          setArchitecture(readiness);
        }
      } catch {
        if (!closed) setError('Saved draft-build status could not be loaded.');
      }
    }
    void sync();
    const poll = window.setInterval(() => void sync(), 2500);
    return () => { closed = true; window.clearInterval(poll); };
  }, [token, taskId, spec?.sha256]);

  useEffect(() => {
    if (result?.job.state !== 'Running') return;
    const timer = window.setInterval(() => setClock(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [result?.job.state]);

  async function start() {
    if (busy || spec?.status !== 'Approved' ||
        !architecture?.readyForSourceGeneration) return;
    setBusy('start'); setError('');
    try {
      // Refresh the immutable binding immediately before admitting a draft.
      // The server rechecks it transactionally; stale snapshots fail closed.
      const current = await api<ArchitectureReadiness>(token,
        `/api/tasks/${taskId}/draft-build/architecture`);
      setArchitecture(current);
      if (!current.readyForSourceGeneration || !current.architectureSha256)
        throw new Error('Complete the Solution Architect approvals before generating source.');
      const started = await api<DraftJob>(token,
        `/api/tasks/${taskId}/draft-build`, {
          specificationVersion: spec.version,
          specificationSha256: spec.sha256,
          architectureSha256: current.architectureSha256,
        });
      setResult({
        job: started, files: [], architecture: null,
        events: [{
          sequence: 1,
          kind: 'BuildAccepted',
          detail: 'Approved specification accepted; source-only build starting',
          createdUtc: started.createdUtc,
        }],
      });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to start draft generation');
    } finally { setBusy(''); }
  }

  async function cancel() {
    if (!result || result.job.state !== 'Running' || busy) return;
    setBusy('cancel'); setError('');
    try {
      const value = await api<DraftSnapshot>(token,
        `/api/tasks/${taskId}/draft-build/${result.job.id}/cancel`, {});
      setResult(value);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to cancel');
    } finally { setBusy(''); }
  }

  async function download() {
    if (!result || result.job.state !== 'Generated') return;
    setBusy('download'); setError('');
    try {
      const response = await fetch(
        `/api/tasks/${taskId}/draft-build/${result.job.id}/zip`, {
          cache: 'no-store', credentials: 'omit',
          referrerPolicy: 'no-referrer',
          headers: { Authorization: `Bearer ${token}` },
        });
      if (!response.ok)
        throw new Error('Project draft ZIP is unavailable (' + response.status + ').');
      const href = URL.createObjectURL(await response.blob());
      const anchor = document.createElement('a');
      anchor.href = href;
      anchor.download = 'codingassistant-project-draft.zip';
      anchor.click();
      window.setTimeout(() => URL.revokeObjectURL(href), 3000);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to download');
    } finally { setBusy(''); }
  }

  async function downloadAgentContract() {
    if (!result || result.job.state !== 'Generated' ||
        spec?.status !== 'Approved' || busy) return;
    setBusy('contract'); setError('');
    try {
      const response = await fetch(
        `/api/tasks/${taskId}/agent-contract-preview/json`, {
          cache: 'no-store', credentials: 'omit',
          referrerPolicy: 'no-referrer',
          headers: { Authorization: `Bearer ${token}` },
        });
      if (!response.ok)
        throw new Error('Task agent contract unavailable (' + response.status +
          '). The task model must be separately qualified for execution.');
      const href = URL.createObjectURL(await response.blob());
      const anchor = document.createElement('a');
      anchor.href = href;
      anchor.download = 'codingassistant-agent-contract.json';
      anchor.click();
      window.setTimeout(() => URL.revokeObjectURL(href), 3000);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message :
        'Unable to download approved task contract');
    } finally { setBusy(''); }
  }

  const job = result?.job;
  const running = job?.state === 'Running';
  const seconds = job
    ? Math.max(0, Math.floor((clock - Date.parse(job.createdUtc)) / 1000)) : 0;
  return <section className="build-stage" aria-label="Task-specific project draft">
    <h2>4. Generate project files</h2>
    <p className="hint">After approving the Product Owner specification and both Solution Architect
      decisions, your local model chooses a small set of source files consistent with
      those exact decisions, writes them one by one,
      and saves the results. This stage does not execute code, install packages,
      compile, run tests, or modify your repositories. Files are unverified drafts.</p>
    <div className="actions">
      <button className="primary" type="button"
        disabled={!!busy || spec?.status !== 'Approved' ||
          !architecture?.readyForSourceGeneration || running}
        onClick={() => void start()}>
        {busy === 'start' ? 'Starting…' : 'Generate approved project draft'}
      </button>
      {running && <button type="button" className="danger"
        disabled={!!busy} onClick={() => void cancel()}>
        Stop generation
      </button>}
      {job?.state === 'Generated' && <button type="button"
        disabled={!!busy} onClick={() => void download()}>
        Download project ZIP
      </button>}
      {job?.state === 'Generated' && spec?.status === 'Approved' &&
        <button type="button" disabled={!!busy}
          onClick={() => void downloadAgentContract()}>
          {busy === 'contract' ? 'Preparing…' : 'Download agent pilot contract'}
        </button>}
    </div>
    {spec?.status !== 'Approved' && <p className="hint">
      Approve a specification above before generating project files.</p>}
    {spec?.status === 'Approved' && architecture &&
      !architecture.readyForSourceGeneration && <p className="activity-warning">
        Architecture approval required ({architecture.status}).
        {architecture.blockingTopics.length > 0 &&
          ' Review: ' + architecture.blockingTopics.join(', ') + '.'}
        An older specification-only draft may still appear below for download,
        but new Mission Control drafts require both approved choices.
      </p>}
    {architecture?.readyForSourceGeneration && <p className="hint">
      Both architecture decisions approved. Binding SHA-256:
      <code className="architect-hash"> {architecture.architectureSha256}</code>
    </p>}
    {error && <div className="alert" role="alert">{error}</div>}
    {job && <div className="draft-status">
      <div className="activity-heading">
        {running && <span className="activity-dot" aria-hidden="true" />}
        <strong>{job.state === 'Generated' ? 'Files generated (NOT TESTED)' :
          job.state === 'Running' ? 'Generating real source files' :
          'Generation stopped: ' + job.state}</strong>
        <span className="pill">{job.phase}</span>
      </div>
      {running && <>
        <div className="activity-track" aria-hidden="true">
          <div className="activity-scan" />
        </div>
        <p className="hint">Elapsed: {Math.floor(seconds / 60)}m {seconds % 60}s.
          The animation is not a model heartbeat or an invented percentage.</p>
      </>}
      {job.errorCode && <p className="activity-warning">
        Safe failure category: {job.errorCode}. Partial files remain visible,
        but no result is claimed as complete.</p>}
      <p className="hint">
        Specification version {job.specificationVersion} · model {job.model} ·
        {result?.files.length ?? 0} actual file(s) saved.
      </p>
      {job.specificationSha256 !== spec?.sha256 &&
        <p className="hint">This draft belongs to an earlier specification.</p>}
      {result?.architecture && <>
        <p className="hint">Generation bound to owner-approved architecture:
          <code className="architect-hash"> {result.architecture.sha256}</code></p>
        <p className="hint">{result.architecture.choices.map(choice =>
          choice.topic + ': ' + choice.label).join(' · ')}</p>
        {architecture?.architectureSha256 !== result.architecture.sha256 &&
          <p className="activity-warning">Architecture decisions have changed since
            this draft began. Review the new decisions before generating again.</p>}
      </>}
      {!result?.architecture && <p className="hint">
        Legacy specification-only draft (not bound to architecture decisions).</p>}
      <h3>Actual build activity</h3>
      <ol className="events">
        {(result?.events ?? []).map(event =>
          <li key={event.sequence}>
            <span className="number">#{event.sequence}</span>
            <span><strong>{event.kind}</strong> · {event.detail}
              <small className="trace-note">
                {new Date(event.createdUtc).toLocaleTimeString()}
              </small>
            </span>
          </li>)}
      </ol>
      {!!result?.files.length && <>
        <h3>Generated source files (unverified)</h3>
        <div className="draft-files">
          {result.files.map(file => <details key={file.path}>
            <summary><strong>{file.path}</strong> · {file.content.length} characters</summary>
            <p className="hint">{file.purpose} · SHA-256 {file.sha256}</p>
            <pre className="specification-text">{file.content}</pre>
          </details>)}
        </div>
      </>}
    </div>}
    {job?.state === 'Generated' && spec?.status === 'Approved' &&
      job.specificationSha256 === spec.sha256 &&
      <TaskBuildPanel token={token} taskId={taskId}
        specificationSha256={spec.sha256} />}
    <p className="hint">The source-only project draft and task-specific build review
      are separate from independently verified coding. The Linux-only OpenCode
      pilot remains a manual qualification path (docs/M1_TASK_PROJECT_PILOT.md).
      The security-negative cases in issue #76 must pass before executable coding
      can be enabled in Mission Control.</p>
  </section>;
}
