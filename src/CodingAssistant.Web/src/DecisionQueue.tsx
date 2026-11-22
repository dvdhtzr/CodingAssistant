import { buildDecisionInbox } from './OwnerDecisionInbox';
import type { CommandEvidence } from './CommandActivity';

export type QueueDestination = 'work' | 'quality';
export interface QueueDecision {
  id: string;
  category: 'approval' | 'next' | 'stale' | 'qa-review' | 'blocked';
  title: string;
  detail: string;
  destination: QueueDestination;
  binding: string;
  requiresApproval: boolean;
}

// Navigational prompts only. Actual authorizations remain in the host-owned
// specification, architecture, plan and QA/repair workflows.
export function buildCommandDecisionQueue(saved: CommandEvidence): QueueDecision[] {
  const items: QueueDecision[] = buildDecisionInbox(saved.decisions).map(item => ({
    id: 'planning:' + item.id + ':' +
      (saved.decisions.specification?.sha256 ?? 'no-spec') + ':' +
      (saved.decisions.plan?.revision ?? 0),
    category: item.kind === 'review' ? 'approval' :
      item.kind === 'stale' ? 'stale' : 'next',
    title: item.title,
    detail: item.detail,
    destination: 'work',
    binding: item.stage === 'specification'
      ? 'Specification v' + (saved.decisions.specification?.version ?? 'not saved') +
        ' · ' + (saved.decisions.specification?.sha256 ?? 'not saved')
      : item.stage === 'architecture'
        ? 'Approved specification SHA-256: ' +
          (saved.decisions.specification?.sha256 ?? 'not saved')
        : item.stage === 'plan'
          ? 'Plan revision: ' + (saved.decisions.plan?.revision ?? 'not saved') +
            ' · current: ' + String(saved.decisions.plan?.current ?? false)
          : 'Project discovery',
    requiresApproval: item.kind === 'review',
  }));
  const engineering = saved.engineering;
  const review = saved.qa;
  const plan = saved.decisions.plan;
  const currentQa = !!engineering && !!review &&
    review.job.engineeringRunId === engineering.job.id &&
    review.job.planSha256 === engineering.job.planSha256 &&
    plan?.current === true && plan.status === 'Approved';
  // Never count historical or unbound findings as current owner decisions.
  const issues = currentQa && review.job.state === 'Reviewed'
    ? review.reports.reduce((count, report) =>
        count + (report.assessment?.findings?.length ?? 0), 0) : 0;
  if (issues > 0) {
    items.push({
      id: 'qa:' + review!.job.id + ':' + engineering!.job.id,
      category: 'qa-review',
      title: 'Inspect ' + issues + ' potential static QA finding' +
        (issues === 1 ? '' : 's'),
      detail: 'Classify findings against the approved requirements. A QA hypothesis is not an approved repair.',
      destination: 'quality',
      binding: 'QA review ' + review!.job.id + ' · source run ' + engineering!.job.id +
        ' · plan SHA-256 ' + review!.job.planSha256,
      requiresApproval: false,
    });
  } else if (engineering?.job.state === 'Failed' ||
      engineering?.job.state === 'Interrupted') {
    items.push({
      id: 'source:' + engineering.job.id + ':' + engineering.job.state,
      category: 'blocked',
      title: 'Inspect interrupted or failed source generation',
      detail: 'A stopped engineering run requires inspection. Saved partial source is not a verified application.',
      destination: 'work',
      binding: 'Engineering run ' + engineering.job.id,
      requiresApproval: false,
    });
  }
  const order: Record<QueueDecision['category'], number> = {
    approval: 0, 'qa-review': 1, blocked: 2, stale: 3, next: 4,
  };
  return items.sort((a, b) => order[a.category] - order[b.category] ||
    a.id.localeCompare(b.id));
}

export function DecisionQueueView({ items, onInspect }: {
  items: readonly QueueDecision[];
  onInspect: (id: string) => void;
}) {
  const approvals = items.filter(item => item.requiresApproval).length;
  return <section className="mc-decision-queue" aria-label="Decision Queue">
    <div className="mc-decision-queue-heading">
      <div><span className="wb-kicker">YOUR ATTENTION</span>
        <h3>Decision Queue</h3></div>
      <span className="pill">{approvals} saved approval{approvals === 1 ? '' : 's'} to review
        {' · '}{items.length} action{items.length === 1 ? '' : 's'}</span>
    </div>
    {items.length ? <ol className="mc-queue-list">{items.map(item =>
      <li key={item.id} data-kind={item.category}>
        <button type="button" aria-haspopup="dialog"
          onClick={() => onInspect(item.id)}>
          <span><strong>{item.title}</strong><small>{item.detail}</small></span>
          <span className="mc-queue-kind">{item.category === 'approval'
            ? 'Owner review' : item.category === 'qa-review' ? 'QA findings' :
              item.category === 'blocked' ? 'Blocked' :
                item.category === 'stale' ? 'Refresh required' : 'Next step'}</span>
        </button>
      </li>)}</ol> :
      <p className="hint">No current decision or outstanding source blocker was found
        in the saved records. This does not establish verification.</p>}
    <p className="hint">Decision Queue is read-only navigation. Viewing a request
      never approves source, starts repairs or authorizes execution.</p>
  </section>;
}
