import { useEffect, useState } from 'react';
import { api } from './api';
import { JourneyStage, type JourneyId, type JourneyStep } from './ProjectJourney';

interface ApprovedSpec {
  version: number;
  sha256: string;
  status: 'Draft' | 'Approved' | 'Superseded';
}
interface WorkItem {
  id: string;
  title: string;
  description: string;
  dependsOn: string[];
  acceptanceCriteria: string[];
}
interface Outline {
  summary: string;
  tasks: WorkItem[];
  testStrategy: string[];
  risks: string[];
}
interface Plan {
  taskId: string;
  revision: number;
  specificationVersion: number;
  specificationSha256: string;
  architectureSha256: string;
  model: string;
  outline: Outline;
  planSha256: string;
  status: 'Proposed' | 'Approved' | 'Superseded';
  current: boolean;
  effectiveStatus: string;
  createdUtc: string;
  approvedUtc: string | null;
}
interface PlanSnapshot {
  schemaVersion: number;
  plan: Plan | null;
  history: Plan[];
}
interface ArchitectureReadiness {
  status: string;
  readyForSourceGeneration: boolean;
  architectureSha256: string | null;
}

export function TechnicalLeadStudio({ token, taskId, spec, architectureSignal, expanded,
  onExpand, onReady, onStageChange }: {
  token: string;
  taskId: string;
  spec: ApprovedSpec | null;
  architectureSignal: string;
  expanded: boolean;
  onExpand: (stage: JourneyId) => void;
  onReady: () => void;
  onStageChange: (stage: JourneyStep) => void;
}) {
  const [saved, setSaved] = useState<PlanSnapshot | null>(null);
  const [architecture, setArchitecture] = useState<ArchitectureReadiness | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [approveChecked, setApproveChecked] = useState(false);

  useEffect(() => {
    let closed = false;
    setSaved(null);
    setArchitecture(null);
    setLoading(true);
    setError('');
    setApproveChecked(false);
    void Promise.all([
      api<PlanSnapshot>(token, '/api/tasks/' + taskId + '/implementation-plan'),
      api<ArchitectureReadiness>(token,
        '/api/tasks/' + taskId + '/draft-build/architecture'),
    ]).then(([plans, currentArchitecture]) => {
      if (!closed) {
        setSaved(plans);
        setArchitecture(currentArchitecture);
        setLoading(false);
      }
    }).catch(() => {
      if (!closed) {
        setError('Could not load saved Technical Lead planning history.');
        setLoading(false);
      }
    });
    return () => { closed = true; };
  }, [token, taskId, spec?.sha256, spec?.status, architectureSignal]);

  const plan = saved?.plan ?? null;
  const eligible = spec?.status === 'Approved' &&
    architecture?.readyForSourceGeneration === true;
  const planCurrent = !!plan?.current &&
    plan.specificationSha256 === spec?.sha256 &&
    plan.architectureSha256 === architecture?.architectureSha256;
  const step: JourneyStep = {
    id: 'plan', title: 'Implementation Plan',
    detail: 'Technical Lead · tasks and acceptance criteria',
    ...(loading ? { tone: 'pending', status: 'Loading saved plan' } :
      error ? { tone: 'blocked', status: 'Plan could not be loaded' } :
        !eligible ? { tone: 'pending', status: 'Waiting for approved architecture' } :
          plan && !planCurrent ? { tone: 'blocked', status: 'Previous plan is stale' } :
            plan?.status === 'Approved' ?
              { tone: 'complete', status: 'Owner approved · v' + plan.revision } :
              plan?.status === 'Proposed' ?
                { tone: 'attention', status: 'Review plan · v' + plan.revision } :
                busy ? { tone: 'active', status: 'Drafting implementation plan' } :
                  { tone: 'active', status: 'Ready for Technical Lead' }),
  } as JourneyStep;

  useEffect(() => { onStageChange(step); },
    [onStageChange, step.tone, step.status]);

  async function generate() {
    if (busy || !eligible || !spec || !architecture?.architectureSha256 ||
        !saved) return;
    setBusy('generate');
    setError('');
    setApproveChecked(false);
    try {
      // Read after the click; the server rechecks all hashes and revisions
      // in one transaction when saving the model output.
      const [latest, currentArchitecture] = await Promise.all([
        api<PlanSnapshot>(token, '/api/tasks/' + taskId + '/implementation-plan'),
        api<ArchitectureReadiness>(token,
          '/api/tasks/' + taskId + '/draft-build/architecture'),
      ]);
      setArchitecture(currentArchitecture);
      if (!currentArchitecture.readyForSourceGeneration ||
          !currentArchitecture.architectureSha256)
        throw new Error('Approve both Solution Architect decisions first.');
      const created = await api<Plan>(token,
        '/api/tasks/' + taskId + '/implementation-plan/generate', {
          schemaVersion: 1,
          specificationVersion: spec.version,
          specificationSha256: spec.sha256,
          architectureSha256: currentArchitecture.architectureSha256,
          expectedRevision: latest.plan?.revision ?? 0,
        });
      setSaved({ schemaVersion: 1, plan: created,
        history: [...latest.history, created] });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to generate plan');
    } finally { setBusy(''); }
  }

  async function approve() {
    if (busy || !approveChecked || !plan || !planCurrent ||
        plan.status !== 'Proposed') return;
    setBusy('approve');
    setError('');
    try {
      const approved = await api<Plan>(token,
        '/api/tasks/' + taskId + '/implementation-plan/approve', {
          schemaVersion: 1, expectedRevision: plan.revision,
          planSha256: plan.planSha256,
          confirmation: 'APPROVE ' + plan.planSha256,
        });
      setSaved(previous => previous ? {
        ...previous, plan: approved,
        history: previous.history.map(p => p.revision === approved.revision ?
          approved : p),
      } : previous);
      setApproveChecked(false);
      onReady();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to approve plan');
    } finally { setBusy(''); }
  }

  return <JourneyStage step={step} index={3}
    open={expanded} onOpen={onExpand}>
    <section className="technical-lead-stage" aria-label="Technical Lead implementation planning">
      <p className="hint">The MAF-backed Technical Lead converts the <strong>approved
        specification and architecture</strong> into ordered engineering tasks,
        dependencies, and testable acceptance criteria. Its plan is a proposal
        until you explicitly approve it. It does not generate or execute code.</p>
      {!eligible && <p className="hint">Approve the Product Owner specification and
        both Solution Architect decisions to activate this stage.</p>}
      {error && <div className="alert" role="alert">{error}
        <button type="button" className="outline" onClick={() => {
          setError('');
          setLoading(true);
          void Promise.all([
            api<PlanSnapshot>(token, '/api/tasks/' + taskId + '/implementation-plan'),
            api<ArchitectureReadiness>(token,
              '/api/tasks/' + taskId + '/draft-build/architecture'),
          ]).then(([plans, arch]) => {
            setSaved(plans);
            setArchitecture(arch);
            setLoading(false);
          }).catch(() => {
            setError('Could not reload the planning history.');
            setLoading(false);
          });
        }}>Reload</button></div>}
      {loading && <p className="hint" role="status">Loading saved planning history…</p>}
      <div className="actions">
        <button type="button" className="primary"
          disabled={!eligible || !!busy || loading || !saved}
          onClick={() => void generate()}>
          {busy === 'generate' ? 'Technical Lead drafting…' :
            plan ? 'Propose revised implementation plan' :
              'Ask Technical Lead to plan the build'}
        </button>
      </div>
      {busy === 'generate' && <p role="status" className="activity">
        The selected local model is drafting. No percentage or completion
        claim is available until the plan is saved.</p>}
      {plan && <>
        <div className="technical-plan-heading">
          <strong>Implementation plan v{plan.revision}</strong>
          <span className="pill">{planCurrent ? plan.status : 'STALE'}</span>
        </div>
        {!planCurrent && <p className="activity-warning">
          The approved specification or architecture changed. This historical
          plan cannot be approved. Generate a new version after reviewing the changes.
        </p>}
        <p className="hint">Model: {plan.model} · SHA-256:
          <code className="architect-hash"> {plan.planSha256}</code></p>
        <p>{plan.outline.summary}</p>
        <h3>Ordered engineering tasks</h3>
        <ol className="technical-plan-tasks">
          {plan.outline.tasks.map(item => <li key={item.id}>
            <strong>{item.title}</strong> <code>{item.id}</code>
            <p>{item.description}</p>
            {!!item.dependsOn.length && <p className="hint">Requires:
              {' ' + item.dependsOn.join(', ')}</p>}
            <strong className="technical-plan-subtitle">Acceptance criteria</strong>
            <ul>{item.acceptanceCriteria.map((criterion, i) =>
              <li key={i}>{criterion}</li>)}</ul>
          </li>)}
        </ol>
        <h3>Independent test strategy</h3>
        <ul>{plan.outline.testStrategy.map((item, i) =>
          <li key={i}>{item}</li>)}</ul>
        {!!plan.outline.risks.length && <>
          <h3>Risks and limitations</h3>
          <ul>{plan.outline.risks.map((item, i) =>
            <li key={i}>{item}</li>)}</ul>
        </>}
        {plan.status === 'Proposed' && planCurrent && <>
          <label className="spec-consent">
            <input type="checkbox" checked={approveChecked}
              onChange={event => setApproveChecked(event.target.checked)} />
            I have reviewed these tasks and acceptance criteria and approve
            this exact implementation plan.
          </label>
          <div className="actions">
            <button type="button" className="primary"
              disabled={!approveChecked || !!busy}
              onClick={() => void approve()}>
              {busy === 'approve' ? 'Saving approval…' : 'Approve implementation plan'}
            </button>
          </div>
        </>}
        {plan.status === 'Approved' && planCurrent && <p className="success">
          Approved. This plan is saved for the future Software Engineer.
          Source generation is still a separate source-only workflow and does
          not yet consume this task breakdown.
        </p>}
      </>}
      {!!saved?.history.length && <details>
        <summary>Saved plan revisions ({saved.history.length})</summary>
        <ol>{saved.history.map(item => <li key={item.revision}>
          v{item.revision} · {item.status} ·
          {item.current ? ' Current approvals' : ' Historical approvals'} ·
          {item.outline.tasks.length} tasks
        </li>)}</ol>
      </details>}
    </section>
  </JourneyStage>;
}
