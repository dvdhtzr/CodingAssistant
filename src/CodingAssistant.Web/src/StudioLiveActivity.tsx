import { useEffect, useState } from 'react';
import { api } from './api';
import type { StudioLedgerEntry } from './StudioPresence';
import type { JourneyId } from './ProjectJourney';

export interface HttpActivityRecord {
  sequence: number;
  recordedUtc: string;
  method: string;
  route: string;
  statusCode: number;
  durationMs: number;
}
interface HttpActivityPage {
  events: HttpActivityRecord[];
  lastSequence: number;
  truncated: boolean;
  capacity: number;
}

function clock(value: string) {
  return new Date(value).toLocaleTimeString([], {
    hour: 'numeric', minute: '2-digit', second: '2-digit',
  });
}
function briefRoute(route: string) {
  // Already sanitized and templated by the host. This display transform
  // does not infer the success of an agent task from an HTTP response.
  return route === 'API request' ? 'API request' : route;
}

export function StudioLiveActivity({ token, active, recorded, onInspect }: {
  token: string;
  active: boolean;
  recorded: readonly StudioLedgerEntry[];
  onInspect: (stage: JourneyId) => void;
}) {
  const [recent, setRecent] = useState<HttpActivityRecord[]>([]);
  const [expanded, setExpanded] = useState(false);
  const [tab, setTab] = useState<'traffic' | 'agents'>('traffic');
  const [paused, setPaused] = useState(false);
  const [connection, setConnection] = useState<'loading' | 'ok' | 'unavailable'>('loading');
  const [skipped, setSkipped] = useState(false);

  useEffect(() => {
    setRecent([]);
    setSkipped(false);
    setConnection('loading');
  }, [token]);

  useEffect(() => {
    if (!active || paused) return;
    setConnection('loading');
    let closed = false;
    let pending = false;
    let cursor = 0;
    async function reconcile() {
      if (closed || pending || document.visibilityState === 'hidden') return;
      pending = true;
      try {
        const result = await api<HttpActivityPage>(token,
          '/api/studio/http-activity?after=' + cursor + '&limit=30');
        if (closed) return;
        if (result.truncated) setSkipped(true);
        cursor = result.lastSequence;
        if (result.events.length) {
          setRecent(previous => {
            const already = new Set(previous.map(item => item.sequence));
            return [...previous, ...result.events.filter(item =>
              !already.has(item.sequence))].slice(-48);
          });
        }
        setConnection('ok');
      } catch {
        if (!closed) setConnection('unavailable');
      } finally { pending = false; }
    }
    void reconcile();
    const timer = window.setInterval(() => void reconcile(), 2500);
    const onVisibility = () => { if (document.visibilityState === 'visible') void reconcile(); };
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      closed = true;
      window.clearInterval(timer);
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [token, active, paused]);

  const current = recent.at(-1);
  const description = paused ? 'Paused' : !active ? 'Inactive' :
    connection === 'loading' ? 'Connecting' :
    connection === 'unavailable' ? 'Connection unavailable' : 'Local API';
  return <section className="ca-live-activity" aria-label="Live Activity">
    <div className="ca-live-activity-strip">
      <button type="button" className="ca-live-activity-toggle"
        aria-expanded={expanded} aria-controls="ca-live-activity-feed"
        onClick={() => setExpanded(value => !value)}>
        <span aria-hidden="true" className="ca-live-activity-icon">▥</span>
        <strong>Live Activity</strong>
        <span className="ca-live-activity-expansion">{expanded ? 'Collapse' : 'Explore feed'}</span>
      </button>
      <span className="ca-live-activity-source" data-state={
        paused ? 'paused' : connection}>
        <span aria-hidden="true" className="ca-live-activity-dot" />
        {description}
      </span>
      <span className="ca-live-activity-latest" title={current ?
        current.method + ' ' + current.route : 'No recorded API traffic in this session'}>
        {current && !paused ? <>
          <strong>{current.method}</strong> {briefRoute(current.route)}
          {' · '}{current.statusCode} · {current.durationMs} ms
        </> : paused ? 'Traffic display paused' : 'No recent API activity'}
      </span>
      <button type="button" className="ca-live-activity-team"
        onClick={() => { setExpanded(true); setTab('agents'); }}>
        {recorded.length} saved team event{recorded.length === 1 ? '' : 's'}
      </button>
    </div>
    {expanded && <div className="ca-live-activity-feed" id="ca-live-activity-feed">
      <div className="ca-live-activity-toolbar">
        <div role="group" aria-label="Activity source">
          <button type="button" aria-pressed={tab === 'traffic'}
            onClick={() => setTab('traffic')}>System traffic</button>
          <button type="button" aria-pressed={tab === 'agents'}
            onClick={() => setTab('agents')}>Agent work</button>
        </div>
        <button type="button" className="ca-live-activity-pause"
          onClick={() => setPaused(value => !value)}>
          {paused ? 'Resume feed' : 'Pause feed'}
        </button>
      </div>
      {tab === 'traffic' && <div className="ca-live-activity-body"
        aria-label="Authenticated API request summaries">
        <p className="ca-live-activity-note">Recent authenticated local API requests.
          An HTTP 200 means the request completed—not that an agent finished work.
          No URLs, IDs, prompts, credentials, or source content are retained.</p>
        {skipped && <p className="ca-live-activity-note">
          Earlier activity rolled off the bounded buffer.</p>}
        {recent.length ? <ol className="ca-live-activity-list" aria-live="off">
          {recent.slice().reverse().slice(0, 18).map(entry =>
            <li key={entry.sequence} data-result={
              entry.statusCode >= 400 ? 'error' : 'ok'}>
              <time dateTime={entry.recordedUtc}>{clock(entry.recordedUtc)}</time>
              <span className="ca-live-activity-method">{entry.method}</span>
              <span className="ca-live-activity-route">{briefRoute(entry.route)}</span>
              <span className="ca-live-activity-metric">{entry.statusCode} · {entry.durationMs} ms</span>
            </li>)}
        </ol> : <p className="ca-studio-muted">{connection === 'unavailable' ?
          'System activity could not be retrieved. The project workflow is unaffected.' :
          'No recent request summaries. The studio is not running background work.'}</p>}
      </div>}
      {tab === 'agents' && <div className="ca-live-activity-body"
        aria-label="Persisted agent event summaries">
        <p className="ca-live-activity-note">Saved source tasks, independent QA reviews,
          and agent handoffs. Historical events are not evidence of current activity.</p>
        {recorded.length ? <ol className="ca-live-activity-list ca-live-agent-list" aria-live="off">
          {recorded.slice(0, 18).map(entry => <li key={entry.id}>
            <time dateTime={entry.at}>{clock(entry.at)}</time>
            <span className="ca-live-activity-agent-label">
              <strong>{entry.title}</strong>
              <small>{entry.detail}{entry.historical ? ' · Historical' : ''}</small>
            </span>
            <button type="button" className="ca-studio-text-action"
              onClick={() => onInspect(entry.stage)}>Inspect</button>
          </li>)}
        </ol> : <p className="ca-studio-muted">No saved agent work or handoffs for this project.</p>}
      </div>}
      <p className="ca-live-activity-footer">System traffic stays in a 120-event memory buffer
        and disappears on restart. The Studio polls only while visible and unpaused.
        The source and approval ledgers remain authoritative.</p>
    </div>}
  </section>;
}
