import { useEffect, useState } from 'react';
import { api } from './api';
import { AgentIdentityPanel } from './AgentIdentityPanel';
import { TeamWorkroom } from './TeamWorkroom';
import type { JourneyId } from './ProjectJourney';

type QaSnapshot = {
  job: { id: string; state: string; engineeringRunId: string; planSha256: string };
  reports: { path: string; reviewedUtc: string; assessment?: {
    summary?: string; findings?: { severity: string; requirement: string }[];
  } }[];
  events: { sequence: number; kind: string; detail: string; createdUtc: string }[];
};
type Handoff = {
  kind: string; createdUtc: string; fromAgent: string; toAgent: string;
  current: boolean;
};
type Engineering = {
  job: { id: string; state: string; planSha256: string };
  events: { sequence: number; kind: string; detail: string; createdUtc: string }[];
};
type RecordedData = {
  qa: QaSnapshot | null;
  handoffs: Handoff[];
  engineering: Engineering | null;
};

function useRecordedProject(token: string, taskId: string): {
  data: RecordedData | null; error: boolean;
} {
  const [data, setData] = useState<RecordedData | null>(null);
  const [error, setError] = useState(false);
  useEffect(() => {
    let closed = false;
    setData(null);
    setError(false);
    const load = async () => {
      try {
        const [qa, handoffs, engineering] = await Promise.all([
          api<{ snapshot: QaSnapshot | null }>(token, '/api/tasks/' + taskId + '/qa-reviews/latest'),
          api<{ events: Handoff[] }>(token, '/api/tasks/' + taskId + '/agent-handoffs'),
          api<{ snapshot: Engineering | null }>(token, '/api/tasks/' + taskId + '/engineering-runs/latest'),
        ]);
        if (!closed) {
          setData({ qa: qa.snapshot, handoffs: handoffs.events, engineering: engineering.snapshot });
          setError(false);
        }
      } catch {
        if (!closed) { setData(null); setError(true); }
      }
    };
    void load();
    const timer = window.setInterval(() => void load(), 8000);
    return () => { closed = true; window.clearInterval(timer); };
  }, [token, taskId]);
  return { data, error };
}

export function TeamDestination({ token, taskId, onWork }: {
  token: string; taskId: string; onWork: () => void;
}) {
  return <section className="mc-destination" aria-label="Engineering team">
    <h2>Team</h2>
    <p className="hint">Host-defined specialists, identities and recorded handoffs.
      A roster is not a live presence signal.</p>
    <AgentIdentityPanel token={token} />
    <TeamWorkroom token={token} taskId={taskId}
      onNavigate={(_stage: JourneyId) => onWork()} />
  </section>;
}

export function QualityDestination({ token, taskId, onWork }: {
  token: string; taskId: string; onWork: () => void;
}) {
  const { data, error } = useRecordedProject(token, taskId);
  return <section className="mc-destination" aria-label="Quality evidence">
    <h2>Quality</h2>
    <p className="hint">Saved static QA evidence. Findings are hypotheses until classified;
      static inspection never verifies compilation or execution.</p>
    {error && <p role="alert" className="alert">Could not load saved quality evidence.</p>}
    {!data && !error && <p role="status">Loading saved QA evidence…</p>}
    {data && <div className="panel">
      <h3>{data.qa ? 'Latest QA review: ' + data.qa.job.state : 'No QA review recorded'}</h3>
      {data.qa && <>
        <p>{data.qa.reports.length} saved file report(s).</p>
        <p className="hint">{data.engineering?.job.id === data.qa.job.engineeringRunId &&
          data.engineering.job.planSha256 === data.qa.job.planSha256
          ? 'Review references the latest recorded engineering run and plan hash.'
          : 'Historical review or current source binding not established.'}</p>
        <ul>{data.qa.reports.map(report => <li key={report.path}>
          <strong>{report.path}</strong>
          <span> · {report.assessment?.findings?.length ?? 0} recorded finding(s)</span>
          {report.assessment?.summary && <p>{report.assessment.summary}</p>}
        </li>)}</ul>
      </>}
      <button type="button" className="primary" onClick={onWork}>
        Open Work for QA review, finding triage and repair authorization
      </button>
      <p className="hint">Approval and repair controls remain in the existing
        host-bound engineering workflow until their dedicated migration is verified.</p>
    </div>}
  </section>;
}

export function SkillsDestination({ token }: { token: string }) {
  return <section className="mc-destination" aria-label="Agent skills">
    <h2>Skills</h2>
    <p className="hint">Portable SKILL.md package management is planned under Architecture
      Extension A1. No installed registry or assignment controls are claimed here.</p>
    <div className="panel">
      <h3>Declared agent capabilities</h3>
      <p className="hint">These are host-defined scope descriptions, not installed skills,
        permissions, or proof that a skill has run.</p>
      <AgentIdentityPanel token={token} />
    </div>
  </section>;
}

export function HistoryDestination({ token, taskId }: {
  token: string; taskId: string;
}) {
  const { data, error } = useRecordedProject(token, taskId);
  const rows = data ? [
    ...data.handoffs.map((e, i) => ({ id: 'handoff-' + i,
      at: e.createdUtc, source: 'Agent handoff',
      detail: e.fromAgent + ' → ' + e.toAgent + ': ' + e.kind,
      context: e.current ? 'Current binding' : 'Historical binding' })),
    ...(data.engineering?.events ?? []).map(e => ({ id: 'engineering-' + e.sequence,
      at: e.createdUtc, source: 'Software Engineer', detail: e.kind + ': ' + e.detail,
      context: 'Engineering event #' + e.sequence })),
    ...(data.qa?.events ?? []).map(e => ({ id: 'qa-' + e.sequence,
      at: e.createdUtc, source: 'Static QA', detail: e.kind + ': ' + e.detail,
      context: 'QA event #' + e.sequence })),
  ].sort((a, b) => b.at.localeCompare(a.at) || a.id.localeCompare(b.id)) : [];
  return <section className="mc-destination" aria-label="Project history">
    <h2>History</h2>
    <p className="hint">Host-recorded events. Sorting independent streams by timestamp
      does not establish that one caused another.</p>
    {error && <p role="alert" className="alert">The saved history could not be loaded.</p>}
    {!data && !error && <p role="status">Loading recorded activity…</p>}
    {data && (rows.length ? <ol className="mc-history">
      {rows.map(row => <li key={row.id}>
        <strong>{row.source}</strong>
        <p>{row.detail}</p>
        <small><time dateTime={row.at}>{new Date(row.at).toLocaleString()}</time>
          {' · '}{row.context}</small>
      </li>)}
    </ol> : <p className="panel">No recorded handoffs or engineering/QA events yet.</p>)}
  </section>;
}
