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
type Repair = {
  cycle: { engineeringRunId: string; qaReviewId: string | null;
    state: string; attempt: number };
};
type RecordedData = {
  qa: QaSnapshot | null;
  handoffs: Handoff[];
  engineering: Engineering | null;
  repair: Repair | null;
};

// Derived only from the latest saved project records. Do not turn a source
// inspection into a build PASS, assume an old QA run reviewed new source,
// or claim a repair was authorized just because an engineer was active.
export function collaborationEvidence(data: RecordedData | null) {
  const engineering = data?.engineering;
  const qa = data?.qa;
  const inspections = engineering?.events.filter(event =>
    event.kind === 'EngineerAtlasInspected').length ?? 0;
  const repair = data?.repair?.cycle;
  const boundRepair = !!repair && !!engineering &&
    repair.engineeringRunId === engineering.job.id;
  const boundQa = !!qa && !!engineering &&
    qa.job.engineeringRunId === engineering.job.id &&
    qa.job.planSha256 === engineering.job.planSha256;
  const qaReceived = boundQa && qa!.events.some(event =>
    event.kind === 'QaReceivedEngineerInspectionEvidence');
  return {
    inspections,
    repairState: boundRepair ? repair!.state : null,
    qaState: boundQa ? qa!.job.state : null,
    qaReceived: !!qaReceived,
  };
}

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
        const [qa, handoffs, engineering, repair] = await Promise.all([
          api<{ snapshot: QaSnapshot | null }>(token, '/api/tasks/' + taskId + '/qa-reviews/latest'),
          api<{ events: Handoff[] }>(token, '/api/tasks/' + taskId + '/agent-handoffs'),
          api<{ snapshot: Engineering | null }>(token, '/api/tasks/' + taskId + '/engineering-runs/latest'),
          api<{ snapshot: Repair | null }>(token, '/api/tasks/' + taskId + '/repair-cycles/latest'),
        ]);
        if (!closed) {
          setData({ qa: qa.snapshot, handoffs: handoffs.events,
            engineering: engineering.snapshot, repair: repair.snapshot });
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

function severityRank(severity: string): 'high' | 'med' | 'low' {
  const s = severity.toLowerCase();
  if (s.includes('crit') || s.includes('high') || s.includes('block')) return 'high';
  if (s.includes('maj') || s.includes('med')) return 'med';
  return 'low';
}

export function QualityDestination({ token, taskId, onWork }: {
  token: string; taskId: string; onWork: () => void;
}) {
  const { data, error } = useRecordedProject(token, taskId);
  const qa = data?.qa ?? null;
  const boundToLatest = !!qa && !!data?.engineering &&
    data.engineering.job.id === qa.job.engineeringRunId &&
    data.engineering.job.planSha256 === qa.job.planSha256;
  const findingCount = qa
    ? qa.reports.reduce((n, r) => n + (r.assessment?.findings?.length ?? 0), 0) : 0;
  const collaboration = collaborationEvidence(data);
  return <section className="mc-destination ca-quality" aria-label="Quality evidence">
    <div className="ca-dest-head">
      <div><span className="wb-kicker">STATIC QA EVIDENCE</span>
        <h2>Quality &amp; verification</h2></div>
      {qa && <span className="pill">Review {qa.job.engineeringRunId.slice(0, 8)} · {qa.job.state}</span>}
    </div>
    <p className="hint">Findings are hypotheses from a static source review until you classify
      them — static inspection never verifies compilation or execution.</p>
    {error && <p role="alert" className="alert">Could not load saved quality evidence.</p>}
    {!data && !error && <p role="status">Loading saved QA evidence…</p>}
    {data && <>
      <div className="ca-evidence">
        <div className="ca-ev"><span className="l">Static review</span>
          <span className="v">{qa ? findingCount + ' finding' + (findingCount === 1 ? '' : 's') : 'None'}</span></div>
        <div className="ca-ev"><span className="l">Build verified</span>
          <span className="v ca-no">Not run</span></div>
        <div className="ca-ev"><span className="l">Tests executed</span>
          <span className="v ca-no">Not run</span></div>
        <div className="ca-ev"><span className="l">Source binding</span>
          <span className="v">{boundToLatest ? 'Matches latest run'
            : qa ? 'Historical / unbound' : '—'}</span></div>
      </div>
      <section className="ca-report" aria-label="Recorded agent collaboration">
        <div className="ca-report-head">
          <strong>Investigation → repair → independent QA</strong>
          <span className="pill">Saved evidence</span>
        </div>
        <ol className="ca-collaboration-list">
          <li><strong>Software Engineer:</strong>{' '}
            {collaboration.inspections > 0
              ? collaboration.inspections + ' Code Atlas source inspection(s) recorded'
              : 'No Code Atlas inspection recorded for the latest source run'}</li>
          <li><strong>Authorized repair:</strong>{' '}
            {collaboration.repairState ?? 'No repair cycle bound to the latest source run'}</li>
          <li><strong>Independent QA:</strong>{' '}
            {collaboration.qaState
              ? collaboration.qaState + (collaboration.qaReceived
                ? ' · received recorded investigation context'
                : ' · no investigation handoff recorded')
              : 'No QA review bound to the latest source run'}</li>
        </ol>
        <p className="hint">These are actual recorded steps, not proof that generated
          code compiled, executed or passed tests.</p>
      </section>
      {!qa && <p className="panel">No QA review recorded yet.</p>}
      {qa && qa.reports.map(report => <div className="ca-report" key={report.path}>
        <div className="ca-report-head">
          <strong className="ca-path">{report.path}</strong>
          <span className="pill">{report.assessment?.findings?.length ?? 0} finding(s)</span>
        </div>
        {report.assessment?.summary && <p className="hint">{report.assessment.summary}</p>}
        {(report.assessment?.findings ?? []).map((finding, index) =>
          <div className="ca-finding" key={index} data-sev={severityRank(finding.severity)}>
            <span className="ca-sevchip">{finding.severity}</span>
            <span className="ca-finding-text">{finding.requirement}</span>
          </div>)}
      </div>)}
      <div className="honest"><b>Repair stays gated:</b> classifying a finding never runs code.
        A repair cycle runs only after you authorize the exact plan hash in Work.</div>
      <button type="button" className="primary" onClick={onWork}>
        Open Work for finding triage and repair authorization
      </button>
    </>}
  </section>;
}

export function SkillsDestination({ token }: { token: string }) {
  return <section className="mc-destination ca-skills" aria-label="Agent skills">
    <div className="ca-dest-head">
      <div><span className="wb-kicker">AGENT CAPABILITIES</span><h2>Skills</h2></div>
      <span className="pill">Planned · Architecture Extension A1</span>
    </div>
    <p className="hint">Portable SKILL.md packages are planned. This view separates declared
      scope from what is actually installed, granted, and used. No installed registry or
      assignment controls are claimed here.</p>
    <div className="ca-skill-cols">
      <div className="ca-skill-col ca-skill-declared">
        <h3>Declared</h3><span className="ca-skill-cap">Host-defined scope</span>
        <AgentIdentityPanel token={token} />
      </div>
      <div className="ca-skill-col">
        <h3>Installed</h3><span className="ca-skill-cap">Portable packages</span>
        <div className="ca-skill-empty">No skill packages installed. The registry is not built yet.</div>
      </div>
      <div className="ca-skill-col">
        <h3>Granted</h3><span className="ca-skill-cap">Permissions</span>
        <div className="ca-skill-empty">Execution requires per-run approval. No standing grants.</div>
      </div>
      <div className="ca-skill-col">
        <h3>Used</h3><span className="ca-skill-cap">Recorded usage</span>
        <div className="ca-skill-empty">No skill invocations recorded.</div>
      </div>
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
