import { useEffect, useState } from 'react';
import { api } from './api';
import type { EngineerRun, EngineerFile } from './EngineeringWorkspace';

export interface SourceSmokeResponse {
  engineeringRunId: string;
  scopeSha256: string;
  status: 'SMOKE_PASS_NOT_VERIFIED' | 'SMOKE_TEST_FAILED' | 'BLOCKED';
  passed: number;
  required: number;
  containerRemoved: boolean;
  browserSandbox: 'DISABLED_CONTAINER_ONLY';
  reason: string | null;
  checks: Record<string, boolean>;
  verified: false;
  evidenceId?: string;
  current?: boolean;
}

export function smokeMatchesScope(
  result: SourceSmokeResponse, scope: VerificationScope
): boolean {
  return result !== null && scope.status === 'SCOPE_CANDIDATE' &&
    result.engineeringRunId === scope.engineeringRunId &&
    result.scopeSha256 === scope.scopeSha256 &&
    result.verified === false && result.current !== false &&
    result.browserSandbox === 'DISABLED_CONTAINER_ONLY' &&
    ['SMOKE_PASS_NOT_VERIFIED','SMOKE_TEST_FAILED','BLOCKED']
      .includes(result.status) &&
    result.required === 9 && Number.isInteger(result.passed) &&
    result.passed >= 0 && result.passed <= 9 &&
    typeof result.containerRemoved === 'boolean' &&
    result.checks !== null && typeof result.checks === 'object' &&
    !Array.isArray(result.checks) &&
    Object.keys(result.checks).length <= 9 &&
    Object.values(result.checks).every(value => typeof value === 'boolean') &&
    (result.status !== 'SMOKE_PASS_NOT_VERIFIED' ||
      (result.containerRemoved && result.passed === 9 &&
       Object.keys(result.checks).length === 9 &&
       Object.values(result.checks).every(Boolean)));
}

interface SavedSmokeEvidence {
  id: string; taskId: string; runId: string; sourceSha256: string;
  scopeSha256: string; planSha256: string; state: string;
  current: boolean; verified: false;
  report: Omit<SourceSmokeResponse, 'engineeringRunId' | 'scopeSha256' | 'verified' | 'evidenceId' | 'current'> | null;
}

function savedSmokeForScope(evidence: SavedSmokeEvidence[], scope: VerificationScope): SourceSmokeResponse | null {
  if (!Array.isArray(evidence) || scope.status !== 'SCOPE_CANDIDATE') return null;
  // Never resurrect a previous pass when the latest attempt was interrupted.
  const row = evidence[0];
  if (!row || !row.current || row.verified !== false || row.state !== 'Recorded' ||
      row.taskId !== scope.taskId || row.runId !== scope.engineeringRunId ||
      row.sourceSha256 !== scope.sourceFingerprint || row.planSha256 !== scope.planSha256 ||
      row.scopeSha256 !== scope.scopeSha256 || !row.report) return null;
  const result: SourceSmokeResponse = {
    ...row.report, engineeringRunId: row.runId, scopeSha256: row.scopeSha256,
    verified: false, current: true, evidenceId: row.id,
  };
  return smokeMatchesScope(result, scope) ? result : null;
}

export interface ProjectSourceReadinessReport {
  runId: string;
  sourceState: string;
  sourceSha256: string | null;
  projectKind: 'NODE_SERVER' | 'STATIC_WEB' | 'DOTNET_PROJECT' | 'UNKNOWN';
  runtimeProfileId: string | null;
  status: string;
  verificationStatus: 'GENERATED_NOT_TESTED';
  executionAvailable: false;
  findings: Array<{
    code: string;
    severity: 'Error' | 'Warning';
    path: string;
    detail: string;
  }>;
}

export function ProjectReadinessView({ report, error }: {
  report: ProjectSourceReadinessReport | null;
  error?: string;
}) {
  if (!report) return <section className="ca-source-brief"
    aria-label="Project source assessment">
    <strong>Engineering assessment</strong>
    <p role="status">{error || 'Inspecting saved source…'}</p>
  </section>;
  const kind = {
    NODE_SERVER: 'Node.js server',
    STATIC_WEB: 'Static website',
    DOTNET_PROJECT: '.NET application',
    UNKNOWN: 'Unclassified project',
  }[report.projectKind];
  const incomplete = report.status === 'INCOMPLETE';
  const pending = report.status === 'PENDING_SOURCE';
  const errors = report.findings.filter(finding => finding.severity === 'Error');
  const count = report.findings.length;
  return <section className="ca-source-brief"
    data-state={incomplete ? 'needs-work' : report.status.toLowerCase()}
    aria-label="Project source assessment">
    <div className="ca-source-brief-header">
      <div>
        <span className="wb-kicker">ENGINEERING WORK ORDER</span>
        <h4>{incomplete ? 'Software Engineer · completion required'
          : pending ? 'Software Engineer · generating source'
          : 'Software Engineer · source review'}</h4>
      </div>
      <span className="ca-source-brief-status">
        {incomplete ? errors.length + ' blocking issue(s)'
          : pending ? 'In progress' : 'Source only'}
      </span>
    </div>
    <p>{incomplete
      ? kind + ' identified. The Software Engineer needs to complete the missing project files before build verification can begin.'
      : report.status === 'SOURCE_READY_FOR_REVIEW'
        ? kind + ' structure has no detected blockers; independent testing is still required.'
        : 'The saved source is not ready for build or delivery verification.'}</p>
    {incomplete && <p className="ca-source-brief-next">
      <strong>Next:</strong> Review the QA and repair options below.
      Missing new files currently require a broader engineering completion workflow;
      the existing narrow repair lane can only change saved files.
    </p>}
    {count > 0 && <details className="ca-source-findings">
      <summary>Inspect {count} source issue{count === 1 ? '' : 's'}</summary>
      <ul aria-label="Source completeness findings">
        {report.findings.map((finding, i) => <li key={finding.code + i}>
          <strong>{finding.severity} · {finding.path || 'Project'}</strong>
          <span>{finding.detail}</span>
        </li>)}
      </ul>
    </details>}
    {report.projectKind === 'NODE_SERVER' && <p className="ca-source-runtime">
      Runtime requirement: server-backed Node.js. Static Web testing does not apply.
    </p>}
    <small className="ca-source-brief-foot">
      Generated source · build not run · tests not run. No executable authority
      is granted by this assessment.
    </small>
  </section>;
}

export interface VerificationScopeFile {
  path: string;
  sha256: string;
  utf8Bytes: number;
}
export interface VerificationScope {
  schemaVersion: number;
  taskId: string;
  engineeringRunId: string;
  planRevision: number;
  planSha256: string;
  specificationVersion: number;
  specificationSha256: string;
  architectureSha256: string;
  model: string;
  sourceFingerprint: string | null;
  scopeSha256: string | null;
  status: 'SCOPE_CANDIDATE' | 'BLOCKED';
  executionEnabled: boolean;
  verificationStatus: 'GENERATED_NOT_TESTED';
  blockers: string[];
  files: VerificationScopeFile[];
}

// Only documented host-owned codes. This mapping is explanatory; it is
// never used as client-side authorization or a way to bypass the API.
export const scopeBlockerDescriptions: Record<string, string> = {
  SOURCE_NOT_COMPLETE: 'Source generation did not complete.',
  SOURCE_NOT_LATEST: 'A newer Software Engineer run exists.',
  APPROVED_PLAN_MISMATCH: 'The approved Technical Lead plan has changed.',
  APPROVED_SPECIFICATION_MISMATCH: 'The approved product specification has changed.',
  APPROVED_ARCHITECTURE_MISMATCH: 'The approved architecture has changed.',
  NOT_STATIC_WEB_PROFILE: 'This project does not use the Static Web profile.',
  MODEL_BINDING_CHANGED: 'The source model differs from the approved plan.',
  SOURCE_FILE_COUNT_OUT_OF_BOUNDS: 'The saved source file count is outside permitted limits.',
  UNSAFE_OR_DUPLICATE_FILE_PATH: 'A filename is unsafe or conflicts with another file.',
  SOURCE_EXTENSION_OUT_OF_PROFILE: 'A saved file type is outside Static Web.',
  SOURCE_FILE_BOUNDS_INVALID: 'A saved file is too large or invalid.',
  SOURCE_FILE_HASH_MISMATCH: 'Saved source bytes no longer match their hash.',
  SOURCE_TOTAL_BOUNDS_INVALID: 'The project exceeds source size limits.',
  MISSING_ROOT_INDEX_HTML: 'The project needs a root index.html entry point.',
  STATIC_SOURCE_PREFLIGHT_HAS_FINDINGS: 'Static checks found issues to resolve before scope admission.',
};

const hash = (value: unknown): value is string =>
  typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const pathSafe = (value: unknown): value is string =>
  typeof value === 'string' && value.length >= 3 && value.length <= 140 &&
  /^[A-Za-z0-9_-]+(?:\/[A-Za-z0-9_-]+)*\.(?:html|css|js|json)$/i.test(value);

/** Client consistency check. The server remains the security authority. */
export function scopeMatchesRun(scope: VerificationScope,
  run: EngineerRun, files?: EngineerFile[]): boolean {
  if (!scope || scope.schemaVersion !== 1 ||
      scope.taskId !== run.taskId || scope.engineeringRunId !== run.id ||
      scope.planRevision !== run.planRevision ||
      scope.planSha256 !== run.planSha256 ||
      scope.specificationVersion !== run.specificationVersion ||
      scope.specificationSha256 !== run.specificationSha256 ||
      scope.architectureSha256 !== run.architectureSha256 ||
      scope.model !== run.model ||
      scope.executionEnabled !== false ||
      scope.verificationStatus !== 'GENERATED_NOT_TESTED' ||
      !Array.isArray(scope.blockers) || !Array.isArray(scope.files) ||
      scope.blockers.length > 20 || scope.files.length > 12 ||
      !scope.blockers.every(code => typeof code === 'string' &&
        /^[A-Z_]{3,64}$/.test(code)) ||
      !scope.files.every(file => file && pathSafe(file.path) &&
        hash(file.sha256) && Number.isInteger(file.utf8Bytes) &&
        file.utf8Bytes > 0 && file.utf8Bytes <= 65536))
    return false;
  const seen = new Set(scope.files.map(file => file.path.toLowerCase()));
  if (seen.size !== scope.files.length) return false;
  if (files && scope.status === 'SCOPE_CANDIDATE' &&
      (files.length !== scope.files.length ||
       files.some(file => !scope.files.some(item =>
         item.path === file.path && item.sha256 === file.sha256))))
    return false;
  if (scope.status === 'SCOPE_CANDIDATE')
    return run.state === 'Generated' && hash(scope.scopeSha256) &&
      hash(scope.sourceFingerprint) && scope.blockers.length === 0 &&
      scope.files.length > 0;
  return scope.status === 'BLOCKED' &&
    scope.blockers.length > 0 && scope.scopeSha256 === null;
}

export function VerificationScopeView({ scope, error, empty = false }: {
  scope: VerificationScope | null;
  error?: string | null;
  empty?: boolean;
}) {
  const candidate = scope?.status === 'SCOPE_CANDIDATE';
  return <section className="verification-scope"
    data-state={candidate ? 'candidate' : scope ? 'blocked' : 'unknown'}
    aria-label="Generated source verification readiness">
    <div className="verification-scope-heading">
      <div>
        <span className="wb-kicker">SOURCE / SCOPE INTEGRITY</span>
        <h4>Verification readiness</h4>
      </div>
      <strong className="verification-scope-state">
        {candidate ? 'Scope candidate' : scope ? 'Needs attention' : 'Not assessed'}
      </strong>
    </div>
    {scope ? <>
      <p>{candidate
        ? 'Current approvals and saved Static Web file hashes meet the read-only scope checks.'
        : 'This source snapshot cannot be admitted to a future test run yet.'}</p>
      {scope.blockers.length > 0 &&
        <ul className="verification-scope-blockers"
          aria-label="Scope blockers">
          {scope.blockers.map(code => <li key={code}>
            <strong>{code.replaceAll('_', ' ')}</strong>
            <span>{scopeBlockerDescriptions[code] ??
              'The host rejected this scope requirement.'}</span>
          </li>)}
        </ul>}
      {candidate && <div className="verification-scope-facts">
        <span>Bound files <strong>{scope.files.length}</strong></span>
        <span>Plan revision <strong>{scope.planRevision}</strong></span>
        <span>Execution <strong>DISABLED</strong></span>
      </div>}
      {scope.files.length > 0 && <details className="verification-scope-manifest">
        <summary>Saved source manifest ({scope.files.length} file(s))</summary>
        <ul>{scope.files.map(file => <li key={file.path}>
          <strong>{file.path}</strong>
          <span>{file.utf8Bytes.toLocaleString('en-US')} bytes</span>
          <code title="Saved file SHA-256">{file.sha256.slice(0, 16)}…</code>
        </li>)}</ul>
      </details>}
      {candidate && <details className="verification-scope-binding">
        <summary>Bound source fingerprint and scope digest</summary>
        <p>Source SHA-256 <code>{scope.sourceFingerprint}</code></p>
        <p>Scope SHA-256 <code>{scope.scopeSha256}</code></p>
        <p>These are fingerprints, not authorization tokens.</p>
      </details>}
    </> : <p>{error
      ? 'Readiness report unavailable. No eligibility can be inferred.'
      : empty ? 'Source generation has not finished; no scope assessment exists yet.'
        : 'Reading saved source and current approvals…'}</p>}
    <p className="verification-scope-boundary">
      <strong>Generated · not tested.</strong> A scope candidate is not an execution
      approval, a passing build, or evidence of browser tests.
      The untrusted Static Web executor remains unqualified.</p>
  </section>;
}

/** Show the authenticated scope with the delivered files; never call a runner. */
export function EngineeringVerificationScope({ token, run, files }: {
  token: string;
  run: EngineerRun;
  files: EngineerFile[];
}) {
  const [scope, setScope] = useState<VerificationScope | null>(null);
  const [readiness, setReadiness] = useState<ProjectSourceReadinessReport | null>(null);
  const [error, setError] = useState('');
  const [smoke, setSmoke] = useState<SourceSmokeResponse | null>(null);
  const [smokeBusy, setSmokeBusy] = useState(false);
  const [smokeError, setSmokeError] = useState('');
  const sourceSignature = files.map(file => file.path + ':' + file.sha256)
    .sort().join('|');
  useEffect(() => {
    let closed = false;
    let pending = false;
    setScope(null);
    setReadiness(null);
    setError('');
    setSmoke(null);
    setSmokeError('');
    async function load() {
      if (pending) return;
      pending = true;
      try {
        const [value, source, history] = await Promise.all([
          api<VerificationScope>(token,
            '/api/tasks/' + run.taskId + '/engineering-runs/' +
            run.id + '/verification-scope'),
          api<ProjectSourceReadinessReport>(token,
            '/api/tasks/' + run.taskId + '/engineering-runs/' +
            run.id + '/source-readiness'),
          api<{ evidence: SavedSmokeEvidence[] }>(token,
            '/api/tasks/' + run.taskId + '/engineering-runs/' +
            run.id + '/source-smoke-evidence').catch(() => null),
        ]);
        if (!closed) {
          if (!scopeMatchesRun(value, run, files))
            throw new Error('Scope binding mismatch');
          if (source.runId !== run.id ||
              source.sourceState !== run.state ||
              source.verificationStatus !== 'GENERATED_NOT_TESTED' ||
              source.executionAvailable !== false ||
              !Array.isArray(source.findings) ||
              source.findings.length > 30 ||
              (run.state === 'Generated' &&
                source.sourceSha256 !== null &&
                !hash(source.sourceSha256)))
            throw new Error('Source readiness binding mismatch');
          setScope(value);
          setReadiness(source);
          if (history) setSmoke(savedSmokeForScope(history.evidence, value));
          setError('');
        }
      } catch {
        if (!closed) {
          setScope(null);
          setReadiness(null);
          setError('Project source assessment is unavailable.');
        }
      } finally { pending = false; }
    }
    void load();
    const interval = window.setInterval(() => void load(), 8000);
    return () => { closed = true; window.clearInterval(interval); };
  }, [token, run.id, run.taskId, run.planSha256, run.state, sourceSignature]);
  async function runSmoke() {
    if (smokeBusy || !scope || !scopeMatchesRun(scope, run, files) ||
        scope.status !== 'SCOPE_CANDIDATE') return;
    setSmokeBusy(true);
    setSmoke(null);
    setSmokeError('');
    try {
      const result = await api<SourceSmokeResponse>(token,
        '/api/tasks/' + run.taskId + '/engineering-runs/' + run.id +
        '/source-smoke', {
          scopeSha256: scope.scopeSha256,
          // Clicking the clearly labeled control is the owner's explicit opt-in.
          acceptContainerOnlyBrowserSandbox: true,
        });
      if (!smokeMatchesScope(result, scope))
        throw new Error('The test report did not match this saved source.');
      setSmoke(result);
    } catch (cause) {
      setSmokeError(cause instanceof Error ? cause.message :
        'Docker smoke test could not start.');
    } finally {
      setSmokeBusy(false);
    }
  }
  return <div className="verification-action-stack">
    <ProjectReadinessView report={readiness} error={error}/>
    {scope?.status === 'SCOPE_CANDIDATE' && <section
      aria-label="Test generated project"
      className="verification-smoke">
      <div>
        <strong>Test this project</strong>
        <p>A one-click, offline Docker smoke test of the saved files.
          Chromium's internal sandbox is disabled inside the restricted
          disposable container. Use only code generated for your own project.
          This is not full functional verification.</p>
      </div>
      <button type="button" className="primary"
        disabled={smokeBusy}
        onClick={() => void runSmoke()}>
        {smokeBusy ? 'Running browser smoke test…' :
          'Test Project'}
      </button>
      {smokeBusy && <p role="status">
        Running your saved source in an isolated container; no ZIP download
        is needed.</p>}
      {smokeError && <p role="alert">{smokeError}</p>}
      {smoke && !smokeBusy && smokeMatchesScope(smoke, scope) && <div className="verification-smoke-result" role="status">
        <strong>{smoke.status === 'SMOKE_PASS_NOT_VERIFIED'
          ? 'Smoke checks passed — not fully verified'
          : smoke.status === 'SMOKE_TEST_FAILED'
            ? 'Source smoke checks failed' : 'Smoke test blocked'}</strong>
        <p>{smoke.passed}/{smoke.required} source-specific checks passed.
          Container removed: {smoke.containerRemoved ? 'Yes' : 'Not confirmed'}.
          {smoke.reason ? ' ' + smoke.reason : ''}</p>
        {Object.entries(smoke.checks).some(([, passed]) => !passed) &&
          <ul aria-label="Failing source smoke checks">
            {Object.entries(smoke.checks)
              .filter(([, passed]) => !passed)
              .map(([name]) => <li key={name}>
                {name.replaceAll('_', ' ')}
              </li>)}
          </ul>}
        {smoke.evidenceId && <small>Saved smoke evidence · {smoke.evidenceId.slice(0, 12)}</small>}
        <p>QA result: smoke-only, NOT VERIFIED. A passing render
          does not establish that every button or requirement works.</p>
      </div>}
    </section>}
    <details className="verification-compact-details">
      <summary>Source verification details · {scope?.status === 'SCOPE_CANDIDATE'
        ? 'Scope candidate' : scope?.status === 'BLOCKED'
          ? 'Needs attention' : 'Not assessed'}</summary>
      <VerificationScopeView scope={scope} error={error}/>
    </details>
  </div>;
}
