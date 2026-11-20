import { useEffect, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import {
  api, newRequestId, isHexId,
  type ApiStatus, type TaskItem, type TaskView, type RunItem,
  type RunEvent, type EventPage, type ExecutionView, type PlanItem,
  type RunResult, type RunResultResponse,
} from './api';

const terminal = new Set(['Succeeded', 'Failed', 'Cancelled', 'Interrupted', 'RecoveryBlocked']);
const runtimeStates = new Set(['Previewed', 'Authorized', 'Starting', 'Running', 'Cancelling',
  'Succeeded', 'Failed', 'Cancelled', 'Interrupted', 'RecoveryBlocked']);

// These descriptions correspond to fixed host-runner milestones, not model text.
const stages: Record<string, string> = {
  ExecutionStageScopeVerified: 'Verified the pinned model and approved disposable scope.',
  ExecutionStageWorkspacePrepared: 'Created an isolated copy of the C# test project.',
  ExecutionStageInferenceBridgeReady: 'Connected the restricted local Ollama socket.',
  ExecutionStageGatewayLaunched: 'Started the isolated Gateway container.',
  ExecutionStageGatewayReady: 'Gateway health checks passed.',
  ExecutionStageCodingRequestSent: 'Entering the coding RPC phase; waiting for the local agent to respond.',
  ExecutionStageCodingRequestReturned: 'Agent returned a result; verifying the workspace.',
  ExecutionStageGatewayRemoved: 'Removed the isolated agent container.',
  ExecutionStageEvaluatorStarted: 'Started independent offline .NET verification.',
  ExecutionStageEvaluatorPassed: 'The independent evaluator passed.',
};
function elapsed(seconds: number): string {
  const safe = Math.max(0, Math.floor(seconds));
  if (safe < 60) return safe + 's';
  if (safe < 3600) return Math.floor(safe / 60) + 'm ' + String(safe % 60).padStart(2, '0') + 's';
  return Math.floor(safe / 3600) + 'h ' + String(Math.floor(safe / 60) % 60).padStart(2, '0') + 'm';
}

function ExactConsent({ planHash, value, onChange, description }: {
  planHash: string; value: string; onChange: (v: string) => void; description: string;
}) {
  return <div>
    <p className="hint">{description} Approval must match this full, immutable SHA-256:</p>
    <div className="hash">{planHash}</div>
    <p className="hint">Enter <code>APPROVE {planHash}</code> exactly (no shortened hash).</p>
    <button type="button" className="outline" onClick={() => onChange('APPROVE ' + planHash)}>
      Fill exact approval (does not authorize)
    </button>
    <label>Exact approval
      <input className="token" value={value} onChange={e => onChange(e.target.value)}
        autoComplete="off" autoCapitalize="off" spellCheck={false}
        placeholder="APPROVE &lt;full hash&gt;" />
    </label>
  </div>;
}

export default function App() {
  const [tokenEntry, setTokenEntry] = useState('');
  const [token, setToken] = useState('');
  const [status, setStatus] = useState<ApiStatus | null>(null);
  const [tasks, setTasks] = useState<TaskItem[]>([]);
  const [taskId, setTaskId] = useState('');
  const [taskView, setTaskView] = useState<TaskView | null>(null);
  const [runs, setRuns] = useState<RunItem[]>([]);
  const [runId, setRunId] = useState('');
  const [run, setRun] = useState<RunItem | null>(null);
  const [events, setEvents] = useState<RunEvent[]>([]);
  const [execution, setExecution] = useState<ExecutionView | null>(null);
  const [result, setResult] = useState<RunResult | null>(null);
  const [resultChecked, setResultChecked] = useState(false);
  const [resultError, setResultError] = useState(false);
  const [newTitle, setNewTitle] = useState('');
  const [newObjective, setNewObjective] = useState('');
  const [planSummary, setPlanSummary] = useState('');
  const [planConsent, setPlanConsent] = useState('');
  const [executionConsent, setExecutionConsent] = useState('');
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [live, setLive] = useState('Not connected');
  const [taskRevision, setTaskRevision] = useState(0);
  const [runRevision, setRunRevision] = useState(0);
  const [clock, setClock] = useState(Date.now());

  // Local display clock only; a ticking indicator is NOT a heartbeat from Qwen.
  useEffect(() => {
    if (!token || !runId) return;
    const timer = window.setInterval(() => setClock(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [token, runId]);

  async function action(name: string, operation: () => Promise<void>) {
    setBusy(name);
    setError('');
    try {
      await operation();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Operation failed.');
    } finally {
      setBusy('');
    }
  }
  function refreshTask() { setTaskRevision(v => v + 1); }
  function refreshRun() { setRunRevision(v => v + 1); }
  function chooseTask(id: string) {
    if (!isHexId(id)) return;
    setTaskId(id);
    setTaskView(null);
    setRuns([]);
    setRunId('');
    setRun(null);
    setExecution(null);
    setResult(null); setResultChecked(false); setResultError(false);
    setEvents([]);
    setPlanConsent('');
    setExecutionConsent('');
  }
  function chooseRun(id: string) {
    if (!isHexId(id)) return;
    setRunId(id);
    setRun(null);
    setExecution(null);
    setResult(null); setResultChecked(false); setResultError(false);
    setEvents([]);
    setExecutionConsent('');
  }
  async function fetchTasks(secret = token) {
    const result = await api<TaskItem[]>(secret, '/api/tasks');
    setTasks(result);
    return result;
  }
  function disconnect() {
    setToken('');
    setTokenEntry('');
    setStatus(null);
    setTasks([]);
    setTaskId('');
    setRunId('');
    setTaskView(null);
    setRuns([]);
    setRun(null);
    setExecution(null);
    setResult(null); setResultChecked(false); setResultError(false);
    setEvents([]);
    setError('');
  }
  useEffect(() => {
    if (!token) return;
    let closed = false;
    void api<TaskItem[]>(token, '/api/tasks')
      .then(list => { if (!closed) setTasks(list); })
      .catch(() => { if (!closed) setError('Unable to load tasks.'); });
    return () => { closed = true; };
  }, [token, taskRevision]);

  useEffect(() => {
    if (!token || !taskId) return;
    let closed = false;
    void Promise.all([
      api<TaskView>(token, `/api/tasks/${taskId}`),
      api<RunItem[]>(token, `/api/tasks/${taskId}/runs`),
    ]).then(([view, list]) => {
      if (closed) return;
      setTaskView(view);
      setRuns(list);
    }).catch((e: unknown) => {
      if (!closed) setError(e instanceof Error ? e.message : 'Task refresh failed.');
    });
    return () => { closed = true; };
  }, [token, taskId, taskRevision]);

  useEffect(() => {
    if (!token || !runId) return;
    let closed = false;
    let cursor = 0;
    let refreshing = false;
    let resultLoaded = false;
    setEvents([]);
    setRun(null);
    setExecution(null);
    setResult(null); setResultChecked(false); setResultError(false);
    async function sync() {
      if (refreshing || closed) return;
      refreshing = true;
      try {
        const current = await api<RunItem>(token, `/api/runs/${runId}`);
        if (closed) return;
        setRun(current);
        const updates: RunEvent[] = [];
        // Bounded paging closes the subscribe/snapshot race and survives reconnect.
        for (let pageNumber = 0; pageNumber < 30; pageNumber++) {
          const page = await api<EventPage>(token,
            `/api/runs/${runId}/events?after=${cursor}&limit=100`);
          if (closed) return;
          for (const event of page.events) {
            if (event.runId !== runId || event.sequence !== cursor + 1) {
              throw new Error('Durable event sequence mismatch.');
            }
            cursor = event.sequence;
            updates.push(event);
          }
          if (page.events.length < 100) break;
        }
        if (updates.length && !closed) setEvents(previous => [...previous, ...updates]);
        if (runtimeStates.has(current.state)) {
          try {
            const preview = await api<ExecutionView>(token, `/api/runs/${runId}/execution-plan`);
            if (!closed) setExecution(preview);
          } catch {
            // A pending intent legitimately has no executable preview yet.
          }
        }
        if (terminal.has(current.state) && !resultLoaded) {
          try {
            const report = await api<RunResultResponse>(token, `/api/runs/${runId}/result`);
            if (!closed) {
              setResult(report.available ? report.result : null);
              setResultChecked(true);
              setResultError(false);
            }
            resultLoaded = true;
          } catch {
            // Do not conflate failure to read an archive with a failed code run.
            if (!closed) { setResultError(true); setResultChecked(true); }
          }
        }
        if (!closed) setLive('Durable history synchronized');
      } catch {
        if (!closed) setLive('Offline — will retry durable replay');
      } finally { refreshing = false; }
    }
    void sync();
    const interval = window.setInterval(() => { void sync(); }, 3000);

    // Browser WebSockets cannot set the Authorization header. Use authenticated
    // SignalR LongPolling; SQLite replay remains authoritative during outages.
    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/api/progress', {
        transport: signalR.HttpTransportType.LongPolling,
        accessTokenFactory: () => token,
      })
      .withAutomaticReconnect([0, 2000, 10000])
      .build();
    connection.on('RunUpdated', () => { void sync(); });
    connection.onreconnected(() => {
      void connection.invoke('WatchRun', runId).then(() => sync()).catch(() => {});
    });
    void connection.start()
      .then(async () => {
        if (closed) return;
        await connection.invoke('WatchRun', runId);
        await sync();
      }).catch(() => {
        if (!closed) setLive('SignalR unavailable — using durable replay');
      });
    return () => {
      closed = true;
      window.clearInterval(interval);
      void connection.stop();
    };
  }, [token, runId, runRevision]);

  const plan: PlanItem | null = taskView?.plan ?? null;
  const planApproved = plan?.status === 'Approved';
  const executionState = execution?.state ?? '';
  const activeRun = run?.state ?? '';
  const canCancel = run && !terminal.has(activeRun) && activeRun !== 'Cancelling';
  const isBusy = busy !== '';
  const active = run?.state === 'Running' || run?.state === 'Starting' || run?.state === 'Cancelling';
  const startedEvent = events.find(event => event.kind === 'ExecutionStarted');
  const latestEvent = events.at(-1);
  const latestStage = [...events].reverse().find(event => event.kind.startsWith('ExecutionStage'));
  const referenceEvent = latestStage ?? startedEvent ?? latestEvent;
  const runSeconds = startedEvent
    ? (clock - Date.parse(startedEvent.createdUtc)) / 1000 : 0;
  const silenceSeconds = referenceEvent
    ? (clock - Date.parse(referenceEvent.createdUtc)) / 1000 : 0;
  const stageMessage = latestStage
    ? stages[latestStage.kind] ?? 'Unrecognized milestone — examine the durable history.'
    : 'No intermediate runner milestone recorded yet.';

  return <>
    <header>
      <div>
        <h1>◈ CodingAssistant <span className="pill">Mission Control / M3</span></h1>
        <p>Local-first coding • OpenClaw → ACPX → OpenCode → Ollama</p>
      </div>
      {token && <div className="actions">
        <span className="pill">{status?.executionEnabled ? 'Local execution enabled' : 'Execution disabled'}</span>
        <button className="outline" onClick={disconnect}>Disconnect</button>
      </div>}
    </header>

    {error && <div className="alert" role="alert">{error}</div>}

    {!token && <section className="panel login">
      <h2>Owner session</h2>
      <p className="hint">Connect to the local API using your existing bearer token.
        The token stays in this tab’s memory, never in browser storage, URLs or logs.</p>
      <form onSubmit={e => { e.preventDefault(); void action('connect', async () => {
        const verified = await api<ApiStatus>(tokenEntry, '/api/status');
        if (!verified.localOnly) throw new Error('Refusing non-local control plane.');
        setStatus(verified);
        setToken(tokenEntry);
        setTokenEntry('');
      }); }}>
        <label>64-character owner token
          <input type="password" className="token" value={tokenEntry}
            onChange={e => setTokenEntry(e.target.value)}
            autoComplete="off" spellCheck={false} aria-label="Owner bearer token" />
        </label>
        <div className="actions"><button className="primary" disabled={isBusy || !/^[0-9a-f]{64}$/.test(tokenEntry)}>
          {busy === 'connect' ? 'Connecting…' : 'Connect locally'}
        </button></div>
      </form>
      <p className="secure">No cloud AI • no automatic coding • explicit approval required</p>
    </section>}

    {token && <div className="layout">
      <aside className="stack">
        <section className="panel">
          <h2>New task</h2>
          <form onSubmit={e => { e.preventDefault(); void action('create-task', async () => {
            const created = await api<TaskItem>(token, '/api/tasks', {
              title: newTitle, objective: newObjective, requestId: newRequestId(),
            });
            setNewTitle(''); setNewObjective('');
            await fetchTasks();
            chooseTask(created.id);
          }); }}>
            <label>Title <input value={newTitle} maxLength={120} required onChange={e => setNewTitle(e.target.value)} /></label>
            <label>Objective <textarea value={newObjective} maxLength={4000} required onChange={e => setNewObjective(e.target.value)} /></label>
            <button className="primary" disabled={isBusy || !newTitle.trim() || !newObjective.trim()}>Create task</button>
          </form>
        </section>
        <section className="panel">
          <div className="actions" style={{ justifyContent: 'space-between' }}>
            <h2>Tasks</h2>
            <button className="outline" disabled={isBusy} onClick={() => void action('refresh', async () => { await fetchTasks(); refreshTask(); })}>↻</button>
          </div>
          {!tasks.length && <p className="hint">No tasks yet.</p>}
          {tasks.map(task => <button key={task.id}
            className={'task' + (task.id === taskId ? ' selected' : '')}
            onClick={() => chooseTask(task.id)}>
            {task.title}<small>{task.status} · plan v{task.currentVersion}</small>
          </button>)}
        </section>
      </aside>
      <main className="stack">
        {!taskView && <section className="panel">
          <h2>Choose a task</h2><p className="hint">Create or select a task to review planning approval and coding history.</p>
        </section>}
        {taskView && <section className="panel">
          <h2>{taskView.task.title} <span className="pill">{taskView.task.status}</span></h2>
          <p>{taskView.task.objective}</p>
          <div className="subtle" />
          <h3>1 · Planning record</h3>
          {!plan && <p className="hint">No plan yet. Drafting does not authorize execution.</p>}
          {plan && <>
            <p>{plan.summary}</p>
            <dl className="meta">
              <dt>Version</dt><dd>{plan.version} · {plan.status}</dd>
              <dt>Model</dt><dd>{plan.model}</dd>
              <dt>Scope</dt><dd>{plan.scope}</dd>
            </dl>
            <div className="hash">{plan.planHash}</div>
          </>}
          <form onSubmit={e => { e.preventDefault(); void action('draft-plan', async () => {
            await api(token, `/api/tasks/${taskId}/plans`, {
              summary: planSummary, expectedVersion: taskView.task.currentVersion,
            });
            setPlanSummary(''); setPlanConsent(''); refreshTask();
          }); }}>
            <label>Draft/revise planning summary
              <textarea value={planSummary} maxLength={1500} required
                onChange={e => setPlanSummary(e.target.value)} />
            </label>
            <button disabled={isBusy || !planSummary.trim()}>Save new immutable plan version</button>
          </form>
          {plan?.status === 'Pending' && <div>
            <div className="subtle" />
            <ExactConsent planHash={plan.planHash} value={planConsent}
              onChange={setPlanConsent} description="Planning-only consent." />
            <button className="primary" disabled={isBusy || planConsent !== 'APPROVE ' + plan.planHash}
              onClick={() => void action('approve-plan', async () => {
                await api(token, `/api/tasks/${taskId}/approvals`,
                  { version: plan.version, approval: planConsent });
                setPlanConsent(''); refreshTask();
              })}>Approve planning hash</button>
          </div>}
        </section>}

        {taskView && <section className="panel">
          <h3>2 · Run intents and execution history</h3>
          <p className="hint">A planning approval creates permission to request a run intent; it does not authorize an agent.</p>
          <button className="primary" disabled={isBusy || !planApproved}
            onClick={() => void action('create-run', async () => {
              if (!plan) return;
              const created = await api<RunItem>(token, `/api/tasks/${taskId}/runs`, {
                planVersion: plan.version, planHash: plan.planHash, requestId: newRequestId(),
              });
              chooseRun(created.id);
              refreshTask();
            })}>Create pending run intent</button>
          {!runs.length && <p className="hint">No run history yet.</p>}
          <div className="run-list">{runs.map(item => <button key={item.id}
            className={'task' + (runId === item.id ? ' selected' : '')}
            onClick={() => chooseRun(item.id)}>
            <span>{item.state}</span>
            <small>{item.id.slice(0, 12)} · plan v{item.planVersion}
              {item.planCurrent ? '' : ' · superseded'}</small>
          </button>)}</div>
        </section>}

        {run && <section className="panel">
          <h3>3 · Run control <span className="pill">{run.state}</span></h3>
          <section className="result-panel" aria-label="Run result">
            <h3>Run result</h3>
            {!terminal.has(run.state) && <p className="hint">
              The run has not finished. Actual verification output will appear here
              automatically when the isolated evaluator finishes or a failure is recorded.
            </p>}
            {terminal.has(run.state) && <>
              <p className={run.state === 'Succeeded' ? 'success' : 'alert'}>
                {run.state === 'Succeeded'
                  ? '✓ Verified execution succeeded'
                  : 'Execution ended: ' + run.state +
                    '. A non-successful or interrupted run is never counted as verified.'}
              </p>
              {result && <>
                <dl className="meta result-meta">
                  <dt>Saved runner verdict</dt><dd>{result.status}</dd>
                  <dt>Last stage</dt><dd>{result.phase.replaceAll('_', ' ')}</dd>
                  <dt>Independent .NET evaluator</dt><dd>
                    {result.evaluatorVerified === true ? 'PASS' :
                      result.evaluatorVerified === false ? 'FAILED' : 'Not reached / not recorded'}
                  </dd>
                  <dt>Source changed</dt><dd>{result.sourceChanged === null ? 'Unknown' :
                    result.sourceChanged ? 'Yes — api/Program.cs (disposable copy)' : 'No'}</dd>
                  <dt>Verified local model responses</dt><dd>
                    {result.inferenceRequests ?? 'Not recorded'}
                  </dd>
                  {result.reasonCode && <><dt>Stop reason</dt><dd>{result.reasonCode}</dd></>}
                </dl>
                {result.patch && <>
                  <h4>Verified C# changes</h4>
                  <p className="hint">Diff of the approved disposable api/Program.cs;
                    no original project files were modified.</p>
                  <pre className="verified-patch">{result.patch}</pre>
                </>}
                {result.patchOmitted && <p className="hint">
                  The verified patch was too large for the bounded results view.</p>}
                {!result.patch && !result.patchOmitted && <p className="hint">
                  No code diff is archived for this run. Older runs still retain
                  their saved verdict and evaluator result.</p>}
              </>}
              {resultChecked && !result && !resultError && <p className="hint">
                No completed evidence report is available for this run. Its durable
                event history below remains accessible.</p>}
              {resultError && <p className="activity-warning">
                The saved evidence could not be read safely. The execution outcome
                above comes from the durable control-plane state; don't treat it as
                additional evaluator evidence.</p>}
            </>}
          </section>
          <p className="hint">Run {run.id} · persisted event cursor {run.lastSequence}</p>
          <p className="secure">{live}</p>
          {active && <section className="activity" role="status" aria-live="off">
            <div className="activity-heading">
              <span className="activity-dot" aria-hidden="true" />
              <strong>{run.state === 'Cancelling' ? 'Cancelling / checking isolation' :
                run.state === 'Starting' ? 'Launching isolated worker' :
                'Isolated coding run admitted'}</strong>
              <span className="pill">{startedEvent ? elapsed(runSeconds) : 'Preparing'}</span>
            </div>
            <div className="activity-track" aria-hidden="true"><div className="activity-scan" /></div>
            <p className="activity-detail">{stageMessage}</p>
            <p className="hint">Last confirmed runner milestone: {referenceEvent
              ? elapsed(silenceSeconds) + ' ago' : 'not yet reported'}.
              Browser animation shows elapsed time, <strong>not</strong> model progress or a percentage.</p>
            {silenceSeconds >= 90 && <p className="activity-warning">
              No new stage for {elapsed(silenceSeconds)}. The model may be computing, stalled,
              or waiting on its deadline; activity is not confirmed. You may cancel this run.
            </p>}
          </section>}
          {!run.planCurrent && <p className="alert">This run refers to a superseded plan. No new execution authorized.</p>}
          {!status?.executionEnabled && <p className="hint">Real execution is disabled on this host. Run history and pending-intent cancellation remain available.</p>}
          {run.state === 'AwaitingExecutionAuthorization' && status?.executionEnabled && run.planCurrent &&
            <button disabled={isBusy} onClick={() => void action('preview-execution', async () => {
              const result = await api<ExecutionView>(token, `/api/runs/${run.id}/execution-plan`, {});
              setExecution(result); refreshRun();
            })}>Prepare exact executable plan (no model)</button>}

          {execution && <>
            <div className="subtle" />
            <h3>Executable M1 plan · {execution.state}</h3>
            <dl className="meta">
              <dt>Fixture</dt><dd>{execution.plan.fixture}</dd>
              <dt>Model</dt><dd>{execution.plan.model}</dd>
              <dt>Writes allowed</dt><dd>{execution.plan.writable_files.join(', ')}</dd>
              <dt>Network / Git</dt><dd>{execution.plan.no_cloud && execution.plan.no_git_remote ? 'Restricted' : 'Unexpected permissions — do not approve'}</dd>
              <dt>Model digest</dt><dd className="hash">{execution.plan.model_digest}</dd>
              <dt>Runtime image</dt><dd className="hash">{execution.plan.image_id}</dd>
              <dt>Evaluator image</dt><dd className="hash">{execution.plan.evaluator_image_id}</dd>
            </dl>
            <div className="hash">{execution.planHash}</div>
            {execution.state === 'Previewed' && <>
              <ExactConsent planHash={execution.planHash} value={executionConsent}
                onChange={setExecutionConsent}
                description="Execution consent — distinct from the planning hash. Review exact model, image and file scope." />
              <button className="primary"
                disabled={isBusy || !run.planCurrent ||
                  executionConsent !== 'APPROVE ' + execution.planHash ||
                  execution.plan.fixture !== '02-build-repair' ||
                  execution.plan.model !== 'qwen3.5:4b' ||
                  execution.plan.writable_files.join() !== 'api/Program.cs' ||
                  !execution.plan.no_cloud || !execution.plan.no_git_remote}
                onClick={() => void action('authorize-execution', async () => {
                  await api(token, `/api/runs/${run.id}/execution-authorization`,
                    { approval: executionConsent });
                  setExecutionConsent(''); refreshRun();
                })}>Authorize this exact M1 plan (no start)</button>
            </>}
            {execution.state === 'Authorized' && <div>
              <p className="hint">Explicitly starting this run invokes local Qwen through the isolated agent. No automatic retries.</p>
              <button className="primary" disabled={isBusy || !run.planCurrent}
                onClick={() => void action('start-execution', async () => {
                  await api(token, `/api/runs/${run.id}/start`, {});
                  refreshRun();
                })}>Start one approved coding run</button>
            </div>}
          </>}

          <div className="actions">
            {canCancel && <button className="danger" disabled={isBusy}
              onClick={() => void action('cancel', async () => {
                await api(token, `/api/runs/${run.id}${runtimeStates.has(run.state) ? '/execution-cancel' : '/cancel'}`, {});
                refreshRun(); refreshTask();
              })}>Cancel this run</button>}
            {run.state === 'RecoveryBlocked' && status?.executionEnabled &&
              <button disabled={isBusy} onClick={() => void action('recover', async () => {
                await api(token, `/api/runs/${run.id}/execution-recover`, {});
                refreshRun();
              })}>Request model-free reconciliation</button>}
            <button className="outline" disabled={isBusy} onClick={refreshRun}>Replay events</button>
          </div>
          <div className="subtle" />
          <h3>Durable progress</h3>
          <ol className="events" aria-label="Run event history">
            {events.map(event => <li key={event.sequence}>
              <span className="number">#{event.sequence}</span>
              <span><strong>{stages[event.kind] ? 'Runner milestone' : event.kind}</strong> · {event.state}
                {stages[event.kind] && <small className="trace-note">{stages[event.kind]}</small>}
                <small className="trace-note">{new Date(event.createdUtc).toLocaleTimeString()}</small>
              </span>
            </li>)}
          </ol>
          <p className="hint">SQLite is authoritative; SignalR only prompts a replay. Independent evaluator evidence, diffs and patch artifacts are not yet exposed by the API.</p>
        </section>}
      </main>
    </div>}
  </>;
}
