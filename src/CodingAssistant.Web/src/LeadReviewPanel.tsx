import { useState } from 'react';
import { api } from './api';
import type { EngineerSnapshot } from './EngineeringWorkspace';

export interface LeadReviewSnapshot {
  job: { id: string; engineeringRunId: string; sourceSha256: string;
    planSha256: string; model: string; state: string; errorCode: string | null };
  current: boolean;
  disposition: string;
  reports: Array<{ path: string; sourceSha256: string; assessment: {
    disposition: string; summary: string;
    findings: Array<{ severity: string; category: string; path: string; line: number;
      evidence: string; impact: string; resolution: string; verification: string }>;
  } }>;
}

/** Owner explicitly starts the revision-bound static review. No repair or execution grant. */
export function LeadReviewPanel({ token, taskId, source, snapshot }: {
  token: string; taskId: string; source: EngineerSnapshot | null;
  snapshot: LeadReviewSnapshot | null;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const running = snapshot?.job.state === 'Running';
  async function act(cancel: boolean) {
    if (busy || (cancel ? !running : running || source?.job.state !== 'Generated')) return;
    setBusy(true); setError(''); setNotice('');
    try {
      const root = '/api/tasks/' + taskId + '/lead-reviews';
      if (cancel && snapshot)
        await api(token, root + '/' + snapshot.job.id + '/cancel', {});
      else if (source)
        await api(token, root, { engineeringRunId: source.job.id, planSha256: source.job.planSha256 });
      setNotice(cancel ? 'Cancellation requested.' : 'Review admitted. Awaiting saved evidence.');
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Review request rejected.'); }
    finally { setBusy(false); }
  }
  return <section aria-label="Independent lead source review" className="ca-studio-specialist-facts">
    <h3>Architecture &amp; code review</h3>
    <p>Static source inspection only. No code changes, builds or tests.</p>
    {snapshot && <p role="status">{snapshot.job.state} · {snapshot.current ? snapshot.disposition : 'Stale source'} · {snapshot.reports.length} saved file reports</p>}
    {!running && <button type="button" disabled={busy || source?.job.state !== 'Generated'}
      onClick={() => void act(false)}>Review generated source</button>}
    {running && <button type="button" disabled={busy} onClick={() => void act(true)}>Cancel review</button>}
    {source?.job.state !== 'Generated' && <p>Complete source generation before review.</p>}
    {notice && <p role="status">{notice}</p>}
    {error && <p role="alert">{error}</p>}
    {snapshot?.job.errorCode && <p role="status">{snapshot.job.errorCode}</p>}
    {snapshot?.reports.map(report => <details key={report.path}>
      <summary>{report.path} · {report.assessment.disposition}</summary>
      <p>{report.assessment.summary}</p>
      {report.assessment.findings.map((finding,index) => <div key={index}>
        <strong>{finding.severity} · {finding.category} · line {finding.line}</strong>
        <pre style={{whiteSpace:'pre-wrap',overflowWrap:'anywhere'}}>{finding.evidence}</pre>
        <p>{finding.impact}</p><p>Resolution: {finding.resolution}</p>
        <p>Independent check: {finding.verification}</p>
      </div>)}
    </details>)}
  </section>;
}
