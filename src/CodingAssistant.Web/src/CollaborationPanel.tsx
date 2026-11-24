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
