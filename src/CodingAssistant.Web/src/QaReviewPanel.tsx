import { useEffect, useState } from 'react';
import { api, newRequestId } from './api';
import type { EngineeringState } from './EngineeringWorkspace';

interface QaFinding {
  severity: 'Critical' | 'Major' | 'Minor';
  requirement: string;
  evidence: string;
  recommendation: string;
  criterionId?: string | null;
  sourceExcerpt?: string | null;
}
interface QaAcceptanceSummary {
  planSha256: string; sourceSha256: string; reviewId: string;
  criteria: { criterion: { id: string; workItemId: string; workItemTitle: string;
    requirement: string }; staticStatus: string; verificationStatus: string;
    concernCount: number; implementationIndicators?: { path: string; sourceSha256: string;
      sourceExcerpt: string; explanation: string }[] }[];
  findings: { path: string; index: number; finding: QaFinding;
    criterionId: string | null; originalIndices: number[] }[];
  rawFindingCount: number;
  unmappedFindingCount: number;
  crossFileEvidence?: { criterionId: string; observedFromPath: string;
    causePath: string; causeExcerpt: string; explanation: string;
    relatedFindingIndex: number | null }[];
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
  acceptance?: QaAcceptanceSummary | null;
  unchangedFailedRepair?: boolean;
  staticPreflight?: { runId: string; sourceSha256: string; status: string;
    verificationStatus: string; findings: { code: string; severity: string; path: string;
      detail: string; sourceExcerpt?: string | null; referencedPath?: string | null }[] } | null;
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
  // A new review or source revision invalidates previous owner selection.
  useEffect(() => {
    setRepairOpen(false); setSelected([]); setConfirmed(false);
  }, [taskId, result?.job.id, source?.id, plan?.planSha256]);

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

  const acceptance = result?.acceptance &&
    result.acceptance.reviewId === result.job.id &&
    result.acceptance.planSha256 === result.job.planSha256 &&
    result.acceptance.sourceSha256 === result.job.sourceSha256
    ? result.acceptance : null;
  const rawIssueCount = result?.reports.reduce((total, report) =>
    total + report.assessment.findings.length, 0) ?? 0;
  const potentialIssues = acceptance?.findings.length ?? rawIssueCount;
  const qaAtlasInvestigations = result?.events.filter(
    event => event.kind === 'QaAtlasInspected').length ?? 0;

  const repairRunning = repair?.cycle.state === 'Running' ||
    repair?.cycle.state === 'QaRunning';
  const availableFindings = (acceptance?.findings ??
    result?.reports.flatMap(report =>
      report.assessment.findings.map((finding, index) => ({
        path: report.path, index, finding, criterionId: null, originalIndices: [index],
      }))) ?? []).map(row => ({ ...row, key: row.path + ':' + row.index }));
  const selectedFindings = availableFindings.filter(f => selected.includes(f.key));
  const selectedFiles = new Set(selectedFindings.map(f => f.path.toLowerCase()));
  const canRequestRepair = currentReport && result?.job.state === 'Reviewed' &&
    (result.sourceState === 'Generated' || result.unchangedFailedRepair === true) &&
    potentialIssues > 0 &&
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
      {currentReport && result?.unchangedFailedRepair ? <>
        {' '}The host confirmed this failed repair preserved the exact previously
        generated source. A fresh review and owner confirmation can authorize
        another repair; the application remains untested.
      </> : <>
        {' '}QA can inspect this <strong>saved source</strong> but cannot certify
        application completeness. Request a fresh review to assess recovery
        eligibility. The engineering failure still needs attention.
      </>}
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
        {availableFindings.map(({ path, finding, key, criterionId, originalIndices }) =>
          <label key={key} className="qa-repair-choice">
            <input type="checkbox" checked={selected.includes(key)}
              onChange={event => {
                setSelected(items => event.target.checked
                  ? [...items, key] : items.filter(item => item !== key));
                setConfirmed(false);
              }}/>
            <span><strong>{finding.severity}:</strong>{' '}
              {finding.requirement} <small>({path})</small>
              <small className="hint"> · {criterionId ?? 'No exact criterion link'}
                {originalIndices.length > 1 && ' · ' + originalIndices.length + ' duplicate observations consolidated'}</small>
              <span className="hint">Source evidence: {finding.evidence}</span>
              <span className="hint">{finding.sourceExcerpt
                ? 'Exact source excerpt: ' + finding.sourceExcerpt
                : 'No host-matched source excerpt; verify before repair.'}</span>
              <span className="hint">Proposed action: {finding.recommendation}</span>
            </span>
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
        {' · '}{repair.cycle.state} · {repair.cycle.engineeringRunId.slice(0, 12)}</p>
      <p className="hint">Software Engineer source: {repair.engineering.job.state}
        {' · '}Saved source: {repair.engineering.files.length} file(s)
        {' · '}Host text preflight: {repair.staticPreflight.status}
        {' · '}Independent QA: {repair.review?.job.state ?? 'Not started'}
        {' · '}Functional verification: NOT VERIFIED
      </p>
      {repair.cycle.errorCode && <p className="activity-warning">
        Repair stopped: {repair.cycle.errorCode}.
        {' '}{repair.engineering.files.length > 0 ? 'Previously saved source is preserved.' :
          'No saved source is attached to this repair record.'}</p>}
      {repair.cycle.state === 'Failed' && repair.cycle.engineeringRunId === source?.id &&
        <p className="hint">{running ? 'Independent QA is reviewing the saved revision.' :
          eligible ? (currentReport && result?.job.state === 'Reviewed' ?
            'Use the current findings and fresh owner approval to authorize the next repair.' :
            'Next action: request a fresh static QA review above. A new repair needs successful independent QA and fresh owner approval.') :
            'Recovery is blocked until the current source and approved plan are available.'}</p>}
      {repair.engineering.events?.filter(event =>
        event.kind === 'EngineerOutputRejected').slice(-1).map(event =>
        <p key={event.sequence} className="activity-warning">
          <strong>Last repair output diagnostic:</strong>{' '}
          {event.detail} · {new Date(event.createdUtc).toLocaleString()}
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
        {' · '} {result.reports.length === 0 && result.job.state !== 'Reviewed'
          ? 'No independent findings available' : potentialIssues + ' potential issue(s)'}
        {result.job.errorCode ? ' · ' + result.job.errorCode : ''}
      </p>
      {result.staticPreflight && result.staticPreflight.runId === result.job.engineeringRunId &&
        result.staticPreflight.sourceSha256 === result.job.sourceSha256 && <details>
        <summary>Host source preflight · {result.staticPreflight.findings.length} observation(s)</summary>
        <p className="hint">{result.staticPreflight.status} · deterministic text checks,
          not executed tests or confirmed runtime defects.</p>
        {result.staticPreflight.findings.map((finding, index) => <div className="qa-finding" key={index}>
          <p><strong>{finding.path} · {finding.code}</strong></p>
          <p>{finding.detail}</p>
          {finding.sourceExcerpt && <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{finding.sourceExcerpt}</pre>}
          {finding.referencedPath && <p className="hint">Candidate referenced path: {finding.referencedPath}</p>}
        </div>)}
      </details>}
      {acceptance && <section aria-label="Acceptance evidence" className="qa-acceptance">
        <h3>Acceptance criteria</h3>
        <p className="hint">Exact approved plan v{result.job.planRevision} ·
          {' '}{acceptance.criteria.length} criteria · no functional tests run.
          {rawIssueCount > potentialIssues &&
            ' ' + (rawIssueCount - potentialIssues) + ' exact duplicate observation(s) consolidated.'}
          {acceptance.unmappedFindingCount > 0 &&
            ' ' + acceptance.unmappedFindingCount + ' concern(s) have no exact criterion link.'}
          {!currentReport && ' Historical revision.'}</p>
        <p><strong>{acceptance.criteria.filter(row => row.concernCount > 0).length}
          {' '}criteria with linked static concerns</strong> · all require testing.</p>
        <p className="hint">{acceptance.criteria.filter(row =>
          (row.implementationIndicators?.length ?? 0) > 0).length}
          {' '}criteria with source-grounded implementation indicators (not functional passes).</p>
        <details>
          <summary>Inspect criteria and evidence links ({acceptance.criteria.length})</summary>
          {acceptance.criteria.map(row => <div key={row.criterion.id} className="qa-finding">
            <p><strong>{row.criterion.requirement}</strong></p>
            <p className="hint">{row.criterion.workItemTitle} · {row.criterion.id}</p>
            <p>{row.concernCount > 0
              ? row.concernCount + ' linked static concern(s)'
              : result.job.state !== 'Reviewed' && result.reports.length === 0
                ? 'Not assessed — no independent file review completed'
                : 'No linked static concern (not proof of implementation)'}
              {' · '}<strong>Requires testing</strong></p>
            {(row.implementationIndicators?.length ?? 0) > 0 && <details>
              <summary>Implementation evidence ({row.implementationIndicators?.length})</summary>
              {row.implementationIndicators?.map((indicator, index) => <div key={index}>
                <p><strong>{indicator.path}</strong> · exact saved excerpt</p>
                <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{indicator.sourceExcerpt}</pre>
                <p className="hint">{indicator.explanation} — QA interpretation, not a test result.</p>
              </div>)}
            </details>}
          </div>)}
        </details>
        {(acceptance.crossFileEvidence?.length ?? 0) > 0 && <details>
          <summary>Cross-file investigation ({acceptance.crossFileEvidence?.length})</summary>
          <p className="hint">Related source observations are not additional repair targets.</p>
          {acceptance.crossFileEvidence?.map((observation, index) => <div className="qa-finding" key={index}>
            <p><strong>{observation.observedFromPath} → {observation.causePath}</strong>
              {' · '}{observation.criterionId}</p>
            <p className="hint">{observation.relatedFindingIndex == null
              ? 'Requires investigation — no unique independent finding'
              : 'Linked to independently reviewed finding ' + (observation.relatedFindingIndex + 1)}</p>
            <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{observation.causeExcerpt}</pre>
            <p>{observation.explanation}</p>
          </div>)}
        </details>}
      </section>}
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
          {result.unchangedFailedRepair
            ? 'QA reviewed unchanged source from an unsuccessful repair. Fresh owner-approved repair is available; no functional test has been performed.'
            : 'QA inspected only saved files from an unfinished engineering run. Unfinished tasks remain unverified; this report is not evidence of a complete application.'}
        </p>}
      {!currentReport && <p className="activity-warning">
        Historical QA report: the latest engineering source or plan has changed.
        These findings are retained for reference, not current verification.</p>}
      {result.job.state === 'Reviewed' && potentialIssues === 0 && <p className="hint">
        No primary-file repair findings were recorded.
        {(acceptance?.crossFileEvidence?.length ?? 0) > 0 &&
          ' Cross-file observations still require investigation.'}
        {' '}This does <strong>not</strong> mean the application builds, runs, or passes tests.</p>}
      {result.job.state === 'Running' && <p className="hint">
        The selected local model is reviewing each primary file with any available bounded project context.
        Only persisted findings count as progress.</p>}
      {result.job.state === 'Failed' && <p className="activity-warning" role="alert">
        Independent QA stopped: {result.job.errorCode ?? 'UNKNOWN_ERROR'}.
        {' '}{result.reports.length === 0 ? 'No files were independently reviewed.' :
          'Completed per-file reports remain available.'}
        {' '}No successful review or test result is claimed.
        {eligible && ' Retry the static review above; you may first select another installed QA model in Assign team models.'}</p>}

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
            {finding.sourceExcerpt ? <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{finding.sourceExcerpt}</pre>
              : <p className="hint">No host-matched source excerpt. This remains a hypothesis.</p>}
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
