import { useEffect, useRef } from 'react';
import type { AgentActivity, ActivityEntry, CommandEvidence } from './CommandActivity';
import type { QueueDecision } from './DecisionQueue';

export type DrawerSelection =
  | { kind: 'agent'; id: string }
  | { kind: 'event'; id: string }
  | { kind: 'decision'; id: string };

export function CommandDrawer({ projectTitle, selection, agents, events, decisions, evidence,
  onClose, onNavigate }: {
  projectTitle: string;
  selection: DrawerSelection;
  agents: readonly AgentActivity[];
  events: readonly ActivityEntry[];
  decisions?: readonly QueueDecision[];
  evidence: CommandEvidence;
  onClose: () => void;
  onNavigate: (destination: 'team' | 'work' | 'quality' | 'history') => void;
}) {
  const panelRef = useRef<HTMLElement>(null);
  const headingId = 'command-drawer-heading';
  const selectedAgent = selection.kind === 'agent'
    ? agents.find(item => item.agent.id === selection.id) : undefined;
  const selectedEvent = selection.kind === 'event'
    ? events.find(item => item.id === selection.id) : undefined;
  const selectedDecision = selection.kind === 'decision'
    ? decisions?.find(item => item.id === selection.id) : undefined;

  useEffect(() => {
    const panel = panelRef.current;
    if (!panel) return;
    panel.focus();
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        event.preventDefault();
        onClose();
        return;
      }
      if (event.key !== 'Tab' || !panel) return;
      const focusable = Array.from(panel.querySelectorAll<HTMLElement>(
        'button:not(:disabled), a[href], [tabindex]:not([tabindex="-1"])'));
      if (!focusable.length) {
        event.preventDefault();
        panel.focus();
        return;
      }
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey && (document.activeElement === first ||
          document.activeElement === panel)) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey &&
          (document.activeElement === last || document.activeElement === panel &&
            focusable.length === 0)) {
        event.preventDefault();
        first.focus();
      }
    }
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [selection, onClose]);

  const title = selectedAgent?.agent.role ?? selectedEvent?.label ??
    selectedDecision?.title ?? 'Record unavailable';
  const relatedHandoffs = selectedAgent
    ? evidence.handoffs.filter(event => event.toAgent === selectedAgent.agent.id ||
        event.fromAgent === selectedAgent.agent.id)
      .slice().sort((a, b) => b.createdUtc.localeCompare(a.createdUtc)).slice(0, 6)
    : [];
  const destination = selectedAgent ? 'team' : selectedDecision?.destination ??
    selectedEvent?.destination ?? 'history';
  return <div className="mc-drawer-overlay">
    <div className="mc-drawer-backdrop" aria-hidden="true" onClick={onClose} />
    <aside ref={panelRef} className="mc-context-drawer" role="dialog" aria-modal="true"
      aria-labelledby={headingId} tabIndex={-1}>
      <div className="mc-drawer-header">
        <div><span className="wb-kicker">PROJECT CONTEXT / {projectTitle}</span>
          <h2 id={headingId}>{title}</h2>
          <small>{selectedAgent ? 'Agent identity & recorded activity' :
            selectedEvent ? 'Recorded event evidence' :
              selectedDecision ? 'Owner action / source-bound evidence' : 'Historical selection'}</small></div>
        <button type="button" className="outline mc-drawer-close" onClick={onClose}
          aria-label="Close contextual drawer">Close</button>
      </div>
      <div className="mc-drawer-content">
        {selectedAgent && <>
          <dl className="mc-drawer-fields">
            <dt>Recorded status</dt><dd>{selectedAgent.status}</dd>
            <dt>Most recent work</dt><dd>{selectedAgent.detail}</dd>
            <dt>Responsibility</dt><dd>{selectedAgent.agent.goal}</dd>
            <dt>Working style</dt><dd>{selectedAgent.agent.workingStyle}</dd>
            <dt>Execution authority</dt><dd>{selectedAgent.agent.canExecuteCode
              ? 'Subject to host authorization' : 'No executable capability declared'}</dd>
          </dl>
          <h3>Recorded handoffs</h3>
          {relatedHandoffs.length ? <ol className="mc-drawer-ledger">
            {relatedHandoffs.map((event, index) => <li key={
              event.createdUtc + ':' + event.kind + ':' + index}>
              <strong>{event.fromAgent} → {event.toAgent}</strong>
              <p>{event.kind}</p>
              <small><time dateTime={event.createdUtc}>
                {new Date(event.createdUtc).toLocaleString()}</time>
                {' · '}{event.current ? 'Current binding' : 'Historical binding'}</small>
            </li>)}
          </ol> : <p className="hint">No saved handoffs for this role.</p>}
          <h3>Skills and permissions</h3>
          <p className="hint">Declared future scopes are not installed skills or permissions.</p>
          <p>{selectedAgent.agent.allowedSkillScopes.length
            ? selectedAgent.agent.allowedSkillScopes.join(' · ')
            : 'No skill scopes declared'}</p>
          <details><summary>Professional profile</summary>
            <p>{selectedAgent.agent.backstory}</p></details>
        </>}
        {selectedEvent && <>
          <dl className="mc-drawer-fields">
            <dt>Recorded by</dt><dd>{selectedEvent.source}</dd>
            <dt>Event</dt><dd>{selectedEvent.label}</dd>
            <dt>Saved timestamp</dt><dd><time dateTime={selectedEvent.at}>
              {new Date(selectedEvent.at).toLocaleString()}</time></dd>
            <dt>Evidence description</dt><dd>{selectedEvent.detail}</dd>
            <dt>Record identity</dt><dd className="mc-drawer-record">{selectedEvent.id}</dd>
          </dl>
          <p className="hint">This event is from persisted host records.
            Independent ledgers do not establish a causal sequence.</p>
        </>}
        {selectedDecision && <>
          <dl className="mc-drawer-fields">
            <dt>Action type</dt><dd>{selectedDecision.category}</dd>
            <dt>Saved context</dt><dd>{selectedDecision.detail}</dd>
            <dt>Source / approval binding</dt><dd className="mc-drawer-record">
              {selectedDecision.binding}</dd>
            <dt>Authorization required</dt><dd>{selectedDecision.requiresApproval
              ? 'Explicit owner review in the Work stage' :
                'No authorization is granted by this navigation item'}</dd>
          </dl>
          <p className="hint">Open the existing workspace to inspect current
            versions, evidence, and exact host-side authorization requirements.</p>
        </>}
        {!selectedAgent && !selectedEvent && !selectedDecision &&
          <p role="status">This selection is no longer in the current project evidence.
            No record or project data has been substituted.</p>}
      </div>
      <div className="mc-drawer-footer">
        <button type="button" className="primary"
          onClick={() => { onClose(); onNavigate(destination); }}>
          Open in {destination[0].toUpperCase() + destination.slice(1)}
        </button>
        <p className="hint">Inspection only. No approval, repair or execution is authorized.</p>
      </div>
    </aside>
  </div>;
}
