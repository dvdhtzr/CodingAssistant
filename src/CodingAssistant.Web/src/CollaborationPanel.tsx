import { useEffect, useState } from 'react';
import { api } from './api';

export interface CollaborationSnapshot {
  job: { id: string; taskId: string; runId: string; sourceSha256: string; planSha256: string;
    policyRevision: number; policySha256: string; model: string; state: string; reason: string | null };
  steps: Array<{ sequence: number; action: string; issueId: string | null; rationale: string;
    referenceId: string | null; createdUtc: string }>;
}
interface Candidate { runId: string; sourceSha256: string; planSha256: string;
  policyRevision: number; policySha256: string }
interface RoutineScope { baselineRunId:string; baselineSourceSha256:string; paths:string[]; maximumRepairs:number }
interface Policy { revision: number; policySha256: string; status: string;
  current: boolean; operations: string[]; routineRepair?:RoutineScope|null }
interface Preview { planSha256: string | null; policy: Policy | null;
  snapshot: CollaborationSnapshot | null; candidate: Candidate | null;
  canStart: boolean; canResume: boolean; reason: string;
  routineRepairCandidate?: {runId:string;sourceSha256:string;paths:string[]}|null;
  linkedRepair?: {cycleId:string;state:string;errorCode:string|null}|null;
  reviewFailure?: {role:string;reviewId:string;model:string;state:string;errorCode:string|null}|null;
  canInvestigateStall?:boolean;
  rootCauseInvestigation?: {id:string;runId:string;sourceSha256:string;model:string;state:string;errorCode:string|null;
    assessment:{findings:Array<{issueId:string;outcome:string;causePath:string|null;line:number|null;
      evidence:string|null;rationale:string;alternativeStrategy:string|null}>}|null;
    independentReview?: {model:string;state:string;errorCode:string|null;assessment:{findings:Array<{
      issueId:string;outcome:string;path:string|null;line:number|null;evidence:string|null;rationale:string}>}|null}|null}|null;
  stallEvidence?: {reason:string;attemptCount:number;recentAttempts:Array<{cycleId:string;state:string;
    originalSourceSha256:string;candidateSourceSha256:string;origin:string;independentStaticResolution:boolean;recurringExactFinding:boolean}>;
    findings:Array<{id:string;origin:string;path:string;requirement:string;evidence:string;recommendation:string}>}|null;
  capacity: { capacity: number; active: number; waiting: number } }
const operations=['ScheduleWork','ReviewCode','ReviewAcceptance','RequestRepair','SendMessage'];
const actionLabel: Record<string,string>={
  ReviewCode:'Independent code review',ReviewAcceptance:'Independent QA review',
  ClassifyIssue:'Classify finding',RequestOwnerRepair:'Request scoped repair',
  RecheckIssue:'Independent finding recheck',AwaitQualifiedVerification:'Await qualified verification',
  InvestigateStall:'Cross-file root cause investigation',
  Escalate:'Escalate',ResumeInterrupted:'Owner resumed checkpoint'
};
const stateLabel:Record<string,string>={
  Running:'Coordinating',AwaitingOwner:'Owner decision needed',
  AwaitingVerification:'Qualified verification needed',Interrupted:'Interrupted',
  Escalated:'Needs inspection',Failed:'Stopped safely'
};


interface ScopedFinding { path:string; index:number; severity:string; requirement:string;
  evidence:string; recommendation:string }
interface ScopedProposal { id:string; taskId:string; engineeringRunId:string; sourceSha256:string;
  planSha256:string; leadReviewId:string|null; qaReviewId:string|null; findings:ScopedFinding[] }
interface ScopedIssue { id:string; state:string; currentSource:boolean; latestReview:boolean;
  references:Array<{reviewId:string;path:string;findingIndex:number}> }
interface ScopedRepair { proposal:ScopedProposal; finding:ScopedFinding; classification:string|null }
async function readScopedRepair(token:string,root:string,snapshot:CollaborationSnapshot):Promise<ScopedRepair> {
  const {job,steps}=snapshot;
  const step=steps[steps.length-1];
  if(job.state!=='AwaitingOwner'||job.reason!=='EXACT_REPAIR_APPROVAL_REQUIRED'||
    step?.action!=='RequestOwnerRepair'||!step.referenceId||!step.issueId)
    throw Error('No exact repair decision is available.');
  const [p,page]=await Promise.all([
    api<{proposal:ScopedProposal|null}>(token,root+'/repair-proposals/latest'),
    api<{currentSource:boolean;issues:ScopedIssue[]}>(token,root+'/engineering-issues?runId='+encodeURIComponent(job.runId))
  ]);
  const proposal=p.proposal;
  if(!proposal||proposal.id!==step.referenceId||proposal.taskId!==job.taskId||
    proposal.engineeringRunId!==job.runId||proposal.sourceSha256!==job.sourceSha256||
    proposal.planSha256!==job.planSha256||!page.currentSource)
    throw Error('Saved repair scope is no longer current.');
  const issue=page.issues.find(i=>i.id===step.issueId&&i.state==='ConfirmedDefect'&&i.currentSource&&i.latestReview);
  const review=proposal.leadReviewId??proposal.qaReviewId;
  const findings=proposal.findings.filter(f=>issue?.references.some(r=>r.reviewId===review&&
    r.path.toLowerCase()===f.path.toLowerCase()&&r.findingIndex===f.index));
  if(findings.length!==1)throw Error('The requested repair needs an exact finding selection in the existing repair workflow.');
  const finding=findings[0];
  const triage=await api<{decisions:Array<{path:string;index:number;classification:string}>}>(token,
    root+'/repair-proposals/'+proposal.id+'/triage');
  return {proposal,finding,classification:triage.decisions.find(d=>d.path.toLowerCase()===finding.path.toLowerCase()&&d.index===finding.index)?.classification??null};
}
function ScopedRepairPanel({token,root,snapshot,disabled,onSaved}:{token:string;root:string;
  snapshot:CollaborationSnapshot;disabled:boolean;onSaved:()=>void}) {
  const [scope,setScope]=useState<ScopedRepair|null>(null);
  const [error,setError]=useState('');
  const [busy,setBusy]=useState(false);
  const [submitted,setSubmitted]=useState(false);
  const jobId=snapshot.job.id;
  const stepId=snapshot.steps[snapshot.steps.length-1]?.referenceId;
  useEffect(()=>{
    let closed=false;
    setScope(null);setError('');setSubmitted(false);
    void readScopedRepair(token,root,snapshot).then(s=>{if(!closed)setScope(s);})
      .catch(e=>{if(!closed)setError(e instanceof Error?e.message:'Scope unavailable.');});
    return()=>{closed=true;};
  },[token,root,jobId,stepId]);
  async function confirm(){
    if(!scope||disabled||busy||submitted)return;
    setBusy(true);setError('');
    try{
      const latest=await readScopedRepair(token,root,snapshot);
      if(latest.proposal.id!==scope.proposal.id||latest.finding.path!==scope.finding.path||
        latest.finding.index!==scope.finding.index)throw Error('Repair scope changed. Inspect it again.');
      if(latest.classification&&latest.classification!=='ImplementationDefect')
        throw Error('Existing owner triage does not authorize an implementation repair.');
      const {proposal,finding}=latest;
      if(!latest.classification)
        await api(token,root+'/repair-proposals/'+proposal.id+'/triage',
          {path:finding.path,index:finding.index,classification:'ImplementationDefect',
            rationale:'Owner confirmed this exact implementation defect in team coordination.'});
      await api(token,root+'/repair-cycles',{proposalId:proposal.id,confirmation:'REPAIR '+proposal.sourceSha256,
        findings:[{path:finding.path,index:finding.index}],continueCollaborationId:jobId});
      setSubmitted(true);onSaved();
    }catch(e){setError(e instanceof Error?e.message:'Repair request rejected.');}
    finally{setBusy(false);}
  }
  return <section aria-label="Exact repair authorization">
    <h4>Owner repair decision</h4>
    {scope?<><p><strong>{scope.finding.severity} · {scope.finding.path}</strong></p>
      <p>{scope.finding.requirement}</p><p>{scope.finding.evidence}</p>
      <p>{scope.finding.recommendation}</p>
      <details><summary>Exact source fingerprint</summary><code>{scope.proposal.sourceSha256}</code></details>
      <p>Authorize only this finding's file scope. The team will independently review the repaired source. No execution permission is granted.</p>
      <button type="button" className="primary" disabled={disabled||busy||submitted||
        (!!scope.classification&&scope.classification!=='ImplementationDefect')}
        onClick={()=>void confirm()}>{submitted?'Repair admitted':'Confirm exact repair and continue'}</button>
    </>:!error?<p role="status">Reading exact repair scope…</p>:null}
    {error&&<p role="alert">{error}</p>}
  </section>;
}

/** Real owner-admitted coordination, not model-generated chat or implicit approvals. */
export function CollaborationPanel({token,taskId,guided=false}:{token:string;taskId:string;guided?:boolean}) {
  const root='/api/tasks/'+taskId;
  const [preview,setPreview]=useState<Preview|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState('');
  const [loadError,setLoadError]=useState(false);
  const [version,setVersion]=useState(0);
  const [routineEnabled,setRoutineEnabled]=useState(false);
  const [repairPaths,setRepairPaths]=useState<string[]>([]);
  const [maximumRepairs,setMaximumRepairs]=useState(4);
  const scopeCandidate=preview?.routineRepairCandidate;
  useEffect(()=>{
    setRoutineEnabled(false);setRepairPaths([]);
  },[taskId,scopeCandidate?.runId,scopeCandidate?.sourceSha256]);
  useEffect(()=>{
    let closed=false,pending=false;
    async function load(){
      if(closed||pending)return;
      pending=true;
      try{
        const value=await api<Preview>(token,root+'/collaboration/preview');
        if(!closed){setPreview(value);setLoadError(false);}
      }catch{if(!closed)setLoadError(true);}
      finally{pending=false;}
    }
    void load();
    const timer=window.setInterval(()=>void load(),3000);
    return()=>{closed=true;window.clearInterval(timer);};
  },[token,root,version]);
  const policy=preview?.policy;
  const job=preview?.snapshot?.job;
  const linkedRepair=preview?.linkedRepair;
  const repairing=linkedRepair?.state==='Running'||linkedRepair?.state==='QaRunning';
  const continuing=linkedRepair?.state==='Generated';
  const dispatching=!linkedRepair&&job?.state==='AwaitingOwner'&&job.reason==='ROUTINE_REPAIR_DISPATCH_PENDING';
  const running=job?.state==='Running'||repairing||continuing||dispatching;
  const recoverHandoff=continuing&&!!preview?.candidate&&
    preview.candidate.policySha256===job?.policySha256&&
    preview.candidate.runId!==job?.runId;
  async function act(action:'propose'|'approve'|'revoke'|'start'|'resume'|'resumeHandoff'|'cancel'){
    if(busy||!preview||loadError)return;
    setBusy(true);setError('');
    try{
      if(action==='propose' && preview.planSha256){
        if(routineEnabled&&(!scopeCandidate||!repairPaths.length||repairPaths.some(p=>!scopeCandidate.paths.includes(p))))
          throw Error('Select the exact saved source files for routine repairs.');
        await api(token,root+'/engineering-delegation/propose',
          {expectedRevision:policy?.revision??0,planSha256:preview.planSha256,
            operations:routineEnabled?[...operations,'WriteSource']:operations,
            ...(routineEnabled&&scopeCandidate?{routineRepair:{baselineRunId:scopeCandidate.runId,
              baselineSourceSha256:scopeCandidate.sourceSha256,paths:repairPaths,maximumRepairs}}:{})});
      }
      else if((action==='approve'||action==='revoke')&&policy) {
        await api(token,root+'/engineering-delegation/'+action,{revision:policy.revision,
          policySha256:policy.policySha256,confirmation:(action==='approve'?'APPROVE ':'REVOKE ')+policy.policySha256});
        if(guided&&action==='approve'){
          // The click authorizes the exact displayed policy and its start.
          // Re-read host admission; never start against a different policy.
          const current=await api<Preview>(token,root+'/collaboration/preview');
          setPreview(current);
          if(current.canStart&&current.candidate&&
            current.candidate.policyRevision===policy.revision&&
            current.candidate.policySha256===policy.policySha256&&
            (!policy.routineRepair||(current.candidate.runId===policy.routineRepair.baselineRunId&&
              current.candidate.sourceSha256===policy.routineRepair.baselineSourceSha256)))
            await api(token,root+'/collaboration',{...current.candidate});
        }
      }
      else if(action==='start'&&preview.canStart&&preview.candidate)
        await api(token,root+'/collaboration',{...preview.candidate});
      else if(action==='resume'&&preview.canResume&&preview.candidate&&job)
        await api(token,root+'/collaboration/'+job.id+'/resume',{...preview.candidate});
      else if(action==='resumeHandoff'&&recoverHandoff&&linkedRepair&&preview.candidate)
        await api(token,root+'/repair-cycles/'+linkedRepair.cycleId+'/resume-collaboration',{...preview.candidate});
      else if(action==='cancel'&&repairing&&linkedRepair)
        await api(token,root+'/repair-cycles/'+linkedRepair.cycleId+'/cancel',{});
      else if(action==='cancel'&&job&&(job.state==='Running'||dispatching))
        await api(token,root+'/collaboration/'+job.id+'/cancel',{});
      else throw Error('This action is no longer available.');
      setVersion(v=>v+1);
    }catch(cause){setError(cause instanceof Error?cause.message:'Request rejected.');}
    finally{setBusy(false);}
  }
  if(!preview)return <section className="ca-coordination" aria-label="Team coordination">
    <h3>Team coordination</h3><p role="status">{loadError?'Coordination unavailable':'Reading saved coordination…'}</p>
  </section>;
  const approved=policy?.status==='Approved'&&policy.current;
  const proposed=policy?.status==='Proposed'&&policy.current;
  return <section className="ca-coordination" aria-label="Team coordination">
    <div className="ca-coordination-state"><h3>Team coordination</h3>
      <strong role="status">{dispatching?'Starting authorized repair':repairing?'Repairing saved source':continuing?'Saved candidate · independent review pending':
        linkedRepair?'Repair '+linkedRepair.state:job?stateLabel[job.state]??job.state:'Not started'}</strong></div>
    {!guided&&<p>Inference: {preview.capacity.active}/{preview.capacity.capacity} active · {preview.capacity.waiting} queued</p>}
    {loadError&&<p role="alert">Refresh unavailable. Actions are disabled until evidence is current.</p>}
    {guided&&preview.reason==='BUILD_NOT_COMPLETE'&&!scopeCandidate&&<p role="status">The saved source is not eligible for coordinated repair. Inspect the stopped work; an incomplete initial build cannot be treated as a completed application.</p>}
    {!approved&&!proposed&&(!guided||!!scopeCandidate)&&<><p>Authorize routine reviews, scheduling and messaging. You can also delegate bounded source-only repairs. Execution always requires separate authorization.</p>
      {scopeCandidate&&<fieldset disabled={busy||loadError||running}>
        <legend>Routine source repairs</legend>
        <label><input type="checkbox" checked={routineEnabled}
          onChange={e=>{setRoutineEnabled(e.target.checked);if(guided)setRepairPaths(e.target.checked&&scopeCandidate.paths.length<=12?[...scopeCandidate.paths]:[]);}}/> Let the team repair grounded defects without asking me each time</label>
        {routineEnabled&&<>
          <p>Choose existing files from source run {scopeCandidate.runId.slice(0,12)}. New files and changes outside this scope are not authorized.</p>
          {scopeCandidate.paths.map(path=><label key={path} className="qa-repair-choice">
            <input type="checkbox" checked={repairPaths.includes(path)}
              onChange={e=>setRepairPaths(old=>e.target.checked?[...old,path]:old.filter(p=>p!==path))}/>{path}</label>)}
          <label>Repair budget <select value={maximumRepairs} onChange={e=>setMaximumRepairs(Number(e.target.value))}>
            {[1,2,3,4,5,6,7,8].map(n=><option key={n} value={n}>{n}</option>)}
          </select></label>
          <p>The team stops if evidence is insufficient, the repair budget is exhausted, or you revoke the policy. No build or runtime verification is granted.</p>
        </>}
      </fieldset>}
      <button type="button" disabled={busy||loadError||!preview.planSha256||running||
        (routineEnabled&&(!scopeCandidate||!repairPaths.length||repairPaths.length>12))}
        onClick={()=>void act('propose')}>Prepare delegation policy</button></>}
    {proposed&&<><p>Review this exact routine-work policy before authorizing it.</p>
      <details open><summary>Policy revision {policy!.revision}</summary>
        <ul>{policy!.operations.map(op=><li key={op}>{op}</li>)}</ul>
        {policy!.routineRepair&&<><p>Up to {policy!.routineRepair.maximumRepairs} routine source repairs from run {policy!.routineRepair.baselineRunId.slice(0,12)}.</p>
          <ul>{policy!.routineRepair.paths.map(path=><li key={path}>{path}</li>)}</ul>
          <p>Starting source fingerprint: <code>{policy!.routineRepair.baselineSourceSha256}</code></p></>}
        <code>{policy!.policySha256}</code>
      </details>
      <button type="button" className="primary" disabled={busy||loadError||running}
        onClick={()=>void act('approve')}>{guided?'Approve exact delegation and start team':'Approve exact delegation'}</button></>}
    {approved&&<><p>Delegation v{policy!.revision} · approved. {policy!.routineRepair?
      'Bounded routine source repairs enabled.':'Individual repair confirmation required.'} No execution permission.</p>
      <details><summary>Inspect or revoke delegation</summary>
        <ul>{policy!.operations.map(op=><li key={op}>{op}</li>)}</ul>
        {policy!.routineRepair&&<><p>Repair budget: {policy!.routineRepair.maximumRepairs}</p>
          <ul>{policy!.routineRepair.paths.map(path=><li key={path}>{path}</li>)}</ul></>}
        <code>{policy!.policySha256}</code>
        <button type="button" disabled={busy||loadError} onClick={()=>void act('revoke')}>Revoke delegation</button>
      </details></>}
    {approved&&preview.reason==='RECOVERY_DELEGATION_REQUIRED'&&<p role="status">The stopped revision needs a fresh exact delegation. Inspect or revoke the previous policy, then prepare a new bounded repair policy for the preserved source.</p>}
    {job?.state==='Escalated'&&job.reason==='LEAD_ESCALATION'&&<p role="status">
      The Technical Lead stopped: {preview.snapshot?.steps.slice().reverse().find(step=>step.action==='Escalate')?.rationale??'No explanation was recorded.'}
      {' '}Your existing delegation is unchanged. Retrying opens a new session on the saved source; it does not regenerate it.</p>}
    {job?.reason==='INDEPENDENT_REVIEW_STOPPED'&&<div role="status">
      {preview.reviewFailure?<><p><strong>{preview.reviewFailure.role==='LeadSoftwareEngineer'?'Lead Software Engineer':'QA Engineer'} review stopped</strong>
        {' · '+preview.reviewFailure.model+' · '+preview.reviewFailure.state}</p>
        <p>Recorded reason: <code>{preview.reviewFailure.errorCode??'NOT_RECORDED'}</code></p>
        <p>{preview.reviewFailure.errorCode==='MODEL_OUTPUT_REJECTED'?
          'The reviewer returned an unusable response. Check the assigned reviewer model before retrying.':
          'Inspect the recorded reviewer failure before retrying.'} Your saved source and delegation are unchanged.</p>
        <details><summary>Exact review receipt</summary><code>{preview.reviewFailure.reviewId}</code></details>
      </>:<p>The independent review stopped without a matching saved failure receipt. No successful review is implied.</p>}
    </div>}
    {preview.stallEvidence&&<div role="status">
      <p><strong>Different repair strategy needed.</strong> {preview.stallEvidence.reason==='RECURRING_UNRESOLVED_FINDING'?
        'The same finding recurred after previous repair attempts without independent resolution.':
        preview.stallEvidence.reason==='REPEATED_SOURCE_SNAPSHOT'?'A previous source fingerprint was repeated.':
        'The latest candidate has a possible blocking regression.'}
        {' '}Retrying the unchanged session will not clear this guard. Your source and delegation remain saved.</p>
      <details open><summary>Evidence for this stop</summary>
        <p>{preview.stallEvidence.attemptCount} recorded repair attempt(s) in this source history.</p>
        {preview.stallEvidence.findings.map(f=><div key={f.id}>
          <strong>{f.path} · {f.origin==='QaEngineer'?'QA':'Lead review'}</strong>
          <p>{f.requirement}</p><p>{f.evidence}</p><p>Suggested correction: {f.recommendation}</p>
        </div>)}
        <ul>{preview.stallEvidence.recentAttempts.map(a=><li key={a.cycleId}>
          {a.cycleId.slice(0,12)} · {a.state} · {a.originalSourceSha256.slice(0,12)} → {a.candidateSourceSha256.slice(0,12)}
          {' · '+(a.independentStaticResolution?'Independent static resolution recorded':'No independent static resolution')}
          {a.recurringExactFinding?' · Exact finding recurred':''}
        </li>)}</ul>
      </details>
      <p>{preview.canInvestigateStall?'The team can investigate the related saved files without changing source.':
        'The original repair guard remains active until an independently supported alternative is admitted.'}
        {' '}Do not regenerate the project or revoke the policy just to clear the stop.</p>
    </div>}
    {preview.rootCauseInvestigation&&<section aria-label="Root cause investigation">
      <h4>Cross-file investigation · {preview.rootCauseInvestigation.state}</h4>
      <p>{preview.rootCauseInvestigation.model} · source-only · no repair or resolution granted.</p>
      {preview.rootCauseInvestigation.errorCode&&<p role="alert">{preview.rootCauseInvestigation.errorCode}</p>}
      {preview.rootCauseInvestigation.assessment?.findings.map(f=><div key={f.issueId}>
        <strong>{f.outcome==='CandidateCause'?'Proposed cause':f.outcome==='ContradictedClaim'?'Possibly incorrect original finding':'Insufficient evidence'}</strong>
        {f.causePath&&<p>{f.causePath}:{f.line} · <code>{f.evidence}</code></p>}
        <p>{f.rationale}</p>{f.alternativeStrategy&&<p>Alternative strategy: {f.alternativeStrategy}</p>}
        {f.causePath&&policy?.routineRepair&&!policy.routineRepair.paths.includes(f.causePath)&&
          <p>Outside approved write scope. No source change authorized.</p>}
      </div>)}
      {preview.rootCauseInvestigation.independentReview&&<section aria-label="Independent root cause assessment">
        <h4>Independent assessment · {preview.rootCauseInvestigation.independentReview.state}</h4>
        <p>{preview.rootCauseInvestigation.independentReview.model}</p>
        {preview.rootCauseInvestigation.independentReview.errorCode&&<p role="alert">{preview.rootCauseInvestigation.independentReview.errorCode}</p>}
        {preview.rootCauseInvestigation.independentReview.assessment?.findings.map(f=><div key={f.issueId}>
          <strong>{f.outcome}</strong>{f.path&&<p>{f.path}:{f.line} · <code>{f.evidence}</code></p>}
          <p>{f.rationale}</p>
        </div>)}
      </section>}
      {preview.rootCauseInvestigation.state==='Recorded'&&<p>{preview.rootCauseInvestigation.independentReview?.state==='Recorded'?
        'Investigation and independent assessment saved. No repair has been admitted and the original stall guard is still active.':
        'Independent assessment of these proposals is required before any repair.'}</p>}
    </section>}
    {preview.canStart&&<button type="button" className="primary" disabled={busy||loadError}
      onClick={()=>void act('start')}>{preview.canInvestigateStall?'Investigate recurring findings':
        job?.state==='Escalated'||job?.state==='Failed'?'Retry team coordination':'Start team coordination'}</button>}
    {preview.canResume&&<button type="button" className="primary" disabled={busy||loadError}
      onClick={()=>void act('resume')}>Resume saved coordination</button>}
    {(repairing||job?.state==='Running'||dispatching)&&<button type="button" disabled={busy||loadError} onClick={()=>void act('cancel')}>Stop coordination</button>}
    {recoverHandoff&&<button type="button" disabled={busy||loadError}
      onClick={()=>void act('resumeHandoff')}>Resume independent review handoff</button>}
    {linkedRepair&&<p>{repairing?'The Software Engineer is working within the admitted scope.':
      continuing?'The repair is saved but not verified. Waiting for the independent review handoff.':
      'The linked repair has stopped.'}{linkedRepair.errorCode?' · '+linkedRepair.errorCode:''}</p>}
    {!linkedRepair&&approved&&job?.state==='AwaitingOwner'&&job.reason==='EXACT_REPAIR_APPROVAL_REQUIRED'&&preview.snapshot&&
      <ScopedRepairPanel key={job.id} token={token} root={root} snapshot={preview.snapshot}
        disabled={busy||loadError||preview.candidate?.sourceSha256!==job.sourceSha256||preview.candidate?.policySha256!==job.policySha256}
        onSaved={()=>setVersion(v=>v+1)} />}
    {dispatching&&<p>The team is dispatching a repair within your existing approval. No confirmation is needed.</p>}
    {!linkedRepair&&!dispatching&&!preview.stallEvidence&&job?.reason&&job.reason!=='INDEPENDENT_REVIEW_STOPPED'&&<p>{job.reason.replaceAll('_',' ').toLowerCase()}</p>}
    {!job&&!preview.canStart&&approved&&<p>{preview.reason==='BUILD_NOT_COMPLETE'?
      'Complete approved source generation before coordinating independent reviews.':preview.reason.replaceAll('_',' ').toLowerCase()}</p>}
    {preview.snapshot?.steps.length ? <details>
      <summary>{preview.snapshot.steps.length} saved team decision(s)</summary>
      <ol>{preview.snapshot.steps.slice(-8).map(step=><li key={step.sequence}>
        <strong>{actionLabel[step.action]??step.action}</strong><p>{step.rationale}</p>
        <small>{step.referenceId?'Linked evidence':'Decision recorded; no action receipt yet'}</small>
      </li>)}</ol></details>:null}
    {error&&<p role="alert">{error}</p>}
  </section>;
}
