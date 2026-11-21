import type { ReactNode } from 'react';

export type JourneyId =
  'discovery' | 'specification' | 'architecture' | 'plan' | 'generation' | 'delivery';
export type JourneyTone = 'complete' | 'active' | 'attention' | 'pending' | 'blocked';

export interface JourneyStep {
  id: JourneyId;
  title: string;
  detail: string;
  status: string;
  tone: JourneyTone;
}

export const journeySteps: readonly JourneyStep[] = [
  { id: 'discovery', title: 'Product Discovery', detail: 'Product Owner interview',
    status: 'Not started', tone: 'pending' },
  { id: 'specification', title: 'Specification', detail: 'Review requirements',
    status: 'Waiting for interview', tone: 'pending' },
  { id: 'architecture', title: 'Solution Architecture', detail: 'Choose stack and approach',
    status: 'Waiting for specification', tone: 'pending' },
  { id: 'plan', title: 'Implementation Plan', detail: 'Technical Lead · plan and review',
    status: 'Waiting for architecture', tone: 'pending' },
  { id: 'generation', title: 'Source Generation', detail: 'Create project files',
    status: 'Waiting for architecture', tone: 'pending' },
  { id: 'delivery', title: 'Artifacts & Delivery', detail: 'Inspect and download',
    status: 'No files yet', tone: 'pending' },
];

/**
 * Restore the most useful saved stage. Never use this to override a manually
 * selected header or a currently focused form.
 */
export function restoreJourneyStage(specStatus: string | null,
  architecture: JourneyTone, plan: JourneyTone, generation: JourneyTone,
  delivery: JourneyTone): JourneyId {
  if (delivery === 'complete') return 'delivery';
  if (generation === 'complete') return 'generation';
  if (architecture === 'complete')
    return plan === 'complete' ? 'generation' : 'plan';
  if (specStatus === 'Approved') return 'architecture';
  if (specStatus === 'Draft') return 'specification';
  return 'discovery';
}

/** Selecting the open header collapses all panels; selecting another opens only it. */
export function toggleJourneyStage(current: JourneyId | null,
  clicked: JourneyId): JourneyId | null {
  return current === clicked ? null : clicked;
}

export function JourneyStage({ step, index, open, onOpen, children }: {
  step: JourneyStep;
  index: number;
  open: boolean;
  onOpen: (id: JourneyId) => void;
  children: ReactNode;
}) {
  const labelId = 'journey-label-' + step.id;
  const panelId = 'journey-panel-' + step.id;
  const symbols: Record<JourneyTone, string> = {
    complete: '✓', active: '●', attention: '!', pending: '○', blocked: '×',
  };
  return <section className={'journey-stage journey-' + step.tone +
    (open ? ' journey-open' : '')} aria-label={step.title}>
    <h3 className="journey-heading">
      <button type="button" className="journey-trigger" id={labelId}
        aria-expanded={open} aria-controls={panelId}
        onClick={() => onOpen(step.id)}>
        <span className="journey-index">{String(index + 1).padStart(2, '0')}</span>
        <span className="journey-heading-copy">
          <strong>{step.title}</strong>
          <small>{step.detail}</small>
        </span>
        <span className="journey-heading-state">
          <span className="journey-state-symbol" aria-hidden="true">{symbols[step.tone]}</span>
          <span>{step.status}</span>
        </span>
        <span className="journey-chevron" aria-hidden="true">{open ? '⌃' : '⌄'}</span>
      </button>
    </h3>
    <div className="journey-stage-body" id={panelId} role="region"
      aria-labelledby={labelId} hidden={!open}>{children}</div>
  </section>;
}

export function JourneyStatusStrip({ title, steps, activeId, onNavigate }: {
  title: string;
  steps: readonly JourneyStep[];
  activeId: JourneyId | null;
  onNavigate?: (id: JourneyId) => void;
}) {
  const active = steps.find(step => step.id === activeId);
  const done = steps.filter(step => step.tone === 'complete').length;
  const attention = steps.filter(step => step.tone === 'attention' ||
    step.tone === 'blocked').length;
  return <div className="journey-status-strip" aria-label="Project journey progress">
    <div className="journey-strip-top">
      <div>
        <span className="wb-kicker">PROJECT JOURNEY</span>
        <h2>{title}</h2>
        <p>{active ? 'Stage ' + (steps.findIndex(step => step.id === activeId) + 1) +
          ' · ' + active.title : 'All stages collapsed · Select a stage to continue'}</p>
      </div>
      <span className={'journey-strip-current journey-' + (active?.tone ?? 'pending')}>
        {active?.status ?? 'No stage open'}
      </span>
    </div>
    <div className="journey-progress-track" aria-label={done + ' of ' + steps.length +
      ' stages completed'}>
      {steps.map(step => <span key={step.id} className={'journey-progress-segment journey-' +
        step.tone} title={step.title + ': ' + step.status} />)}
    </div>
    {onNavigate && <nav className="journey-quick-nav"
      aria-label="Jump to a project stage">
      {steps.map((step, index) => <button type="button" key={step.id}
        className={'journey-quick-step journey-' + step.tone +
          (activeId === step.id ? ' journey-quick-current' : '')}
        aria-label={'Open ' + step.title + ': ' + step.status}
        aria-current={activeId === step.id ? 'step' : undefined}
        aria-controls={'journey-panel-' + step.id}
        onClick={() => onNavigate(step.id)}>
        <span className="journey-quick-index">{String(index + 1).padStart(2, '0')}</span>
        <span className="journey-quick-name">{step.title}</span>
        <span className="journey-quick-tone" aria-hidden="true" />
      </button>)}
    </nav>}
    <p className="journey-strip-summary">
      {done} of {steps.length} stages completed
      {attention ? ' · ' + attention + ' requiring review' : ''}
      {' · Statuses reflect saved work, not estimated model progress'}
    </p>
  </div>;
}
