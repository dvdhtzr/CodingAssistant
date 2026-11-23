import { useCallback, useEffect, useRef, useState } from 'react';
import { CommandDrawer, type DrawerSelection } from './CommandDrawer';
import { buildCommandDecisionQueue, DecisionQueueView } from './DecisionQueue';
import { api, type TaskItem } from './api';
import type { DecisionInboxSnapshot } from './OwnerDecisionInbox';
import type { AgentIdentity } from './AgentIdentityPanel';
import type { EngineerSnapshot } from './EngineeringWorkspace';
import type { ProjectSourceReadinessReport } from './VerificationScope';
import type { TeamHandoff, TeamQaSnapshot } from './TeamWorkroom';

export interface CommandEvidence {
  agents: AgentIdentity[];
  handoffs: TeamHandoff[];
  engineering: EngineerSnapshot | null;
  sourceReadiness?: ProjectSourceReadinessReport | null;
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

const HUE_CLASS: Record<string, string> = {
  ProductOwner: 'ca-ag-po',
  SolutionArchitect: 'ca-ag-sa',
  TechnicalLead: 'ca-ag-tl',
  SoftwareEngineer: 'ca-ag-se',
  QaEngineer: 'ca-ag-qa',
};

export interface JourneyTile {
  id: string;
  label: string;
  agent: string;
  state: 'done' | 'now' | 'attention' | 'todo';
  hint: string;
}

// Derived purely from saved records. A tile never claims progress beyond what
// the persisted specification, architecture, plan, engineering and QA state show.
export function commandJourney(evidence: CommandEvidence): JourneyTile[] {
  const d = evidence.decisions;
  const spec = d.specification;
  const arch = d.decisions ?? [];
  const plan = d.plan;
  const eng = evidence.engineering?.job;
  const qa = evidence.qa;

  const discovery: JourneyTile['state'] = spec || d.readyForSpecification
    ? 'done' : d.ownerAnswers > 0 ? 'now' : 'todo';
  const specification: JourneyTile['state'] = spec?.status === 'Approved' ? 'done'
    : spec ? 'attention'
      : d.ownerAnswers > 0 ? 'now' : 'todo';
  const architecture: JourneyTile['state'] = arch.length
    ? (arch.every(item => item.status === 'Approved' && item.specificationCurrent)
      ? 'done' : 'attention')
    : spec?.status === 'Approved' ? 'now' : 'todo';
  const planning: JourneyTile['state'] = plan?.status === 'Approved' && plan.current ? 'done'
    : plan ? 'attention' : architecture === 'done' ? 'now' : 'todo';
  // "Generated" is not a built application. Missing files are engineering
  // work, and static QA is not functional verification.
  const needsCompletion = evidence.sourceReadiness?.status === 'INCOMPLETE' &&
    evidence.sourceReadiness.runId === eng?.id;
  const build: JourneyTile['state'] = eng?.state === 'Running' ? 'now'
    : needsCompletion || eng?.state === 'Failed' ||
      eng?.state === 'Interrupted' ? 'attention'
    : eng?.state === 'Generated' ? 'attention'
    : planning === 'done' ? 'now' : 'todo';
  const findings = qa && evidence.engineering &&
    qa.job.engineeringRunId === evidence.engineering.job.id &&
    qa.job.planSha256 === evidence.engineering.job.planSha256
    ? qa.reports.reduce((n, r) => n + (r.assessment?.findings?.length ?? 0), 0) : 0;
  const verify: JourneyTile['state'] = findings > 0 ||
    qa?.job.state === 'Reviewed' ? 'attention'
    : eng?.state === 'Generated' && !needsCompletion ? 'now' : 'todo';

  return [
    { id: 'discovery', label: 'Discovery', agent: 'ProductOwner', state: discovery,
      hint: discovery === 'done' ? 'Captured' : discovery === 'now' ? 'In discovery' : 'Not started' },
    { id: 'specification', label: 'Specification', agent: 'ProductOwner', state: specification,
      hint: spec ? spec.status + ' · v' + spec.version : 'Awaiting interview' },
    { id: 'architecture', label: 'Architecture', agent: 'SolutionArchitect', state: architecture,
      hint: arch.length ? arch.length + ' decision' + (arch.length === 1 ? '' : 's') : 'Not started' },
    { id: 'plan', label: 'Plan', agent: 'TechnicalLead', state: planning,
      hint: plan ? plan.status + ' · rev ' + plan.revision : 'Not started' },
    { id: 'build', label: 'Build', agent: 'SoftwareEngineer', state: build,
      hint: needsCompletion ? 'Missing project files' :
        eng?.state === 'Generated' ? 'Source only · not built' :
        eng ? 'Source run: ' + eng.state : 'Not started' },
    { id: 'verify', label: 'Verify', agent: 'QaEngineer', state: verify,
      hint: findings > 0 ? findings + ' to triage' :
        qa?.job.state === 'Reviewed' ? 'Static review only' :
        needsCompletion ? 'Waiting for complete source' :
        qa ? 'Review pending' : 'Not independently tested' },
  ];
}

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
  const blockedSource = evidence.sourceReadiness?.status === 'INCOMPLETE' &&
    evidence.sourceReadiness.runId === latestEngineering?.id &&
    latestEngineering?.state === 'Generated';
  const recommendation = queue[0]?.requiresApproval
    ? { title: queue[0].title, detail: queue[0].detail, to: queue[0].destination }
    : blockedSource
      ? { title: 'Open engineering work order', detail:
          'Saved source has unresolved project completeness findings.', to: 'work' as const }
    : queue[0]
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
  const stages = commandJourney(evidence);
  const activeStage = stages.find(step => step.state === 'now')
    ?? stages.find(step => step.state === 'attention')
    ?? stages.find(step => step.state === 'todo');
  const missionState = blockedSource ? 'Engineering needs attention' :
    activeStage ? activeStage.label : 'No pending stage';
  const handoffs = [...evidence.handoffs].sort((a, b) =>
    b.createdUtc.localeCompare(a.createdUtc)).slice(0, 3);
  const recentEvents = events.slice(0, 5);

  return <section className="mc-command ca-command ca-console" aria-label="Team Console">
    <header className="ca-console-header">
      <div className="ca-console-project">
        <span className="wb-kicker">TEAM CONSOLE · {task.status}</span>
        <h2>{task.title}</h2>
        <p title={task.objective}>{task.objective}</p>
      </div>
      <div className="ca-console-focus">
        <span className="wb-kicker">CURRENT STAGE</span>
        <strong>{missionState}</strong>
        <button type="button" className="primary"
          onClick={() => onNavigate(recommendation.to)}>
          {recommendation.title}
        </button>
      </div>
    </header>

    <div className="ca-console-stats" aria-label="Saved project status">
      <span><strong>{running.length}</strong> recorded active agent{running.length === 1 ? '' : 's'}</span>
      <span><strong>{reviewCount}</strong> approval{reviewCount === 1 ? '' : 's'} to review</span>
      <span><strong>{evidence.engineering?.tasks.filter(t => t.state === 'Completed').length ?? 0}</strong>
        {' / '}{evidence.engineering?.tasks.length ?? 0} engineering assignments</span>
      <span>Source: <strong>{latestEngineering?.state ?? 'Not started'}</strong> · unverified</span>
    </div>

    <div className="ca-console-grid">
      <section className="ca-console-floor" aria-label="Agent collaboration board">
        <div className="ca-console-section-head">
          <div><span className="wb-kicker">COLLABORATING SPECIALISTS</span>
            <h3>Agent Activity Board</h3></div>
          <button type="button" className="link" onClick={() => onNavigate('team')}>
            Team details
          </button>
        </div>
        <div className="ca-crew ca-console-crew" aria-label="Agent responsibilities and recorded states">
          {agents.map(item => <button type="button" key={item.agent.id}
            className={'ca-node ' + (HUE_CLASS[item.agent.id] ?? '') +
              (item.active ? ' ca-running' : ' ca-idle')}
            onClick={() => showDrawer({ kind: 'agent', id: item.agent.id })}
            aria-haspopup="dialog"
            aria-label={'Inspect ' + item.agent.role + ': ' + item.status}>
            <span className="ca-wire" aria-hidden="true" />
            <span className="ca-orb" aria-hidden="true">
              {item.agent.role.split(' ').map(word => word[0]).join('').slice(0, 2)}
            </span>
            <span className="ca-node-role">{item.agent.role}</span>
            <span className="ca-node-stat" title={item.detail}>{item.detail}</span>
            <span className="ca-console-agent-state">
              {item.active ? 'Recorded running' : item.status}
            </span>
          </button>)}
          {!agents.length && <p className="hint">No registered agent identities returned.</p>}
        </div>
        <div className="ca-console-handoffs">
          <div className="ca-console-section-head">
            <div><span className="wb-kicker">SAVED DELEGATION RECORDS</span>
              <h3>Handoffs</h3></div>
            <button type="button" className="link" onClick={() => onNavigate('history')}>
              Full history
            </button>
          </div>
          {handoffs.length ? <ol className="ca-console-handoff-list">
            {handoffs.map((handoff, index) => <li key={
              handoff.createdUtc + handoff.kind + handoff.fromAgent + index}>
              <span className="ca-console-handoff-route">
                <strong>{handoff.fromAgent}</strong>
                <span aria-label="handed off to">→</span>
                <strong>{handoff.toAgent}</strong>
              </span>
              <span className="ca-console-handoff-kind" title={handoff.kind}>
                {handoff.kind}{handoff.workItemId ? ' · ' + handoff.workItemId : ''}
              </span>
              <time dateTime={handoff.createdUtc}>
                {new Date(handoff.createdUtc).toLocaleTimeString()}
              </time>
            </li>)}
          </ol> : <p className="hint">No handoff has been saved for this project yet.</p>}
        </div>
        <div className="ca-console-current" aria-label="Current recorded operation">
          <span className="wb-kicker">NOW</span>
          <strong>{running.length
            ? running.map(item => item.agent.role).join(' · ')
            : 'No active agent run recorded'}</strong>
          <span title={running.map(item => item.detail).join(' · ')}>
            {running.length ? running.map(item => item.detail).join(' · ') :
              latestEngineering
                ? 'Most recent source run: ' + latestEngineering.state
                : 'Awaiting an approved project step'}
          </span>
        </div>
        {blockedSource && <p className="ca-console-blocked">
          <strong>Source needs attention:</strong>{' '}
          {evidence.sourceReadiness?.findings.filter(f => f.severity === 'Error').length}
          {' '}blocking completeness issue(s).
          <button type="button" className="link" onClick={() => onNavigate('work')}>
            Inspect source
          </button>
        </p>}
      </section>

      <aside className="ca-console-side" aria-label="Owner attention and activity">
        <DecisionQueueView items={queue} maxVisible={2}
          onInspect={id => showDrawer({ kind: 'decision', id })}
          onViewAll={() => onNavigate('work')} />
        <section className="mc-command-panel ca-activity ca-console-activity"
          aria-label="Recent Activity Stream">
          <div className="ca-console-section-head">
            <div><span className="wb-kicker">PERSISTED EVENTS</span>
              <h3>Recent activity</h3></div>
            <button type="button" className="link"
              onClick={() => onNavigate('history')}>All events</button>
          </div>
          {recentEvents.length ? <ol className="mc-recent-events ca-ticker">
            {recentEvents.map(event => <li key={event.id}>
              <button type="button" aria-haspopup="dialog"
                onClick={() => showDrawer({ kind: 'event', id: event.id })}>
                <span className="mc-event-top"><strong>{event.source}</strong>
                  <time dateTime={event.at}>
                    {new Date(event.at).toLocaleTimeString()}
                  </time></span>
                <span>{event.label}</span>
                <small title={event.detail}>{event.detail}</small>
              </button>
            </li>)}
          </ol> : <p className="hint">No agent activity has been recorded yet.</p>}
        </section>
      </aside>
    </div>

    <footer className="ca-console-journey" aria-label="Project stage overview">
      <span className="wb-kicker">PROGRESS</span>
      <ol className="ca-console-steps">
        {stages.map(stage => <li key={stage.id}
          className={'ca-console-step ca-' + stage.state}
          title={stage.hint}>
          <span aria-hidden="true">{stage.state === 'done' ? '✓' :
            stage.state === 'attention' ? '!' :
              stage.state === 'now' ? '●' : '○'}</span>
          {stage.label}
        </li>)}
      </ol>
      <small>Saved source and static QA are not verified builds or executed tests.</small>
    </footer>
    {selected && <CommandDrawer projectTitle={task.title} selection={selected}
      agents={agents} events={events} decisions={queue} evidence={evidence}
      onClose={closeDrawer} onNavigate={onNavigate} />}
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
        // An incomplete source snapshot becomes a Command work order.
        // A failed optional readiness read never upgrades the project to READY.
        let sourceReadiness: ProjectSourceReadinessReport | null = null;
        if (engineering.snapshot?.job.id) {
          try {
            const result = await api<ProjectSourceReadinessReport>(token,
              prefix + '/engineering-runs/' +
              engineering.snapshot.job.id + '/source-readiness');
            if (result.runId === engineering.snapshot.job.id &&
                result.executionAvailable === false &&
                result.verificationStatus === 'GENERATED_NOT_TESTED')
              sourceReadiness = result;
          } catch { /* Preserve saved agent evidence, not an inferred readiness pass. */ }
        }
        if (!disposed) {
          setEvidence({
            agents: roster.agents,
            sourceReadiness,
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
