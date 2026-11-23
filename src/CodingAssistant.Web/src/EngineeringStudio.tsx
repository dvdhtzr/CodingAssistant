import { useCallback, useEffect, useMemo, useState } from 'react';
import { InterviewStudio } from './InterviewStudio';
import { api, type TaskItem } from './api';
import type { AgentIdentity } from './AgentIdentityPanel';
import type { EngineerSnapshot } from './EngineeringWorkspace';
import { buildDecisionInbox, type DecisionInboxSnapshot } from './OwnerDecisionInbox';
import { workAgentStatuses, type WorkTeamEvidence } from './WorkTeamStrip';
import type { JourneyId } from './ProjectJourney';
import type { TeamHandoff, TeamQaSnapshot } from './TeamWorkroom';

interface StudioQaSnapshot extends TeamQaSnapshot {
  reports: Array<{ path: string; reviewedUtc: string;
    assessment?: { findings?: Array<{ severity: string; requirement: string }> } }>;
  events?: Array<{ sequence: number; kind: string; detail: string; createdUtc: string }>;
}
interface StudioRepair {
  cycle: { state: string; attempt: number; errorCode: string | null };
}
export interface StudioEvidence {
  agents: AgentIdentity[];
  handoffs: TeamHandoff[];
  engineering: EngineerSnapshot | null;
  qa: StudioQaSnapshot | null;
  repair: StudioRepair | null;
}
export interface StudioFocus {
  stage: JourneyId;
  title: string;
  detail: string;
  kind: 'review' | 'warning' | 'next';
}

/** Never offer an approval or repair action. The existing hash-bound forms own all writes. */
export function studioAttention(saved: StudioEvidence | null,
  decisions: DecisionInboxSnapshot | null): StudioFocus[] {
  const items: StudioFocus[] = [];
  if (decisions) {
    for (const item of buildDecisionInbox(decisions).slice(0, 2)) {
      items.push({ stage: item.stage, title: item.title, detail: item.detail,
        kind: item.kind === 'review' ? 'review' : item.kind === 'stale' ? 'warning' : 'next' });
    }
  }
  if (!saved) return items;
  const eng = saved.engineering?.job;
  const qa = saved.qa;
  const currentQa = !!eng && !!qa &&
    qa.job.engineeringRunId === eng.id &&
    qa.job.planSha256 === eng.planSha256;
  const findings = currentQa ? qa.reports.reduce((count, report) =>
    count + (report.assessment?.findings?.length ?? 0), 0) : 0;
  if (saved.repair?.cycle.state === 'Failed' ||
    saved.repair?.cycle.state === 'Interrupted') {
    items.unshift({ stage: 'delivery', kind: 'warning',
      title: 'Repair cycle ' + saved.repair.cycle.attempt + ' stopped',
      detail: saved.repair.cycle.errorCode ?? 'Inspect the saved repair history; prior source is preserved.' });
  }
  if (currentQa && qa.job.state === 'Reviewed' && findings > 0) {
    items.unshift({ stage: 'delivery', kind: 'review',
      title: findings + ' potential QA finding' + (findings === 1 ? '' : 's'),
      detail: 'Inspect current source evidence and select only confirmed defects for a separately approved repair.' });
  } else if (eng && (eng.state === 'Failed' || eng.state === 'Interrupted') &&
    saved.repair?.cycle.state !== 'Running' &&
    saved.repair?.cycle.state !== 'QaRunning') {
    items.push({ stage: 'generation', kind: 'warning',
      title: 'Engineering stopped with saved work',
      detail: eng.errorCode ?? 'Inspect the stopped assignment and available source before resuming.' });
  }
  return items.slice(0, 3);
}

const stages: Record<string, JourneyId> = {
  ProductOwner: 'discovery', SolutionArchitect: 'architecture',
  TechnicalLead: 'plan', SoftwareEngineer: 'generation', QaEngineer: 'delivery',
};

export function EngineeringStudio({ token, task, onTeam }: {
  token: string; task: TaskItem; onTeam: () => void;
}) {
  const [saved, setSaved] = useState<StudioEvidence | null>(null);
  const [decisions, setDecisions] = useState<DecisionInboxSnapshot | null>(null);
  const [teamError, setTeamError] = useState(false);
  const [decisionError, setDecisionError] = useState(false);
  const [selectedStage, setSelectedStage] = useState<JourneyId>('discovery');
  const [request, setRequest] = useState<{ stage: JourneyId; revision: number } | null>(null);
  const navigate = useCallback((stage: JourneyId) => {
    setSelectedStage(stage);
    setRequest(previous => ({ stage, revision: (previous?.revision ?? 0) + 1 }));
  }, []);

  useEffect(() => {
    let closed = false;
    let loading = false;
    setSaved(null); setDecisions(null); setTeamError(false); setDecisionError(false);
    async function load() {
      if (closed || loading) return;
      loading = true;
      const root = '/api/tasks/' + task.id;
      try {
        const [team, owner] = await Promise.allSettled([
          Promise.all([
            api<{ agents: AgentIdentity[] }>(token, '/api/agents/roster'),
            api<{ events: TeamHandoff[] }>(token, root + '/agent-handoffs'),
            api<{ snapshot: EngineerSnapshot | null }>(token, root + '/engineering-runs/latest'),
            api<{ snapshot: StudioQaSnapshot | null }>(token, root + '/qa-reviews/latest'),
            api<{ snapshot: StudioRepair | null }>(token, root + '/repair-cycles/latest'),
          ]),
          Promise.all([
            api<{ specification: DecisionInboxSnapshot['specification'];
              readyForSpecification: boolean; messages: { role: string }[] }>(token,
              root + '/interview'),
            api<{ decisions: DecisionInboxSnapshot['decisions'] }>(token,
              root + '/architecture-decisions'),
            api<{ plan: DecisionInboxSnapshot['plan'] }>(token,
              root + '/implementation-plan'),
          ]),
        ]);
        if (closed) return;
        if (team.status === 'fulfilled') {
          const [roster, handoffs, engineering, qa, repair] = team.value;
          setSaved({ agents: roster.agents, handoffs: handoffs.events,
            engineering: engineering.snapshot, qa: qa.snapshot, repair: repair.snapshot });
          setTeamError(false);
        } else { setSaved(null); setTeamError(true); }
        if (owner.status === 'fulfilled') {
          const [interview, architecture, plan] = owner.value;
          setDecisions({ specification: interview.specification,
            readyForSpecification: interview.readyForSpecification,
            ownerAnswers: interview.messages.filter(message => message.role === 'owner').length,
            decisions: architecture.decisions, plan: plan.plan });
          setDecisionError(false);
        } else { setDecisions(null); setDecisionError(true); }
      } finally { loading = false; }
    }
    void load();
    const interval = window.setInterval(() => void load(), 6000);
    return () => { closed = true; window.clearInterval(interval); };
  }, [token, task.id]);

  const status = useMemo(() => saved ? workAgentStatuses(saved as WorkTeamEvidence) : [],
    [saved]);
  const attention = useMemo(() => studioAttention(saved, decisions), [saved, decisions]);
  const handoffs = useMemo(() => saved?.handoffs.slice()
    .sort((a, b) => b.createdUtc.localeCompare(a.createdUtc)).slice(0, 3) ?? [], [saved]);
  const latest = saved?.repair?.cycle;
  const activeQa = saved?.qa?.job.state === 'Running' || latest?.state === 'QaRunning';
  const activeEngineer = saved?.engineering?.job.state === 'Running' || latest?.state === 'Running';
  const working = activeQa ? 'QA Engineer reviewing' :
    activeEngineer ? 'Software Engineer working' : 'No active agent run recorded';
  const currentActivity = saved?.engineering?.tasks.find(item => item.state === 'Running');
  return <section className="ca-studio" aria-label="AI Engineering Studio">
    <header className="ca-studio-header">
      <div className="ca-studio-heading">
        <span className="ca-studio-eyebrow">ENGINEERING STUDIO / {task.title}</span>
        <h1>Your team at work</h1>
        <p>{activeEngineer && currentActivity ? currentActivity.title :
          activeQa ? 'Independent static review of saved source' :
            'Choose a specialist or work stage. All progress comes from saved records.'}</p>
      </div>
      <span className={'ca-studio-run-state' + (activeQa || activeEngineer ? ' is-working' : '')}>
        <span aria-hidden="true" className="ca-studio-state-dot" />{working}
      </span>
    </header>
    <div className="ca-studio-layout">
      <aside className="ca-studio-people" aria-label="Engineering team">
        <div className="ca-studio-rail-heading">
          <span className="ca-studio-eyebrow">YOUR TEAM</span>
          <button type="button" className="ca-studio-text-action" onClick={onTeam}>Team details</button>
        </div>
        {teamError && <p role="status" className="ca-studio-muted">
          Team records are unavailable. Work controls remain accessible.</p>}
        {!saved && !teamError && <p className="ca-studio-muted">Loading saved team…</p>}
        {status.map(({ agent, active, detail }) => {
          const stage = stages[agent.id];
          return <button key={agent.id} type="button" className={'ca-studio-person' +
            (active ? ' is-working' : '') + (stage === selectedStage ? ' is-selected' : '')}
            onClick={() => stage && navigate(stage)} disabled={!stage}
            aria-pressed={stage === selectedStage} data-agent={agent.id}>
            <span className="ca-studio-avatar" aria-hidden="true">
              {agent.role.split(' ').map(word => word[0]).join('').slice(0, 2)}
            </span>
            <span className="ca-studio-person-copy">
              <strong>{agent.role}</strong>
              <small>{active ? detail : agent.id === 'SoftwareEngineer' ||
                agent.id === 'QaEngineer' ? detail : 'Available for planned work'}</small>
            </span>
            <span className={'ca-studio-person-dot' + (active ? ' is-active' : '')}
              aria-label={active ? 'Recorded running activity' : 'No active run recorded'} />
          </button>;
        })}
        <div className="ca-studio-rail-footer">
          <span className="ca-studio-eyebrow">SOURCE OF TRUTH</span>
          <p>Saved work, approvals and handoffs. No simulated agent activity.</p>
        </div>
      </aside>
      <section className="ca-studio-canvas" aria-label="Focused engineering work">
        <InterviewStudio token={token} task={task}
          studioCanvas focusRequest={request} onStageSelected={setSelectedStage} />
      </section>
      <aside className="ca-studio-context" aria-label="Owner attention and handoffs">
        <div className="ca-studio-context-heading">
          <span className="ca-studio-eyebrow">YOUR ATTENTION</span>
          <strong>{attention.length ? 'Next decisions' : 'No pending decisions'}</strong>
        </div>
        {decisionError && <p role="status" className="ca-studio-muted">
          Approval records could not be read. No approval status is assumed.</p>}
        {!decisions && !decisionError && <p className="ca-studio-muted">
          Reading saved approvals…</p>}
        {attention.length ? <ul className="ca-studio-attention-list">
          {attention.map((item, index) => <li key={item.stage + ':' + item.title + ':' + index}
            data-kind={item.kind}>
            <span className="ca-studio-attention-type">{item.kind === 'review' ?
              'OWNER REVIEW' : item.kind === 'warning' ? 'NEEDS INSPECTION' : 'NEXT STEP'}</span>
            <strong>{item.title}</strong>
            <p>{item.detail}</p>
            <button type="button" onClick={() => navigate(item.stage)}>
              Open {item.stage === 'delivery' ? 'QA & source' :
                item.stage === 'generation' ? 'Engineering' : 'work stage'}
            </button>
          </li>)}
        </ul> : decisions && <p className="ca-studio-muted">
          No current approval is waiting. Generated files still require independent verification.</p>}
        <div className="ca-studio-handoffs">
          <span className="ca-studio-eyebrow">RECORDED HANDOFFS</span>
          {handoffs.length ? <ol>{handoffs.map((event, i) => <li
            key={event.createdUtc + ':' + event.kind + ':' + i}>
            <span>{event.fromAgent} → {event.toAgent}</span>
            <small>{event.kind}{event.current ? '' : ' · historical'}</small>
          </li>)}</ol> : <p className="ca-studio-muted">
            {saved ? 'No handoffs recorded for this project.' : 'No verified handoff data.'}</p>}
        </div>
        <p className="ca-studio-verification">Source generation and static QA do not
          establish a verified build. Execution requires separate host qualification and approval.</p>
      </aside>
    </div>
  </section>;
}
