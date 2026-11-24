import { useEffect, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { api } from './api';

export interface EngineeringActivity {
  sequence: number; taskId: string; category: 'Team' | 'Engineering' | 'Diagnostics';
  kind: string; label: string; origin: 'HostObservation' | 'AgentProposal';
  agentId: string; toAgent: string | null; referenceType: string; referenceId: string;
  referenceSequence: number | null; sourceSha256: string | null;
  model: string | null; createdUtc: string;
}
export interface EngineeringActivityPage {
  taskId: string; lastSequence: number; events: EngineeringActivity[];
}
interface ActivityTail { cursor: number; events: EngineeringActivity[]; }
const tailLimit = 120;

// A project-specific contiguous cursor, not a timestamp or a UI-generated
// identifier. Never silently bridge a sequence gap or mix project histories.
export function mergeActivityPage(taskId: string, tail: ActivityTail,
  page: EngineeringActivityPage): ActivityTail {
  if (page.taskId !== taskId || !Number.isSafeInteger(page.lastSequence) ||
      page.lastSequence < tail.cursor) throw Error('Activity scope changed');
  let cursor = tail.cursor;
  const events = [...tail.events];
  for (const entry of page.events) {
    if (entry.taskId !== taskId || !Number.isSafeInteger(entry.sequence) ||
        entry.sequence > page.lastSequence) throw Error('Activity scope changed');
    if (entry.sequence <= cursor) continue;
    if (entry.sequence !== cursor + 1) throw Error('Activity sequence gap');
    cursor = entry.sequence;
    events.push(entry);
  }
  if (cursor < page.lastSequence && cursor === tail.cursor)
    throw Error('Activity replay did not advance');
  return { cursor, events: events.slice(-tailLimit) };
}

type ConnectionState = 'connecting' | 'live' | 'replay' | 'recovering';
function useEngineeringActivity(token: string, taskId: string) {
  const [tail, setTail] = useState<ActivityTail>({ cursor: 0, events: [] });
  const [status, setStatus] = useState<ConnectionState>('connecting');
  useEffect(() => {
    let closed = false;
    let syncing = false;
    let again = false;
    let subscribed = false;
    let scheduled: number | undefined;
    let current: ActivityTail = { cursor: 0, events: [] };
    setTail(current); setStatus('connecting');
    const base = '/api/tasks/' + taskId + '/engineering-activity';
    function schedule() {
      if (closed || scheduled !== undefined) return;
      scheduled = window.setTimeout(() => {
        scheduled = undefined;
        void reconcile();
      }, 100);
    }
    async function reconcile() {
      if (closed) return;
      if (syncing) { again = true; return; }
      syncing = true;
      try {
        for (let count = 0; count < 8; count++) {
          const page = await api<EngineeringActivityPage>(token,
            base + '?after=' + current.cursor + '&limit=200');
          if (closed) return;
          current = mergeActivityPage(taskId,current,page);
          if (current.cursor >= page.lastSequence) break;
          if (count === 7) again = true;
        }
        if (!closed) {
          setTail(current);
          setStatus(subscribed ? 'live' : 'replay');
        }
      } catch {
        if (!closed) setStatus('recovering');
      } finally {
        syncing = false;
        if (again && !closed) { again = false; schedule(); }
      }
    }
    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/api/progress', {
        transport: signalR.HttpTransportType.LongPolling,
        accessTokenFactory: () => token,
      }).withAutomaticReconnect([0,2000,10000])
      .configureLogging(signalR.LogLevel.None).build();
    connection.on('EngineeringActivityUpdated',(hint: { taskId: string }) => {
      if (hint.taskId === taskId) schedule();
    });
    connection.onreconnecting(() => {
      subscribed = false;
      if (!closed) setStatus('replay');
    });
    connection.onclose(() => {
      subscribed = false;
      if (!closed) setStatus('replay');
    });
    async function subscribe() {
      if (closed) return;
      try {
        await connection.invoke('WatchEngineeringActivity',taskId);
        if (closed) return;
        subscribed = true;
      } catch { subscribed = false; }
      // Subscribe first, then fetch durable history; reconnect uses the same cursor.
      await reconcile();
    }
    connection.onreconnected(() => void subscribe());
    void connection.start().then(subscribe).catch(() => {
      if (!closed) void reconcile();
    });
    // Independent bounded replay fallback; no model requests and no mutations.
    const timer = window.setInterval(() => void reconcile(),3000);
    return () => {
      closed = true;
      window.clearInterval(timer);
      if (scheduled !== undefined) window.clearTimeout(scheduled);
      void connection.stop();
    };
  },[token,taskId]);
  return { ...tail,status };
}

const roleNames: Record<string,string> = {
  ProductOwner: 'Product Owner', SolutionArchitect: 'Solution Architect',
  TechnicalLead: 'Technical Lead', SoftwareEngineer: 'Software Engineer',
  LeadSoftwareEngineer: 'Lead Software Engineer', QaEngineer: 'QA Engineer',
  HumanOwner: 'Owner', Host: 'Host',
};

export function EngineeringActivityFeed({ token,taskId,compact }: {
  token: string; taskId: string; compact: boolean;
}) {
  const { events,cursor,status } = useEngineeringActivity(token,taskId);
  const [frozen,setFrozen] = useState<ActivityTail | null>(null);
  const [filter,setFilter] = useState('All');
  const displayed = frozen?.events ?? events;
  const rows = displayed.filter(e => filter === 'All' || e.category === filter)
    .slice(compact ? -3 : -tailLimit).reverse();
  const pending = frozen ? Math.max(0,cursor-frozen.cursor) : 0;
  return <section className={'ca-live-activity' + (compact ? ' is-compact' : '')}
    aria-label="Live engineering activity">
    <div className="ca-live-heading">
      <strong>Live activity</strong>
      <span className="ca-live-status" data-state={status} role="status">
        {status === 'live' ? 'Connected' : status === 'replay' ? 'Saved replay' :
          status === 'recovering' ? 'Recovering' : 'Connecting'}
      </span>
    </div>
    <div className="ca-live-controls">
      {!compact && <select aria-label="Activity category" value={filter}
        onChange={e => setFilter(e.target.value)}>
        <option>All</option><option>Team</option><option>Engineering</option>
        <option>Diagnostics</option>
      </select>}
      <button type="button" className="ca-studio-text-action"
        onClick={() => setFrozen(frozen ? null : { cursor,events })}>
        {frozen ? 'Resume activity feed' : 'Pause activity feed'}
      </button>
    </div>
    {frozen && <p className="ca-live-paused" role="status">Presentation paused
      {pending > 0 ? ' · ' + pending + ' new event' + (pending === 1 ? '' : 's') : ''}
      . Engineering continues.</p>}
    {rows.length ? <ol className="ca-live-rows" aria-label="Committed activity records"
      tabIndex={0}>
      {rows.map(entry => <li key={entry.sequence} data-category={entry.category}>
        <div className="ca-live-row-meta">
          <time dateTime={entry.createdUtc}>{new Date(entry.createdUtc).toLocaleTimeString([],{
            hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false })}</time>
          <span>{entry.origin === 'AgentProposal' ? 'Proposal' : entry.category}</span>
        </div>
        <strong>{roleNames[entry.agentId] ?? entry.agentId}
          {entry.toAgent ? ' → ' + (roleNames[entry.toAgent] ?? entry.toAgent) : ''}</strong>
        <span>{entry.label}</span>
        {!compact && entry.model && <small>{entry.model}</small>}
      </li>)}
    </ol> : <p className="ca-studio-muted">{displayed.length ?
      'No events in this category.' : 'No new activity recorded yet.'}</p>}
    {!compact && <p className="ca-live-note">Committed events since the activity
      upgrade. Earlier records remain in the saved history below.</p>}
  </section>;
}
