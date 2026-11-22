import { useCallback, useEffect, useRef, useState } from 'react';
import { CommandDrawer, type DrawerSelection } from './CommandDrawer';
import { buildCommandDecisionQueue, DecisionQueueView } from './DecisionQueue';
import { api, type TaskItem } from './api';
import type { DecisionInboxSnapshot } from './OwnerDecisionInbox';
import type { AgentIdentity } from './AgentIdentityPanel';
import type { EngineerSnapshot } from './EngineeringWorkspace';
import type { TeamHandoff, TeamQaSnapshot } from './TeamWorkroom';

export interface CommandEvidence {
  agents: AgentIdentity[];
  handoffs: TeamHandoff[];
  engineering: EngineerSnapshot | null;
  qa: (Omit<TeamQaSnapshot, 'reports'> & {
    reports: Array<{ path: string; reviewedUtc: string;
      assessment?: { findings?: Array<{ severity: string; requirement: string }> } }>;
    events?: { sequence: number; kind: string; detail: string; createdUtc: string }[];
  }) | null;
  decisions: DecisionInboxSnapshot;
}
export interface ActivityEntry {
  id: string;
  at: string;
  source: string;
  label: string;
  detail: string;
  destination: 'team' | 'work' | 'quality' | 'history';
}
export interface AgentActivity {
  agent: AgentIdentity;
  status: string;
  detail: string;
  active: boolean;
}

const accents: Record<string, string> = {
  ProductOwner: 'visionary',
  SolutionArchitect: 'designer',
  TechnicalLead: 'strategist',
  SoftwareEngineer: 'builder',
  QaEngineer: 'guardian',
};

export function commandActivity(evidence: CommandEvidence): ActivityEntry[] {
  const engineeringId = evidence.engineering?.job.id ?? '';
  const qaId = evidence.qa?.job.id ?? '';
  const handoffs = evidence.handoffs.map((event, i): ActivityEntry => ({
    id: 'handoff:' + event.createdUtc + ':' + event.kind + ':' +
      event.fromAgent + ':' + event.toAgent + ':' + (event.workItemId ?? '') + ':' + i,
    at: event.createdUtc,
    source: event.fromAgent + ' → ' + event.toAgent,
    label: event.kind,
    detail: event.current ? 'Current saved binding' : 'Historical saved binding',
    destination: 'history',
  }));
  const engineering = (evidence.engineering?.events ?? []).map((event): ActivityEntry => ({
    id: 'engineering:' + engineeringId + ':' + event.sequence,
    at: event.createdUtc,
    source: 'Software Engineer',
    label: event.kind,
    detail: event.detail,
    destination: 'work',
  }));
  // QA event timestamps and sequences are stored with the latest QA review.
  const qa = (evidence.qa?.events ?? []).map((event): ActivityEntry => ({
    id: 'qa:' + qaId + ':' + event.sequence,
    at: event.createdUtc,
    source: 'QA Engineer',
    label: event.kind,
    detail: event.detail,
    destination: 'quality',
  }));
  // A stable presentation order is not a causal claim across independent ledgers.
  return [...handoffs, ...engineering, ...qa]
    .sort((a, b) => b.at.localeCompare(a.at) || a.id.localeCompare(b.id));
}

export function commandAgents(evidence: CommandEvidence): AgentActivity[] {
  return evidence.agents.map(agent => {
    const related = evidence.handoffs.filter(event =>
      event.fromAgent === agent.id || event.toAgent === agent.id);
    const latest = related.slice().sort((a, b) =>
      b.createdUtc.localeCompare(a.createdUtc))[0];
    if (agent.id === 'SoftwareEngineer' && evidence.engineering) {
      const run = evidence.engineering;
      const current = run.tasks.find(task => task.state === 'Running');
      return { agent, active: run.job.state === 'Running',
        status: 'Source run: ' + run.job.state,
        detail: current?.title ?? (run.job.state === 'Running'
          ? 'No active assignment recorded yet'
          : run.tasks.filter(task => task.state === 'Completed').length +
            ' of ' + run.tasks.length + ' assignments completed (source only)') };
    }
    if (agent.id === 'QaEngineer' && evidence.qa) {
      return { agent, active: evidence.qa.job.state === 'Running',
        status: 'Static QA: ' + evidence.qa.job.state,
        detail: evidence.qa.reports.length + ' saved file report(s) · Not executable verification' };
    }
    return { agent, active: false,
      status: latest ? 'Recorded handoff' : 'No work recorded',
      detail: latest ? latest.kind + ' · ' + new Date(latest.createdUtc).toLocaleString() :
        'No live status is reported for this role' };
  });
}

export function CommandActivityView({ task, evidence, onNavigate }: {
  task: TaskItem;
  evidence: CommandEvidence;
  onNavigate: (destination: 'team' | 'work' | 'quality' | 'history' | 'diagnostics') => void;
}) {
  const events = commandActivity(evidence);
  const agents = commandAgents(evidence);
  const [selected, setSelected] = useState<DrawerSelection | null>(null);
  const triggerRef = useRef<HTMLElement | null>(null);
  const showDrawer = (selection: DrawerSelection) => {
    triggerRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement : null;
    setSelected(selection);
  };
  const closeDrawer = useCallback(() => {
    setSelected(null);
    // Only restore focus to a still-connected trigger in this same project view.
    const prior = triggerRef.current;
    if (prior?.isConnected) prior.focus();
    triggerRef.current = null;
  }, []);
  const queue = buildCommandDecisionQueue(evidence);
  const reviewCount = queue.filter(item => item.requiresApproval).length;
  const latestEngineering = evidence.engineering?.job;
  const latestQa = evidence.qa?.job;
  const recommendation = queue[0]
    ? { title: queue[0].title, detail: queue[0].detail, to: queue[0].destination }
    : latestEngineering?.state === 'Running'
      ? { title: 'Inspect ongoing engineering', detail:
          'A saved Software Engineer run is currently running.', to: 'work' as const }
      : latestQa?.state === 'Reviewed'
        ? { title: 'Review static QA findings', detail:
            'A saved QA review is available. Findings still require owner classification.',
            to: 'quality' as const }
        : latestEngineering?.state === 'Generated'
          ? { title: 'Review generated source and QA options', detail:
              'Source generation completed, but build and test verification is not established.',
              to: 'work' as const }
          : { title: 'Continue project work', detail:
              'Inspect saved work and select the next permitted step.',
              to: 'work' as const };
  const running = agents.filter(agent => agent.active);

  return <section className="mc-command" aria-label="Mission Control command">
    <div className="mc-command-heading">
      <div><p className="wb-kicker">MISSION CONTROL / COMMAND</p>
        <h2>{task.title}</h2><p>{task.objective}</p></div>
      <span className="pill">{task.status} · local saved project</span>
    </div>
    <section className="mc-next-action" aria-label="Recommended next action">
      <div><span className="wb-kicker">GUIDED SUPERVISION</span>
        <strong>{recommendation.title}</strong>
        <p>{recommendation.detail}</p></div>
      <button type="button" className="primary"
        onClick={() => onNavigate(recommendation.to)}>Open {recommendation.to}</button>
      <span className="mc-decision-count">{reviewCount} saved owner review
        {reviewCount === 1 ? '' : 's'} awaiting attention</span>
    </section>
    <DecisionQueueView items={queue} onInspect={id => showDrawer({ kind: 'decision', id })} />
    <div className="mc-command-columns">
      <section className="mc-command-panel" aria-label="Agent Activity Board">
        <div className="mc-panel-heading"><div><span className="wb-kicker">TEAM STATUS</span>
          <h3>Agent Activity Board</h3></div>
          <button type="button" className="outline"
            onClick={() => onNavigate('team')}>Team</button></div>
        <div className="mc-agent-grid">
          {agents.map(item => <button type="button" key={item.agent.id}
            className="mc-agent-card" data-agent={item.agent.id}
            data-accent={accents[item.agent.id] ?? 'neutral'}
            onClick={() => showDrawer({ kind: 'agent', id: item.agent.id })}
            aria-haspopup="dialog"
            aria-label={'Inspect ' + item.agent.role + ': ' + item.status}>
            <span className="mc-agent-emblem" aria-hidden="true">
              {item.agent.role.split(' ').map(word => word[0]).join('').slice(0, 2)}
            </span>
            <span className="mc-agent-copy"><strong>{item.agent.role}</strong>
              <span className="mc-agent-state">{item.status}</span>
              <small>{item.detail}</small></span>
            {item.active && <span className="mc-agent-active">Recorded running</span>}
          </button>)}
        </div>
        {!agents.length && <p className="hint">No host-defined agent identities returned.</p>}
      </section>
      <section className="mc-command-panel" aria-label="Recent Activity Stream">
        <div className="mc-panel-heading"><div><span className="wb-kicker">SAVED EVIDENCE</span>
          <h3>Recent activity</h3></div>
          <button type="button" className="outline"
            onClick={() => onNavigate('history')}>View all</button></div>
        {events.length ? <ol className="mc-recent-events">
          {events.slice(0, 5).map(event => <li key={event.id}>
            <button type="button" aria-haspopup="dialog"
              onClick={() => showDrawer({ kind: 'event', id: event.id })}>
              <span className="mc-event-top"><strong>{event.source}</strong>
                <time dateTime={event.at}>{new Date(event.at).toLocaleString()}</time></span>
              <span>{event.label}</span><small>{event.detail}</small>
            </button>
          </li>)}
        </ol> : <p className="hint">No saved agent, engineering, or QA events yet.</p>}
      </section>
    </div>
    <section className="mc-operation" aria-label="Current engineering operation">
      <strong>{running.length ? 'Recorded active operation' : 'No active agent run recorded'}</strong>
      {running.length ? <p>{running.map(item => item.agent.role + ': ' + item.detail).join(' · ')}</p>
        : <p>{latestEngineering ? 'Latest source run: ' + latestEngineering.state :
          'Engineering has not started.'} No activity is inferred from an idle browser.</p>}
      <small>Agent activity is derived from saved host records. Static QA and generated source
        do not establish a verified application. Opening a workspace never authorizes work.</small>
      <button type="button" className="outline mc-legacy-link"
        onClick={() => onNavigate('diagnostics')}>Legacy diagnostics</button>
    </section>
    {selected && <CommandDrawer projectTitle={task.title} selection={selected}
      agents={agents} events={events} decisions={queue} evidence={evidence} onClose={closeDrawer}
      onNavigate={onNavigate} />}
  </section>;
}

export function CommandActivity({ token, task, onNavigate }: {
  token: string;
  task: TaskItem;
  onNavigate: (destination: 'team' | 'work' | 'quality' | 'history' | 'diagnostics') => void;
}) {
  const [evidence, setEvidence] = useState<CommandEvidence | null>(null);
  const [error, setError] = useState(false);
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    let disposed = false;
    let pending = false;
    setEvidence(null);
    setError(false);
    async function reload() {
      if (pending) return;
      pending = true;
      try {
        const prefix = '/api/tasks/' + task.id;
        const [roster, handoffs, engineering, qa, interview, architecture, plan] =
          await Promise.all([
            api<{ agents: AgentIdentity[] }>(token, '/api/agents/roster'),
            api<{ events: TeamHandoff[] }>(token, prefix + '/agent-handoffs'),
            api<{ snapshot: EngineerSnapshot | null }>(token, prefix + '/engineering-runs/latest'),
            api<{ snapshot: TeamQaSnapshot | null }>(token, prefix + '/qa-reviews/latest'),
            api<{ specification: DecisionInboxSnapshot['specification'];
              readyForSpecification: boolean; messages: { role: string }[] }>(
              token, prefix + '/interview'),
            api<{ decisions: DecisionInboxSnapshot['decisions'] }>(
              token, prefix + '/architecture-decisions'),
            api<{ plan: DecisionInboxSnapshot['plan'] }>(
              token, prefix + '/implementation-plan'),
          ]);
        if (!disposed) {
          setEvidence({
            agents: roster.agents,
            handoffs: handoffs.events,
            engineering: engineering.snapshot,
            qa: qa.snapshot,
            decisions: {
              specification: interview.specification,
              readyForSpecification: interview.readyForSpecification,
              ownerAnswers: interview.messages.filter(message => message.role === 'owner').length,
              decisions: architecture.decisions,
              plan: plan.plan,
            },
          });
          setError(false);
        }
      } catch {
        if (!disposed) { setEvidence(null); setError(true); }
      } finally {
        pending = false;
      }
    }
    void reload();
    const timer = window.setInterval(() => void reload(), 8000);
    return () => { disposed = true; window.clearInterval(timer); };
  }, [token, task.id, refresh]);
  if (error) return <section className="panel" role="alert">
    <h2>Command evidence unavailable</h2>
    <p>The host-owned ledgers could not all be loaded. No status or approval is inferred.</p>
    <button type="button" onClick={() => setRefresh(value => value + 1)}>Retry</button>
  </section>;
  if (!evidence) return <section className="panel" role="status">
    <h2>Loading Command evidence…</h2>
    <p>Reading saved agent assignments, approvals, engineering and QA history.</p>
  </section>;
  return <CommandActivityView task={task} evidence={evidence} onNavigate={onNavigate}/>;
}
