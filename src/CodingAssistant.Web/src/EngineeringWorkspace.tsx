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
    async function load() {
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

  return { token, taskId, snapshot, plan, busy, error, refresh, start, cancel, download };
}

export type EngineeringState = ReturnType<typeof useEngineeringRun>;

export function EngineeringGenerationPanel({ engine, legacyRunning }: {
  engine: EngineeringState;
  legacyRunning: boolean;
}) {
  const job = engine.snapshot?.job;
  const running = job?.state === 'Running';
  const assignments = engine.snapshot?.tasks ?? [];
  const completedCount = assignments.filter(item => item.state === 'Completed').length;
  const activeTask = assignments.find(item => item.state === 'Running');
  const approved = engine.plan?.status === 'Approved' &&
    engine.plan.current === true;
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
      <button type="button" className="primary"
        disabled={!approved || !!engine.busy || legacyRunning || running}
        onClick={() => void engine.start()}>
        {engine.busy === 'start' ? 'Admitting tasks…' :
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
    <details className="atlas-repository-entry">
      <summary>Code Atlas · Explore an existing GitHub repository</summary>
      <CodeAtlasRepositoryPanel token={engine.token}
        taskId={engine.taskId}/>
    </details>
    {job && <>
      <p className="hint" role="status"><strong>{job.state}</strong>
        {' · '}plan v{job.planRevision}{' · '}{job.phase}
        {' · '}model {job.model}
        {job.errorCode ? ' · ' + job.errorCode : ''}
      </p>
      {running && <p className="hint">Your selected model is working on the
        recorded tasks. This is not a fabricated progress percentage.</p>}
      {job.state === 'Failed' && <p className="activity-warning" role="alert">
        Source generation stopped safely. Completed task files remain inspectable.
        No verification or successful full build is claimed.</p>}
      <CodeAtlasPanel token={engine.token} taskId={job.taskId}
        runId={job.id} files={engine.snapshot?.files ?? []} running={running}/>
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
            {item.state === 'Failed' && <p role="alert" className="engineering-task-failure">
              <strong>Source generation failed on this assignment.</strong>
              {' '}Reason code: <code>{job.errorCode ?? 'UNKNOWN_FAILURE'}</code>.
              Earlier completed source remains saved. Expand
              {' '}<strong>Saved engineering activity</strong> below for the durable failure event.
              No build or runtime testing was performed by this agent.
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
