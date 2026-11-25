import { useEffect, useState } from 'react';
import { api } from './api';
import { JourneyStage, type JourneyId, type JourneyStep } from './ProjectJourney';

interface Spec {
  version: number;
  sha256: string;
  status: 'Draft' | 'Approved' | 'Superseded';
  model: string;
}
interface Profile {
  id: string;
  version: string;
  qualificationStatus: string;
  executableViaRegistry: boolean;
}
interface Option {
  id: string;
  label: string;
  benefit: string;
  tradeoff: string;
  runtimeProfileId: string | null;
}
interface Decision {
  topic: string;
  revision: number;
  specificationSha256: string;
  recommendedOptionId: string;
  rationale: string;
  options: Option[];
  proposalSha256: string;
  status: string;
  selectedOptionId: string | null;
  specificationCurrent: boolean;
}
const topics = [
  { id: 'technology-stack', label: 'Technology stack' },
  { id: 'architecture-style', label: 'Architecture approach' },
];

export function ArchitectureStudio({ token, taskId, spec, expanded, onExpand, onReady,
  onStageChange }: {
  token: string; taskId: string; spec: Spec | null;
  expanded: boolean;
  onExpand: (stage: JourneyId) => void;
  onReady: () => void;
  onStageChange: (stage: JourneyStep) => void;
}) {
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [decisions, setDecisions] = useState<Decision[]>([]);
  const [choices, setChoices] = useState<Record<string, string>>({});
  const [confirmations, setConfirmations] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let closed = false;
    setDecisions([]); setProfiles([]); setChoices({}); setConfirmations({});
    setError(''); setLoading(true);
    void Promise.all([
      api<{ profiles: Profile[] }>(token, '/api/runtime-profiles'),
      api<{ decisions: Decision[] }>(token,
        '/api/tasks/' + taskId + '/architecture-decisions'),
    ]).then(([registry, saved]) => {
      if (closed) return;
      setProfiles(registry.profiles);
      setDecisions(saved.decisions);
      setChoices(Object.fromEntries(saved.decisions.map(d =>
        [d.topic, d.selectedOptionId ?? ''])));
    }).catch(cause => {
      if (!closed) setError(cause instanceof Error ? cause.message : 'Unable to load decisions.');
    }).finally(() => { if (!closed) setLoading(false); });
    return () => { closed = true; };
  }, [token, taskId, spec?.sha256]);

  async function recommend(topic: string) {
    if (!spec || spec.status !== 'Approved' || busy) return;
    setBusy(topic + ':recommend'); setError('');
    try {
      const previous = decisions.find(d => d.topic === topic);
      const updated = await api<Decision>(token,
        '/api/tasks/' + taskId + '/architecture-decisions/recommend', {
          schemaVersion: 1,
          specificationVersion: spec.version,
          specificationSha256: spec.sha256,
          topic,
          expectedRevision: previous?.revision ?? 0,
        });
      setDecisions(current => [...current.filter(d => d.topic !== topic), updated]);
      setChoices(current => ({ ...current, [topic]: '' }));
      setConfirmations(current => ({ ...current, [topic]: '' }));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Recommendation unavailable.');
    } finally { setBusy(''); }
  }

  async function review(decision: Decision, action: 'Approve' | 'Defer') {
    if (!spec || spec.status !== 'Approved' || busy) return;
    const choice = action === 'Approve' ? choices[decision.topic] : null;
    const exact = (action === 'Approve' ? 'APPROVE ' : 'DEFER ') + decision.proposalSha256;
    if (confirmations[decision.topic] !== exact || (action === 'Approve' && !choice))
      return;
    setBusy(decision.topic + ':review'); setError('');
    try {
      const updated = await api<Decision>(token,
        '/api/tasks/' + taskId + '/architecture-decisions/' + decision.topic + '/review', {
          schemaVersion: 1,
          expectedRevision: decision.revision,
          proposalSha256: decision.proposalSha256,
          action,
          selectedOptionId: choice,
          confirmation: exact,
        });
      const revised = decisions.map(d => d.topic === updated.topic ? updated : d);
      setDecisions(revised);
      setConfirmations(current => ({ ...current, [decision.topic]: '' }));
      if (action === 'Approve' && topics.every(topic =>
        revised.some(d => d.topic === topic.id && d.status === 'Approved' &&
          d.specificationCurrent && d.specificationSha256 === spec.sha256))) {
        onReady();
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Review could not be saved.');
    } finally { setBusy(''); }
  }

  const fresh = decisions.filter(d => d.specificationCurrent &&
    d.specificationSha256 === spec?.sha256);
  const approvedCount = topics.filter(topic => fresh.some(d =>
    d.topic === topic.id && d.status === 'Approved')).length;
  const awaiting = fresh.filter(d => d.status === 'Proposed').length;
  const stale = decisions.some(d => !d.specificationCurrent ||
    d.specificationSha256 !== spec?.sha256);
  // Saved alternatives never authorize a decision when their source spec
  // changed. Show a route back to an approved spec, then new alternatives.
  const needsApprovedSpec = spec?.status !== 'Approved';
  const staleTopics = topics.filter(topic => decisions.some(d => d.topic === topic.id &&
    (!d.specificationCurrent || d.specificationSha256 !== spec?.sha256)));
  const step: JourneyStep = { id: 'architecture', title: 'Solution Architecture',
    detail: 'Technology stack and design choices',
    ...((spec?.status !== 'Approved')
      ? { tone: 'pending' as const, status: 'Waiting for approved specification' }
      : loading
        ? { tone: 'pending' as const, status: 'Loading saved decisions' }
        : stale
          ? { tone: 'blocked' as const, status: 'Re-review changed requirements' }
          : approvedCount === topics.length
            ? { tone: 'complete' as const, status: 'Both decisions approved' }
            : awaiting
              ? { tone: 'attention' as const,
                status: awaiting + ' decision(s) awaiting approval' }
              : { tone: 'active' as const,
                status: approvedCount + ' of ' + topics.length + ' approved' }),
  };
  useEffect(() => { onStageChange(step); },
    [onStageChange, step.tone, step.status]);
  return <JourneyStage step={step} index={2} open={expanded} onOpen={onExpand}>
    <section className="architect-stage" aria-label="Solution Architect decisions">
    <h2>3. Review the Solution Architect's recommendations</h2>
    <p className="hint">The selected local Ollama model compares options for your approved
      specification. AI recommendations are not owner approval or executable permission.</p>
    {!loading && needsApprovedSpec && <div className="architect-recovery"
      role="status" data-reason="specification-needs-approval">
      <div><strong>The current specification needs approval</strong>
        <p>These saved architecture alternatives are not available for approval
          until you review and approve the current requirements. Historical
          recommendations are kept read-only.</p></div>
      <button type="button" className="primary"
        onClick={() => onExpand(spec ? 'specification' : 'discovery')}>
        {spec ? 'Review current specification' : 'Return to Product Discovery'}
      </button>
    </div>}
    {!loading && !needsApprovedSpec && staleTopics.length > 0 &&
      <div className="architect-recovery" role="status" data-reason="stale-recommendations">
        <div><strong>{staleTopics.length} architecture recommendation{
          staleTopics.length === 1 ? '' : 's'} need refreshing</strong>
          <p>Your current specification is approved, but the alternatives below
            were created for older requirements. Use the
            <strong> Refresh recommendations</strong> action in each affected section.
            You'll be able to select alternatives after the updated proposal is
            saved. Nothing will be approved automatically.</p></div>
      </div>}
    {loading && <p className="hint">Loading saved architecture decisions…</p>}
    {error && <div className="alert" role="alert">{error}
      {error.includes('MODEL_OUTPUT_REJECTED') &&
        <p>The local model returned an invalid recommendation. Nothing was saved.</p>}
    </div>}
    {!loading && topics.map(topic => {
      const decision = decisions.find(d => d.topic === topic.id);
      const fresh = !!decision && decision.specificationCurrent &&
        decision.specificationSha256 === spec?.sha256;
      const pending = fresh && decision?.status === 'Proposed';
      const choice = choices[topic.id] ?? '';
      const exactText = confirmations[topic.id] ?? '';
      const action = exactText.startsWith('DEFER ') ? 'Defer' : 'Approve';
      const exact = (action === 'Defer' ? 'DEFER ' : 'APPROVE ') +
        (decision?.proposalSha256 ?? '');
      return <div className="architect-decision" key={topic.id}>
        <div className="actions">
          <strong>{topic.label}</strong>
          <span className="pill">{!decision ? 'Not proposed' :
            !fresh ? 'Stale specification' : decision.status}</span>
        </div>
        {needsApprovedSpec ?
          <button type="button" className="outline architect-unblock"
            disabled={!!busy}
            onClick={() => onExpand(spec ? 'specification' : 'discovery')}>
            {spec ? 'Open specification to continue' :
              'Complete Product Discovery to continue'}
          </button> :
          <button type="button" className={!fresh && decision ?
            'primary architect-unblock' : 'outline'}
            disabled={!!busy}
            onClick={() => void recommend(topic.id)}>
            {busy === topic.id + ':recommend' ? 'Asking local model…' :
              decision && !fresh ? 'Refresh ' + topic.label.toLowerCase() +
                ' recommendations' :
              decision ? 'Request updated recommendations' : 'Ask Solution Architect'}
          </button>}
        {busy === topic.id + ':recommend' && <div className="activity" role="status">
          <span className="activity-dot" aria-hidden="true" />
          <strong>Waiting for the local Solution Architect</strong>
          <p className="hint">No simulated completion percentage. Only validated results are saved.</p>
        </div>}
        {decision && <>
          <p className="hint">Proposal v{decision.revision}. SHA-256:
            <code className="architect-hash"> {decision.proposalSha256}</code></p>
          {!fresh && <p className="architect-stale-note" role="note">
            These alternatives belong to an older specification. They are preserved
            for reference and cannot be selected or approved. {
              needsApprovedSpec ? 'Approve the current specification first.' :
                'Refresh recommendations above to continue.'}
          </p>}
          <p className="architect-rationale">{decision.rationale}</p>
          {pending ? <fieldset className="architect-options" disabled={!!busy}>
            <legend>Select an alternative for review</legend>
            {decision.options.map(option => {
              const profile = profiles.find(p => p.id === option.runtimeProfileId);
              return <label className="architect-option" key={option.id}>
                <input type="radio" name={'architecture-' + topic.id}
                  checked={choice === option.id}
                  onChange={() => {
                    setChoices(current => ({ ...current, [topic.id]: option.id }));
                    setConfirmations(current => ({ ...current, [topic.id]: '' }));
                  }} />
                <span>
                  <strong>{option.label}</strong>
                  {decision.recommendedOptionId === option.id &&
                    <span className="pill">AI recommendation only</span>}
                  <small>Benefit: {option.benefit}</small>
                  <small>Tradeoff: {option.tradeoff}</small>
                  <small>{profile
                    ? 'Runtime ' + profile.id + ' v' + profile.version + ': ' +
                      profile.qualificationStatus +
                      (profile.executableViaRegistry ? '' : ' (not executable)')
                    : 'No qualified execution profile assigned'}</small>
                </span>
              </label>;
            })}
          </fieldset> : <details className="architect-archived">
            <summary>{!fresh ? 'View older alternatives (read-only)' :
              decision.status === 'Approved' ? 'View approved alternatives (read-only)' :
                'View saved alternatives (read-only)'}</summary>
            <div className="architect-archived-list">
              {decision.options.map(option => {
                const profile = profiles.find(p => p.id === option.runtimeProfileId);
                return <div className="architect-option" key={option.id}>
                  <span>
                    <strong>{option.label}</strong>
                    {decision.selectedOptionId === option.id && fresh &&
                      decision.status === 'Approved' &&
                      <span className="pill">Owner-approved choice</span>}
                    <small>Benefit: {option.benefit}</small>
                    <small>Tradeoff: {option.tradeoff}</small>
                    <small>{profile
                      ? 'Runtime ' + profile.id + ' v' + profile.version + ': ' +
                        profile.qualificationStatus +
                        (profile.executableViaRegistry ? '' : ' (not executable)')
                      : 'No qualified execution profile assigned'}</small>
                  </span>
                </div>;
              })}
            </div>
          </details>}
          {fresh && decision.status === 'Approved' &&
            <p className="success">✓ Owner approved: {decision.options.find(o =>
              o.id === decision.selectedOptionId)?.label}.
              This is not execution approval.</p>}
          {fresh && decision.status === 'Deferred' &&
            <p className="hint">Decision deferred. No stack was approved.</p>}
          {pending && <>
            <p className="hint">Select an option for explicit approval or defer this decision.
              Recommendations are never preselected.</p>
            <div className="actions">
              <button className="outline" type="button" disabled={!choice || !!busy}
                onClick={() => setConfirmations(current =>
                  ({ ...current, [topic.id]: 'APPROVE ' + decision.proposalSha256 }))}>
                Prepare approval
              </button>
              <button className="outline" type="button" disabled={!!busy}
                onClick={() => setConfirmations(current =>
                  ({ ...current, [topic.id]: 'DEFER ' + decision.proposalSha256 }))}>
                Prepare deferral
              </button>
            </div>
            <label>Exact decision confirmation
              <input className="token" value={exactText} spellCheck={false}
                autoComplete="off"
                onChange={e => setConfirmations(current =>
                  ({ ...current, [topic.id]: e.target.value }))} />
            </label>
            <button className="primary" type="button"
              disabled={!!busy || exactText !== exact || (action === 'Approve' && !choice)}
              onClick={() => void review(decision, action)}>
              {action === 'Approve' ? 'Approve selected alternative' : 'Defer this decision'}
            </button>
          </>}
        </>}
      </div>;
    })}
    <p className="hint">The source-only generator uses both owner-approved decisions.
      Architecture approval is not execution authorization; generated files remain
      unverified until an independent build and test.</p>
    </section>
  </JourneyStage>;
}
