import { useEffect, useState } from 'react';
import { api } from './api';
import type { JourneyId } from './ProjectJourney';

export interface InboxSpec {
  version: number;
  sha256: string;
  status: 'Draft' | 'Approved' | 'Superseded';
}
export interface InboxArchitectureDecision {
  topic: string;
  revision: number;
  specificationSha256: string;
  specificationCurrent: boolean;
  status: string;
}
export interface InboxPlan {
  revision: number;
  status: 'Proposed' | 'Approved' | 'Superseded';
  current: boolean;
}

export interface DecisionInboxSnapshot {
  specification: InboxSpec | null;
  ownerAnswers: number;
  readyForSpecification: boolean;
  decisions: InboxArchitectureDecision[];
  plan: InboxPlan | null;
}
export interface DecisionInboxItem {
  id: string;
  stage: JourneyId;
  kind: 'review' | 'next' | 'stale';
  title: string;
  detail: string;
}

const topics = [
  { id: 'technology-stack', label: 'Technology stack' },
  { id: 'architecture-style', label: 'Architecture approach' },
] as const;

/**
 * Derive actionable navigation solely from saved, exact-hash approval states.
 * No user approval happens here; the existing stage controls own every write.
 * Stale dependent decisions cannot be advertised as approved.
 */
export function buildDecisionInbox(saved: DecisionInboxSnapshot): DecisionInboxItem[] {
  const spec = saved.specification;
  if (!spec) return [{
    id: 'specification', stage: saved.ownerAnswers ? 'specification' : 'discovery',
    kind: 'next',
    title: saved.ownerAnswers ? 'Draft your first specification' : 'Start product discovery',
    detail: saved.ownerAnswers ?
      'Review the recorded interview before drafting an owner-approvable specification.' :
      'Discuss project requirements with the Product Owner.',
  }];
  if (spec.status === 'Draft') return [{
    id: 'specification', stage: 'specification', kind: 'review',
    title: 'Review specification v' + spec.version,
    detail: 'Read the complete saved requirements and approve the exact version and SHA-256.',
  }];
  if (spec.status !== 'Approved') return [{
    id: 'specification', stage: 'specification', kind: 'stale',
    title: 'Specification needs revision',
    detail: 'The previous specification is not approved for current downstream work.',
  }];

  const architecture: DecisionInboxItem[] = [];
  for (const topic of topics) {
    const decision = saved.decisions.find(item => item.topic === topic.id);
    if (!decision) {
      architecture.push({
        id: topic.id, stage: 'architecture', kind: 'next',
        title: 'Request ' + topic.label.toLowerCase() + ' alternatives',
        detail: 'The Solution Architect has not saved a proposal for this topic.',
      });
    } else if (!decision.specificationCurrent ||
      decision.specificationSha256 !== spec.sha256) {
      architecture.push({
        id: topic.id, stage: 'architecture', kind: 'stale',
        title: 'Refresh ' + topic.label.toLowerCase() + ' proposal',
        detail: 'Saved proposal v' + decision.revision +
          ' is bound to an older specification. Request updated alternatives.',
      });
    } else if (decision.status === 'Proposed') {
      architecture.push({
        id: topic.id, stage: 'architecture', kind: 'review',
        title: 'Review ' + topic.label.toLowerCase() + ' v' + decision.revision,
        detail: 'Compare the saved alternatives and explicitly approve or defer the proposal.',
      });
    } else if (decision.status !== 'Approved') {
      architecture.push({
        id: topic.id, stage: 'architecture', kind: 'next',
        title: 'Revisit ' + topic.label.toLowerCase(),
        detail: 'No current owner approval exists for this topic.',
      });
    }
  }
  if (architecture.length) {
    const order: Record<DecisionInboxItem['kind'], number> =
      { review: 0, stale: 1, next: 2 };
    return architecture.sort((a, b) => order[a.kind] - order[b.kind]);
  }

  const plan = saved.plan;
  if (!plan) return [{
    id: 'plan', stage: 'plan', kind: 'next',
    title: 'Request an implementation plan',
    detail: 'Both architecture choices are approved. The Technical Lead can propose the next work.',
  }];
  if (!plan.current || plan.status === 'Superseded') return [{
    id: 'plan', stage: 'plan', kind: 'stale',
    title: 'Refresh implementation plan v' + plan.revision,
    detail: 'The previous plan is no longer current with its approved dependencies.',
  }];
  if (plan.status === 'Proposed') return [{
    id: 'plan', stage: 'plan', kind: 'review',
    title: 'Review implementation plan v' + plan.revision,
    detail: 'Review the exact saved task breakdown and acceptance criteria before approval.',
  }];
  return [];
}

/** Small, reviewable navigation cards. The original hash-bound approval forms remain authoritative. */
export function DecisionInboxView({ items, onNavigate }: {
  items: readonly DecisionInboxItem[];
  onNavigate: (stage: JourneyId) => void;
}) {
  const waiting = items.filter(item => item.kind === 'review').length;
  const primary = items[0];
  return <section className="owner-decision-inbox" aria-label="Owner decision inbox">
    <div className="owner-inbox-top">
      <div><span className="wb-kicker">OWNER DECISION INBOX</span>
        <strong>{waiting ? waiting + ' approval' + (waiting === 1 ? '' : 's') +
          ' awaiting your review' : items.length ? 'Your next engineering decision' :
            'All current approvals are recorded'}</strong>
      </div>
      <span className="owner-inbox-count">{items.length} action{items.length === 1 ? '' : 's'}</span>
    </div>
    {primary ? <>
      <div className="owner-inbox-featured" data-kind={primary.kind}>
        <span className="owner-inbox-kind">{primary.kind === 'review' ?
          'WAITING FOR OWNER REVIEW' : primary.kind === 'stale' ?
            'PRIOR DECISION NOT CURRENT' : 'NEXT WORK ITEM'}</span>
        <strong>{primary.title}</strong>
        <p>{primary.detail}</p>
        <button type="button" className="outline"
          onClick={() => onNavigate(primary.stage)}>
          Open {primary.stage === 'specification' ? 'Specification' :
            primary.stage === 'architecture' ? 'Solution Architecture' :
              primary.stage === 'plan' ? 'Implementation Plan' :
                'Product Discovery'} →
        </button>
      </div>
      {items.length > 1 && <details className="owner-inbox-other">
        <summary>View {items.length - 1} additional saved action{items.length === 2 ? '' : 's'}</summary>
        <ul>{items.slice(1).map(item => <li key={item.id} data-kind={item.kind}>
          <strong>{item.title}</strong>
          <p>{item.detail}</p>
          <button type="button" className="outline"
            onClick={() => onNavigate(item.stage)}>Open {item.stage} stage →</button>
        </li>)}</ul>
      </details>}
    </> : <p className="owner-inbox-clear">
      No saved proposal is awaiting owner approval. Approved source is still unverified;
      review generated files and QA evidence in their own stages.
    </p>}
    <p className="owner-inbox-boundary">This inbox is read-only navigation. No AI
      recommendation, pending proposal, or selected stage is an approval.</p>
  </section>;
}

export function OwnerDecisionInbox({ token, taskId, refreshSignal, onNavigate }: {
  token: string;
  taskId: string;
  refreshSignal: string;
  onNavigate: (stage: JourneyId) => void;
}) {
  const [data, setData] = useState<DecisionInboxSnapshot | null>(null);
  const [error, setError] = useState('');
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    let closed = false;
    let loading = false;
    setData(null);
    setError('');
    async function load() {
      if (loading) return;
      loading = true;
      try {
        const [interview, decisions, plan] = await Promise.all([
          api<{ specification: InboxSpec | null; messages: { role: string }[];
            readyForSpecification: boolean }>(
            token, '/api/tasks/' + taskId + '/interview'),
          api<{ decisions: InboxArchitectureDecision[] }>(
            token, '/api/tasks/' + taskId + '/architecture-decisions'),
          api<{ plan: InboxPlan | null }>(
            token, '/api/tasks/' + taskId + '/implementation-plan'),
        ]);
        if (!closed) {
          setData({
            specification: interview.specification,
            ownerAnswers: interview.messages.filter(message => message.role === 'owner').length,
            readyForSpecification: interview.readyForSpecification,
            decisions: decisions.decisions,
            plan: plan.plan,
          });
          setError('');
        }
      } catch {
        if (!closed) {
          // Never keep displaying stale approval state after a failed refresh.
          setData(null);
          setError('Unable to read all saved approvals; no review state is inferred.');
        }
      } finally {
        loading = false;
      }
    }
    void load();
    // Saved recommendation revisions can change while the parent stage keeps
    // the same status label. Poll the ledger, not a fabricated agent heartbeat.
    const interval = window.setInterval(() => void load(), 8000);
    return () => { closed = true; window.clearInterval(interval); };
  }, [token, taskId, refreshSignal, refresh]);

  return <div className="owner-decision-shell">
    {error && <p role="alert" className="alert">{error}
      <button type="button" className="outline"
        onClick={() => setRefresh(value => value + 1)}>Retry</button>
    </p>}
    {!data && !error && <p className="owner-inbox-loading" role="status">
      Reading the saved owner decision ledger…</p>}
    {data && <DecisionInboxView items={buildDecisionInbox(data)} onNavigate={onNavigate}/>}
  </div>;
}
