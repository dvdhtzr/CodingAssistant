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
interface Policy { revision: number; policySha256: string; status: string;
  current: boolean; operations: string[] }
interface Preview { planSha256: string | null; policy: Policy | null;
  snapshot: CollaborationSnapshot | null; candidate: Candidate | null;
  canStart: boolean; canResume: boolean; reason: string;
  capacity: { capacity: number; active: number; waiting: number } }
const operations=['ScheduleWork','ReviewCode','ReviewAcceptance','RequestRepair','SendMessage'];
const actionLabel: Record<string,string>={
  ReviewCode:'Independent code review',ReviewAcceptance:'Independent QA review',
  ClassifyIssue:'Classify finding',RequestOwnerRepair:'Request scoped repair',
  RecheckIssue:'Independent finding recheck',AwaitQualifiedVerification:'Await qualified verification',
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
export function CollaborationPanel({token,taskId}:{token:string;taskId:string}) {
  const root='/api/tasks/'+taskId;
  const [preview,setPreview]=useState<Preview|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState('');
  const [loadError,setLoadError]=useState(false);
  const [version,setVersion]=useState(0);
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
  const running=job?.state==='Running';
  async function act(action:'propose'|'approve'|'revoke'|'start'|'resume'|'cancel'){
    if(busy||!preview||loadError)return;
    setBusy(true);setError('');
    try{
      if(action==='propose' && preview.planSha256)
        await api(token,root+'/engineering-delegation/propose',
          {expectedRevision:policy?.revision??0,planSha256:preview.planSha256,operations});
      else if((action==='approve'||action==='revoke')&&policy)
        await api(token,root+'/engineering-delegation/'+action,{revision:policy.revision,
          policySha256:policy.policySha256,confirmation:(action==='approve'?'APPROVE ':'REVOKE ')+policy.policySha256});
      else if(action==='start'&&preview.canStart&&preview.candidate)
        await api(token,root+'/collaboration',{...preview.candidate});
      else if(action==='resume'&&preview.canResume&&preview.candidate&&job)
        await api(token,root+'/collaboration/'+job.id+'/resume',{...preview.candidate});
      else if(action==='cancel'&&running&&job)
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
      <strong role="status">{job?stateLabel[job.state]??job.state:'Not started'}</strong></div>
    <p>Inference: {preview.capacity.active}/{preview.capacity.capacity} active · {preview.capacity.waiting} queued</p>
    {loadError&&<p role="alert">Refresh unavailable. Actions are disabled until evidence is current.</p>}
    {!approved&&!proposed&&<><p>Authorize review, scheduling, messaging and repair requests for the current approved plan. Source repair and execution still require separate approval.</p>
      <button type="button" disabled={busy||loadError||!preview.planSha256||running}
        onClick={()=>void act('propose')}>Prepare delegation policy</button></>}
    {proposed&&<><p>Review this exact routine-work policy before authorizing it.</p>
      <details open><summary>Policy revision {policy!.revision}</summary>
        <ul>{policy!.operations.map(op=><li key={op}>{op}</li>)}</ul>
        <code>{policy!.policySha256}</code>
      </details>
      <button type="button" className="primary" disabled={busy||loadError||running}
        onClick={()=>void act('approve')}>Approve exact delegation</button></>}
    {approved&&<><p>Delegation v{policy!.revision} · approved. No execution permission.</p>
      <details><summary>Inspect or revoke delegation</summary>
        <ul>{policy!.operations.map(op=><li key={op}>{op}</li>)}</ul><code>{policy!.policySha256}</code>
        <button type="button" disabled={busy||loadError} onClick={()=>void act('revoke')}>Revoke delegation</button>
      </details></>}
    {preview.canStart&&<button type="button" className="primary" disabled={busy||loadError}
      onClick={()=>void act('start')}>Start team coordination</button>}
    {preview.canResume&&<button type="button" className="primary" disabled={busy||loadError}
      onClick={()=>void act('resume')}>Resume saved coordination</button>}
    {running&&<button type="button" disabled={busy||loadError} onClick={()=>void act('cancel')}>Stop coordination</button>}
    {approved&&job?.state==='AwaitingOwner'&&job.reason==='EXACT_REPAIR_APPROVAL_REQUIRED'&&preview.snapshot&&
      <ScopedRepairPanel key={job.id} token={token} root={root} snapshot={preview.snapshot}
        disabled={busy||loadError||preview.candidate?.sourceSha256!==job.sourceSha256||preview.candidate?.policySha256!==job.policySha256}
        onSaved={()=>setVersion(v=>v+1)} />}
    {job?.reason&&<p>{job.reason.replaceAll('_',' ').toLowerCase()}</p>}
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
