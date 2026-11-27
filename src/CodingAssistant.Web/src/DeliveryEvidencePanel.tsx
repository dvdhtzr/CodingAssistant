import { useEffect, useState } from 'react';
import { api } from './api';

interface DeliveryEvidence {
  runId: string | null; sourceSha256: string | null; currentSource: boolean;
  routineCandidate?: {runId:string;baseRunId:string;baseSourceSha256:string;disposition:string}|null;
  implementationComplete: boolean; engineeringComplete: boolean;
  productAccepted: boolean; deliveryAllowed: boolean; blockers: string[];
  work: Array<{id:string;title:string;state:string;acceptanceCriteria:string[]}>;
  reviews: Array<{role:string;id:string|null;state:string;model:string|null;filesReviewed:number;complete:boolean}>;
  findings: Array<{id:string;path:string;requirement:string;severity:string;state:string;currentSource:boolean}>;
  repairs: Array<{id:string;attempt:number;state:string;errorCode:string|null}>;
  smoke: Array<{id:string;state:string;status:string|null;current:boolean}>;
  modelUse: Array<{agentId:string;model:string;state:string;recordedRequests:number}>;
  decisions: Array<{topic:string;label:string}>;
  requiredVerification: string[]; plannedRisks: string[]; approvedSpecification: string|null;
  verification: {staticReviewStatus:string;buildStatus:string;functionalStatus:string;runtimeProfileId:string|null;runtimeQualificationStatus:string;mandatoryRuntimeChecks:string[]};
  artifacts: Array<{path:string;sha256:string}>;
}
const reasons: Record<string,string> = {
  QUALIFIED_VERIFICATION_REQUIRED: 'Independent qualified build and functional verification are required.',
  PRODUCT_ACCEPTANCE_REQUIRED: 'Product Owner acceptance is not recorded.',
  INDEPENDENT_REVIEWS_REQUIRED: 'Current independent code and QA reviews are incomplete.',
  UNRESOLVED_FINDINGS: 'Review findings still need disposition.',
  IMPLEMENTATION_INCOMPLETE: 'Approved work packages are not all complete.',
  NO_SOURCE_RUN: 'No source run is recorded.',
  POTENTIAL_CANDIDATE_REGRESSION: 'A repair introduced a grounded blocking concern. The candidate is rejected; the preceding source is preserved.',
};
export function DeliveryEvidencePanel({token,taskId,runId,onClose}:{
  token:string;taskId:string;runId:string|null;onClose:()=>void;
}) {
  const [saved,setSaved]=useState<DeliveryEvidence|null>(null);
  const [error,setError]=useState(false);
  const [revision,setRevision]=useState(0);
  useEffect(()=>{
    let closed=false,pending=false;
    setSaved(null);setError(false);
    async function load(){
      if(closed||pending)return;
      pending=true;
      try{
        const value=await api<DeliveryEvidence>(token,'/api/tasks/'+taskId+'/delivery-evidence');
        if(!closed){setSaved(value);setError(false);}
      }catch{if(!closed){setSaved(null);setError(true);}}
      finally{pending=false;}
    }
    void load();
    const timer=window.setInterval(()=>void load(),6000);
    return()=>{closed=true;window.clearInterval(timer);};
  },[token,taskId,runId,revision]);
  return <section aria-label="Delivery evidence" className="ca-studio-delivery">
    <div className="ca-studio-context-heading"><span className="ca-studio-eyebrow">DELIVERY EVIDENCE</span>
      <strong>Completion & retrospective</strong></div>
    <button type="button" className="ca-studio-text-action" onClick={onClose}>Back to attention</button>
    <button type="button" className="ca-studio-text-action" onClick={()=>setRevision(v=>v+1)}>Refresh evidence</button>
    {error?<p role="alert">Delivery evidence unavailable. No completion is established.</p>:!saved?
      <p role="status">Reading saved delivery evidence…</p>:<>
      <p role="status"><strong>{saved.deliveryAllowed?'Delivery gate satisfied':'Delivery blocked'}</strong></p>
      <dl>
        <dt>Implementation</dt><dd>{saved.implementationComplete?'Source complete':'Incomplete or stale'}</dd>
        <dt>Static review</dt><dd>{saved.verification.staticReviewStatus.replaceAll('_',' ').toLowerCase()}</dd>
        <dt>Build verification</dt><dd>{saved.verification.buildStatus.replaceAll('_',' ').toLowerCase()}</dd>
        <dt>Functional verification</dt><dd>{saved.verification.functionalStatus.replaceAll('_',' ').toLowerCase()}</dd>
        <dt>Engineering</dt><dd>{saved.engineeringComplete?'Complete':'Not verified'}</dd>
        <dt>Product acceptance</dt><dd>{saved.productAccepted?'Accepted':'Not accepted'}</dd>
      </dl>
      {saved.runId&&!saved.currentSource&&<p>Source evidence is not current.</p>}
      {saved.routineCandidate&&<div>
        <strong>{saved.routineCandidate.disposition==='REJECTED_POTENTIAL_REGRESSION'?
          'Candidate rejected — possible regression':saved.routineCandidate.disposition==='STOPPED_UNVERIFIED'?
          'Candidate stopped — not verified':'Candidate saved — not verified'}</strong>
        <p>Candidate {saved.routineCandidate.runId.slice(0,12)} · preserved predecessor {saved.routineCandidate.baseRunId.slice(0,12)}</p>
        <p>The predecessor remains inspectable. Neither source is certified for delivery.</p>
      </div>}
      <ul>{saved.blockers.map(b=><li key={b}>{reasons[b]??b.replaceAll('_',' ').toLowerCase()}</li>)}</ul>
      <details><summary>Requirements and work ({saved.work.length})</summary>
        {saved.approvedSpecification&&<p style={{whiteSpace:'pre-wrap'}}>{saved.approvedSpecification}</p>}
        {saved.work.map(w=><div key={w.id}><strong>{w.title} · {w.state}</strong>
          <ul>{w.acceptanceCriteria.map((c,i)=><li key={i}>{c}</li>)}</ul></div>)}
      </details>
      <details><summary>Architecture and planned risks</summary>
        <ul>{saved.decisions.map(d=><li key={d.topic}>{d.topic}: {d.label}</li>)}
          {saved.plannedRisks.map((risk,i)=><li key={i}>{risk}</li>)}</ul>
      </details>
      <details><summary>Independent reviews and findings ({saved.findings.length})</summary>
        <ul>{saved.reviews.map(r=><li key={r.role}>{r.role}: {r.state} · {r.filesReviewed} file(s) inspected</li>)}
        {saved.findings.map(f=><li key={f.id}>{f.severity} · {f.path}: {f.requirement} — {f.state}
          {!f.currentSource?' (historical source)':''}</li>)}</ul>
      </details>
      <details><summary>Repair history ({saved.repairs.length})</summary>
        <ul>{saved.repairs.map(r=><li key={r.id}>Attempt {r.attempt}: {r.state}{r.errorCode?' · '+r.errorCode:''}</li>)}</ul>
      </details>
      <details><summary>Verification evidence ({saved.smoke.length} smoke records)</summary>
        <p>Smoke checks are not qualified functional tests.</p>
        <ul>{saved.smoke.map(s=><li key={s.id}>{s.status??s.state}{s.current?'':' (historical)'}</li>)}</ul>
        <p>Runtime profile: {saved.verification.runtimeProfileId??'None'} · {saved.verification.runtimeQualificationStatus}</p>
        <p>Mandatory profile checks — not executed:</p>
        <ul>{saved.verification.mandatoryRuntimeChecks.map((v,i)=><li key={i}>{v}</li>)}</ul>
        <p>Approved test strategy — not an execution receipt:</p>
        <ul>{saved.requiredVerification.map((v,i)=><li key={i}>{v}</li>)}</ul>
      </details>
      <details><summary>Recorded local model use</summary>
        <p>Project-wide request receipts, not measured tokens or GPU usage.</p>
        <ul>{saved.modelUse.map(m=><li key={m.agentId+':'+m.model+':'+m.state}>
          {m.agentId} · {m.model} · {m.state}: {m.recordedRequests}</li>)}</ul>
      </details>
      <details><summary>Source identity</summary><code style={{overflowWrap:'anywhere'}}>{saved.sourceSha256??'No source fingerprint'}</code><p>Saved source manifest — not certified for delivery:</p>
        <ul>{saved.artifacts.map(a=><li key={a.path}>{a.path} · <code style={{overflowWrap:'anywhere'}}>{a.sha256}</code></li>)}</ul></details>
    </>}
  </section>;
}
