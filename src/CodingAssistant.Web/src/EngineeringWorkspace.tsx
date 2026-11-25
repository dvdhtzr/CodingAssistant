import { useCallback, useEffect, useState } from 'react';
import { api } from './api';
import { SourceArtifactExplorer } from './SourceArtifactExplorer';
import { CodeAtlasPanel } from './CodeAtlasPanel';
import { CodeAtlasRepositoryPanel } from './CodeAtlasRepositoryPanel';
import { EngineeringVerificationScope } from './VerificationScope';

export interface EngineerRun {
  id: string;
  taskId: string;
  planRevision: number;
  planSha256: string;
  specificationVersion: number;
  specificationSha256: string;
  architectureSha256: string;
  model: string;
  state: 'Running' | 'Generated' | 'Failed' | 'Interrupted';
  phase: string;
  errorCode: string | null;
  createdUtc: string;
  updatedUtc: string;
}
export interface EngineerAssignment {
  id: string;
  ordinal: number;
  title: string;
  state: 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Interrupted';
  summary: string | null;
  startedUtc: string | null;
  completedUtc: string | null;
}
export interface EngineerFile {
  path: string;
  purpose: string;
  content: string;
  sha256: string;
  lastWorkItemId: string;
  updatedUtc: string;
}
export interface EngineerEvent {
  sequence: number;
  kind: string;
  detail: string;
  createdUtc: string;
}
export interface EngineerSnapshot {
  job: EngineerRun;
  tasks: EngineerAssignment[];
  events: EngineerEvent[];
  files: EngineerFile[];
}
interface PlanResponse {
  plan: {
    revision: number;
    planSha256: string;
    status: string;
    current: boolean;
  } | null;
}

export function useEngineeringRun(token: string, taskId: string) {
  const [snapshot, setSnapshot] = useState<EngineerSnapshot | null>(null);
  const [plan, setPlan] = useState<PlanResponse['plan']>(null);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [refreshKey, setRefreshKey] = useState(0);

  const refresh = useCallback(() => setRefreshKey(value => value + 1), []);
  // Clear the displayed run only when the owner/task scope changes.
  // Clearing on every refresh unmounts Code Atlas, which replays events,
  // requests another refresh, and creates an endless flash/remount cycle.
  useEffect(() => {
    setSnapshot(null);
    setPlan(null);
    setError('');
  }, [token, taskId]);

  useEffect(() => {
    let closed = false;
    // A slow response must not race with the next interval and overwrite
    // newer terminal events with an older snapshot.
    let inFlight = false;
    async function load() {
      if (inFlight) return;
      inFlight = true;
      try {
        const [result, currentPlan] = await Promise.all([
          api<{ snapshot: EngineerSnapshot | null }>(
            token, '/api/tasks/' + taskId + '/engineering-runs/latest'),
          api<PlanResponse>(token,
            '/api/tasks/' + taskId + '/implementation-plan'),
        ]);
        if (!closed) {
          setSnapshot(result.snapshot);
          setPlan(currentPlan.plan);
        }
      } catch (cause) {
        if (!closed) setError(cause instanceof Error ? cause.message :
          'Cannot load Software Engineer history');
      } finally {
        inFlight = false;
      }
    }
    void load();
    // Poll authentic persisted state; the renderer never invents a percentage.
    const interval = window.setInterval(() => void load(), 3000);
    return () => { closed = true; window.clearInterval(interval); };
  }, [token, taskId, refreshKey]);

  async function start() {
    if (busy || snapshot?.job.state === 'Running') return;
    setBusy('start');
    setError('');
    try {
      // Always get an authoritative current approval before starting.
      const approval = await api<PlanResponse>(token,
        '/api/tasks/' + taskId + '/implementation-plan');
      setPlan(approval.plan);
      if (!approval.plan || !approval.plan.current ||
          approval.plan.status !== 'Approved')
        throw new Error('Approve the current Technical Lead plan first.');
      const job = await api<EngineerRun>(token,
        '/api/tasks/' + taskId + '/engineering-runs', {
          planRevision: approval.plan.revision,
          planSha256: approval.plan.planSha256,
        });
      setSnapshot({
        job, tasks: [], files: [],
        events: [{ sequence: 1, kind: 'EngineeringRunAdmitted',
          detail: 'Approved plan accepted for source-only tasks',
          createdUtc: job.createdUtc }],
      });
      refresh();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not start engineering');
    } finally { setBusy(''); }
  }

  async function resume() {
    const prior = snapshot;
    if (busy || !prior ||
        !['Failed', 'Interrupted'].includes(prior.job.state) ||
        prior.tasks.filter(t => t.state === 'Completed').length === 0 ||
        prior.tasks.every(t => t.state === 'Completed')) return;
    setBusy('resume');
    setError('');
    try {
      const approval = await api<PlanResponse>(token,
        '/api/tasks/' + taskId + '/implementation-plan');
      setPlan(approval.plan);
      if (!approval.plan || !approval.plan.current ||
          approval.plan.status !== 'Approved' ||
          approval.plan.planSha256 !== prior.job.planSha256 ||
          approval.plan.revision !== prior.job.planRevision)
        throw new Error('The approved plan changed; cannot resume an older run.');
      const resumed = await api<EngineerRun>(token,
        '/api/tasks/' + taskId + '/engineering-runs', {
          planRevision: approval.plan.revision,
          planSha256: approval.plan.planSha256,
          resumeRunId: prior.job.id,
        });
      // Load the exact returned run so inherited files and task receipts
      // appear immediately; do not briefly replace them with an empty graph.
      const saved = await api<EngineerSnapshot>(token,
        '/api/tasks/' + taskId + '/engineering-runs/' + resumed.id);
      setSnapshot(saved);
      refresh();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not resume saved assignments');
    } finally { setBusy(''); }
  }

  async function cancel() {
    if (!snapshot || snapshot.job.state !== 'Running' || busy) return;
    setBusy('cancel');
    setError('');
    try {
      const updated = await api<EngineerSnapshot>(token,
        '/api/tasks/' + taskId + '/engineering-runs/' +
          snapshot.job.id + '/cancel', {});
      setSnapshot(updated);
      refresh();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not stop engineering');
    } finally { setBusy(''); }
  }

  async function download() {
    if (!snapshot || !['Generated', 'Failed', 'Interrupted'].includes(
      snapshot.job.state) || snapshot.files.length === 0 || busy) return;
    setBusy('download');
    setError('');
    try {
      const response = await fetch(
        '/api/tasks/' + taskId + '/engineering-runs/' +
        snapshot.job.id + '/zip', {
          cache: 'no-store',
          credentials: 'omit',
          referrerPolicy: 'no-referrer',
          headers: { Authorization: 'Bearer ' + token },
        });
      if (!response.ok)
        throw new Error('Source ZIP unavailable (' + response.status + ').');
      const href = URL.createObjectURL(await response.blob());
      const element = document.createElement('a');
      element.href = href;
      element.download = snapshot.job.state === 'Generated'
        ? 'codingassistant-engineering-source.zip'
        : 'codingassistant-engineering-PARTIAL-unverified.zip';
      element.click();
      window.setTimeout(() => URL.revokeObjectURL(href), 3000);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not download source');
    } finally { setBusy(''); }
  }

  return { token, taskId, snapshot, plan, busy, error, refresh, start, resume, cancel, download };
}

export type EngineeringState = ReturnType<typeof useEngineeringRun>;

export function EngineeringGenerationPanel({ engine, legacyRunning, studioMode = false }: {
  engine: EngineeringState;
  legacyRunning: boolean;
  studioMode?: boolean;
}) {
  const job = engine.snapshot?.job;
  const running = job?.state === 'Running';
  const assignments = engine.snapshot?.tasks ?? [];
  const completedCount = assignments.filter(item => item.state === 'Completed').length;
  const activeTask = assignments.find(item => item.state === 'Running');
  const approved = engine.plan?.status === 'Approved' &&
    engine.plan.current === true;
  const resumable = approved &&
    (job?.state === 'Failed' || job?.state === 'Interrupted') &&
    job.planRevision === engine.plan?.revision &&
    job.planSha256 === engine.plan?.planSha256 &&
    completedCount > 0 && completedCount < assignments.length;
  return <section className="engineering-workspace"
    data-has-run={!!job}
    aria-label="MAF Software Engineer tasks">
    <div className="technical-plan-heading">
      <strong>Software Engineer · MAF task-by-task</strong>
      <span className="pill">Local Ollama · source only</span>
    </div>
    <p className="hint">A separate agent implements each dependency-ordered
      assignment from your <strong>approved Technical Lead plan</strong>.
      Its independent role, goals and professional backstory are host-owned.
      The source is not built, executed or independently tested.</p>
    {!approved && <p className="hint">
      Approve the current Technical Lead plan to enable this workflow.
      Existing source generation remains available below.</p>}
    <div className="actions">
      {resumable && <button type="button" className="primary"
        disabled={!!engine.busy || legacyRunning}
        onClick={() => void engine.resume()}>
        {engine.busy === 'resume' ? 'Resuming saved source…' :
          'Resume remaining ' + (assignments.length - completedCount) + ' assignment(s)'}
      </button>}
      <button type="button" className={resumable ? 'outline' : 'primary'}
        disabled={!approved || !!engine.busy || legacyRunning || running}
        onClick={() => void engine.start()}>
        {engine.busy === 'start' ? 'Admitting tasks…' :
          resumable ? 'Start a separate new run from task 1' :
          'Ask Software Engineer to implement approved tasks'}
      </button>
      {running && <button type="button" className="danger"
        disabled={!!engine.busy} onClick={() => void engine.cancel()}>
        Stop engineering run
      </button>}
      <button type="button" className="outline" onClick={engine.refresh}
        disabled={!!engine.busy}>Refresh saved task progress</button>
    </div>
    {engine.error && <div className="alert" role="alert">{engine.error}</div>}
    {studioMode && job && <div className="ca-studio-atlas-focus">
      <span className="ca-studio-eyebrow">LIVE WORKSPACE / SAVED SOURCE</span>
      <CodeAtlasPanel token={engine.token} taskId={job.taskId}
        runId={job.id} files={engine.snapshot?.files ?? []} running={running}/>
    </div>}
    <details className="atlas-repository-entry">
      <summary>Code Atlas · Explore an existing GitHub repository</summary>
      <CodeAtlasRepositoryPanel token={engine.token}
        taskId={engine.taskId}/>
    </details>
    {job && <>
      <p className="hint" role="status"><strong>{job.state}</strong>
        {' · '}plan v{job.planRevision}{' · '}{job.phase}
        {' · '}model {job.model}
        {' · '}{engine.snapshot?.events.length ?? 0} saved activity events
        {job.id ? ' · run ' + job.id.slice(0, 8) : ''}
        {job.errorCode ? ' · reason: ' + job.errorCode : ''}
      </p>
      {running && <p className="hint">Your selected model is working on the
        recorded tasks. This is not a fabricated progress percentage.</p>}
      {(job.state === 'Failed' || job.state === 'Interrupted') &&
        <p className="activity-warning" role="alert">
          <strong>{job.state === 'Interrupted' ? 'Engineering run interrupted' :
            'Engineering run failed'}.</strong>
          {' '}Recorded reason: <code>{job.errorCode ?? 'NOT_RECORDED'}</code>.
          {' '}Earlier completed source remains saved and inspectable.
          {job.errorCode === 'PROCESS_INTERRUPTED' &&
            ' The application restarted while generation was active; this is not a failed build or test.'}
          {' '}No independent build or runtime verification is claimed.
        </p>}
      {!studioMode && <CodeAtlasPanel token={engine.token} taskId={job.taskId}
        runId={job.id} files={engine.snapshot?.files ?? []} running={running}/>}
      <div className="engineering-board-header">
        <div><span className="wb-kicker">SAVED ENGINEERING ASSIGNMENTS</span>
          <h3>Implementation workstream</h3></div>
        <span className="engineering-board-count">{completedCount} of
          {' '}{assignments.length} completed</span>
      </div>
      {job && <div className="engineering-now-working" role="status">
        <span className="engineering-run-dot" aria-hidden="true" />
        {running ? activeTask ?
          'Recorded active assignment: ' + activeTask.title :
          'Engineering run active · waiting for a persisted assignment' :
          'Engineering run: ' + job.state}
      </div>}
      <ol className="engineering-tasks engineering-task-board"
        aria-label="Saved Software Engineer task progression">
        {assignments.map(item => {
          const changedFiles = engine.snapshot?.files.filter(file =>
            file.lastWorkItemId === item.id).length ?? 0;
          const lastRejection = engine.snapshot?.events.filter(event =>
            event.kind === 'EngineerOutputRejected' &&
            event.detail.startsWith(item.id + ':')).slice(-1)[0];
          return <li key={item.id} data-task-state={item.state}>
            <div className="engineering-task-heading">
              <span className="engineering-task-number" aria-hidden="true">
                {String(item.ordinal + 1).padStart(2, '0')}</span>
              <strong>{item.title}</strong>
              <span className="engineering-task-state">{item.state}</span>
            </div>
            <div className="engineering-task-evidence">
              <span>Assignment {item.ordinal + 1} · {changedFiles} source file(s)
                {' '}last saved by this assignment</span>
              {item.completedUtc && <span>Completed
                {' '}{new Date(item.completedUtc).toLocaleString()}</span>}
              {item.startedUtc && !item.completedUtc && <span>Started
                {' '}{new Date(item.startedUtc).toLocaleString()}</span>}
            </div>
            {(item.state === 'Failed' || item.state === 'Interrupted') &&
              <p role="alert" className="engineering-task-failure">
              <strong>{item.state === 'Interrupted' ?
                'Assignment interrupted before completion.' :
                'Source generation failed on this assignment.'}</strong>
              {' '}Reason code: <code>{job.errorCode ?? 'NOT_RECORDED'}</code>.
              {lastRejection && <span className="engineering-rejection-reason">
                {' '}Last model-output diagnostic: {lastRejection.detail}.
              </span>}
              Earlier completed source remains saved. Open
              {' '}<strong>Saved engineering activity</strong> for the event history.
              No build or runtime testing was performed by this agent.
              {job.state === 'Interrupted' &&
                !engine.snapshot?.events.some(e => e.kind === 'EngineeringRunInterrupted') &&
                ' This older run has no terminal interruption event; restart recovery now repairs that missing receipt.'}
            </p>}
            {item.summary && <p className="hint">{item.summary}</p>}
          </li>;
        })}
      </ol>
      {assignments.length === 0 && <p className="hint">
        No persisted task assignments yet. This is not an estimated backlog.
      </p>}
      <details>
        <summary>Saved engineering activity ({engine.snapshot?.events.length ?? 0})</summary>
        <ol className="events" aria-label="Software Engineer saved activity">
          {engine.snapshot?.events.map(event => <li key={event.sequence}>
            <span className="number">#{event.sequence}</span>
            <span><strong>{event.kind}</strong> · {event.detail}</span>
          </li>)}
        </ol>
      </details>
    </>}
  </section>;
}

export function EngineeringDeliveryPanel({ engine }: {
  engine: EngineeringState;
}) {
  if (!engine.snapshot) return null;
  const { job, files } = engine.snapshot;
  const deliverable = files.length > 0 &&
    ['Generated', 'Failed', 'Interrupted'].includes(job.state);
  return <section className="engineering-workspace engineering-delivery"
    aria-label="Software Engineer source artifacts">
    <div className="technical-plan-heading">
      <strong>Generated source · {files.length} file(s)</strong>
      <span className="pill">{job.state === 'Generated'
        ? 'Generated · not verified' : job.state}</span>
    </div>
    {job.state !== 'Generated' && files.length > 0 &&
      <p className="activity-warning">Partial source only. Remaining work is
        incomplete and no build or test success is claimed.</p>}
    {deliverable && <EngineeringVerificationScope
      key={job.id} token={engine.token} run={job} files={files}/>}
    {files.length > 0 && <details className="engineering-source-details">
      <summary>Inspect saved source ({files.length} file(s))</summary>
      <p className="hint">Plan v{job.planRevision} · state {job.state}.
        Source is unverified and belongs to this engineering run.</p>
      {deliverable && <div className="actions">
        <button type="button" disabled={!!engine.busy}
          onClick={() => void engine.download()}>
          {engine.busy === 'download' ? 'Preparing ZIP…' :
            job.state === 'Generated'
              ? 'Download Software Engineer ZIP'
              : 'Download PARTIAL source ZIP'}
        </button>
      </div>}
      <SourceArtifactExplorer key={job.id}
        source="MAF Software Engineer" files={files} state={job.state}
        runId={job.id} isCurrent={!!engine.plan?.current &&
          engine.plan.status === 'Approved' &&
          engine.plan.planSha256 === job.planSha256}
        assignments={engine.snapshot.tasks}/>
    </details>}
  </section>;
}
