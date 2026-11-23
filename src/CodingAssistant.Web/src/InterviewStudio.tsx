import { useCallback, useEffect, useRef, useState } from 'react';
import { ProjectDraftPanel } from './ProjectDraftPanel';
import { InterviewAnswerComposer, type InterviewChoice } from './InterviewAnswerComposer';
import { ConversationWorkroom } from './ConversationWorkroom';
import { OwnerDecisionInbox } from './OwnerDecisionInbox';
import { TeamWorkroom } from './TeamWorkroom';
import { VerificationTimeline } from './VerificationTimeline';
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
const MODEL_DRIVEN_ANSWER_LIMIT = 12;
const MAX_OWNER_ANSWERS = MODEL_DRIVEN_ANSWER_LIMIT + 1;

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
      <p>Your final interview answer is saved. No more questions will be asked.
        Your next step is to {hasSpecification ? 'review the saved' : 'draft a'} specification.</p>
    </div>
    <button type="button" className="primary" onClick={onContinue}>
      {hasSpecification ? 'Review specification' : 'Continue to specification'} →
    </button>
  </div>;
}

/** Feedback stays beside the specification action, not in the hidden
 * Product Discovery panel. A failed request must never appear inert. */
export function SpecificationDraftFeedback({ error, drafting }: {
  error: string; drafting: boolean;
}) {
  return <>
    {error && <div className="alert" role="alert">{error}</div>}
    {drafting && <p className="hint" role="status">
      The local Product Owner is drafting your specification. The saved
      interview is unchanged until the complete draft is validated.
    </p>}
  </>;
}

export function InterviewStudio({ token, task, studioCanvas = false,
  focusRequest, onStageSelected }: {
  token: string;
  task: TaskItem;
  studioCanvas?: boolean;
  focusRequest?: { stage: JourneyId; revision: number } | null;
  onStageSelected?: (stage: JourneyId) => void;
}) {
  const [snapshot, setSnapshot] = useState<InterviewSnapshot | null>(null);
  const [chosenModel, setChosenModel] = useState('');
  // Explicit drafting choice; never silently switch the saved interview model.
  const [draftModel, setDraftModel] = useState('');
  const [installedModels, setInstalledModels] = useState<string[]>([]);
  const [modelsLoading, setModelsLoading] = useState(true);
  const [modelsError, setModelsError] = useState('');
  const [modelRefresh, setModelRefresh] = useState(0);
  const [busy, setBusy] = useState<InterviewBusy>('');
  const [error, setError] = useState('');
  const [approveChecked, setApproveChecked] = useState(false);
  const [elapsed, setElapsed] = useState(0);
  const [expanded, setExpanded] = useState<JourneyId | null>('discovery');
  const [showAllStages, setShowAllStages] = useState(false);
  const manuallySelected = useRef(false);
  const [architectureStep, setArchitectureStep] = useState<JourneyStep>(journeySteps[2]);
  const [planStep, setPlanStep] = useState<JourneyStep>(journeySteps[3]);
  const [generationStep, setGenerationStep] = useState<JourneyStep>(journeySteps[4]);
  const [deliveryStep, setDeliveryStep] = useState<JourneyStep>(journeySteps[5]);

  const openStage = useCallback((id: JourneyId) => {
    manuallySelected.current = true;
    setExpanded(current => showAllStages ? toggleJourneyStage(current, id) : id);
  }, [showAllStages]);
  const navigateToStage = useCallback((id: JourneyId) => {
    // Explicit stage navigation never changes approvals or workflow state.
    manuallySelected.current = true;
    setExpanded(id);
    // The focused studio owns its own viewport; never move the entire page.
    if (studioCanvas) return;
    window.requestAnimationFrame(() => {
      const reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches;
      document.getElementById('journey-panel-' + id)?.scrollIntoView({
        block: 'start', behavior: reducedMotion ? 'auto' : 'smooth',
      });
    });
  }, [studioCanvas]);
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

  useEffect(() => {
    let cancelled = false;
    setSnapshot(null);
    setError('');
    setApproveChecked(false);
    setChosenModel('');
    setDraftModel('');
    void api<InterviewSnapshot>(token, `/api/tasks/${task.id}/interview`)
      .then(data => {
        if (!cancelled) {
          setSnapshot(data);
          if (data.model) {
            setChosenModel(data.model);
            setDraftModel(data.model);
          }
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
        setDraftModel(previous => data.models.includes(previous) ? previous : '');
      })
      .catch(() => {
        if (cancelled) return;
        setInstalledModels([]);
        setChosenModel('');
        setDraftModel('');
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
            : code.includes('MODEL_THINKING_ONLY')
              ? 'The selected Ollama model returned internal reasoning but no finished specification. Nothing was saved. Choose a different installed instruct or coding model from the selector below.'
            : code.includes('MODEL_OUTPUT_TRUNCATED')
              ? 'Ollama stopped before completing the specification. Nothing was saved. Try again, or use a model with more available context.'
              : code.includes('MODEL_OUTPUT_REJECTED')
                ? 'Ollama returned no usable specification text, even after a recovery attempt. Nothing was saved. Retry or try another installed model.'
              : code.includes('INVALID_INPUT') && operation === 'draft'
                ? 'The saved interview could not be processed. Your answers remain saved. Please report this error.'
                : 'Could not complete this step: ' + code);
    } finally {
      setBusy('');
    }
  }

  const spec = snapshot?.specification;
  const ownerAnswers = snapshot?.messages.filter(item => item.role === 'owner').length ?? 0;
  const latestInterviewMessage = snapshot?.messages[snapshot.messages.length - 1];
  const hasPendingFinalQuestion = ownerAnswers === MODEL_DRIVEN_ANSWER_LIMIT &&
    latestInterviewMessage?.role === 'assistant' &&
    latestInterviewMessage.content.includes('?');
  const canAnswerInterview = ownerAnswers < MODEL_DRIVEN_ANSWER_LIMIT ||
    hasPendingFinalQuestion;
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

  useEffect(() => {
    if (!focusRequest) return;
    manuallySelected.current = true;
    setExpanded(focusRequest.stage);
  }, [focusRequest]);
  useEffect(() => {
    if (expanded) onStageSelected?.(expanded);
  }, [expanded, onStageSelected]);

  const currentStep = steps.find(step => step.id === expanded) ?? discoveryStep;
  return <div className={'journey-shell' +
    (showAllStages ? ' journey-shell--expanded' : ' journey-shell--focused') +
    (studioCanvas ? ' ca-studio-journey' : '')}>
    <section className={studioCanvas ? 'ca-studio-stagebar' : 'mc-work-focus'}
      aria-label="Focused project work">
      <div className="mc-work-focus-heading">
        <div><span className="wb-kicker">WORK / CURRENT STEP</span>
          <h2>{currentStep.title}</h2>
          <p>{currentStep.detail}</p></div>
        <span className={'journey-strip-current journey-' + currentStep.tone}>
          {currentStep.status}</span>
      </div>
      {studioCanvas ? <nav className="ca-studio-stage-tabs" aria-label="Project work stages">
        {steps.map((step, index) => <button type="button" key={step.id}
          className={'ca-studio-stage-tab' + (expanded === step.id ? ' is-selected' : '')}
          data-tone={step.tone}
          aria-current={expanded === step.id ? 'step' : undefined}
          onClick={() => navigateToStage(step.id)}>
          <span>{String(index + 1).padStart(2, '0')}</span>{step.title}
        </button>)}
      </nav> : <>
      <ol className="ca-work-track" aria-label="Build journey">
        {steps.map((step, index) => <li key={step.id}>
          <button type="button"
            className={'ca-wt ca-wt-' + step.tone + (expanded === step.id ? ' ca-wt-current' : '')}
            aria-current={expanded === step.id ? 'step' : undefined}
            onClick={() => navigateToStage(step.id)}>
            <span className="ca-wt-n">{String(index + 1).padStart(2, '0')}</span>
            <span className="ca-wt-nm">{step.title}</span>
            <span className="ca-wt-sp">{step.status}</span>
          </button>
        </li>)}
      </ol>
      <div className="mc-work-focus-actions">
        <label htmlFor="mc-work-stage">Choose a stage
          <select id="mc-work-stage" value={expanded ?? ''}
            onChange={event => {
              const next = event.target.value as JourneyId;
              if (next) navigateToStage(next);
            }}>
            {!expanded && <option value="">Choose a stage</option>}
            {steps.map((step, index) => <option key={step.id} value={step.id}>
              {index + 1}. {step.title} — {step.status}
            </option>)}
          </select>
        </label>
        <button type="button" className="outline"
          aria-pressed={showAllStages}
          onClick={() => setShowAllStages(value => !value)}>
          {showAllStages ? 'Focus on current step' : 'Show all stages'}
        </button>
      </div>
      <p className="hint">Only the selected step is shown. Use the stage selector to
        review earlier decisions or continue work; no approvals happen automatically.</p>
      </>}
    </section>
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
        {ownerAnswers > 0 && ownerAnswers < MAX_OWNER_ANSWERS && !spec &&
          (snapshot.readyForSpecification || ownerAnswers >= 5) &&
          <div className="interview-ready-action">
            <p className="hint">
              {ownerAnswers} answers saved. You can draft a first specification now;
              answering more questions is optional. You'll review it before approval.
            </p>
            <button type="button" className="outline"
              disabled={!!busy} onClick={continueToSpecification}>
              Draft first specification →
            </button>
          </div>}
        {hasPendingFinalQuestion && <p className="hint">
          This is the final Product Owner question. Your answer will be saved,
          and discovery will finish without asking another question.
        </p>}
        {canAnswerInterview && <InterviewAnswerComposer
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
        <SpecificationDraftFeedback error={error} drafting={busy === 'draft'} />
        <div className="spec-draft-model-controls">
          <label htmlFor="spec-draft-model">Local model for this specification
            <select id="spec-draft-model" value={draftModel}
              disabled={!!busy || modelsLoading}
              onChange={event => setDraftModel(event.target.value)}>
              <option value="">Select an installed model</option>
              {installedModels.map(name => <option key={name} value={name}>{name}</option>)}
            </select>
          </label>
          <button type="button" className="outline" disabled={!!busy || modelsLoading}
            onClick={() => setModelRefresh(value => value + 1)}>
            {modelsLoading ? 'Checking Ollama…' : 'Refresh models'}
          </button>
        </div>
        {modelsError && <p role="alert" className="hint">{modelsError}</p>}
        {draftModel === 'qwen3:30b' && <p className="hint" role="status">
          This Qwen 3 model can continue reasoning without producing a finished
          answer, even with thinking disabled. For more reliable specification
          drafting, choose an installed instruct or coding model above.
          Your recorded Product Owner interview will not change.
        </p>}
        <button type="button"
          disabled={!!busy || ownerAnswers < 1 || modelsLoading ||
            !installedModels.includes(draftModel)}
          onClick={() => void submit('draft', () => api<InterviewSnapshot>(
            token, `/api/tasks/${task.id}/interview/specification`, {
              expectedSequence: snapshot.lastSequence,
              model: draftModel,
            }))}>
          {busy === 'draft' ? 'Drafting specification…' :
            spec ? 'Revise specification from discussion' : 'Draft detailed specification'}
        </button>
        {spec && <div className="spec-review">
          <div className="actions">
            <strong>Specification v{spec.version}</strong>
            <span className="pill">{spec.status}</span>
            <span className="hint">Drafted with {spec.model}</span>
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

      : <div>
        {error && <div className="alert" role="alert">{error}</div>}
        {!error && <p className="hint">Loading the saved specification…</p>}
      </div>}
    </JourneyStage>

    <ArchitectureStudio token={token} taskId={task.id} spec={spec ?? null}
      expanded={expanded === 'architecture'} onExpand={openStage}
      onReady={architectureReady} onStageChange={reportArchitecture} />
    <TechnicalLeadStudio token={token} taskId={task.id} spec={spec ?? null}
      architectureSignal={architectureStep.status}
      expanded={expanded === 'plan'} onExpand={openStage}
      onReady={planReady} onStageChange={reportPlan} />
    <ProjectDraftPanel token={token} taskId={task.id} spec={spec ?? null}
      expanded={expanded} onExpand={openStage} studioCanvas={studioCanvas}
      onStageChange={reportDraft} />
    <details className="mc-work-secondary">
      <summary>Project progress & technical history</summary>
      <div className="mc-work-secondary-content">
        <JourneyStatusStrip title={task.title} steps={steps} activeId={expanded}
          onNavigate={navigateToStage} />
        <OwnerDecisionInbox token={token} taskId={task.id}
          refreshSignal={inboxSignal} onNavigate={navigateToStage} />
        <TeamWorkroom token={token} taskId={task.id} onNavigate={navigateToStage} />
        <VerificationTimeline token={token} taskId={task.id} />
      </div>
    </details>
  </div>;
}
