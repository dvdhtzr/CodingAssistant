import { useEffect, useMemo, useState } from 'react';
import { api } from './api';
import type { EngineerSnapshot } from './EngineeringWorkspace';

interface SavedEvent {
  sequence: number;
  kind: string;
  detail: string;
  createdUtc: string;
}
export interface VerificationQaSnapshot {
  job: {
    id: string;
    engineeringRunId: string;
    planSha256: string;
    sourceSha256: string;
    model: string;
    state: string;
    errorCode: string | null;
    createdUtc: string;
  };
  sourceState: string;
  reports: { path: string; reviewedUtc: string }[];
  events: SavedEvent[];
}
export interface VerificationEvidence {
  engineering: EngineerSnapshot | null;
  qa: VerificationQaSnapshot | null;
  plan: {
    revision: number;
    planSha256: string;
    status: string;
    current: boolean;
  } | null;
  profile: {
    id: string;
    version: string;
    qualificationStatus: string;
    executableViaRegistry: boolean;
    requiredIndependentTests: string[];
  } | null;
  registryExecutionAvailable: boolean;
}
export interface TimelineEntry extends SavedEvent {
  source: 'Software Engineer' | 'QA static review';
  current: boolean;
}
export function summarizeVerificationEvidence(evidence: VerificationEvidence) {
  const { engineering, qa, plan } = evidence;
  const run = engineering?.job;
  const sourceCurrent = !!run && !!plan && plan.status === 'Approved' &&
    plan.current === true && plan.planSha256 === run.planSha256 &&
    plan.revision === run.planRevision;
  const qaCurrent = !!qa && !!run && sourceCurrent &&
    qa.job.engineeringRunId === run.id &&
    qa.job.planSha256 === run.planSha256;
  const sourceStatus = !run ? 'NO SOURCE RUN' :
    !sourceCurrent ? 'HISTORICAL · NOT TESTED' :
    run.state === 'Generated' ? 'GENERATED · NOT TESTED' :
    run.state === 'Running' ? 'GENERATING SOURCE · NOT TESTED' :
    'PARTIAL SOURCE · NOT TESTED';
  const events: TimelineEntry[] = [
    ...(engineering?.events ?? []).map(event => ({
      ...event, source: 'Software Engineer' as const, current: sourceCurrent,
    })),
    ...(qa?.events ?? []).map(event => ({
      ...event, source: 'QA static review' as const, current: qaCurrent,
    })),
  ];
  // ISO UTC timestamps are host-recorded; sequence orders events within each ledger.
  events.sort((a, b) => a.createdUtc.localeCompare(b.createdUtc) ||
    a.source.localeCompare(b.source) || a.sequence - b.sequence);
  return { sourceCurrent, qaCurrent, sourceStatus, events };
}

export function VerificationTimelineView({ evidence }: {
  evidence: VerificationEvidence;
}) {
  const summary = useMemo(() => summarizeVerificationEvidence(evidence), [evidence]);
  const { engineering, qa, profile } = evidence;
  const run = engineering?.job;
  const profileBlocked = !evidence.registryExecutionAvailable ||
    !profile?.executableViaRegistry || profile.qualificationStatus !== 'QUALIFIED';
  return <section className="verification-workroom"
    aria-label="Qualified execution and verification timeline">
    <div className="verification-heading">
      <div><span className="wb-kicker">EXECUTION / EVIDENCE LEDGER</span>
        <h3>Execution &amp; verification</h3>
        <p>Actual saved events, not a model-generated progress story.</p></div>
      <strong className="verification-outcome" data-state={run?.state ?? 'Absent'}>
        {summary.sourceStatus}</strong>
    </div>
    <div className="verification-cards">
      <article className="verification-card">
        <span>01 / Source attempt</span>
        <strong>{run ? run.state : 'Not started'}</strong>
        <small>{run ? engineering!.files.length + ' saved source file(s) · ' +
          engineering!.tasks.filter(task => task.state === 'Completed').length +
          ' completed assignment(s)' : 'No MAF engineering attempt saved'}</small>
        {run && <small>Plan v{run.planRevision} · {summary.sourceCurrent
          ? 'current approved binding' : 'historical or unapproved binding'}</small>}
      </article>
      <article className="verification-card">
        <span>02 / QA source inspection</span>
        <strong>{qa ? qa.job.state : 'Not requested'}</strong>
        <small>{qa ? qa.reports.length + ' saved static file review(s)' :
          'No static QA attempt saved'}</small>
        {qa && <small>{summary.qaCurrent
          ? 'Review bound to this current engineering run' :
          'Historical or mismatched run/plan binding'}</small>}
      </article>
      <article className="verification-card verification-card-blocked">
        <span>03 / Independent evaluation</span>
        <strong>{profileBlocked ? 'Not qualified / blocked' : 'No attempt recorded'}</strong>
        <small>Build, test, and browser outcomes: NOT TESTED</small>
        <small>Only a qualified host evaluator can record VERIFIED evidence.</small>
      </article>
    </div>
    <div className="verification-qualification">
      <div><strong>Static Web runtime profile</strong>
        <span>{profile ? profile.id + ' v' + profile.version + ' · ' +
          profile.qualificationStatus : 'Profile metadata unavailable'}</span></div>
      <p>{profileBlocked
        ? 'No task-specific execution admission through this registry. Security and task-scope gates #76 and #92 remain open.'
        : 'A qualified profile descriptor alone does not authorize a run. Exact owner scope consent and independent results are still required.'}</p>
      {profile && <details><summary>Required independent checks ({profile.requiredIndependentTests.length})</summary>
        <ul>{profile.requiredIndependentTests.map(check =>
          <li key={check}>{check} — not evidenced by this timeline</li>)}</ul>
      </details>}
    </div>
    {(run || qa) && <details className="verification-binding">
      <summary>Saved run provenance and bindings</summary>
      {run && <p>Engineering run <code>{run.id}</code> · model {run.model} ·
        plan SHA-256 <code>{run.planSha256}</code> ·
        {run.errorCode ? ' failure ' + run.errorCode : ' no recorded generation error'}</p>}
      {qa && <p>QA run <code>{qa.job.id}</code> · source fingerprint
        <code>{qa.job.sourceSha256}</code> · model {qa.job.model} ·
        {summary.qaCurrent ? ' current review binding' : ' historical review binding'}</p>}
    </details>}
    <div className="verification-ledger-heading">
      <strong>Persisted activity timeline</strong>
      <span>{summary.events.length} recorded event(s) · latest run of each kind only</span>
    </div>
    {summary.events.length ? <ol className="verification-events"
      aria-label="Recorded engineering and QA evidence events">
      {summary.events.map(event => <li key={event.source + ':' + event.sequence}
        data-current={event.current}
        data-source={event.source === 'Software Engineer' ? 'engineer' : 'qa'}>
        <div><strong>{event.kind}</strong>
          <span>{event.source} · sequence {event.sequence} ·
            {' '}{event.current ? 'current binding' : 'historical binding'}</span></div>
        <p>{event.detail}</p>
        <time dateTime={event.createdUtc}>
          {new Date(event.createdUtc).toLocaleString()}</time>
      </li>)}
    </ol> : <p className="verification-empty">
      No engineering or QA events have been recorded for the latest attempts.
      This is not evidence that execution or tests ran.</p>}
    <p className="verification-boundary">
      This is a read-only record of the latest MAF Software Engineer and QA static
      review attempts, not the separate legacy C# pilot. No independent
      build, browser test, security qualification, repair, or VERIFIED result is
      recorded by these APIs. A source ZIP and a model's static QA assessment
      cannot establish a passing test.</p>
  </section>;
}

export function VerificationTimeline({ token, taskId }: {
  token: string;
  taskId: string;
}) {
  const [expanded, setExpanded] = useState(false);
  const [evidence, setEvidence] = useState<VerificationEvidence | null>(null);
  const [error, setError] = useState('');
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    if (!expanded) return;
    let closed = false;
    let loading = false;
    setEvidence(null);
    setError('');
    async function load() {
      if (loading) return;
      loading = true;
      try {
        const [engineer, qa, plan, registry] = await Promise.all([
          api<{ snapshot: EngineerSnapshot | null }>(token,
            '/api/tasks/' + taskId + '/engineering-runs/latest'),
          api<{ snapshot: VerificationQaSnapshot | null }>(token,
            '/api/tasks/' + taskId + '/qa-reviews/latest'),
          api<{ plan: VerificationEvidence['plan'] }>(token,
            '/api/tasks/' + taskId + '/implementation-plan'),
          api<{ registryExecutionAvailable: boolean;
            profiles: VerificationEvidence['profile'][] }>(token,
            '/api/runtime-profiles'),
        ]);
        if (!closed) {
          setEvidence({
            engineering: engineer.snapshot, qa: qa.snapshot, plan: plan.plan,
            profile: registry.profiles.find(item => item?.id === 'static-web') ?? null,
            registryExecutionAvailable: registry.registryExecutionAvailable,
          });
          setError('');
        }
      } catch {
        if (!closed) {
          setEvidence(null);
          setError('Saved verification evidence could not be read. Status is unknown, not passed or failed.');
        }
      } finally { loading = false; }
    }
    void load();
    const interval = window.setInterval(() => void load(), 8000);
    return () => { closed = true; window.clearInterval(interval); };
  }, [expanded, token, taskId, refresh]);
  return <details className="verification-disclosure"
    onToggle={event => setExpanded(event.currentTarget.open)}>
    <summary><span className="verification-chevron" aria-hidden="true">▸</span>
      <span><strong>Execution &amp; Verification Timeline</strong>
        <small>Saved source attempts, QA inspection, and qualification blockers</small></span>
      <span className="verification-disclosure-status">{evidence
        ? 'Evidence loaded' : error ? 'Unavailable' :
          expanded ? 'Reading records' : 'Inspect evidence'}</span>
    </summary>
    <div className="verification-disclosure-body">
      {error && <div className="activity-warning" role="alert">{error}
        <button className="outline" type="button"
          onClick={() => setRefresh(n => n + 1)}>Retry evidence read</button>
      </div>}
      {!evidence && !error && <p className="wb-muted" role="status">
        Loading authenticated saved records…</p>}
      {evidence && <VerificationTimelineView evidence={evidence}/>}
    </div>
  </details>;
}
