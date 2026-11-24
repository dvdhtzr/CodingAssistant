import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { InterviewStudio } from './InterviewStudio';
import { AgentRoleModels, type AgentRoleSelection } from './AgentRoleModels';
import { studioAgentPresence, studioRecordedActivity } from './StudioPresence';
import { api, type TaskItem } from './api';
import type { AgentIdentity } from './AgentIdentityPanel';
import type { EngineerSnapshot } from './EngineeringWorkspace';
import { buildDecisionInbox, type DecisionInboxSnapshot } from './OwnerDecisionInbox';
import type { JourneyId } from './ProjectJourney';
import type { TeamHandoff, TeamQaSnapshot } from './TeamWorkroom';

interface StudioQaSnapshot extends TeamQaSnapshot {
  job: TeamQaSnapshot['job'] & { model?: string };
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
  const [roleModelsOpen, setRoleModelsOpen] = useState(false);
  const [models, setModels] = useState<AgentRoleSelection[] | null>(null);
  const [modelsFailed, setModelsFailed] = useState(false);
  const [modelVersion, setModelVersion] = useState(0);
  const [inspectedAgentId, setInspectedAgentId] = useState<string | null>(null);
  const [sideView, setSideView] = useState<'attention' | 'activity' | 'specialist'>('attention');
  const [request, setRequest] = useState<{ stage: JourneyId; revision: number } | null>(null);
  const canvasRef = useRef<HTMLElement | null>(null);
  const navigate = useCallback((stage: JourneyId) => {
    setSelectedStage(stage);
    setRequest(previous => ({ stage, revision: (previous?.revision ?? 0) + 1 }));
    // This is a locally scrollable canvas: never leave the next stage halfway
    // down a previous stage, and never change the document's scroll position.
    if (canvasRef.current) canvasRef.current.scrollTop = 0;
  }, []);
  const openRoleModels = useCallback(() => {
    setRoleModelsOpen(true);
    if (canvasRef.current) canvasRef.current.scrollTop = 0;
  }, []);
  useEffect(() => {
    // Also covers stage transitions initiated INSIDE the original work forms
    // (e.g. owner approval), without reacting to polling or status refreshes.
    if (canvasRef.current) canvasRef.current.scrollTop = 0;
  }, [selectedStage]);

  useEffect(() => {
    setInspectedAgentId(null);
    setSideView('attention');
    setModels(null);
  }, [task.id]);

  useEffect(() => {
    let closed = false;
    let pending = false;
    async function load() {
      if (closed || pending) return;
      pending = true;
      try {
        const result = await api<{ agents: AgentRoleSelection[] }>(token,
          '/api/tasks/' + task.id + '/agent-models');
        if (!closed) { setModels(result.agents); setModelsFailed(false); }
      } catch {
        if (!closed) setModelsFailed(true);
      } finally { pending = false; }
    }
    void load();
    const interval = window.setInterval(() => void load(), 15000);
    return () => { closed = true; window.clearInterval(interval); };
  }, [token, task.id, modelVersion]);

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
        } else { setTeamError(true); }
        if (owner.status === 'fulfilled') {
          const [interview, architecture, plan] = owner.value;
          setDecisions({ specification: interview.specification,
            readyForSpecification: interview.readyForSpecification,
            ownerAnswers: interview.messages.filter(message => message.role === 'owner').length,
            decisions: architecture.decisions, plan: plan.plan });
          setDecisionError(false);
        } else { setDecisionError(true); }
      } finally { loading = false; }
    }
    void load();
    const interval = window.setInterval(() => void load(), 6000);
    return () => { closed = true; window.clearInterval(interval); };
  }, [token, task.id]);

  const status = useMemo(() => studioAgentPresence(saved, models), [saved, models]);
  const activity = useMemo(() => studioRecordedActivity(saved), [saved]);
  const inspected = status.find(item => item.agent.id === inspectedAgentId) ??
    status.find(item => item.active) ?? status[0];
  const relatedActivity = inspected ? activity.filter(item =>
    item.roleIds.includes(inspected.agent.id)) : [];
  const attention = useMemo(() => studioAttention(saved, decisions), [saved, decisions]);
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
      <div className="ca-studio-header-actions">
        <button type="button" className="outline ca-studio-model-button"
          aria-expanded={roleModelsOpen} aria-controls="ca-studio-model-panel"
          onClick={() => roleModelsOpen ? setRoleModelsOpen(false) : openRoleModels()}>
          {roleModelsOpen ? 'Hide team models' : 'Assign team models'}
        </button>
        <span className={'ca-studio-run-state' + (activeQa || activeEngineer ? ' is-working' : '')}>
          <span aria-hidden="true" className="ca-studio-state-dot" />{working}
        </span>
      </div>
    </header>
    <div className="ca-studio-layout">
      <aside className="ca-studio-people" role="complementary" aria-label="Engineering team">
        <div className="ca-studio-rail-heading">
          <span className="ca-studio-eyebrow">YOUR TEAM</span>
          <button type="button" className="ca-studio-rail-models"
            onClick={openRoleModels}>Models</button>
        </div>
        {teamError && <p role="status" className="ca-studio-muted">
          Team refresh unavailable. Previously saved evidence remains visible.</p>}
        {!saved && !teamError && <p className="ca-studio-muted">Reading saved team…</p>}
        {status.map(item => {
          const stage = stages[item.agent.id];
          return <button key={item.agent.id} type="button"
            className={'ca-studio-person' +
              (item.active ? ' is-working' : '') +
              (stage === selectedStage ? ' is-selected' : '')}
            onClick={() => {
              if (stage) navigate(stage);
              setInspectedAgentId(item.agent.id);
              setSideView('specialist');
            }}
            disabled={!stage}
            aria-pressed={stage === selectedStage}
            data-agent={item.agent.id} data-activity={item.state}>
            <span className="ca-studio-avatar" aria-hidden="true">
              {item.agent.role.split(' ').map(word => word[0]).join('').slice(0, 2)}
            </span>
            <span className="ca-studio-person-copy">
              <strong>{item.agent.role}</strong>
              <small className="ca-studio-person-status">{item.status}</small>
              <small className="ca-studio-person-model" title={
                item.modelForCurrentRun ? 'Model recorded on the active job' :
                  'Model selected for new work'}>
                {item.modelForCurrentRun ? 'Running · ' + item.modelForCurrentRun :
                  item.modelForNextRun ? 'Next · ' + item.modelForNextRun :
                    'Model not loaded'}
              </small>
            </span>
            <span className={'ca-studio-person-dot' + (item.active ? ' is-active' : '')}
              aria-label={item.active ? 'Recorded running activity' :
                item.state === 'stopped' ? 'Recorded stopped work' :
                  'No active run recorded'} />
          </button>;
        })}
        {modelsFailed && <p role="status" className="ca-studio-muted">
          Model assignments could not be refreshed.</p>}
        <div className="ca-studio-rail-footer">
          <span className="ca-studio-eyebrow">RECORDED WORK ONLY</span>
          <p>{activity.length} saved event{activity.length === 1 ? '' : 's'} in
            recent source, QA and handoff ledgers.</p>
          <button type="button" className="ca-studio-text-action"
            onClick={() => setSideView('activity')}>View activity</button>
          <button type="button" className="ca-studio-text-action"
            onClick={onTeam}>Full team details</button>
        </div>
      </aside>
      <section ref={canvasRef} className="ca-studio-canvas"
        aria-label="Focused engineering work">
        {roleModelsOpen && <div id="ca-studio-model-panel">
          <AgentRoleModels token={token} taskId={task.id}
            onClose={() => setRoleModelsOpen(false)}
            onSaved={() => setModelVersion(value => value + 1)} />
        </div>}
        <InterviewStudio token={token} task={task}
          studioCanvas focusRequest={request} onStageSelected={setSelectedStage} />
      </section>
      <aside className="ca-studio-context" role="complementary"
        aria-label="Owner attention and handoffs">
        <div role="group" className="ca-studio-side-tabs" aria-label="Team context view">
          <button type="button" aria-pressed={sideView === 'attention'}
            onClick={() => setSideView('attention')}>Attention
            {attention.length > 0 && <span> {attention.length}</span>}
          </button>
          <button type="button" aria-pressed={sideView === 'activity'}
            onClick={() => setSideView('activity')}>Activity</button>
          <button type="button" aria-pressed={sideView === 'specialist'}
            disabled={!inspected}
            onClick={() => setSideView('specialist')}>Specialist</button>
        </div>
        {sideView === 'attention' && <section aria-label="Owner decisions">
          <div className="ca-studio-context-heading">
            <span className="ca-studio-eyebrow">YOUR ATTENTION</span>
            <strong>{attention.length ? 'Next decisions' : 'No pending decisions'}</strong>
          </div>
          {decisionError && <p role="status" className="ca-studio-muted">
            Approval refresh unavailable. Existing approvals are not changed.</p>}
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
            No current approvals are waiting. Source still requires independent verification.</p>}
          <div className="ca-studio-activity-teaser">
            <span className="ca-studio-eyebrow">LAST SAVED WORK</span>
            {activity.length ? <p><strong>{activity[0].title}</strong>
              <small>{activity[0].detail}</small></p> :
              <p className="ca-studio-muted">No recorded work yet.</p>}
            <button type="button" className="ca-studio-text-action"
              onClick={() => setSideView('activity')}>Explore activity</button>
          </div>
        </section>}
        {sideView === 'activity' && <section aria-label="Recorded team activity">
          <div className="ca-studio-context-heading">
            <span className="ca-studio-eyebrow">TEAM COLLABORATION</span>
            <strong>Recorded activity</strong>
            <p className="ca-studio-muted">Saved handoffs, engineering task receipts and
              independent QA events. Not a simulated agent conversation.</p>
          </div>
          {activity.length ? <ol className="ca-studio-event-list">
            {activity.slice(0, 12).map(entry => <li key={entry.id}
              data-historical={entry.historical}>
              <span className="ca-studio-event-time">
                <time dateTime={entry.at}>{new Date(entry.at).toLocaleTimeString([], {
                  hour: 'numeric', minute: '2-digit' })}</time>
                {entry.historical && <span>Historical</span>}
              </span>
              <strong>{entry.title}</strong>
              <p>{entry.detail}</p>
              <button type="button" className="ca-studio-text-action"
                onClick={() => navigate(entry.stage)}>Inspect work</button>
            </li>)}
          </ol> : <p className="ca-studio-muted">No activity receipts have been saved.</p>}
          {activity.length > 12 && <p className="ca-studio-muted">
            Showing 12 most recent receipts. Full history is available in Team.</p>}
          <button type="button" className="ca-studio-text-action"
            onClick={onTeam}>Open complete team ledger</button>
        </section>}
        {sideView === 'specialist' && <section aria-label="Selected specialist">
          {inspected ? <>
            <div className="ca-studio-specialist-head">
              <span className="ca-studio-avatar" aria-hidden="true">
                {inspected.agent.role.split(' ').map(word => word[0]).join('').slice(0, 2)}
              </span>
              <div><span className="ca-studio-eyebrow">SELECTED SPECIALIST</span>
                <h2>{inspected.agent.role}</h2></div>
            </div>
            <p className="ca-studio-role-state" data-state={inspected.state}>
              {inspected.status}</p>
            <p className="ca-studio-specialist-goal">{inspected.agent.goal}</p>
            <div className="ca-studio-specialist-facts">
              <span>MODEL FOR NEW WORK</span>
              <strong>{inspected.modelForNextRun ?? 'Model preference unavailable'}</strong>
              {inspected.modelForCurrentRun && <>
                <span>MODEL PINNED TO ACTIVE WORK</span>
                <strong>{inspected.modelForCurrentRun}</strong>
              </>}
              <span>CURRENT SAVED EVIDENCE</span>
              <p>{inspected.assignment}</p>
              <span>RECORDED HANDOFFS</span>
              <p>{inspected.handoffCount} saved</p>
            </div>
            <details className="ca-studio-specialist-story">
              <summary>Working style & background</summary>
              <p>{inspected.agent.workingStyle}</p>
              <p>{inspected.agent.backstory}</p>
              <small>Fictional role background, not a record of actual work.</small>
            </details>
            <div className="ca-studio-specialist-actions">
              <button type="button" className="ca-studio-text-action"
                onClick={() => { if (stages[inspected.agent.id])
                  navigate(stages[inspected.agent.id]); }}>Open role work</button>
              {inspected.agent.id !== 'ProductOwner' &&
                <button type="button" className="ca-studio-text-action"
                  onClick={openRoleModels}>Change model</button>}
            </div>
            <div className="ca-studio-specialist-activity">
              <span className="ca-studio-eyebrow">SPECIALIST RECORDS</span>
              {relatedActivity.length ? <ol>{relatedActivity.slice(0, 4).map(item =>
                <li key={item.id}><strong>{item.title}</strong>
                  <small>{item.detail}</small></li>)}</ol> :
                <p className="ca-studio-muted">No work receipts involving this specialist.</p>}
            </div>
            <p className="ca-studio-verification">Executable tools: {inspected.agent.canExecuteCode ?
              'Requires host authorization' : 'Not authorized'}.
              Identity and personality do not grant execution rights.</p>
          </> : <p className="ca-studio-muted">Select a team member to inspect.</p>}
        </section>}
        <p className="ca-studio-verification">Saved source and static QA are not
          independently built or verified. Runtime testing requires separate approval.</p>
      </aside>
    </div>
  </section>;
}
