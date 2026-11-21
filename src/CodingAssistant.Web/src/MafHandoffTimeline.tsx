import { useCallback, useEffect, useState } from 'react';
import { api } from './api';

interface HandoffEvent {
  taskId: string;
  kind: 'SpecificationHandoff' | 'ArchitectureProposalSaved' |
    'ArchitectureHandoff' | 'PlanProposed' | 'PlanApproved' |
    'EngineeringTaskAssigned' | 'EngineeringTaskCompleted' |
    'QaReviewRequested' | 'QaStaticReportSaved';
  fromAgent: string;
  toAgent: string;
  specificationVersion: number;
  specificationSha256: string;
  revision: number;
  model: string;
  planSha256?: string | null;
  architectureSha256?: string | null;
  workItemId?: string | null;
  createdUtc: string;
  current: boolean;
}

interface HandoffResponse {
  engine: string;
  executionAvailable: boolean;
  events: HandoffEvent[];
}

/** Only persisted actions, never invented "agent thinking" or percentage. */
export function MafHandoffTimeline({ token, taskId }: {
  token: string;
  taskId: string;
}) {
  const [data, setData] = useState<HandoffResponse | null>(null);
  const [error, setError] = useState(false);
  const [refresh, setRefresh] = useState(0);

  const reload = useCallback(() => setRefresh(x => x + 1), []);
  useEffect(() => {
    let cancelled = false;
    setData(null);
    setError(false);
    void api<HandoffResponse>(token, '/api/tasks/' + taskId + '/agent-handoffs')
      .then(result => { if (!cancelled) setData(result); })
      .catch(() => { if (!cancelled) setError(true); });
    return () => { cancelled = true; };
  }, [token, taskId, refresh]);

  return <section className="wb-maf-handoffs" aria-label="Agent handoff history">
    <div className="wb-maf-handoff-title">
      <p className="wb-kicker">AGENT HANDOFFS</p>
      <button type="button" className="wb-refresh" onClick={reload}
        aria-label="Refresh agent handoffs" title="Refresh agent handoffs">↻</button>
    </div>
    {!data && !error && <p className="wb-muted">Loading saved handoffs…</p>}
    {error && <p className="wb-muted">Could not load agent history.
      Your saved work is unchanged.</p>}
    {data && <>
      <p className="wb-muted">MAF · Local Ollama · Planning and unverified source</p>
      {data.events.length === 0 && <p className="wb-muted">
        No approved agent handoffs recorded for this project yet.</p>}
      <ol className="wb-maf-events">
        {data.events.slice(-6).map((item, i) => <li key={
          item.kind + ':' + item.specificationVersion + ':' + item.revision + ':' + i}>
          <strong>{item.fromAgent} → {item.toAgent}</strong>
          <small>{item.kind === 'SpecificationHandoff'
            ? 'Approved specification handed to Architect'
            : item.kind === 'ArchitectureProposalSaved'
              ? 'Architecture proposal saved for human review'
              : item.kind === 'ArchitectureHandoff'
                ? 'Approved architecture given to Technical Lead'
                : item.kind === 'PlanProposed'
                  ? 'Implementation plan proposed for your review'
                  : item.kind === 'PlanApproved'
                    ? 'Owner approved plan for engineering backlog (not executed)'
                    : item.kind === 'EngineeringTaskAssigned'
                      ? 'Engineering task assigned to Software Engineer'
                      : item.kind === 'EngineeringTaskCompleted'
                        ? 'Source changes saved for engineering task (not tested)'
                        : item.kind === 'QaReviewRequested'
                          ? 'Independent static source review requested'
                          : 'QA report saved (hypotheses only; no tests run)'}</small>
          <small>Spec v{item.specificationVersion} · {item.model}
            {item.planSha256 ? ' · Plan ' + item.planSha256.slice(0, 10) : ''}
            {item.workItemId ? ' · ' + item.workItemId : ''}
            {item.current ? '' : ' · Historical'}</small>
        </li>)}
      </ol>
      <p className="wb-muted">These are saved events, not live model telemetry.
        Source generation does not authorize code execution.</p>
    </>}
  </section>;
}
