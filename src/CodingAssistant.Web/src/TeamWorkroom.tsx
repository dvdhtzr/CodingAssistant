import { useEffect, useMemo, useState } from 'react';
import { api } from './api';
import type { AgentIdentity } from './AgentIdentityPanel';
import type { EngineerSnapshot } from './EngineeringWorkspace';
import type { JourneyId } from './ProjectJourney';

export interface TeamHandoff {
  kind: string;
  fromAgent: string;
  toAgent: string;
  createdUtc: string;
  current: boolean;
  specificationVersion: number;
  revision: number;
  model: string;
  workItemId?: string | null;
  planSha256?: string | null;
}
export interface TeamQaSnapshot {
  job: {
    id: string;
    state: 'Running' | 'Reviewed' | 'Failed' | 'Interrupted';
    engineeringRunId: string;
    planSha256: string;
  };
  sourceState: string;
  reports: { path: string; reviewedUtc: string }[];
}
export interface TeamEvidence {
  agents: AgentIdentity[];
  events: TeamHandoff[];
  engineering: EngineerSnapshot | null;
  qa: TeamQaSnapshot | null;
}

export interface AgentRecordSummary {
  agent: AgentIdentity;
  recordedHandoffs: number;
  historicalHandoffs: number;
  taskCount: number;
  completedTasks: number;
  reportedState: string;
}

const stageForRole: Record<string, JourneyId> = {
  ProductOwner: 'discovery',
  SolutionArchitect: 'architecture',
  TechnicalLead: 'plan',
  SoftwareEngineer: 'generation',
  QaEngineer: 'delivery',
};

/** All counts and statuses derive from host-owned records, never from LLM claims. */
export function summarizeTeamEvidence(evidence: TeamEvidence): AgentRecordSummary[] {
  return evidence.agents.map(agent => {
    const related = evidence.events.filter(event =>
      event.fromAgent === agent.id || event.toAgent === agent.id);
    const tasks = agent.id === 'SoftwareEngineer'
      ? evidence.engineering?.tasks ?? [] : [];
    const reportedState = agent.id === 'SoftwareEngineer' && evidence.engineering
      ? 'Engineering run: ' + evidence.engineering.job.state
      : agent.id === 'QaEngineer' && evidence.qa
        ? 'QA static review: ' + evidence.qa.job.state
        : related.length ? 'Recorded handoffs' : 'No saved handoffs yet';
    return {
      agent,
      recordedHandoffs: related.length,
      historicalHandoffs: related.filter(event => !event.current).length,
      taskCount: tasks.length,
      completedTasks: tasks.filter(task => task.state === 'Completed').length,
      reportedState,
    };
  });
}

const roleAbbreviation = (role: string) => role.split(' ')
  .slice(0, 2).map(word => word[0] ?? '').join('').toUpperCase();

export function TeamWorkroomView({ evidence, onNavigate }: {
  evidence: TeamEvidence;
  onNavigate: (stage: JourneyId) => void;
}) {
  const summary = useMemo(() => summarizeTeamEvidence(evidence), [evidence]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const selected = summary.find(item => item.agent.id === selectedId) ?? summary[0];
  const events = selected ? evidence.events.filter(event =>
    event.fromAgent === selected.agent.id || event.toAgent === selected.agent.id)
    .slice().reverse() : [];
  const lastRun = evidence.engineering?.job;
  const review = evidence.qa;
  const qaCurrent = !!lastRun && !!review &&
    review.job.engineeringRunId === lastRun.id &&
    review.job.planSha256 === lastRun.planSha256;
  const suggestedStage = selected ? stageForRole[selected.agent.id] : undefined;

  return <section className="team-workroom" aria-label="Project agent team workroom">
    <div className="team-workroom-intro">
      <div>
        <span className="wb-kicker">AGENT TEAM / RECORDED WORK</span>
        <h3>Engineering team</h3>
        <p>Independent host-defined MAF roles, saved assignments, and actual handoffs.</p>
      </div>
      <span className="team-workroom-evidence">
        {evidence.agents.length} roles · {evidence.events.length} saved handoffs
      </span>
    </div>
    {!summary.length ? <p className="wb-muted">No agents are registered.</p> : <>
      <div className="team-workroom-roster"
        role="group" aria-label="Select an engineering specialist">
        {summary.map(item => <button type="button" key={item.agent.id}
          className={'team-workroom-member' +
            (selected?.agent.id === item.agent.id ? ' team-member-selected' : '')}
          data-agent={item.agent.id}
          aria-pressed={selected?.agent.id === item.agent.id}
          onClick={() => setSelectedId(item.agent.id)}>
          <span className="team-member-initials" aria-hidden="true">
            {roleAbbreviation(item.agent.role)}</span>
          <span className="team-member-copy">
            <strong>{item.agent.role}</strong>
            <small>{item.reportedState}</small>
            <span>{item.recordedHandoffs} saved handoff{item.recordedHandoffs === 1 ? '' : 's'}</span>
          </span>
        </button>)}
      </div>
      {selected && <div className="team-workroom-detail" key={selected.agent.id}>
        <div className="team-workroom-profile">
          <div className="team-profile-heading">
            <div><span className="wb-kicker">SELECTED AGENT IDENTITY</span>
              <h4>{selected.agent.role}</h4>
              <small>{selected.agent.id}</small></div>
            {suggestedStage && <button type="button" className="outline"
              onClick={() => onNavigate(suggestedStage)}>
              Open {selected.agent.id === 'QaEngineer'
                ? 'Artifacts & Delivery' : selected.agent.role + ' stage'} →
            </button>}
          </div>
          <div className="team-profile-text">
            <strong>Mission</strong><p>{selected.agent.goal}</p>
            <strong>Working style</strong><p>{selected.agent.workingStyle}</p>
            <details className="team-profile-background">
              <summary>Professional backstory (fictional)</summary>
              <p>{selected.agent.backstory}</p>
            </details>
          </div>
          <div className="team-skill-descriptors" aria-label="Declared future skill scopes">
            <strong>Future skill scopes</strong>
            <p>Descriptors only. No SKILL.md package is installed or
              loaded by this view; none grants tool or execution rights.</p>
            <div className="team-skill-list">
              {selected.agent.allowedSkillScopes.map(scope =>
                <span key={scope} className="team-skill-scope">
                  {scope} <small>not enabled</small>
                </span>)}
              {!selected.agent.allowedSkillScopes.length &&
                <span className="wb-muted">No skill scopes declared.</span>}
            </div>
            <small>Executable tools: {selected.agent.canExecuteCode
              ? 'Host authorization required' : 'Not authorized'}</small>
          </div>
        </div>
        <div className="team-workroom-ledger">
          <span className="wb-kicker">SAVED WORK / EVIDENCE</span>
          {selected.agent.id === 'SoftwareEngineer' && <>
            <p className="team-ledger-run">
              {lastRun ? 'Run ' + lastRun.state + ' · plan v' + lastRun.planRevision +
                ' · ' + lastRun.model : 'No saved engineering run'}
            </p>
            <p className="team-ledger-caption">{selected.completedTasks} of
              {' '}{selected.taskCount} saved tasks completed.
              Completion means source saved, not compiled or tested.</p>
            {!!evidence.engineering?.tasks.length && <ol className="team-work-items">
              {evidence.engineering.tasks.map(task => <li key={task.id}
                data-state={task.state}>
                <span className="team-work-number">
                  {String(task.ordinal + 1).padStart(2, '0')}</span>
                <span><strong>{task.title}</strong>
                  <small>{task.id} · {task.state}
                    {task.completedUtc ? ' · saved ' +
                      new Date(task.completedUtc).toLocaleString() : ''}</small>
                  {task.summary && <small>{task.summary}</small>}
                </span>
              </li>)}
            </ol>}
          </>}
          {selected.agent.id === 'QaEngineer' && <>
            <p className="team-ledger-run">{review ?
              'Static review ' + review.job.state +
                ' · ' + review.reports.length + ' file report(s)' :
              'No saved QA review'}</p>
            {review && <p className="team-ledger-caption">
              {qaCurrent ? 'Review references the latest saved engineering run and plan hash.' :
                'Historical QA review — current source/plan binding is not established.'}
              {' '}Static inspection only. No build, test, or VERIFIED claim.</p>}
          </>}
          <p className="team-ledger-caption">
            {selected.recordedHandoffs} recorded handoff{selected.recordedHandoffs === 1 ? '' : 's'}
            {selected.historicalHandoffs ? ' · ' + selected.historicalHandoffs +
              ' historical' : ''}.
          </p>
          {events.length ? <ol className="team-workroom-events"
            aria-label={'Saved ' + selected.agent.role + ' handoffs'}>
            {events.slice(0, 12).map((event, index) => <li key={
              event.kind + ':' + event.createdUtc + ':' + index}
              data-current={event.current}>
              <strong>{event.fromAgent} → {event.toAgent}</strong>
              <span>{event.kind}{event.workItemId ? ' · task ' +
                event.workItemId : ''}</span>
              <small>Spec v{event.specificationVersion} ·
                { ' '}{new Date(event.createdUtc).toLocaleString()} ·
                {event.current ? ' Current binding' : ' Historical binding'}</small>
            </li>)}
            </ol> : <p className="team-ledger-empty">
            No recorded handoffs for this agent yet. The roster is an identity
            definition, not a live activity feed.</p>}
          {events.length > 12 && <small className="team-ledger-caption">
            Showing 12 most recent handoffs. Complete history remains in the inspector.</small>}
        </div>
      </div>}
    </>}
    <p className="team-workroom-boundary">No synthetic model activity.
      No role can approve its own work. Skills are not installed by displaying them.
      Generated source and QA hypotheses are not independently verified.</p>
  </section>;
}

export function TeamWorkroom({ token, taskId, onNavigate }: {
  token: string;
  taskId: string;
  onNavigate: (stage: JourneyId) => void;
}) {
  const [evidence, setEvidence] = useState<TeamEvidence | null>(null);
  const [error, setError] = useState('');
  const [refresh, setRefresh] = useState(0);
  const [expanded, setExpanded] = useState(false);
  useEffect(() => {
    if (!expanded) return;
    let closed = false;
    let loading = false;
    setEvidence(null);
    setError('');
    async function load() {
      if (loading) return;
      loading = true;
      try {
        const [roster, handoffs, engineering, qa] = await Promise.all([
          api<{ agents: AgentIdentity[] }>(token, '/api/agents/roster'),
          api<{ events: TeamHandoff[] }>(token,
            '/api/tasks/' + taskId + '/agent-handoffs'),
          api<{ snapshot: EngineerSnapshot | null }>(token,
            '/api/tasks/' + taskId + '/engineering-runs/latest'),
          api<{ snapshot: TeamQaSnapshot | null }>(token,
            '/api/tasks/' + taskId + '/qa-reviews/latest'),
        ]);
        if (!closed) {
          setEvidence({ agents: roster.agents, events: handoffs.events,
            engineering: engineering.snapshot, qa: qa.snapshot });
          setError('');
        }
      } catch {
        if (!closed) {
          setEvidence(null);
          setError('Could not read the saved team ledger. No activity status is inferred.');
        }
      } finally {
        loading = false;
      }
    }
    void load();
    // Authenticated persisted data polling, not an LLM heartbeat.
    const interval = window.setInterval(() => void load(), 8000);
    return () => { closed = true; window.clearInterval(interval); };
  }, [token, taskId, refresh, expanded]);

  return <details className="team-workroom-disclosure"
    onToggle={event => setExpanded(event.currentTarget.open)}>
    <summary>
      <span className="team-disclosure-icon" aria-hidden="true">▸</span>
      <span><strong>Engineering Team & Handoffs</strong>
        <small>Inspect real assignments, backstories and skill descriptors</small>
      </span>
      <span className="team-disclosure-evidence">
        {evidence ? evidence.events.length + ' saved handoffs' :
          error ? 'Unavailable' : expanded ? 'Loading' : 'View team evidence'}
      </span>
    </summary>
    <div className="team-workroom-disclosure-body">
      {error && <div className="activity-warning" role="alert">{error}
        <button type="button" className="outline" onClick={() => setRefresh(x => x + 1)}>
          Retry saved ledger</button>
      </div>}
      {!evidence && !error && <p className="wb-muted" role="status">
        Reading host-owned roster, assignments and handoffs…</p>}
      {evidence && <TeamWorkroomView evidence={evidence} onNavigate={onNavigate}/>}
    </div>
  </details>;
}
