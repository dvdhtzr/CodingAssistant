import { useEffect, useState } from 'react';
import { api } from './api';
import type { EngineeringState } from './EngineeringWorkspace';

interface QaFinding {
  severity: 'Critical' | 'Major' | 'Minor';
  requirement: string;
  evidence: string;
  recommendation: string;
}
interface QaFileReport {
  path: string;
  sourceSha256: string;
  assessment: {
    summary: string;
    findings: QaFinding[];
    manualChecks: string[];
  };
  reviewedUtc: string;
}
interface QaReviewJob {
  id: string;
  taskId: string;
  engineeringRunId: string;
  sourceSha256: string;
  planRevision: number;
  planSha256: string;
  model: string;
  state: 'Running' | 'Reviewed' | 'Failed' | 'Interrupted';
  phase: string;
  errorCode: string | null;
}
interface QaReviewSnapshot {
  job: QaReviewJob;
  sourceState: 'Generated' | 'Failed' | 'Interrupted' | 'Unavailable';
  reports: QaFileReport[];
  events: {
    sequence: number;
    kind: string;
    detail: string;
    createdUtc: string;
  }[];
}

export function QaReviewPanel({ token, taskId, engineering }: {
  token: string;
  taskId: string;
  engineering: EngineeringState;
}) {
  const [result, setResult] = useState<QaReviewSnapshot | null>(null);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [reload, setReload] = useState(0);

  useEffect(() => {
    let closed = false;
    setResult(null);
    setError('');
    const sync = async () => {
      try {
        const value = await api<{ snapshot: QaReviewSnapshot | null }>(token,
          '/api/tasks/' + taskId + '/qa-reviews/latest');
        if (!closed) {
          setResult(value.snapshot);
          setError('');
        }
      } catch {
        if (!closed) setError('Could not load saved QA review history.');
      }
    };
    void sync();
    const timer = window.setInterval(() => void sync(), 3000);
    return () => { closed = true; window.clearInterval(timer); };
  }, [token, taskId, reload]);

  const source = engineering.snapshot?.job;
  const plan = engineering.plan;
  const terminalSource = !!source &&
    ['Generated', 'Failed', 'Interrupted'].includes(source.state);
  const hasSavedSource = (engineering.snapshot?.files.length ?? 0) > 0;
  const eligible = terminalSource && hasSavedSource &&
    !!plan && plan.status === 'Approved' && plan.current &&
    source?.planSha256 === plan.planSha256;
  const partialSource = source?.state === 'Failed' ||
    source?.state === 'Interrupted';
  const running = result?.job.state === 'Running';
  const currentReport = !!source &&
    result?.job.engineeringRunId === source.id &&
    result.job.planSha256 === plan?.planSha256 &&
    plan?.current === true;

  async function start() {
    if (busy || !eligible || !source || running) return;
    setBusy('start');
    setError('');
    try {
      const admitted = await api<QaReviewJob>(token,
        '/api/tasks/' + taskId + '/qa-reviews', {
          engineeringRunId: source.id,
          planSha256: source.planSha256,
        });
      setResult({ job: admitted, reports: [], events: [],
        sourceState: source.state as 'Generated' | 'Failed' | 'Interrupted' });
      setReload(x => x + 1);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'QA review could not start');
    } finally { setBusy(''); }
  }

  async function cancel() {
    if (!result || !running || busy) return;
    setBusy('cancel');
    setError('');
    try {
      const stopped = await api<QaReviewSnapshot>(token,
        '/api/tasks/' + taskId + '/qa-reviews/' +
        result.job.id + '/cancel', {});
      setResult(stopped);
      setReload(x => x + 1);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'QA review could not stop');
    } finally { setBusy(''); }
  }

  const potentialIssues = result?.reports.reduce((total, report) =>
    total + report.assessment.findings.length, 0) ?? 0;

  return <section className="engineering-workspace" aria-label="Independent QA static source review">
    <div className="technical-plan-heading">
      <strong>QA Engineer · independent source review</strong>
      <span className="pill">MAF · static analysis only</span>
    </div>
    <p className="hint">An independent QA specialist inspects saved
      Software Engineer files against the approved specification and Technical
      Lead acceptance criteria, recording possible defects and checks for later.
      <strong> No build, browser test, or runtime verification is performed.</strong></p>

    {partialSource && hasSavedSource && <p className="activity-warning">
      The Software Engineer run ended <strong>{source?.state}</strong> with
      { ' '}{engineering.snapshot?.files.length} saved file(s).
      QA can inspect this <strong>incomplete, partial source</strong> to
      identify possible issues, but cannot certify the application complete.
      The engineering failure still needs attention.
      {source?.errorCode ? ' Failure: ' + source.errorCode : ''}
    </p>}
    {!eligible && <p className="hint">
      {source?.state === 'Running'
        ? 'Engineering is still modifying source. QA cannot start until the run stops.'
        : !hasSavedSource
          ? 'QA requires at least one saved Software Engineer source file.'
          : 'Approve the current Technical Lead plan to review this source.'}
      {' '}A failed or interrupted run with saved files is eligible for
      explicitly requested partial-source review.</p>}

    <div className="actions">
      <button type="button" className="primary"
        disabled={!eligible || running || !!busy}
        onClick={() => void start()}>
        {busy === 'start' ? 'Starting QA…' :
          result ? (partialSource
            ? 'Request a fresh partial-source QA review'
            : 'Request a fresh static QA review') :
            (partialSource ? 'Ask QA to inspect partial source' :
              'Ask QA Engineer to inspect generated source')}
      </button>
      {running && <button type="button" className="danger"
        disabled={!!busy} onClick={() => void cancel()}>
        Stop QA review
      </button>}
      <button type="button" className="outline"
        disabled={!!busy} onClick={() => setReload(x => x + 1)}>
        Refresh QA findings
      </button>
    </div>
    {error && <p className="activity-warning" role="alert">{error}</p>}
    {result && <>
      <p className="hint" role="status">
        <strong>{result.job.state === 'Reviewed'
          ? result.sourceState === 'Generated'
            ? 'Static review saved'
            : 'Partial-source static review saved'
          : result.job.state}</strong>
        {' · '} {result.reports.length} file(s) inspected
        {' · '} {potentialIssues} potential issue(s)
        {result.job.errorCode ? ' · ' + result.job.errorCode : ''}
      </p>
      {result.sourceState !== 'Generated' &&
        <p className="activity-warning">
          QA inspected only saved files from a {result.sourceState} engineering
          run. Unfinished tasks remain unverified; this report is not
          evidence of a complete application.
        </p>}
      {!currentReport && <p className="activity-warning">
        Historical QA report: the latest engineering source or plan has changed.
        These findings are retained for reference, not current verification.</p>}
      {result.job.state === 'Reviewed' && potentialIssues === 0 && <p className="hint">
        The static model identified no issues in this review. That does
        <strong> not</strong> mean the application builds, runs, or passes tests.</p>}
      {result.job.state === 'Running' && <p className="hint">
        The selected local model is reviewing saved files one at a time.
        Only persisted findings count as progress.</p>}
      {result.job.state === 'Failed' && <p className="activity-warning">
        The static review stopped. Completed per-file reports remain available;
        no successful review or test result is claimed.</p>}

      {result.reports.map(report => <details className="qa-file-report"
        key={report.path}>
        <summary><strong>{report.path}</strong> ·
          {' '}{report.assessment.findings.length} possible issue(s)</summary>
        <p className="hint">Source SHA-256: {report.sourceSha256}</p>
        <p>{report.assessment.summary}</p>
        {report.assessment.findings.map((finding, i) =>
          <article className="qa-finding" key={i}>
            <p><strong>{finding.severity}: {finding.requirement}</strong></p>
            <p><strong>Source evidence:</strong> {finding.evidence}</p>
            <p><strong>Proposed action:</strong> {finding.recommendation}</p>
          </article>)}
        <h4>Checks still to perform</h4>
        <ul>{report.assessment.manualChecks.map((check, i) =>
          <li key={i}>{check}</li>)}</ul>
      </details>)}

      <details>
        <summary>Saved QA activity ({result.events.length})</summary>
        <ol className="events" aria-label="Saved QA review activity">
          {result.events.map(event => <li key={event.sequence}>
            <span className="number">#{event.sequence}</span>
            <span><strong>{event.kind}</strong> · {event.detail}</span>
          </li>)}
        </ol>
      </details>
    </>}
  </section>;
}
