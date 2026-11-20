import { useEffect, useState } from 'react';
import { api, newRequestId } from './api';

interface TaskContract {
  schema: string;
  taskId: string;
  objective: string;
  specificationVersion: number;
  approvedSpecificationSha256: string;
  model: string;
  files: string[];
}
interface TaskBuild {
  id: string;
  taskId: string;
  contractSha256: string;
  contract: TaskContract;
  state: 'Prepared' | 'ScopeAcknowledged' | 'Cancelled';
  executionEnabled: boolean;
  blocker: string;
  createdUtc: string;
  updatedUtc: string;
}
interface TaskBuildEvent {
  sequence: number;
  kind: string;
  createdUtc: string;
}
interface TaskBuildSnapshot {
  build: TaskBuild;
  events: TaskBuildEvent[];
}
interface LatestBuildResponse {
  available: boolean;
  snapshot: TaskBuildSnapshot | null;
}

export function TaskBuildPanel({ token, taskId, specificationSha256 }: {
  token: string;
  taskId: string;
  specificationSha256: string;
}) {
  const [snapshot, setSnapshot] = useState<TaskBuildSnapshot | null>(null);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [consent, setConsent] = useState(false);

  useEffect(() => {
    let closed = false;
    setSnapshot(null);
    setConsent(false);
    setError('');
    void api<LatestBuildResponse>(token, `/api/tasks/${taskId}/task-build`)
      .then(response => { if (!closed) setSnapshot(response.snapshot); })
      .catch(() => { if (!closed) setError('Saved build review could not be loaded.'); });
    return () => { closed = true; };
  }, [token, taskId]);

  async function prepare() {
    if (busy) return;
    setBusy('prepare'); setError(''); setConsent(false);
    try {
      const result = await api<TaskBuildSnapshot>(token,
        `/api/tasks/${taskId}/task-build`, { requestId: newRequestId() });
      setSnapshot(result);
    } catch (cause) {
      const message = cause instanceof Error ? cause.message : 'Scope review unavailable';
      setError(message.includes('TASK_PROFILE_UNSUPPORTED')
        ? 'This restricted pilot supports only small offline .NET projects (.cs and .csproj files, up to five). Revise the project plan for the .NET profile before requesting real coding.'
        : message);
    } finally { setBusy(''); }
  }

  async function acknowledge() {
    if (!snapshot || !consent || busy) return;
    setBusy('acknowledge'); setError('');
    try {
      const result = await api<TaskBuildSnapshot>(token,
        `/api/task-builds/${snapshot.build.id}/scope-acknowledgement`,
        { approval: `APPROVE ${snapshot.build.contractSha256}` });
      setSnapshot(result);
      setConsent(false);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Scope acknowledgement rejected');
    } finally { setBusy(''); }
  }

  async function cancel() {
    if (!snapshot || busy) return;
    setBusy('cancel'); setError('');
    try {
      setSnapshot(await api<TaskBuildSnapshot>(token,
        `/api/task-builds/${snapshot.build.id}/cancel`, {}));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Cancellation unavailable');
    } finally { setBusy(''); }
  }

  const build = snapshot?.build;
  const current = build?.contract.approvedSpecificationSha256 === specificationSha256;
  const canReview = !build || build.state === 'Cancelled' || !current;
  return <section className="build-stage" aria-label="Real coding build scope">
    <h2>4. Build project — execution scope</h2>
    <p className="hint">This review uses the actual approved objective and source-file plan,
      not the fixed C# repair fixture. It stores an immutable, restart-safe record.
      The real OpenCode execution remains disabled until its separate container,
      model and evaluator authorization and security tests are qualified.</p>
    <div className="actions">
      <button type="button" className="primary" disabled={!!busy}
        onClick={() => void prepare()}>
        {busy === 'prepare' ? 'Preparing review…'
          : canReview ? 'Review coding scope' : 'Review scope again'}
      </button>
      {build && build.state !== 'Cancelled' &&
        <button type="button" disabled={!!busy} onClick={() => void cancel()}>
          Cancel scope review
        </button>}
    </div>
    {error && <div className="alert" role="alert">{error}</div>}
    {build && <>
      <p><strong>Build scope:</strong> <span className="pill">{build.state}</span>
        {' '}<span className="pill">Execution unavailable</span></p>
      {!current && <p className="activity-warning">
        This saved scope belongs to an earlier specification. Prepare a new review.</p>}
      <p className="hint"><strong>Objective:</strong> {build.contract.objective}</p>
      <p className="hint"><strong>Approved specification:</strong> v
        {build.contract.specificationVersion} · {build.contract.approvedSpecificationSha256}</p>
      <p className="hint"><strong>Local model:</strong> {build.contract.model}</p>
      <p className="hint"><strong>Workspace:</strong> new disposable directory,
        no existing repository mount, no cloud inference, no Git remote.</p>
      <h3>Exact authorized file scope</h3>
      <ul>{build.contract.files.map(file => <li key={file}><code>{file}</code></li>)}</ul>
      <details>
        <summary>Immutable contract SHA-256</summary>
        <code className="trace-note">{build.contractSha256}</code>
      </details>
      {current && build.state === 'Prepared' && <>
        <label className="spec-consent">
          <input type="checkbox" checked={consent}
            onChange={event => setConsent(event.target.checked)} />
          I reviewed this exact objective and restricted file scope. This
          acknowledgement does not authorize an agent or start executable code.
        </label>
        <button type="button" className="primary"
          disabled={!consent || !!busy} onClick={() => void acknowledge()}>
          {busy === 'acknowledge' ? 'Saving…' : 'Acknowledge this scope'}
        </button>
      </>}
      {build.state === 'ScopeAcknowledged' && current && <p className="success">
        ✓ Scope acknowledgement saved. Real coding still requires a new
        executable-plan approval and successful integrated security qualification.
      </p>}
      <p className="activity-warning">Runtime gate: {build.blocker}.
        No inference or code execution was started by this review.</p>
      <h3>Saved build review history</h3>
      <ol className="events">{(snapshot?.events ?? []).map(event =>
        <li key={event.sequence}>
          <span className="number">#{event.sequence}</span>
          <span><strong>{event.kind}</strong>
            <small className="trace-note">
              {new Date(event.createdUtc).toLocaleString()}
            </small></span>
        </li>)}</ol>
    </>}
  </section>;
}
