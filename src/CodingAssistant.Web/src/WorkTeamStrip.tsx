import { useEffect, useState } from 'react';
import { api } from './api';
import type { AgentIdentity } from './AgentIdentityPanel';
import type { EngineerSnapshot } from './EngineeringWorkspace';
import type { TeamHandoff, TeamQaSnapshot } from './TeamWorkroom';

export interface WorkTeamEvidence {
  agents: AgentIdentity[];
  handoffs: TeamHandoff[];
  engineering: EngineerSnapshot | null;
  qa: TeamQaSnapshot | null;
  repair: { cycle: { state: string; attempt: number; engineeringRunId: string } } | null;
}

// Only persisted work can mark an agent active. In particular, a QA-driven
// repair is not shown as running just because a proposal exists.
export function workAgentStatuses(saved: WorkTeamEvidence) {
  const eng = saved.engineering?.job;
  const repair = saved.repair?.cycle.engineeringRunId === eng?.id ? saved.repair : null;
  const qa = saved.qa?.job.engineeringRunId === eng?.id &&
    saved.qa?.job.planSha256 === eng?.planSha256 ? saved.qa : null;
  return saved.agents.map(agent => {
    const engineerRunning = saved.engineering?.job.state === 'Running' ||
      repair?.cycle.state === 'Running';
    const qaRunning = qa?.job.state === 'Running' ||
      repair?.cycle.state === 'QaRunning';
    const active = agent.id === 'SoftwareEngineer' ? engineerRunning :
      agent.id === 'QaEngineer' ? qaRunning : false;
    return { agent, active,
      detail: agent.id === 'SoftwareEngineer' ? (
        repair?.cycle.state === 'Running'
          ? 'Authorized repair · cycle ' + repair.cycle.attempt
          : saved.engineering?.job.state === 'Running'
            ? saved.engineering.tasks.find(task => task.state === 'Running')?.title ??
              'Source run active'
            : saved.engineering?.job.state ?? 'Not started'
      ) : agent.id === 'QaEngineer' ? (
        repair?.cycle.state === 'QaRunning' ? 'Independent repair review' :
          qa?.job.state === 'Running' ? 'Reviewing saved source' :
            qa?.job.state ?? 'Not started'
      ) : 'No active run recorded' };
  });
}

export function WorkTeamStrip({ token, taskId, onOpen }: {
  token: string; taskId: string; onOpen: () => void;
}) {
  const [saved, setSaved] = useState<WorkTeamEvidence | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let stopped = false;
    let busy = false;
    setSaved(null);
    setFailed(false);
    const load = async () => {
      if (busy || stopped) return;
      busy = true;
      try {
        const prefix = '/api/tasks/' + taskId;
        const [roster, handoffs, engineering, qa, repair] = await Promise.all([
          api<{ agents: AgentIdentity[] }>(token, '/api/agents/roster'),
          api<{ events: TeamHandoff[] }>(token, prefix + '/agent-handoffs'),
          api<{ snapshot: EngineerSnapshot | null }>(token,
            prefix + '/engineering-runs/latest'),
          api<{ snapshot: TeamQaSnapshot | null }>(token, prefix + '/qa-reviews/latest'),
          api<{ snapshot: WorkTeamEvidence['repair'] }>(token,
            prefix + '/repair-cycles/latest'),
        ]);
        if (!stopped) {
          setSaved({ agents: roster.agents, handoffs: handoffs.events,
            engineering: engineering.snapshot, qa: qa.snapshot, repair: repair.snapshot });
          setFailed(false);
        }
      } catch {
        if (!stopped) setFailed(true);
      } finally { busy = false; }
    };
    void load();
    const interval = window.setInterval(() => void load(), 6000);
    return () => { stopped = true; window.clearInterval(interval); };
  }, [token, taskId]);
  const agents = saved ? workAgentStatuses(saved) : [];
  const latest = saved?.handoffs.slice().sort((a, b) =>
    b.createdUtc.localeCompare(a.createdUtc))[0];
  return <section className="ca-work-team-strip" aria-label="Agent collaboration status">
    <div className="ca-work-team-label">
      <span className="wb-kicker">TEAM CONSOLE</span>
      <strong>{agents.some(agent => agent.active) ?
        'Recorded agent work in progress' : 'Agent handoffs & status'}</strong>
    </div>
    {saved ? <div className="ca-work-agent-pips" aria-label="Recorded agent states">
      {agents.map(item => <span key={item.agent.id}
        className={'ca-work-agent-pip' + (item.active ? ' is-active' : '')}
        title={item.agent.role + ': ' + item.detail}>
        <span aria-hidden="true">{item.agent.role.split(' ').map(word =>
          word[0]).join('').slice(0, 2)}</span>
        {item.agent.role}
        <small>{item.active ? 'Running' : '—'}</small>
      </span>)}
    </div> : <span className="ca-work-team-empty" role="status">
      {failed ? 'Saved agent status unavailable' : 'Reading saved agent activity…'}</span>}
    <div className="ca-work-team-handoff">
      {latest ? <span title={latest.kind + ' · ' + latest.workItemId}>
        {latest.fromAgent} → {latest.toAgent}
      </span> : <span>No recorded handoff</span>}
    </div>
    <button type="button" className="outline" onClick={onOpen}>
      Open Team Console
    </button>
  </section>;
}
