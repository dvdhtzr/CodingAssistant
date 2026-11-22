import { useEffect, useState } from 'react';
import { api } from './api';
import type { EngineerRun, EngineerFile } from './EngineeringWorkspace';

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
      : empty ? 'No saved Engineering run is available.'
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
  const [error, setError] = useState('');
  const sourceSignature = files.map(file => file.path + ':' + file.sha256)
    .sort().join('|');
  useEffect(() => {
    let closed = false;
    let pending = false;
    setScope(null);
    setError('');
    async function load() {
      if (pending) return;
      pending = true;
      try {
        const value = await api<VerificationScope>(token,
          '/api/tasks/' + run.taskId + '/engineering-runs/' +
          run.id + '/verification-scope');
        if (!closed) {
          if (!scopeMatchesRun(value, run, files))
            throw new Error('Scope binding mismatch');
          setScope(value);
          setError('');
        }
      } catch {
        if (!closed) {
          setScope(null);
          setError('No valid current source-scope response');
        }
      } finally { pending = false; }
    }
    void load();
    const interval = window.setInterval(() => void load(), 8000);
    return () => { closed = true; window.clearInterval(interval); };
  }, [token, run.id, run.taskId, run.planSha256, run.state, sourceSignature]);
  return <VerificationScopeView scope={scope} error={error}/>;
}
