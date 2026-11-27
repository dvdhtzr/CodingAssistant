import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { InterviewStudio } from './InterviewStudio';
import { DeliveryEvidencePanel } from './DeliveryEvidencePanel';
import { EngineeringActivityFeed } from './EngineeringActivityFeed';
import { LeadReviewPanel, type LeadReviewSnapshot } from './LeadReviewPanel';
import { CollaborationPanel, type CollaborationSnapshot } from './CollaborationPanel';
import { StudioLiveActivity } from './StudioLiveActivity';
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
  cycle: { state: string; attempt: number; errorCode: string | null; engineeringRunId: string };
}
export interface StudioEvidence {
  agents: AgentIdentity[];
  handoffs: TeamHandoff[];
  engineering: EngineerSnapshot | null;
  qa: StudioQaSnapshot | null;
  lead?: LeadReviewSnapshot | null;
  collaboration?: CollaborationSnapshot | null;
  repair: StudioRepair | null;
}
export interface StudioFocus {
  stage: JourneyId;
  title: string;
  detail: string;
  kind: 'review' | 'warning' | 'next';
  specialistId?: string;
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
  const coordination = saved.collaboration?.job;
  if (coordination?.state === 'AwaitingOwner' && coordination.reason === 'EXACT_REPAIR_APPROVAL_REQUIRED' && coordination.runId === eng?.id)
    items.unshift({ stage: 'plan', kind: 'review', title: 'Technical Lead requests scoped repair', specialistId: 'TechnicalLead',
      detail: 'Open the Technical Lead workstation to inspect the saved coordination decision.' });
  const currentRepair = saved.repair?.cycle.engineeringRunId === eng?.id ? saved.repair?.cycle : null;
  if (currentRepair?.state === 'Failed' || currentRepair?.state === 'Interrupted') {
    items.unshift({ stage: 'delivery', kind: 'warning',
      title: 'Repair cycle ' + currentRepair.attempt + ' stopped',
      detail: currentRepair.errorCode ?? 'Inspect the saved repair history; prior source is preserved.' });
  }
  if (currentQa && qa.job.state === 'Reviewed' && findings > 0) {
    items.unshift({ stage: 'delivery', kind: 'review',
      title: findings + ' potential QA finding' + (findings === 1 ? '' : 's'),
      detail: 'Inspect current source evidence and select only confirmed defects for a separately approved repair.' });
  } else if (eng?.state === 'Generated' && !currentQa) {
    items.unshift({ stage: 'delivery', kind: 'next', title: 'New source ready for QA',
      detail: 'Review the current saved source, not earlier repair history.' });
  } else if (eng && (eng.state === 'Failed' || eng.state === 'Interrupted') &&
    currentRepair?.state !== 'Running' &&
    currentRepair?.state !== 'QaRunning') {
    items.push({ stage: 'generation', kind: 'warning',
      title: 'Engineering stopped with saved work',
      detail: eng.errorCode ?? 'Inspect the stopped assignment and available source before resuming.' });
  }
  return items.slice(0, 3);
}

const stages: Record<string, JourneyId> = {
  ProductOwner: 'discovery', SolutionArchitect: 'architecture',
  TechnicalLead: 'plan', SoftwareEngineer: 'generation', LeadSoftwareEngineer: 'delivery', QaEngineer: 'delivery',
};

export function EngineeringStudio({ token, task, onTeam, active = true }: {
  token: string; task: TaskItem; onTeam: () => void; active?: boolean;
}) {
  const [detailed, setDetailed] = useState(false);
  const [guidedDecisionOpen, setGuidedDecisionOpen] = useState(false);
  const [activityOpen, setActivityOpen] = useState(false);
  const [saved, setSaved] = useState<StudioEvidence | null>(null);
  const [upfront,setUpfront]=useState<{state:string;runId:string|null;planRevision:number;planSha256:string;reason:string|null;boundPolicySha256:string|null}|null>(null);
  const [decisions, setDecisions] = useState<DecisionInboxSnapshot | null>(null);
  const [teamError, setTeamError] = useState(false);
  const [decisionError, setDecisionError] = useState(false);
  const [selectedStage, setSelectedStage] = useState<JourneyId>('discovery');
  const [roleModelsOpen, setRoleModelsOpen] = useState(false);
  const [models, setModels] = useState<AgentRoleSelection[] | null>(null);
  const [modelsFailed, setModelsFailed] = useState(false);
  const [modelVersion, setModelVersion] = useState(0);
  const [inspectedAgentId, setInspectedAgentId] = useState<string | null>(null);
  const [sideView, setSideView] = useState<'attention' | 'activity' | 'specialist' | 'delivery'>('attention');
  const [request, setRequest] = useState<{ stage: JourneyId; revision: number } | null>(null);
  const canvasRef = useRef<HTMLElement | null>(null);
  const navigate = useCallback((stage: JourneyId) => {
    setDetailed(true);
    setGuidedDecisionOpen(false);
    setSelectedStage(stage);
    setRequest(previous => ({ stage, revision: (previous?.revision ?? 0) + 1 }));
    // This is a locally scrollable canvas: never leave the next stage halfway
    // down a previous stage, and never change the document's scroll position.
    if (canvasRef.current) canvasRef.current.scrollTop = 0;
  }, []);
  const openRoleModels = useCallback(() => {
    setDetailed(true);
    setRoleModelsOpen(true);
    if (canvasRef.current) canvasRef.current.scrollTop = 0;
  }, []);
  useEffect(() => {
    // Also covers stage transitions initiated INSIDE the original work forms
    // (e.g. owner approval), without reacting to polling or status refreshes.
    if (canvasRef.current) canvasRef.current.scrollTop = 0;
  }, [selectedStage]);

  useEffect(() => {
    setDetailed(false);
    setGuidedDecisionOpen(false);
    setActivityOpen(false);
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
    setSaved(null); setUpfront(null); setDecisions(null); setTeamError(false); setDecisionError(false);
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
            api<{ snapshot: LeadReviewSnapshot | null }>(token, root + '/lead-reviews/latest').catch(() => ({ snapshot: null })),
            api<{ snapshot: StudioRepair | null }>(token, root + '/repair-cycles/latest'),
            api<{ snapshot: CollaborationSnapshot | null }>(token, root + '/collaboration/latest'),
            api<{authorization:typeof upfront}>(token,root+'/upfront-team-authorization'),
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
          const [roster, handoffs, engineering, qa, lead, repair, collaboration, authorization] = team.value;
          setUpfront(authorization.authorization);
          setSaved({ agents: roster.agents, handoffs: handoffs.events,
            engineering: engineering.snapshot, qa: qa.snapshot, lead: lead.snapshot, repair: repair.snapshot, collaboration: collaboration.snapshot });
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
  const engId = saved?.engineering?.job.id;
  const latest = saved?.repair?.cycle.engineeringRunId === engId ? saved?.repair?.cycle : null;
  const activeQa = (saved?.qa?.job.engineeringRunId === engId && saved?.qa?.job.planSha256 === saved?.engineering?.job.planSha256 && saved?.qa?.job.state === 'Running') || latest?.state === 'QaRunning';
  const activeEngineer = saved?.engineering?.job.state === 'Running' || latest?.state === 'Running';
  const activeLead = saved?.lead?.job.state === 'Running';
  const coordinating = saved?.collaboration?.job.state === 'Running' ||
    (saved?.collaboration?.job.state === 'AwaitingOwner' &&
      saved.collaboration.job.reason === 'ROUTINE_REPAIR_DISPATCH_PENDING' &&
      saved.collaboration.job.runId === saved.engineering?.job.id);
  const working = activeLead ? 'Lead Software Engineer reviewing' : activeQa ? 'QA Engineer reviewing' :
    activeEngineer ? 'Software Engineer working' : coordinating ? 'Technical Lead coordinating' : 'No active agent run recorded';
  const currentActivity = saved?.engineering?.tasks.find(item => item.state === 'Running');
  const nextDecision = decisions ? buildDecisionInbox(decisions)[0] : undefined;
  const source = saved?.engineering?.job;
  const activeWork = activeLead || activeQa || activeEngineer || coordinating;
  const upfrontHandoff=!!source&&upfront?.runId===source.id&&
    (upfront.state==='Claimed'||(upfront.state==='Bound'&&
      saved?.collaboration?.job.policySha256!==upfront.boundPolicySha256));
  const currentCoordination=saved?.collaboration?.job;
  const coordinationStopped=!!source && currentCoordination?.runId===source.id &&
    currentCoordination.planSha256===source.planSha256 &&
    (currentCoordination.state==='Escalated'||currentCoordination.state==='Failed');
  const awaitingVerification=!!source && currentCoordination?.runId===source.id &&
    currentCoordination.planSha256===source.planSha256 && currentCoordination.state==='AwaitingVerification';
  const canCoordinate = !awaitingVerification && !upfrontHandoff && !teamError && !decisionError && !!decisions && !nextDecision &&
    (source?.state === 'Generated' || !!saved?.collaboration ||
      ((source?.state === 'Failed' || source?.state === 'Interrupted') &&
        latest?.engineeringRunId === source.id && !!saved?.engineering?.files.length));
  const stopped = source?.state === 'Failed' || source?.state === 'Interrupted';
  const [starting, setStarting] = useState(false);
  const [stopping, setStopping] = useState(false);
  const completedAssignments=saved?.engineering?.tasks.filter(item=>item.state==='Completed').length??0;
  const totalAssignments=saved?.engineering?.tasks.length??0;
  const openGuidedDecision=(stage:JourneyId)=>{
    setRequest(previous=>({stage,revision:(previous?.revision??0)+1}));
    setGuidedDecisionOpen(true);
  };
  const onGuidedStageSelected=useCallback((stage:JourneyId)=>{
    // Existing forms advance only after a saved approval. Do not launch a
    // build or expose expert source generation automatically.
    if(stage==='generation'||stage==='delivery')setGuidedDecisionOpen(false);
  },[]);
  const [startError, setStartError] = useState('');
  async function startApprovedWork() {
    if (starting || !decisions?.plan || nextDecision || source || teamError || decisionError) return;
    setStarting(true);setStartError('');
    try {
      const root='/api/tasks/'+task.id;
      const latest=await api<{plan:{revision:number;planSha256:string;status:string;current:boolean}|null}>(token,root+'/implementation-plan');
      const plan=latest.plan;
      if (!plan?.current || plan.status!=='Approved' || plan.revision!==decisions.plan.revision)
        throw Error('The approved plan has changed. Review the current plan.');
      const {authorization}=await api<{authorization:{state:string;planRevision:number;planSha256:string}|null}>(token,root+'/upfront-team-authorization');
      const useApprovedTeamAuthorization=authorization?.state==='Pending'&&
        authorization.planRevision===plan.revision&&authorization.planSha256===plan.planSha256;
      const job=await api<EngineerSnapshot['job']>(token,root+'/engineering-runs',
        {planRevision:plan.revision,planSha256:plan.planSha256,useApprovedTeamAuthorization});
      setSaved(previous=>previous?{...previous,engineering:{job,tasks:[],events:[],files:[]}}:previous);
    } catch (cause) {setStartError(cause instanceof Error?cause.message:'Could not start approved work.');}
    finally {setStarting(false);}
  }
  async function stopInitialEngineering() {
    if(stopping||!source||(source.state!=='Running'&&!upfrontHandoff)||canCoordinate||teamError)return;
    setStopping(true);setStartError('');
    try {
      const updated=await api<EngineerSnapshot>(token,'/api/tasks/'+task.id+'/engineering-runs/'+source.id+'/cancel',{});
      setSaved(previous=>previous?.engineering?.job.id===source.id?{...previous,engineering:updated}:previous);
    } catch(cause){setStartError(cause instanceof Error?cause.message:'Could not stop engineering.');}
    finally{setStopping(false);}
  }
  if (!detailed) return <section className="ca-studio ca-guided-studio" aria-label="AI Engineering Studio">
    <header className="ca-studio-header">
      <div className="ca-studio-heading">
        <span className="ca-studio-eyebrow">YOUR PROJECT</span>
        <h1>{task.title}</h1>
        <p>{activeWork?working:'Supervise your team, not each individual handoff.'}</p>
      </div>
      {guidedDecisionOpen?<button type="button" className="outline" onClick={()=>setGuidedDecisionOpen(false)}>Back to project overview</button>:
        <button type="button" className="outline" onClick={()=>setDetailed(true)}>Inspect workspaces</button>}
    </header>
    <main className="ca-guided-main" aria-label="Project supervision">
      {guidedDecisionOpen?<div className="ca-guided-content ca-guided-decision">
        <InterviewStudio token={token} task={task} studioCanvas guidedOwner
          focusRequest={request} onStageSelected={onGuidedStageSelected}/>
      </div>:<div className="ca-guided-content">
        <div className="ca-guided-roster" aria-label="Recorded team status">
          {status.map(item=><div key={item.agent.id} data-active={item.active} className="ca-guided-role">
            <span className="ca-studio-avatar" aria-hidden="true">{item.agent.role.split(' ').map(w=>w[0]).join('').slice(0,2)}</span>
            <strong>{item.agent.role}</strong><small>{item.status}</small>
          </div>)}
        </div>
        <section className="ca-guided-next" aria-label="Next project action">
          {teamError||decisionError ? <><h2>Unable to refresh project status</h2><p>Wait for current saved evidence before starting new work.</p></> :
            !saved||!decisions ? <p role="status">Reading saved project state…</p> :
            upfrontHandoff&&source?.state==='Generated' ? <><h2>Starting independent review</h2><p>The host is binding the saved source to your approved repair scope. No additional authorization is required.</p>
              <button type="button" className="outline" disabled={stopping} onClick={()=>void stopInitialEngineering()}>{stopping?'Stopping…':'Stop team'}</button></> :
            activeWork ? <><h2>{working}</h2><p>{currentActivity?.title??'The team is working from saved project evidence.'}</p>
              {activeEngineer&&totalAssignments>0&&<div className="ca-guided-progress">
                <progress value={completedAssignments} max={totalAssignments} aria-label="Completed engineering assignments"/>
                <span>{completedAssignments} of {totalAssignments} assignments saved</span>
              </div>}
              {!canCoordinate&&source?.state==='Running'&&!latest?.state?.match(/^(Running|QaRunning)$/)&&
                <button type="button" className="outline" disabled={stopping} onClick={()=>void stopInitialEngineering()}>{stopping?'Stopping…':'Stop engineering'}</button>}
              {!canCoordinate&&(source?.state!=='Running'||latest?.state?.match(/^(Running|QaRunning)$/))&&
                <button type="button" className="outline" onClick={()=>navigate(activeEngineer?'generation':'delivery')}>Inspect or stop active work</button>}</> :
            nextDecision ? <><h2>{nextDecision.title}</h2><p>{nextDecision.detail}</p><button type="button" className="primary" onClick={()=>openGuidedDecision(nextDecision.stage)}>Continue</button></> :
            stopped ? <><h2>Work stopped — saved source preserved</h2><p>{source?.errorCode??latest?.errorCode??'Inspect the reason before authorizing another attempt.'}</p>
              {!canCoordinate&&<button type="button" className="primary" onClick={()=>navigate(latest?'delivery':'generation')}>Inspect stopped work</button>}</> :
            !source ? <><h2>Ready to implement</h2><p>Your approved plan is ready. Start source-only engineering; independent review follows separate authorization.</p><button type="button" className="primary" disabled={starting} onClick={()=>void startApprovedWork()}>{starting?'Starting…':'Start approved work'}</button></> :
            awaitingVerification ? <><h2>Independent verification needed</h2><p>The team has reached the end of its source-only workflow. Saved review and repair evidence is available; functional verification and delivery are not authorized.</p><button type="button" className="primary" onClick={()=>navigate('delivery')}>Inspect verification evidence</button></> :
            coordinationStopped ? <><h2>Team coordination stopped — source preserved</h2><p>Inspect the reason below before retrying. A stopped session does not mean your existing approval was lost.</p></> :
            <><h2>Source saved — coordinate independent review</h2><p>Use the team workflow below to authorize review and bounded repairs once. Source remains unverified.</p></>}
          {upfront?.state==='Stopped'&&upfront.runId===source?.id&&<p role="status">Automatic handoff stopped: {upfront.reason??'Owner decision required'}. Saved source is preserved.</p>}
          {startError&&<p role="alert">{startError}</p>}
          {canCoordinate&&<CollaborationPanel key={task.id} token={token} taskId={task.id} guided />}
        </section>
        {activity.length>0&&<section className="ca-guided-last" aria-label="Latest saved activity">
          <span className="ca-studio-eyebrow">LATEST SAVED ACTIVITY</span><strong>{activity[0].title}</strong><p>{activity[0].detail}</p>
          <details onToggle={event=>setActivityOpen(event.currentTarget.open)}><summary>Inspect recorded activity</summary>{activityOpen&&<EngineeringActivityFeed key={task.id} token={token} taskId={task.id} compact={false}/>}</details>
        </section>}
        <p className="ca-studio-verification">Source-only work. No independent functional verification or delivery is implied.</p>
      </div>}
    </main>
  </section>;
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
        <button type="button" className="outline" onClick={()=>{setDetailed(false);setRoleModelsOpen(false);}}>Back to project overview</button>
        <button type="button" className="outline ca-studio-model-button"
          aria-expanded={roleModelsOpen} aria-controls="ca-studio-model-panel"
          onClick={() => roleModelsOpen ? setRoleModelsOpen(false) : openRoleModels()}>
          {roleModelsOpen ? 'Hide team models' : 'Assign team models'}
        </button>
        <span className={'ca-studio-run-state' + (activeLead || activeQa || activeEngineer || coordinating ? ' is-working' : '')}>
          <span aria-hidden="true" className="ca-studio-state-dot" />{working}
        </span>
      </div>
    </header>
    <StudioLiveActivity token={token} active={active}
      recorded={activity} onInspect={navigate} />
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
        <EngineeringActivityFeed key={task.id} token={token} taskId={task.id}
          compact={sideView !== 'activity'} />
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
              <button type="button" onClick={() => {navigate(item.stage); if(item.specialistId){setInspectedAgentId(item.specialistId);setSideView('specialist');}}}>
                Open {item.stage === 'delivery' ? 'QA & source' :
                  item.stage === 'generation' ? 'Engineering' : 'work stage'}
              </button>
            </li>)}
          </ul> : decisions && <p className="ca-studio-muted">
            No current approvals are waiting. Source still requires independent verification.</p>}
          <button type="button" className="ca-studio-text-action" onClick={()=>setSideView('delivery')}>Inspect delivery readiness</button>
          <div className="ca-studio-activity-teaser">
            <span className="ca-studio-eyebrow">LAST SAVED WORK</span>
            {activity.length ? <p><strong>{activity[0].title}</strong>
              <small>{activity[0].detail}</small></p> :
              <p className="ca-studio-muted">No recorded work yet.</p>}
            <button type="button" className="ca-studio-text-action"
              onClick={() => setSideView('activity')}>Explore activity</button>
          </div>
        </section>}
        {sideView === 'delivery' && <DeliveryEvidencePanel key={task.id} token={token} taskId={task.id}
          runId={saved?.engineering?.job.id??null} onClose={()=>setSideView('attention')} />}
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
            {inspected.agent.id === 'TechnicalLead' &&
              <CollaborationPanel key={task.id} token={token} taskId={task.id} />}
            {inspected.agent.id === 'LeadSoftwareEngineer' &&
              <LeadReviewPanel key={task.id} token={token} taskId={task.id}
                source={saved?.engineering ?? null} snapshot={saved?.lead ?? null} />}
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
