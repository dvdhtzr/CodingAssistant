import { useEffect, useRef, useState } from 'react';
import { EngineeringGenerationPanel, EngineeringDeliveryPanel, useEngineeringRun }
  from './EngineeringWorkspace';
import { api } from './api';
import { TaskBuildPanel } from './TaskBuildPanel';
import { JourneyStage, type JourneyId, type JourneyStep } from './ProjectJourney';

interface SpecContract {
  version: number;
  sha256: string;
  status: 'Draft' | 'Approved' | 'Superseded';
}
interface DraftJob {
  id: string;
  specificationVersion: number;
  specificationSha256: string;
  model: string;
  state: 'Running' | 'Generated' | 'Failed' | 'Interrupted';
  phase: string;
  errorCode: string | null;
  createdUtc: string;
  updatedUtc: string;
}
interface DraftEvent {
  sequence: number;
  kind: string;
  detail: string;
  createdUtc: string;
}
interface DraftFile {
  path: string;
  purpose: string;
  content: string;
  sha256: string;
  createdUtc: string;
}
interface DraftSnapshot {
  job: DraftJob;
  events: DraftEvent[];
  files: DraftFile[];
  architecture: { sha256: string; choices: {
    topic: string; label: string; selectedOptionId: string;
  }[] } | null;
}
interface ArchitectureReadiness {
  status: 'READY' | 'INCOMPLETE' | 'LEGACY_UNBOUND' | 'SPECIFICATION_NOT_APPROVED';
  readyForSourceGeneration: boolean;
  architectureSha256: string | null;
  blockingTopics: string[];
  approvedChoices: { topic: string; label: string; selectedOptionId: string }[];
}
interface DraftResponse {
  available: boolean;
  snapshot: DraftSnapshot | null;
}

export function ProjectDraftPanel({ token, taskId, spec, expanded,
  onExpand, onGenerated, onStageChange }: {
  token: string;
  taskId: string;
  spec: SpecContract | null;
  expanded: JourneyId | null;
  onExpand: (stage: JourneyId) => void;
  onGenerated: () => void;
  onStageChange: (generation: JourneyStep, delivery: JourneyStep) => void;
}) {
  const [result, setResult] = useState<DraftSnapshot | null>(null);
  const [architecture, setArchitecture] = useState<ArchitectureReadiness | null>(null);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [clock, setClock] = useState(Date.now());
  const engineer = useEngineeringRun(token, taskId);

  useEffect(() => {
    let closed = false;
    setResult(null);
    setArchitecture(null);
    setError('');
    async function sync() {
      try {
        const [response, readiness] = await Promise.all([
          api<DraftResponse>(token, `/api/tasks/${taskId}/draft-build`),
          api<ArchitectureReadiness>(token,
            `/api/tasks/${taskId}/draft-build/architecture`),
        ]);
        if (!closed) {
          setResult(response.snapshot);
          setArchitecture(readiness);
        }
      } catch {
        if (!closed) setError('Saved draft-build status could not be loaded.');
      }
    }
    void sync();
    const poll = window.setInterval(() => void sync(), 2500);
    return () => { closed = true; window.clearInterval(poll); };
  }, [token, taskId, spec?.sha256]);

  useEffect(() => {
    if (result?.job.state !== 'Running') return;
    const timer = window.setInterval(() => setClock(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [result?.job.state]);

  async function start() {
    if (busy || spec?.status !== 'Approved' ||
        !architecture?.readyForSourceGeneration) return;
    setBusy('start'); setError('');
    try {
      // Refresh the immutable binding immediately before admitting a draft.
      // The server rechecks it transactionally; stale snapshots fail closed.
      const current = await api<ArchitectureReadiness>(token,
        `/api/tasks/${taskId}/draft-build/architecture`);
      setArchitecture(current);
      if (!current.readyForSourceGeneration || !current.architectureSha256)
        throw new Error('Complete the Solution Architect approvals before generating source.');
      const started = await api<DraftJob>(token,
        `/api/tasks/${taskId}/draft-build`, {
          specificationVersion: spec.version,
          specificationSha256: spec.sha256,
          architectureSha256: current.architectureSha256,
        });
      setResult({
        job: started, files: [],
        architecture: {
          sha256: current.architectureSha256,
          choices: current.approvedChoices,
        },
        events: [{
          sequence: 1,
          kind: 'BuildAccepted',
          detail: 'Approved specification accepted; source-only build starting',
          createdUtc: started.createdUtc,
        }],
      });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to start draft generation');
    } finally { setBusy(''); }
  }

  async function cancel() {
    if (!result || result.job.state !== 'Running' || busy) return;
    setBusy('cancel'); setError('');
    try {
      const value = await api<DraftSnapshot>(token,
        `/api/tasks/${taskId}/draft-build/${result.job.id}/cancel`, {});
      setResult(value);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to cancel');
    } finally { setBusy(''); }
  }

  async function download() {
    if (!result || result.job.state !== 'Generated') return;
    setBusy('download'); setError('');
    try {
      const response = await fetch(
        `/api/tasks/${taskId}/draft-build/${result.job.id}/zip`, {
          cache: 'no-store', credentials: 'omit',
          referrerPolicy: 'no-referrer',
          headers: { Authorization: `Bearer ${token}` },
        });
      if (!response.ok)
        throw new Error('Project draft ZIP is unavailable (' + response.status + ').');
      const href = URL.createObjectURL(await response.blob());
      const anchor = document.createElement('a');
      anchor.href = href;
      anchor.download = 'codingassistant-project-draft.zip';
      anchor.click();
      window.setTimeout(() => URL.revokeObjectURL(href), 3000);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to download');
    } finally { setBusy(''); }
  }

  async function downloadAgentContract() {
    if (!result || result.job.state !== 'Generated' ||
        spec?.status !== 'Approved' || busy) return;
    setBusy('contract'); setError('');
    try {
      const response = await fetch(
        `/api/tasks/${taskId}/agent-contract-preview/json`, {
          cache: 'no-store', credentials: 'omit',
          referrerPolicy: 'no-referrer',
          headers: { Authorization: `Bearer ${token}` },
        });
      if (!response.ok)
        throw new Error('Task agent contract unavailable (' + response.status +
          '). The task model must be separately qualified for execution.');
      const href = URL.createObjectURL(await response.blob());
      const anchor = document.createElement('a');
      anchor.href = href;
      anchor.download = 'codingassistant-agent-contract.json';
      anchor.click();
      window.setTimeout(() => URL.revokeObjectURL(href), 3000);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message :
        'Unable to download approved task contract');
    } finally { setBusy(''); }
  }

  const job = result?.job;
  const running = job?.state === 'Running';
  const seconds = job
    ? Math.max(0, Math.floor((clock - Date.parse(job.createdUtc)) / 1000)) : 0;
  const currentSpec = !!spec && job?.specificationSha256 === spec.sha256;
  const currentArchitecture = !result?.architecture ||
    result.architecture.sha256 === architecture?.architectureSha256;
  const generated = job?.state === 'Generated' && !!result?.files.length;
  const engineerJob = engineer.snapshot?.job;
  const engineerGenerated = engineerJob?.state === 'Generated' &&
    !!engineer.snapshot?.files.length;
  const currentEngineeringPlan = engineer.plan?.current === true &&
    engineer.plan.status === 'Approved' &&
    engineer.plan.planSha256 === engineerJob?.planSha256;

  const generationStep: JourneyStep = {
    id: 'generation', title: 'Source Generation', detail: 'Create project source files',
    ...(engineerJob?.state === 'Running'
      ? { tone: 'active' as const, status: 'MAF Engineer implementing tasks' }
      : engineerGenerated && currentEngineeringPlan
        ? { tone: 'complete' as const, status: 'Engineering source — not tested' }
        : engineerGenerated
          ? { tone: 'attention' as const,
            status: 'Historical engineering source — plan changed' }
          : job?.state === 'Running'
      ? { tone: 'active' as const, status: 'Generating files locally' }
      : job?.state === 'Failed' || job?.state === 'Interrupted'
        ? { tone: 'blocked' as const, status: 'Generation ' + job.state.toLowerCase() }
        : generated && currentSpec && currentArchitecture
          ? { tone: 'complete' as const, status: 'Generated — not tested' }
          : job && !currentSpec
            ? { tone: 'attention' as const, status: 'Older specification — review' }
            : spec?.status !== 'Approved'
              ? { tone: 'pending' as const, status: 'Waiting for specification' }
              : architecture?.readyForSourceGeneration
                ? { tone: 'active' as const, status: 'Ready to generate' }
                : { tone: 'pending' as const, status: 'Waiting for architecture' }),
  };
  const deliveryStep: JourneyStep = {
    id: 'delivery', title: 'Artifacts & Delivery', detail: 'Inspect files and download ZIP',
    ...(engineerGenerated && currentEngineeringPlan
      ? { tone: 'complete' as const,
        status: engineer.snapshot?.files.length + ' engineering file(s) — unverified' }
      : engineerGenerated
        ? { tone: 'attention' as const,
          status: 'Historical engineering ZIP — changed plan' }
        : generated
      ? currentSpec && currentArchitecture
        ? { tone: 'complete' as const,
          status: result?.files.length + ' file(s) — unverified' }
        : { tone: 'attention' as const, status: 'Historical files — review bindings' }
      : (result?.files.length ?? 0) > 0
        ? { tone: 'blocked' as const, status: 'Partial files — not verified' }
        : { tone: 'pending' as const, status: 'No generated files yet' }),
  };
  useEffect(() => { onStageChange(generationStep, deliveryStep); },
    [onStageChange, generationStep.tone, generationStep.status,
      deliveryStep.tone, deliveryStep.status]);

  // Navigate forward only when an observed running job completes. Merely opening
  // a saved completed project must not silently interrupt the owner's navigation.
  const previousJobState = useRef<string | null>(null);
  const previousEngineerState = useRef<string | null>(null);
  useEffect(() => {
    if (previousJobState.current === 'Running' && job?.state === 'Generated')
      onGenerated();
    previousJobState.current = job?.state ?? null;
  }, [job?.state, onGenerated]);
  useEffect(() => {
    if (previousEngineerState.current === 'Running' &&
        engineerJob?.state === 'Generated')
      onGenerated();
    previousEngineerState.current = engineerJob?.state ?? null;
  }, [engineerJob?.state, onGenerated]);

  return <>
    <JourneyStage step={generationStep} index={4}
      open={expanded === 'generation'} onOpen={onExpand}>
      <section className="build-stage" aria-label="Task-specific project draft generation">
        <EngineeringGenerationPanel engine={engineer}
          legacyRunning={running} />
        <div className="wb-inspector-divider" />
        <h3>Classic source generator (fallback)</h3>
        <p className="hint">With an approved Product Owner specification and both
          Solution Architect decisions, your local model writes source files one by one.
          This does not execute, compile, test, or install anything. Files are unverified drafts.</p>
        <div className="actions">
          <button className="primary" type="button"
            disabled={!!busy || spec?.status !== 'Approved' ||
              !architecture?.readyForSourceGeneration || running ||
              engineerJob?.state === 'Running'}
            onClick={() => void start()}>
            {busy === 'start' ? 'Starting…' : 'Generate approved project draft'}
          </button>
          {running && <button type="button" className="danger"
            disabled={!!busy} onClick={() => void cancel()}>
            Stop generation
          </button>}
        </div>
        {spec?.status !== 'Approved' && <p className="hint">
          Approve a specification before generating project files.</p>}
        {spec?.status === 'Approved' && architecture &&
          !architecture.readyForSourceGeneration && <p className="activity-warning">
            Architecture approval required ({architecture.status}).
            {architecture.blockingTopics.length > 0 &&
              ' Review: ' + architecture.blockingTopics.join(', ') + '.'}
            An older draft, if present, remains inspectable in Artifacts & Delivery.
          </p>}
        {architecture?.readyForSourceGeneration && <p className="hint">
          Both architecture decisions approved. Binding SHA-256:
          <code className="architect-hash"> {architecture.architectureSha256}</code>
        </p>}
        {error && <div className="alert" role="alert">{error}</div>}
        {job && <div className="draft-status">
          <div className="activity-heading">
            {running && <span className="activity-dot" aria-hidden="true" />}
            <strong>{job.state === 'Generated' ? 'Files generated (NOT TESTED)' :
              job.state === 'Running' ? 'Generating source files' :
              'Generation stopped: ' + job.state}</strong>
            <span className="pill">{job.phase}</span>
          </div>
          {running && <>
            <div className="activity-track" aria-hidden="true">
              <div className="activity-scan" />
            </div>
            <p className="hint">Elapsed: {Math.floor(seconds / 60)}m {seconds % 60}s.
              The animation is not a model heartbeat or an invented percentage.</p>
          </>}
          {job.errorCode && <div className="activity-warning" role="alert">
            {['INVALID_PROJECT_PLAN', 'INVALID_PROJECT_PATH',
              'ARCHITECTURE_PLAN_CONFLICT'].includes(job.errorCode) ? <>
              <strong>Source file manifest was rejected.</strong>
              <p>{job.errorCode === 'ARCHITECTURE_PLAN_CONFLICT'
                ? 'The proposed files did not match your approved architecture.'
                : job.errorCode === 'INVALID_PROJECT_PATH'
                  ? 'The model proposed an unsafe or unsupported relative file path.'
                  : 'The model supplied an invalid JSON file list, missing or duplicate paths, or invalid file descriptions.'}
                {' '}The generator attempted one bounded correction; the result
                still did not pass independent validation. No validation rules
                or approvals were bypassed.</p>
              <p>{result?.files.length ? 'Partially saved source remains available under Artifacts & Delivery.' :
                'No project source files were saved.'}
                {' '}You may select <strong>Generate approved project draft</strong> again,
                without repeating the interview, architecture approvals, or
                Technical Lead review. This stage still generates unverified source.</p>
            </> : <>
              Safe failure category: {job.errorCode}. Any partial files remain
              available under Artifacts & Delivery; no complete or verified
              build is claimed.
            </>}
          </div>}
          <p className="hint">
            Specification v{job.specificationVersion} · model {job.model} ·
            {result?.files.length ?? 0} file(s) saved.
          </p>
          {result?.architecture && <>
            <p className="hint">Generation bound to owner-approved architecture:
              <code className="architect-hash"> {result.architecture.sha256}</code></p>
            <p className="hint">{result.architecture.choices.map(choice =>
              choice.topic + ': ' + choice.label).join(' · ')}</p>
            {!currentArchitecture && <p className="activity-warning">
              Architecture decisions have changed since this draft began.</p>}
          </>}
          {!result?.architecture && <p className="hint">
            Legacy specification-only draft (not bound to architecture decisions).</p>}
          <h3>Saved generation activity</h3>
          <ol className="events" aria-label="Saved generation activity">
            {(result?.events ?? []).map(event => <li key={event.sequence}>
              <span className="number">#{event.sequence}</span>
              <span><strong>{event.kind}</strong> · {event.detail}
                <small className="trace-note">
                  {new Date(event.createdUtc).toLocaleTimeString()}
                </small>
              </span>
            </li>)}
          </ol>
        </div>}
      </section>
    </JourneyStage>

    <JourneyStage step={deliveryStep} index={5}
      open={expanded === 'delivery'} onOpen={onExpand}>
      <section className="build-stage" aria-label="Project files and delivery">
        <EngineeringDeliveryPanel engine={engineer} />
        <p className="hint">Inspect saved source files and export them. Generated source
          is not an independently tested or verified build.</p>
        {error && <div className="alert" role="alert">{error}</div>}
        {job && <p className="hint">
          Source job: {job.state} · specification v{job.specificationVersion}.
          {!currentSpec && ' These files belong to an earlier specification.'}
          {!currentArchitecture && ' Architecture decisions have changed.'}
        </p>}
        <div className="actions">
          {job?.state === 'Generated' && <button type="button"
            disabled={!!busy} onClick={() => void download()}>
            {busy === 'download' ? 'Preparing ZIP…' : 'Download project ZIP'}
          </button>}
          {job?.state === 'Generated' && spec?.status === 'Approved' &&
            <button type="button" disabled={!!busy}
              onClick={() => void downloadAgentContract()}>
              {busy === 'contract' ? 'Preparing…' : 'Download agent pilot contract'}
            </button>}
        </div>
        {!job && <p className="hint">No generated files yet. Start with Source Generation.</p>}
        {!!result?.files.length && <>
          <h3>Generated source files (unverified)</h3>
          <div className="draft-files">
            {result.files.map(file => <details key={file.path}>
              <summary><strong>{file.path}</strong> · {file.content.length} characters</summary>
              <p className="hint">{file.purpose} · SHA-256 {file.sha256}</p>
              <pre className="specification-text">{file.content}</pre>
            </details>)}
          </div>
        </>}
        {job?.state === 'Generated' && spec?.status === 'Approved' &&
          currentSpec &&
          <TaskBuildPanel token={token} taskId={taskId}
            specificationSha256={spec.sha256} />}
        <p className="hint">Source-only generation and task-specific scope review
          are separate from independently verified coding. The restricted
          OpenCode pilot remains a separate qualification path, and security
          issue #76 must be cleared before executable coding can be enabled.</p>
      </section>
    </JourneyStage>
  </>;
}
