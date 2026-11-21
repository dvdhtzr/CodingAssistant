import { useCallback, useEffect, useRef, useState } from 'react';
import { ProjectDraftPanel } from './ProjectDraftPanel';
import { InterviewAnswerComposer, type InterviewChoice } from './InterviewAnswerComposer';
import { ConversationWorkroom } from './ConversationWorkroom';
import { OwnerDecisionInbox } from './OwnerDecisionInbox';
import { ArchitectureStudio } from './ArchitectureStudio';
import { TechnicalLeadStudio } from './TechnicalLeadStudio';
import { api, type TaskItem } from './api';
import { JourneyStage, JourneyStatusStrip, journeySteps, restoreJourneyStage,
  toggleJourneyStage, type JourneyId, type JourneyStep } from './ProjectJourney';

interface InterviewMessage {
  sequence: number;
  role: 'owner' | 'assistant';
  content: string;
  createdUtc: string;
  choice?: InterviewChoice | null;
}
interface InterviewSpecification {
  version: number;
  model: string;
  content: string;
  sha256: string;
  status: 'Draft' | 'Approved' | 'Superseded';
  createdUtc: string;
  approvedUtc: string | null;
}
interface InterviewSnapshot {
  taskId: string;
  model: string | null;
  lastSequence: number;
  readyForSpecification: boolean;
  messages: InterviewMessage[];
  specification: InterviewSpecification | null;
}
type InterviewBusy = 'start' | 'answer' | 'draft' | 'approve' | '';
const MAX_OWNER_ANSWERS = 12;

/** A visible route forward when the backend's interview limit has been reached. */
export function InterviewCompletionNotice({ ownerAnswers, hasSpecification, onContinue }: {
  ownerAnswers: number;
  hasSpecification: boolean;
  onContinue: () => void;
}) {
  if (ownerAnswers < MAX_OWNER_ANSWERS) return null;
  return <div className="interview-completion-notice" role="status">
    <div>
      <strong>Product discovery is ready for your review</strong>
      <p>All {MAX_OWNER_ANSWERS} interview answers are saved.
        The interview has reached its question limit, so there is no more answer form.
        Your next step is to {hasSpecification ? 'review the saved' : 'draft a'} specification.</p>
    </div>
    <button type="button" className="primary" onClick={onContinue}>
      {hasSpecification ? 'Review specification' : 'Continue to specification'} →
    </button>
  </div>;
}

export function InterviewStudio({ token, task }: {
  token: string;
  task: TaskItem;
}) {
  const [snapshot, setSnapshot] = useState<InterviewSnapshot | null>(null);
  const [chosenModel, setChosenModel] = useState('');
  const [installedModels, setInstalledModels] = useState<string[]>([]);
  const [modelsLoading, setModelsLoading] = useState(true);
  const [modelsError, setModelsError] = useState('');
  const [modelRefresh, setModelRefresh] = useState(0);
  const [busy, setBusy] = useState<InterviewBusy>('');
  const [error, setError] = useState('');
  const [approveChecked, setApproveChecked] = useState(false);
  const [elapsed, setElapsed] = useState(0);
  const [expanded, setExpanded] = useState<JourneyId | null>('discovery');
  const manuallySelected = useRef(false);
  const [architectureStep, setArchitectureStep] = useState<JourneyStep>(journeySteps[2]);
  const [planStep, setPlanStep] = useState<JourneyStep>(journeySteps[3]);
  const [generationStep, setGenerationStep] = useState<JourneyStep>(journeySteps[4]);
  const [deliveryStep, setDeliveryStep] = useState<JourneyStep>(journeySteps[5]);

  const openStage = useCallback((id: JourneyId) => {
    manuallySelected.current = true;
    setExpanded(current => toggleJourneyStage(current, id));
  }, []);
  const navigateToStage = useCallback((id: JourneyId) => {
    // Direct navigation opens the requested panel; clicking its progress tile
    // again must not collapse it. Honor deliberate navigation over polling.
    manuallySelected.current = true;
    setExpanded(id);
    window.requestAnimationFrame(() => {
      const reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches;
      document.getElementById('journey-label-' + id)
        ?.scrollIntoView({ block: 'start', behavior: reducedMotion ? 'auto' : 'smooth' });
    });
  }, []);
  const reportArchitecture = useCallback((step: JourneyStep) => {
    setArchitectureStep(previous => previous.tone === step.tone &&
      previous.status === step.status ? previous : step);
  }, []);
  const reportPlan = useCallback((plan: JourneyStep) => {
    setPlanStep(previous => previous.tone === plan.tone &&
      previous.status === plan.status ? previous : plan);
  }, []);
  const reportDraft = useCallback((generation: JourneyStep, delivery: JourneyStep) => {
    setGenerationStep(previous => previous.tone === generation.tone &&
      previous.status === generation.status ? previous : generation);
    setDeliveryStep(previous => previous.tone === delivery.tone &&
      previous.status === delivery.status ? previous : delivery);
  }, []);
  const architectureReady = useCallback(() => {
    setExpanded(current => current === 'architecture' ? 'plan' : current);
  }, []);
  const planReady = useCallback(() => {
    setExpanded(current => current === 'plan' ? 'generation' : current);
  }, []);
  const generationComplete = useCallback(() => {
    setExpanded(current => current === 'generation' ? 'delivery' : current);
  }, []);

  useEffect(() => {
    let cancelled = false;
    setSnapshot(null);
    setError('');
    setApproveChecked(false);
    setChosenModel('');
    void api<InterviewSnapshot>(token, `/api/tasks/${task.id}/interview`)
      .then(data => {
        if (!cancelled) {
          setSnapshot(data);
          if (data.model) setChosenModel(data.model);
        }
      })
      .catch(() => { if (!cancelled) setError('Unable to load the saved Product Owner conversation.'); });
    return () => { cancelled = true; };
  }, [token, task.id]);

  // Only the authenticated API speaks to the fixed local Ollama port.
  // Never silently substitute another model when the selection disappears.
  useEffect(() => {
    let cancelled = false;
    setModelsLoading(true);
    setModelsError('');
    void api<{ models: string[] }>(token, '/api/models')
      .then(data => {
        if (cancelled) return;
        setInstalledModels(data.models);
        setChosenModel(previous => data.models.includes(previous) ? previous : '');
      })
      .catch(() => {
        if (cancelled) return;
        setInstalledModels([]);
        setChosenModel('');
        setModelsError('Could not query local Ollama. Confirm it is running, then refresh.');
      })
      .finally(() => { if (!cancelled) setModelsLoading(false); });
    return () => { cancelled = true; };
  }, [token, task.id, modelRefresh]);

  const continueToSpecification = useCallback(() => {
    navigateToStage('specification');
  }, [navigateToStage]);

  useEffect(() => {
    if (!busy) return;
    const start = Date.now();
    setElapsed(0);
    const timer = window.setInterval(() => {
      setElapsed(Math.floor((Date.now() - start) / 1000));
    }, 1000);
    return () => window.clearInterval(timer);
  }, [busy]);

  async function submit(
    operation: InterviewBusy,
    work: () => Promise<InterviewSnapshot>,
  ) {
    if (busy) return;
    setBusy(operation);
    setError('');
    try {
      const updated = await work();
      setSnapshot(updated);
      setApproveChecked(false);
      if (operation === 'approve') {
        manuallySelected.current = true;
        // Honor a deliberate collapse while the approval request was in flight.
        setExpanded(current => current === 'specification' ? 'architecture' : current);
      }
    } catch (cause) {
      const code = cause instanceof Error ? cause.message : 'Unknown error';
      setError(code.includes('STALE_INTERVIEW')
        ? 'Your Product Owner conversation changed elsewhere. Reload the page to review it before continuing.'
        : code.includes('MODEL_NOT_INSTALLED')
          ? 'That local model is not installed. Select an installed Ollama model.'
          : code.includes('LOCAL_MODEL_UNAVAILABLE')
            ? 'The local model is unavailable or timed out. Your saved answers are unchanged; retry.'
            : code.includes('MODEL_OUTPUT_REJECTED')
              ? 'The local model returned a response that was empty or could not be validated, even after recovery. No malformed AI response was saved. Your typed answer is still here; please retry.'
              : 'Could not complete this step: ' + code);
    } finally {
      setBusy('');
    }
  }

  const spec = snapshot?.specification;
  const ownerAnswers = snapshot?.messages.filter(item => item.role === 'owner').length ?? 0;
  const discoveryReady = !!spec || !!snapshot?.readyForSpecification ||
    ownerAnswers >= MAX_OWNER_ANSWERS;
  const discoveryStep: JourneyStep = { ...journeySteps[0],
    tone: discoveryReady ? 'complete' : snapshot?.lastSequence ? 'active' : 'pending',
    status: spec ? ownerAnswers + ' answer(s) · interview captured' :
      discoveryReady ? ownerAnswers + ' answer(s) · ready to draft' :
        snapshot?.lastSequence ? ownerAnswers + ' answer(s) saved' : 'Not started',
  };
  const specificationStep: JourneyStep = { ...journeySteps[1],
    tone: spec?.status === 'Approved' ? 'complete' :
      spec?.status === 'Superseded' ? 'blocked' :
        spec?.status === 'Draft' ? 'attention' :
          ownerAnswers ? 'active' : 'pending',
    status: spec?.status === 'Approved' ? 'Approved · v' + spec.version :
      spec?.status === 'Superseded' ? 'Requirements changed' :
        spec?.status === 'Draft' ? 'Approval required · v' + spec.version :
          ownerAnswers ? 'Ready to draft' : 'Waiting for interview',
  };
  const steps = [discoveryStep, specificationStep, architectureStep,
    planStep, generationStep, deliveryStep];
  const inboxSignal = [
    snapshot?.lastSequence ?? 0, spec?.sha256 ?? '', spec?.status ?? '',
    architectureStep.status, planStep.status,
  ].join('|');

  // Saved-project restore opens the next useful stage; a human-selected stage
  // takes precedence. Only confirmed approvals and a completed running job
  // can move the user forward once they have interacted.
  useEffect(() => {
    if (!snapshot || manuallySelected.current) return;
    setExpanded(restoreJourneyStage(spec?.status ?? null,
      architectureStep.tone, planStep.tone, generationStep.tone,
      deliveryStep.tone));
  }, [snapshot, spec?.status, architectureStep.tone, planStep.tone,
    generationStep.tone, deliveryStep.tone]);

  return <div className="journey-shell">
    <JourneyStatusStrip title={task.title} steps={steps} activeId={expanded}
      onNavigate={navigateToStage} />
    <OwnerDecisionInbox token={token} taskId={task.id}
      refreshSignal={inboxSignal} onNavigate={navigateToStage} />
    {!!busy && <div className="activity" role="status" aria-live="polite">
      <div className="activity-heading">
        <span className="activity-dot" aria-hidden="true" />
        <strong>{busy === 'draft' ? 'Drafting your specification' :
          busy === 'approve' ? 'Recording specification approval' :
          'Waiting for the local model'}</strong>
        <span className="pill">{elapsed}s</span>
      </div>
      <div className="activity-track" aria-hidden="true"><div className="activity-scan" /></div>
      <p className="hint">The timer is not confirmed model progress.
        The API saves only a complete, validated result.</p>
    </div>}
    <JourneyStage step={discoveryStep} index={0}
      open={expanded === 'discovery'} onOpen={openStage}>
    <section className="interview-studio">
    <div className="interview-title">
      <div>
        <span className="pill">Product Owner · requirements discovery</span>
        <h2>1. Discuss your project with the Product Owner</h2>
        <details className="interview-objective">
          <summary>Project objective <span>View original brief</span></summary>
          <p className="hint">{task.objective}</p>
        </details>
      </div>
      <span className="pill">{snapshot?.lastSequence ? 'Conversation saved' : 'Not started'}</span>
    </div>

    {!snapshot && !error && <p className="hint">Loading your saved conversation…</p>}
    {error && <div className="alert" role="alert">{error}</div>}

    {snapshot && <>
      {!snapshot.lastSequence && <div className="interview-start">
        <p>Your Product Owner will ask relevant questions about <em>your</em> project.
          Questions adapt to your answers, without a rigid questionnaire or demo task.</p>
        <label>Installed local Ollama model
          <select value={chosenModel}
            disabled={!!busy || modelsLoading || installedModels.length === 0}
            onChange={event => setChosenModel(event.target.value)}>
            <option value="">Select a downloaded model</option>
            {installedModels.map(model => <option key={model} value={model}>{model}</option>)}
          </select>
        </label>
        <button type="button" className="outline" disabled={modelsLoading || !!busy}
          onClick={() => setModelRefresh(previous => previous + 1)}>
          {modelsLoading ? 'Checking Ollama…' : 'Refresh installed models'}
        </button>
        {modelsError && <p className="hint" role="alert">{modelsError}</p>}
        {!modelsLoading && !modelsError && installedModels.length === 0 &&
          <p className="hint">No downloaded local models found. Install a chat-capable model
            in Ollama and refresh this list.</p>}
        <p className="hint">Only downloaded local models are shown. Choose a chat-capable
          model; embedding-only models cannot conduct interviews.</p>
        <button className="primary"
          disabled={!!busy || modelsLoading || !chosenModel || !installedModels.includes(chosenModel)}
          onClick={() => void submit('start', () =>
            api<InterviewSnapshot>(token, `/api/tasks/${task.id}/interview/turn`, {
              expectedSequence: 0, model: chosenModel,
            }))}>
          Begin Product Owner conversation
        </button>
      </div>}

      {!!snapshot.lastSequence && <>
        <ConversationWorkroom messages={snapshot.messages} />
        <p className="hint">Model: {snapshot.model} · {ownerAnswers} answer(s) recorded.
          {snapshot.readyForSpecification
            ? ' The Product Owner believes there is enough information to draft a specification.'
            : ' Continue discussing requirements, or draft a first specification when you are ready.'}
        </p>
        {ownerAnswers < MAX_OWNER_ANSWERS && <InterviewAnswerComposer
          key={snapshot.lastSequence}
          choice={snapshot.messages[snapshot.messages.length - 1]?.choice ?? null}
          busy={!!busy}
          onAnswer={message => void submit('answer', () => api<InterviewSnapshot>(
            token, `/api/tasks/${task.id}/interview/turn`, {
              expectedSequence: snapshot.lastSequence,
              message,
              model: snapshot.model,
            }))} />}
        {ownerAnswers >= MAX_OWNER_ANSWERS &&
          <InterviewCompletionNotice ownerAnswers={ownerAnswers}
            hasSpecification={!!spec} onContinue={continueToSpecification} />}
        {ownerAnswers > 0 && ownerAnswers < MAX_OWNER_ANSWERS &&
          snapshot.readyForSpecification && !spec &&
          <button type="button" className="outline interview-ready-action"
            disabled={!!busy} onClick={continueToSpecification}>
            Ready to draft? Open Specification →
          </button>}
      </>}

    </>}
    </section>
    </JourneyStage>

    <JourneyStage step={specificationStep} index={1}
      open={expanded === 'specification'} onOpen={openStage}>
      {snapshot ?       <div className="specification-stage">
        <h2>2. Review and approve the specification</h2>
        <p className="hint">The Product Owner's specification will reflect this conversation, not a hard-coded
          application template. You control when to draft and approve it.</p>
        <button type="button" disabled={!!busy || ownerAnswers < 1}
          onClick={() => void submit('draft', () => api<InterviewSnapshot>(
            token, `/api/tasks/${task.id}/interview/specification`, {
              expectedSequence: snapshot.lastSequence,
            }))}>
          {spec ? 'Revise specification from discussion' : 'Draft detailed specification'}
        </button>
        {spec && <div className="spec-review">
          <div className="actions">
            <strong>Specification v{spec.version}</strong>
            <span className="pill">{spec.status}</span>
          </div>
          <p className="hint">Immutable SHA-256: <code>{spec.sha256}</code></p>
          <pre className="specification-text">{spec.content}</pre>
          {spec.status === 'Draft' && <>
            <label className="spec-consent">
              <input type="checkbox" checked={approveChecked}
                onChange={event => setApproveChecked(event.target.checked)} />
              I've reviewed this specification and approve it as the intended build contract.
            </label>
            <button className="primary"
              disabled={!!busy || !approveChecked}
              onClick={() => void submit('approve', () => api<InterviewSnapshot>(
                token, `/api/tasks/${task.id}/interview/approve`, {
                  version: spec.version, sha256: spec.sha256,
                }))}>
              Approve this specification
            </button>
          </>}
          {spec.status === 'Approved' && <p className="success">
            ✓ Specification approved and saved. This does not start coding.
          </p>}
          {spec.status === 'Superseded' && <p className="hint">
            The requirements changed. Draft a new specification before approval.</p>}
        </div>}
      </div>

      : <p className="hint">Loading the saved specification…</p>}
    </JourneyStage>

    <ArchitectureStudio token={token} taskId={task.id} spec={spec ?? null}
      expanded={expanded === 'architecture'} onExpand={openStage}
      onReady={architectureReady} onStageChange={reportArchitecture} />
    <TechnicalLeadStudio token={token} taskId={task.id} spec={spec ?? null}
      architectureSignal={architectureStep.status}
      expanded={expanded === 'plan'} onExpand={openStage}
      onReady={planReady} onStageChange={reportPlan} />
    <ProjectDraftPanel token={token} taskId={task.id} spec={spec ?? null}
      expanded={expanded} onExpand={openStage} onGenerated={generationComplete}
      onStageChange={reportDraft} />
  </div>;
}
