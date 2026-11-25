import { useEffect, useState } from 'react';
import { api, newRequestId } from './api';
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

interface RepairFindingReference {
  path: string; index: number; severity: string; requirement: string;
  evidence: string; recommendation: string; classification: string;
}
interface RepairProposal {
  id: string; taskId: string; qaReviewId: string; engineeringRunId: string;
  sourceSha256: string; findings: RepairFindingReference[];
}
interface RepairCycleView {
  cycle: {
    id: string; state: 'Running' | 'Generated' | 'QaRunning' |
      'QaReviewed' | 'Failed' | 'Interrupted';
    attempt: number; errorCode: string | null;
    engineeringRunId: string; qaReviewId: string | null;
  };
  engineering: { job: { state: string; errorCode: string | null };
    files: unknown[];
    events?: { sequence: number; kind: string; detail: string; createdUtc: string }[] };
  review: { job: { state: string }; reports: QaFileReport[] } | null;
  staticPreflight: { status: string; verificationStatus: string; findings: unknown[] };
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
  const [repair, setRepair] = useState<RepairCycleView | null>(null);
  const [repairHistory, setRepairHistory] = useState<RepairCycleView['cycle'][]>([]);
  const [repairOpen, setRepairOpen] = useState(false);
  const [selected, setSelected] = useState<string[]>([]);
  const [confirmed, setConfirmed] = useState(false);
  const [qaSkill, setQaSkill] = useState<{ skill: { name: string; role: string };
    revision: number; reviewSha256: string; approvedForLocalInstructionReview: boolean } | null>(null);
  const [useQaSkill, setUseQaSkill] = useState(false);
  useEffect(() => {
    let closed = false;
    setQaSkill(null); setUseQaSkill(false);
    void api<{ skills: NonNullable<typeof qaSkill>[] }>(token, '/api/tasks/' + taskId + '/skill-reviews')
      .then(value => { if (!closed) setQaSkill(value.skills.find(item =>
        item.skill.name === 'static-web-verification' && item.skill.role === 'QaEngineer' &&
        item.approvedForLocalInstructionReview) ?? null); })
      .catch(() => { if (!closed) setQaSkill(null); });
    return () => { closed = true; };
  }, [token, taskId, reload, engineering.snapshot?.job.id]);

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

  useEffect(() => {
    let closed = false;
    async function sync() {
      try {
        const [value, history] = await Promise.all([
          api<{ snapshot: RepairCycleView | null }>(token,
            '/api/tasks/' + taskId + '/repair-cycles/latest'),
          api<{ cycles: RepairCycleView['cycle'][] }>(token,
            '/api/tasks/' + taskId + '/repair-cycles'),
        ]);
        if (!closed) {
          setRepair(value.snapshot);
          setRepairHistory(history.cycles);
        }
      } catch {
        // QA itself remains usable if repair history cannot be loaded.
      }
    }
    setRepair(null);
    setRepairHistory([]);
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
    const selectedSkill = useQaSkill ? qaSkill : null;
    setUseQaSkill(false);
    setBusy('start');
    setError('');
    try {
      const admitted = await api<QaReviewJob>(token,
        '/api/tasks/' + taskId + '/qa-reviews', {
          engineeringRunId: source.id,
          planSha256: source.planSha256,
          ...(selectedSkill ? { skill: { name: selectedSkill.skill.name,
            reviewRevision: selectedSkill.revision, reviewSha256: selectedSkill.reviewSha256,
            requestId: newRequestId() } } : {}),
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
  const qaAtlasInvestigations = result?.events.filter(
    event => event.kind === 'QaAtlasInspected').length ?? 0;

  const repairRunning = repair?.cycle.state === 'Running' ||
    repair?.cycle.state === 'QaRunning';
  const availableFindings = result?.reports.flatMap(report =>
    report.assessment.findings.map((finding, index) => ({
      path: report.path, index, finding,
      key: report.path + ':' + index,
    }))) ?? [];
  const selectedFindings = availableFindings.filter(f => selected.includes(f.key));
  const selectedFiles = new Set(selectedFindings.map(f => f.path.toLowerCase()));
  const canRequestRepair = currentReport && result?.job.state === 'Reviewed' &&
    result.sourceState === 'Generated' && potentialIssues > 0 &&
    !repairRunning;
  async function requestRepairs() {
    if (!canRequestRepair || busy || !confirmed ||
        selectedFindings.length === 0 || selectedFiles.size !== 1 ||
        !result) return;
    setBusy('repair');
    setError('');
    try {
      const proposal = await api<RepairProposal>(token,
        '/api/tasks/' + taskId + '/repair-proposals', {
          qaReviewId: result.job.id,
        });
      for (const selected of selectedFindings) {
        await api(token, '/api/tasks/' + taskId +
          '/repair-proposals/' + proposal.id + '/triage', {
          path: selected.path, index: selected.index,
          classification: 'ImplementationDefect',
          rationale: 'Owner confirms this finding contradicts the approved application requirements.',
        });
      }
      const admitted = await api(token,
        '/api/tasks/' + taskId + '/repair-cycles', {
          proposalId: proposal.id,
          confirmation: 'REPAIR ' + proposal.sourceSha256,
          findings: selectedFindings.map(f => ({
            path: f.path, index: f.index,
          })),
        });
      void admitted;
      setRepairOpen(false);
      setSelected([]);
      setConfirmed(false);
      setReload(x => x + 1);
      engineering.refresh();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Repair could not start');
    } finally { setBusy(''); }
  }
  async function cancelRepair() {
    if (!repairRunning || !repair || busy) return;
    setBusy('stopRepair');
    setError('');
    try {
      await api(token, '/api/tasks/' + taskId +
        '/repair-cycles/' + repair.cycle.id + '/cancel', {});
      setReload(x => x + 1);
      engineering.refresh();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not stop repair');
    } finally { setBusy(''); }
  }

  return <section className="engineering-workspace" aria-label="Independent QA static source review">
    <div className="technical-plan-heading">
      <strong>QA Engineer · independent source review</strong>
      <span className="pill">MAF · static analysis only</span>
    </div>
    <p className="hint">An independent QA specialist inspects saved
      Software Engineer files against the approved specification and Technical
      Lead acceptance criteria. For approved Static Web projects, QA may
      receive bounded companion HTML, JavaScript and CSS from the same saved
      revision. Missing context and dynamic behavior require further checks,
      not automatic repairs.
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

    {qaSkill && eligible && !running && <label className="hint">
      <input type="checkbox" checked={useQaSkill} disabled={!!busy}
        onChange={event => setUseQaSkill(event.target.checked)} />
      Use reviewed static-web-verification skill for this static-web QA review only
      (not a runtime test; other project types are rejected)
    </label>}
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
    {canRequestRepair && <div className="repair-action" aria-label="Software Engineer repair workflow">
      <p><strong>{potentialIssues} potential QA issue(s)</strong> — select
        only findings that really contradict your approved specification.
        Ambiguous recommendations should be checked before applying repairs.</p>
      <button type="button" className="primary"
        disabled={!!busy} onClick={() => setRepairOpen(value => !value)}>
        {repairOpen ? 'Close repair selection' : 'Request Repairs'}
      </button>
      {repairOpen && <div className="qa-repair-selection">
        <p className="hint">Select findings in <strong>one source file</strong>
          for this owner-approved cycle. After independent QA, you can
          authorize another cycle against fresh evidence if needed.</p>
        {availableFindings.map(({ path, index, finding, key }) =>
          <label key={key} className="qa-repair-choice">
            <input type="checkbox" checked={selected.includes(key)}
              onChange={event => {
                setSelected(items => event.target.checked
                  ? [...items, key] : items.filter(item => item !== key));
                setConfirmed(false);
              }}/>
            <span><strong>{finding.severity}:</strong>{' '}
              {finding.requirement} <small>({path})</small></span>
          </label>)}
        <label className="qa-repair-choice">
          <input type="checkbox" checked={confirmed}
            onChange={event => setConfirmed(event.target.checked)}/>
          <span>I confirm these selected findings require changes under the
            approved specification, and authorize a bounded source-only repair.</span>
        </label>
        {selectedFiles.size > 1 && <p className="activity-warning">
          Select findings from only one source file for this cycle.</p>}
        <div className="actions">
          <button type="button" className="primary"
            disabled={!!busy || !confirmed || selectedFindings.length === 0 ||
              selectedFiles.size !== 1}
            onClick={() => void requestRepairs()}>
            {busy === 'repair' ? 'Admitting repair…' :
              'Repair selected findings'}
          </button>
        </div>
      </div>}
    </div>}
    {repair && <div className="qa-repair-progress" role="status">
      <p><strong>Repair cycle {repair.cycle.attempt}</strong>
        {' · '}{repair.cycle.state}</p>
      <p className="hint">Software Engineer source: {repair.engineering.job.state}
        {' · '}Static inspection: {repair.staticPreflight.status}
        {' · '}Independent QA: {repair.review?.job.state ?? 'Not started'}
        {' · '}Functional verification: NOT VERIFIED
      </p>
      {repair.cycle.errorCode && <p className="activity-warning">
        Repair stopped: {repair.cycle.errorCode}.
        {' '}Previously saved source is preserved.</p>}
      {repair.engineering.events?.filter(event =>
        event.kind === 'EngineerOutputRejected').slice(-1).map(event =>
        <p key={event.sequence} className="activity-warning">
          <strong>Last repair output diagnostic:</strong>{' '}
          {event.detail}
        </p>)}
      {repair.cycle.state === 'QaReviewed' &&
        <p className="hint" role="status">
          Independent QA reassessment recorded:
          {' '}{repair.review?.reports.reduce((sum, report) =>
            sum + (report.assessment?.findings?.length ?? 0), 0) ?? 0}
          {' '}potential finding(s). Inspect the current QA report and classify
          any remaining defects before authorizing another repair.
          {' '}A static review does not prove functional correctness.
        </p>}
      {repairRunning && <button type="button" className="danger"
        disabled={!!busy} onClick={() => void cancelRepair()}>
        Stop repair cycle
      </button>}
    </div>}
    {repairHistory.length > 0 && <details className="qa-repair-history">
      <summary>Repair history ({repairHistory.length})</summary>
      <ol className="events" aria-label="Saved Software Engineer repair cycles">
        {repairHistory.map(cycle => <li key={cycle.id}>
          <strong>Cycle {cycle.attempt}: {cycle.state}</strong>
          {' · '}{cycle.engineeringRunId.slice(0, 12)}
          {cycle.qaReviewId ? ' · Independent QA recorded' : ' · No completed QA yet'}
          {cycle.errorCode ? ' · ' + cycle.errorCode : ''}
        </li>)}
      </ol>
    </details>}
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
      {qaAtlasInvestigations > 0 && <p className="hint"
        aria-label="Independent QA Code Atlas investigations">
        QA independently requested <strong>{qaAtlasInvestigations} Code Atlas
        observation(s)</strong> of its assigned source file(s).
        {' '}These are saved static investigation records, not executed tests
        or verified repairs.
        {!currentReport && ' Historical source only.'}
      </p>}
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
        The selected local model is reviewing each primary file with any available bounded project context.
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
