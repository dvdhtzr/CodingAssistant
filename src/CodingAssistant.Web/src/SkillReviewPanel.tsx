import { useEffect, useState } from 'react';
import { api } from './api';

type SkillUse = { requestId: string; skillName: string; role: string; topic: string;
  model: string; status: string; createdUtc: string; contentSha256: string; reportsSaved?: number };

export function SkillUsagePanel({ token, taskId }: { token: string; taskId: string }) {
  const [events, setEvents] = useState<SkillUse[] | null>(null);
  const [error, setError] = useState(false);
  const [reload, setReload] = useState(0);
  useEffect(() => {
    let closed = false;
    setEvents(null); setError(false);
    void api<{ events: SkillUse[]; discoveryEvents?: SkillUse[]; qaEvents?: SkillUse[] }>(token, '/api/tasks/' + taskId + '/skill-uses')
      .then(value => { if (!closed) setEvents([...value.events, ...(value.discoveryEvents ?? []), ...(value.qaEvents ?? [])]
        .sort((a, b) => a.createdUtc.localeCompare(b.createdUtc))); })
      .catch(() => { if (!closed) setError(true); });
    return () => { closed = true; };
  }, [token, taskId, reload]);
  return <>
    <button type="button" onClick={() => setReload(n => n + 1)}>Refresh skill usage</button>
    {error && <p role="alert">Skill usage could not be loaded.</p>}
    {!events && !error && <p role="status">Loading recorded skill usage…</p>}
    {events?.length === 0 && <p>No skill invocations recorded.</p>}
    {events?.map(event => <details key={event.requestId}>
      <summary>{event.skillName} · {event.status === 'ProposalReturned' ? 'Proposal returned' :
        event.status === 'ResponseReturned' ? 'Validated response returned' :
        event.status === 'CallFailed' ? 'Call failed' :
        event.status === 'Reviewed' ? 'Static reports saved' :
        event.status === 'Running' ? 'QA admitted / running' :
        event.status === 'Interrupted' ? 'QA interrupted' :
        event.status === 'Failed' ? 'QA failed' : 'Admitted; outcome not recorded'}</summary>
      <p>{event.role} · {event.topic} · {event.model}</p>
      <p className="hint">{event.createdUtc} · {event.contentSha256}</p>
      {event.reportsSaved !== undefined && <p>{event.reportsSaved} static file report(s) saved; no tests executed.</p>}
      <p className="hint">Not proof of a saved or approved artifact, execution grant, or runtime verification.</p>
    </details>)}
  </>;
}

type ReviewEvent = { revision: number; decision: string; createdUtc: string; reviewSha256: string };
type Review = {
  skill: { name: string; role: string; version: string; license: string; instructions?: string;
    references?: { relativePath: string; text: string; contentSha256: string }[] | null };
  reviewSha256: string; revision: number; reviewStatus: string;
  approvedForLocalInstructionReview: boolean; active: boolean; history: ReviewEvent[];
};

function SkillReviewItem({ token, taskId, review, onChange }: {
  token: string; taskId: string; review: Review; onChange: (value: Review) => void;
}) {
  const [confirmation, setConfirmation] = useState('');
  const [permission, setPermission] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);
  const approve = !review.approvedForLocalInstructionReview;
  const phrase = (approve ? 'APPROVE LOCAL USE ' : 'REVOKE ') + review.reviewSha256;
  async function submit() {
    if (busy || confirmation !== phrase || (approve && !permission)) return;
    setBusy(true);
    setError(false);
    try {
      const result = await api<Review>(token,
        '/api/tasks/' + taskId + '/skill-reviews/' + review.skill.name, {
          schemaVersion: 1, expectedRevision: review.revision,
          reviewSha256: review.reviewSha256,
          decision: approve ? 'ApproveLocalUse' : 'Revoke',
          confirmation, localUsePermissionConfirmed: approve && permission,
        });
      setConfirmation('');
      setPermission(false);
      onChange(result);
    } catch {
      setError(true);
    } finally {
      setBusy(false);
    }
  }
  return <details>
    <summary>{review.skill.name} · {review.reviewStatus === 'APPROVED_LOCAL_REVIEW_NOT_ACTIVE'
      ? 'Reviewed, inactive' : review.reviewStatus === 'REVOKED' ? 'Revoked'
        : review.reviewStatus === 'STALE_REVIEW' ? 'Review stale' : 'Not reviewed'}</summary>
    <p className="hint">{review.skill.role} · v{review.skill.version}</p>
    <p>Review does not activate model context, execute tools or grant redistribution rights.</p>
    {review.skill.instructions && <p>{review.skill.instructions}</p>}
    {review.skill.references?.map(reference => <details key={reference.relativePath}>
      <summary>Review reference: {reference.relativePath}</summary>
      <p style={{ whiteSpace: 'pre-wrap' }}>{reference.text}</p>
      <p className="hint" style={{ overflowWrap: 'anywhere' }}>{reference.contentSha256}</p>
    </details>)}
    {approve && <label>
      <input type="checkbox" checked={permission} disabled={busy}
        onChange={event => setPermission(event.target.checked)} />
      I reviewed the pinned instructions and all listed references and confirm permission for local instruction use.
      License declaration: {review.skill.license}.
    </label>}
    <p className="hint" style={{ overflowWrap: 'anywhere' }}>{phrase}</p>
    <label>Enter exact review confirmation
      <input type="text" value={confirmation} disabled={busy}
        onChange={event => setConfirmation(event.target.value)} autoComplete="off" />
    </label>
    <button type="button" disabled={busy || confirmation !== phrase || (approve && !permission)}
      onClick={() => void submit()}>{busy ? 'Saving…' : approve ? 'Approve local instruction review' : 'Revoke review'}</button>
    {error && <p role="alert">Review was not confirmed. Refresh the catalog before trying again.</p>}
    {review.history.length > 0 && <details>
      <summary>Review history ({review.history.length})</summary>
      <ol>{review.history.map(event => <li key={event.revision}>
        {event.decision} · <time dateTime={event.createdUtc}>{event.createdUtc}</time>
      </li>)}</ol>
    </details>}
  </details>;
}

export function SkillReviewPanel({ token, taskId }: { token: string; taskId: string }) {
  const [reviews, setReviews] = useState<Review[] | null>(null);
  const [error, setError] = useState(false);
  const [reload, setReload] = useState(0);
  useEffect(() => {
    let closed = false;
    setReviews(null);
    setError(false);
    void api<{ skills: Review[] }>(token, '/api/tasks/' + taskId + '/skill-reviews')
      .then(value => { if (!closed) setReviews(value.skills); })
      .catch(() => { if (!closed) setError(true); });
    return () => { closed = true; };
  }, [token, taskId, reload]);
  return <>
    <p className="hint">Project-specific owner review. No activation or execution grants.</p>
    {error && <p role="alert">Project skill reviews could not be loaded.</p>}
    {!reviews && !error && <p role="status">Loading project skill reviews…</p>}
    <button type="button" onClick={() => setReload(n => n + 1)}>Refresh reviews</button>
    {reviews?.map(review => <SkillReviewItem
      key={review.skill.name + ':' + review.revision + ':' + review.reviewSha256}
      token={token} taskId={taskId} review={review}
      onChange={updated => setReviews(current => current?.map(item =>
        item.skill.name === updated.skill.name ? updated : item) ?? null)} />)}
  </>;
}
