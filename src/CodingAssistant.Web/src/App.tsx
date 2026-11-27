import { useEffect, useState } from 'react';
import { CommandActivity } from './CommandActivity';
import { EngineeringStudio } from './EngineeringStudio';
import { TeamDestination, QualityDestination, SkillsDestination, HistoryDestination } from './MissionDestinations';
import { WorkbenchInspector, WorkbenchSidebar, ProjectSwitcher,
  type WorkbenchView } from './Workbench';
import * as signalR from '@microsoft/signalr';
import {
  api, deleteTask, bootstrapLocalSession, newRequestId, isHexId,
  type ApiStatus, type TaskItem, type TaskView, type RunItem,
  type RunEvent, type EventPage, type ExecutionView, type PlanItem,
  type HtmlArtifact, type HtmlArtifactResponse,
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
  const [sessionMode, setSessionMode] = useState<
    'checking' | 'automatic' | 'manual' | 'failed'>('checking');
  const [status, setStatus] = useState<ApiStatus | null>(null);
  const [tasks, setTasks] = useState<TaskItem[]>([]);
  const [taskId, setTaskId] = useState('');
  const [view, setView] = useState<WorkbenchView>('command');
  // Inspection is contextual and opt-in, not a permanent third column.
  const [inspectorOpen, setInspectorOpen] = useState(false);
  const [taskView, setTaskView] = useState<TaskView | null>(null);
  const [runs, setRuns] = useState<RunItem[]>([]);
  const [runId, setRunId] = useState('');
  const [run, setRun] = useState<RunItem | null>(null);
  const [events, setEvents] = useState<RunEvent[]>([]);
  const [execution, setExecution] = useState<ExecutionView | null>(null);
  const [result, setResult] = useState<RunResult | null>(null);
  const [resultChecked, setResultChecked] = useState(false);
  const [resultError, setResultError] = useState(false);
  const [htmlArtifact, setHtmlArtifact] = useState<HtmlArtifact | null>(null);
  const [htmlModel, setHtmlModel] = useState('qwen3.5:4b');
  const [htmlStart, setHtmlStart] = useState(0);
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
  const [theme, setTheme] = useState<'dark' | 'light'>(() =>
    (typeof document !== 'undefined' &&
      document.documentElement.dataset.theme === 'light') ? 'light' : 'dark');

  function toggleTheme() {
    setTheme(previous => {
      const next = previous === 'dark' ? 'light' : 'dark';
      document.documentElement.dataset.theme = next;
      try { localStorage.setItem('ca-theme', next); } catch { /* storage may be blocked */ }
      return next;
    });
  }

  // Inspector is an optional contextual surface; Escape dismisses it.
  useEffect(() => {
    if (!inspectorOpen) return;
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setInspectorOpen(false);
    };
    window.addEventListener('keydown', closeOnEscape);
    return () => window.removeEventListener('keydown', closeOnEscape);
  }, [inspectorOpen]);

  // Windows opens Mission Control immediately: browser JS requests a new
  // short-lived same-origin bearer, never the owner's persisted DPAPI token.
  // Linux keeps the existing manual bearer login.
  useEffect(() => {
    let closed = false;
    void (async () => {
      try {
        const localToken = await bootstrapLocalSession();
        if (closed) return;
        if (localToken === null) {
          setSessionMode('manual');
          return;
        }
        const connected = await api<ApiStatus>(localToken, '/api/status');
        if (!connected.localOnly)
          throw new Error('Refusing a non-local control plane.');
        if (!closed) {
          setStatus(connected);
          setToken(localToken);
          setSessionMode('automatic');
        }
      } catch {
        if (!closed) setSessionMode('failed');
      }
    })();
    return () => { closed = true; };
  }, []);

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
  function chooseTask(id: string, initialView: WorkbenchView = 'command') {
    if (id !== '' && !isHexId(id)) return;
    setView(initialView);
    setInspectorOpen(false);
    setTaskId(id);
    setHtmlArtifact(null);
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
    setHtmlArtifact(null);
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
      .then(list => { if (!closed) {
        setTasks(list);
      } })
      .catch(() => { if (!closed) setError('Unable to load tasks.'); });
    return () => { closed = true; };
  }, [token, taskRevision]);

  useEffect(() => {
    if (!token || !taskId) return;
    let closed = false;
    void api<HtmlArtifactResponse>(token, `/api/tasks/${taskId}/html`)
      .then(response => {
        if (!closed) setHtmlArtifact(response.artifact);
      })
      .catch(() => {
        if (!closed) setError('Saved HTML output could not be loaded.');
      });
    return () => { closed = true; };
  }, [token, taskId]);

  useEffect(() => {
    if (busy !== 'html-generate') return;
    const ticker = window.setInterval(() => setClock(Date.now()), 1000);
    return () => window.clearInterval(ticker);
  }, [busy]);

  function downloadHtml(artifact: HtmlArtifact) {
    // Download source as data, never execute model-produced markup in the UI.
    const data = new Blob([artifact.html], { type: 'text/html;charset=utf-8' });
    const href = URL.createObjectURL(data);
    const anchor = document.createElement('a');
    anchor.href = href;
    anchor.download = 'index.html';
    anchor.click();
    window.setTimeout(() => URL.revokeObjectURL(href), 2000);
  }

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
      setRunId(current => current || (list[0]?.id ?? ''));
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
    <a className="wb-skip-link" href="#mission-workspace">Skip to workspace</a>
    {!token && <header className="ca-auth-header">
      <div className="wb-brand">
        <span className="wb-mark" aria-hidden="true"><span>◆</span></span>
        <div>
          <h1>CodingAssistant</h1>
          <p><span>CODINGASSISTANT</span><span aria-hidden="true"> / </span>
            <span>LOCAL ENGINEERING ENVIRONMENT</span></p>
        </div>
      </div>
    </header>}

    {!token && error && <div className="alert" role="alert">{error}</div>}

    {!token && sessionMode === 'checking' && <section className="panel login"
      role="status">
      <h2>Connecting to your local workspace…</h2>
      <p className="hint">Establishing a short-lived local session without
        copying or pasting credentials.</p>
    </section>}
    {!token && sessionMode === 'failed' && <section className="panel login"
      role="alert">
      <h2>Local connection unavailable</h2>
      <p className="hint">CodingAssistant could not establish a secure local
        browser session. Open this app at <code>http://127.0.0.1:5098</code>,
        confirm the API is running, then retry. No token entry is needed.</p>
      <button className="primary" type="button"
        onClick={() => window.location.reload()}>Retry local connection</button>
    </section>}
    {!token && sessionMode === 'manual' && <section className="panel login">
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

    {token && <div className={'ca-shell' +
      (inspectorOpen ? ' ca-shell--inspector' : '')}>
      <WorkbenchSidebar selectedId={taskId} view={view} disabled={isBusy}
        onNewProject={() => chooseTask('')} onChangeView={setView} />
      <div className="ca-work">
        <div className="ca-ctxbar">
          <ProjectSwitcher tasks={tasks} selectedId={taskId} disabled={isBusy}
            onSelectTask={chooseTask}
            onRefresh={() => void action('refresh', async () => {
              await fetchTasks(); refreshTask();
            })}
            onDeleteTask={async id => {
              setBusy('delete-task');
              setError('');
              try {
                await deleteTask(token, id);
                setTasks(previous => previous.filter(task => task.id !== id));
                if (taskId === id) chooseTask('');
                return true;
              } catch (cause) {
                const code = cause instanceof Error ? cause.message : 'Unknown error';
                setError(code.includes('BUILD_BUSY') || code.includes('EXECUTION_BUSY')
                  ? 'This project has an active job. Stop or complete it before deletion.'
                  : 'Could not delete project: ' + code);
                return false;
              } finally {
                setBusy('');
              }
            }} />
          <span className="ca-ctx-spacer" />
          <span className="pill wb-local-pill"><span className="wb-local-dot" aria-hidden="true" />Local session</span>
          <span className="pill wb-execution-pill">{status?.executionEnabled ?
            'Execution requires approval' : 'Execution unavailable'}</span>
          <button type="button" className="outline ca-theme-toggle" onClick={toggleTheme}
            aria-label={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
            title="Toggle light or dark theme">{theme === 'dark' ? '☀' : '☾'}</button>
          <button type="button" className="outline wb-inspector-toggle"
            aria-expanded={inspectorOpen} aria-controls="mission-inspector"
            onClick={() => setInspectorOpen(value => !value)}>
            {inspectorOpen ? 'Hide inspector' : 'Show inspector'}
          </button>
          {sessionMode === 'automatic'
            ? <button type="button" className="outline" onClick={() => window.location.reload()}>
                Refresh local session
              </button>
            : <button type="button" className="outline" onClick={disconnect}>Disconnect</button>}
        </div>
        {error && <div className="alert" role="alert">{error}</div>}
      <main className="stack wb-main" id="mission-workspace" tabIndex={-1}>
        {!taskId && <section className="panel new-project-panel">
          <span className="pill">Start a project</span>
          <h2>What would you like to build?</h2>
          <p className="hint">Describe your idea in your own words. Your Product Owner will discuss your goals and requirements with you
            before you approve a specification.
            No example project is selected automatically.</p>
          <form onSubmit={e => { e.preventDefault(); void action('create-task', async () => {
            const objective = newObjective.trim();
            const created = await api<TaskItem>(token, '/api/tasks', {
              title: newTitle.trim() || objective.replace(/\s+/g, ' ').slice(0, 100),
              objective,
              requestId: newRequestId(),
            });
            setNewTitle(''); setNewObjective('');
            await fetchTasks();
            chooseTask(created.id, 'command');
          }); }}>
            <label htmlFor="project-objective">What do you want to build?
              <textarea id="project-objective" value={newObjective} maxLength={4000}
                required autoFocus rows={5}
                placeholder="Describe the app, feature, or change you have in mind…"
                onChange={e => setNewObjective(e.target.value)} />
            </label>
            <label htmlFor="project-title">Project name (optional)
              <input id="project-title" value={newTitle} maxLength={120}
                placeholder="We'll use your objective if left blank"
                onChange={e => setNewTitle(e.target.value)} />
            </label>
            <div className="actions">
              <button className="primary" disabled={isBusy || !newObjective.trim()}>
                {busy === 'create-task' ? 'Creating project…' : 'Create project and continue →'}
              </button>
            </div>
          </form>
          <p className="hint">Your work is saved locally. Creating a project does not
            run code or authorize agent execution.</p>
        </section>}
        {taskId && !taskView && <section className="panel">
          <h2>Opening your project…</h2>
          <p className="hint">Loading the selected project's saved Product Owner conversation and history.</p>
        </section>}
        {taskView && <div hidden={view !== 'command' && view !== 'work'}>
          <EngineeringStudio key={taskView.task.id} token={token} task={taskView.task}
            active={view === 'command' || view === 'work'} onTeam={() => setView('team')}/>
        </div>}
        {view === 'command' && taskView && <CommandActivity
          token={token} task={taskView.task} supervised onNavigate={setView} />}
        {taskView && view === 'team' && <TeamDestination token={token} taskId={taskId}
          onWork={() => setView('work')} />}
        {taskView && view === 'quality' && <QualityDestination token={token}
          taskId={taskId} onWork={() => setView('work')} />}
        {taskView && view === 'skills' && <SkillsDestination token={token} taskId={taskView.task.id} />}
        {taskView && view === 'history' && <HistoryDestination token={token} taskId={taskId} />}
        {view === 'diagnostics' && taskView && <div className="wb-diagnostic-heading">
          <h2>Developer tools</h2>
          <p className="hint">Optional HTML generation and isolated C# qualification tools.
            These are not execution steps for your current project.</p>
        </div>}
        {view === 'diagnostics' && <details className="advanced-gate" hidden={!taskView}>
          <summary>Single-file HTML utility (separate from project engineering)</summary>
        {taskView && <section className="panel html-preview-panel">
          <h2>Make your HTML page</h2>
          <p className="hint">This uses your task objective to generate a real <code>index.html</code>
            with a local Ollama model. It needs no planning hash or coding approvals
            because the model cannot run commands or edit your repository.
            The generated HTML is saved privately and shown as source code.</p>
          <div className="actions">
            <label>Local model
              <select value={htmlModel} onChange={e => setHtmlModel(e.target.value)}
                disabled={isBusy}>
                <option value="qwen3.5:4b">Qwen 3.5 4B (existing)</option>
                <option value="qwen2.5-coder:3b">Qwen 2.5 Coder 3B (if installed)</option>
              </select>
            </label>
            <button className="primary" disabled={isBusy}
              onClick={() => void action('html-generate', async () => {
                setHtmlStart(Date.now());
                const artifact = await api<HtmlArtifact>(
                  token, `/api/tasks/${taskId}/html`, { model: htmlModel });
                setHtmlArtifact(artifact);
              })}>
              {busy === 'html-generate' ? 'Generating on the Dell…' :
                htmlArtifact ? 'Regenerate HTML' : 'Generate index.html'}
            </button>
          </div>
          {busy === 'html-generate' && <div className="activity" role="status">
            <div className="activity-heading">
              <span className="activity-dot" aria-hidden="true" />
              <strong>Waiting for local {htmlModel}</strong>
              <span className="pill">{elapsed((clock - htmlStart) / 1000)}</span>
            </div>
            <div className="activity-track" aria-hidden="true">
              <div className="activity-scan" />
            </div>
            <p className="hint">This is a single bounded text-generation request,
              not an autonomous coding job. The clock measures elapsed time,
              not confirmed model progress. Existing saved output is preserved
              if the request fails.</p>
          </div>}
          {htmlArtifact && <div className="html-artifact" aria-label="Generated HTML output">
            <div className="actions">
              <strong>index.html — saved output</strong>
              <span className="pill">{htmlArtifact.model}</span>
              <button type="button" className="primary"
                onClick={() => downloadHtml(htmlArtifact)}>Download index.html</button>
            </div>
            <p className="hint">Saved {new Date(htmlArtifact.createdUtc).toLocaleString()}.
              This file is a draft. No repository files have been changed or executed.</p>
            <pre className="verified-patch">{htmlArtifact.html}</pre>
          </div>}
          <p className="hint">To edit your actual project or create multiple files autonomously,
            the general-purpose agent still needs separate workspace-scope qualification.</p>
        </section>}
        </details>}
        {view === 'diagnostics' && <details className="advanced-gate" hidden={!taskView}>
          <summary>Advanced: isolated C# repair qualification (not your task)</summary>
          <p className="hint">These steps always run the fixed disposable
            <code>02-build-repair/api/Program.cs</code> test fixture and do
            not use your task objective. They are engineering tests, not the
            HTML generator above.</p>
        {taskView && <section className="panel">
          <h2>{taskView.task.title} <span className="pill">{taskView.task.status}</span></h2>
          <p>{taskView.task.objective}</p>
          {run && <div className="actions">
            <span className="pill">Latest selected run: {run.state}</span>
            <button type="button" className="outline"
              onClick={() => document.getElementById('run-result')?.scrollIntoView({
                behavior: 'smooth', block: 'start',
              })}>View run output ↓</button>
          </div>}
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
          <section id="run-result" className="result-panel" aria-label="Run result">
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
        </details>}
      </main>
      </div>
      <div id="mission-inspector" className="wb-inspector-host" hidden={!inspectorOpen}>
        <WorkbenchInspector token={token} view={view} taskView={taskView} run={run}
          status={status} collapsed={!inspectorOpen}
          onContinue={() => setView('work')} />
      </div>
    </div>}
  </>;
}
