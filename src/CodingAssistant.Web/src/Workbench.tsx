import { useEffect, useState } from 'react';
import { api, type ApiStatus, type RunItem, type TaskItem, type TaskView } from './api';
import { MafHandoffTimeline } from './MafHandoffTimeline';
import { AgentIdentityPanel } from './AgentIdentityPanel';

export type WorkbenchView = 'command' | 'team' | 'work' | 'quality' | 'skills' | 'history' | 'diagnostics';

const DESTINATIONS = [
  ['command', 'Command', '◫'],
  ['team', 'Team', '◉'],
  ['work', 'Work', '⌘'],
  ['quality', 'Quality', '◇'],
  ['skills', 'Skills', '✦'],
  ['history', 'History', '◷'],
] as const;

/** Thin destination rail — the shell's primary navigation. Project switching
 *  and session status now live in the context bar; the engineering team's
 *  identities and recorded activity live in the Team destination. */
export function WorkbenchSidebar({ selectedId, view, disabled, onNewProject,
  onChangeView }: {
  selectedId: string;
  view: WorkbenchView;
  disabled: boolean;
  onNewProject: () => void;
  onChangeView: (view: WorkbenchView) => void;
}) {
  return <nav className="ca-rail" aria-label="Workspace navigation">
    <span className="ca-rail-logo" aria-hidden="true">◆</span>
    <button type="button" className="ca-rail-new" disabled={disabled}
      onClick={onNewProject} title="Start a new project">
      <span className="ca-rail-new-glyph" aria-hidden="true">＋</span>New
    </button>
    {selectedId && <div className="ca-rail-nav">
      {DESTINATIONS.map(([id, label, icon]) =>
        <button type="button" key={id}
          aria-current={view === id ? 'page' : undefined}
          className={'ca-rail-item' + (view === id ? ' ca-rail-selected' : '')}
          onClick={() => onChangeView(id)}>
          <span className="ca-rail-icon" aria-hidden="true">{icon}</span>{label}
        </button>)}
    </div>}
  </nav>;
}

/** Project switcher: lives in the context bar. Search, switch between, and
 *  guarded deletion of saved local projects. */
export function ProjectSwitcher({ tasks, selectedId, disabled, onSelectTask,
  onRefresh, onDeleteTask }: {
  tasks: TaskItem[];
  selectedId: string;
  disabled: boolean;
  onSelectTask: (id: string) => void;
  onRefresh: () => void;
  onDeleteTask: (id: string) => Promise<boolean>;
}) {
  const [confirmingId, setConfirmingId] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const matchingTasks = tasks.filter(task =>
    (task.title + ' ' + task.objective).toLocaleLowerCase()
      .includes(query.trim().toLocaleLowerCase()));
  const currentTask = tasks.find(task => task.id === selectedId);
  return <details className="wb-projects wb-project-switcher ca-ctx-switcher">
    <summary className="wb-project-switcher-trigger">
      <span className="wb-kicker">PROJECT SWITCHER</span>
      <strong>{currentTask?.title ?? 'Choose a project'}</strong>
      <small>{tasks.length} saved project{tasks.length === 1 ? '' : 's'} · Search, switch or manage</small>
    </summary>
    <div className="ca-ctx-switcher-panel">
      <div className="wb-section-title">
        <span className="wb-kicker">SAVED PROJECTS <span className="wb-project-count">{tasks.length}</span></span>
        <button type="button" className="wb-refresh" disabled={disabled}
          aria-label="Refresh saved projects" onClick={onRefresh}>↻</button>
      </div>
      {tasks.length > 0 && <label className="wb-project-search-label">
        <span className="wb-visually-hidden">Search saved projects</span>
        <input type="search" value={query} onChange={event => setQuery(event.target.value)}
          placeholder="Find a project…" aria-label="Search saved projects" />
      </label>}
      {tasks.length === 0 && <p className="wb-muted">Start with your own objective.</p>}
      {tasks.length > 0 && matchingTasks.length === 0 && <p className="wb-muted">
        No projects match your search.</p>}
      {matchingTasks.map(task => <div className="wb-project-entry" key={task.id}>
        <div className="wb-project-entry-row">
          <button type="button"
            className={'wb-project-link' + (selectedId === task.id ? ' wb-project-active' : '')}
            onClick={() => { setConfirmingId(null); onSelectTask(task.id); }}
            aria-current={selectedId === task.id ? 'true' : undefined}>
            <strong>{task.title}</strong>
            <small>{task.status}</small>
          </button>
          <button type="button" className="wb-project-delete" disabled={disabled}
            aria-label={'Delete project ' + task.title} title={'Delete project ' + task.title}
            aria-expanded={confirmingId === task.id}
            onClick={() => setConfirmingId(open => open === task.id ? null : task.id)}>
            Delete
          </button>
        </div>
        {confirmingId === task.id && <div className="wb-delete-confirm"
          role="group" aria-label={'Confirm deletion of ' + task.title}>
          <strong>Delete “{task.title}”?</strong>
          <p>Its conversation, decisions, generated files and saved run history
            will be permanently deleted. This cannot be undone.</p>
          <div className="wb-delete-actions">
            <button type="button" className="danger" disabled={disabled}
              onClick={() => void onDeleteTask(task.id).then(success => {
                if (success) setConfirmingId(null);
              })}>
              Delete permanently
            </button>
            <button type="button" className="outline" disabled={disabled}
              onClick={() => setConfirmingId(null)}>Cancel</button>
          </div>
        </div>}
      </div>)}
    </div>
  </details>;
}

export function WorkbenchInspector({ token, view, taskView, run, status, onContinue, collapsed }: {
  collapsed?: boolean;
  token: string;
  view: WorkbenchView;
  taskView: TaskView | null;
  run: RunItem | null;
  status: ApiStatus | null;
  onContinue: () => void;
}) {
  return <aside className="wb-inspector" aria-label="Project inspector" hidden={collapsed}>
    <p className="wb-kicker">PROJECT INSPECTOR</p>
    <h2>Current context</h2>
    {taskView ? <>
      <div className="wb-inspector-row"><span>Project</span><strong>{taskView.task.title}</strong></div>
      <div className="wb-inspector-row"><span>Project record</span><strong>{taskView.task.status}</strong></div>
      <div className="wb-inspector-row"><span>Current view</span><strong>{
        view === 'diagnostics' ? 'Developer tools' :
          view[0].toUpperCase() + view.slice(1)
      }</strong></div>
      {run && <div className="wb-inspector-row"><span>Saved run state</span>
        <strong>{run.state}</strong></div>}
      {view !== 'work' && <button type="button" className="wb-inspector-action"
        onClick={onContinue}>Open Engineering Studio</button>}
    </> : <p className="wb-muted">Create or open a project to inspect its work.</p>}
    <details className="wb-inspector-disclosure" open>
      <summary>Recorded handoffs</summary>
      {taskView && <MafHandoffTimeline token={token} taskId={taskView.task.id} />}
      {!taskView && <p className="wb-muted">Select a project to view recorded events.</p>}
    </details>
    <details className="wb-inspector-disclosure">
      <summary>Agent identities & backstories</summary>
      <AgentIdentityPanel token={token} />
    </details>
    <div className="wb-inspector-divider" />
    <p className="wb-kicker">CONTROL PLANE</p>
    <div className="wb-inspector-row"><span>Inference</span><strong>Local Ollama</strong></div>
    <div className="wb-inspector-row"><span>Execution</span>
      <strong>{status?.executionEnabled ? 'Host enabled — approval required' : 'Unavailable on this host'}</strong>
    </div>
    <p className="wb-muted">A generated source ZIP is not a verified build. Planning and executable approvals remain separate.</p>
  </aside>;
}

interface OverviewSnapshot {
  specification: { version: number; status: string } | null;
  architecture: {
    status: string; readyForSourceGeneration: boolean; blockingTopics: string[];
  } | null;
  draft: { job: { state: string; phase: string; errorCode: string | null } } | null;
  plan: {
    revision: number; status: string; current: boolean;
    outline: { tasks: { id: string; title: string }[] };
  } | null;
  engineering: {
    job: { state: string; phase: string; errorCode: string | null };
    tasks: { id: string; title: string; state: string }[];
    files: { path: string }[];
    events: { kind: string; detail: string; createdUtc: string }[];
  } | null;
}

/** Read-only overview of actual saved state; it never starts or authorizes work. */
export function ProjectOverview({ token, task, onContinue, onDiagnostics }: {
  token: string;
  task: TaskItem;
  onContinue: () => void;
  onDiagnostics: () => void;
}) {
  const [data, setData] = useState<OverviewSnapshot | null>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    let closed = false;
    let loading = false;
    setData(null);
    setError('');
    async function load() {
      if (loading) return;
      loading = true;
      try {
        const [interview, architecture, archive, plans, engineering] = await Promise.all([
          api<{ specification: OverviewSnapshot['specification'] }>(token,
            '/api/tasks/' + task.id + '/interview'),
          api<NonNullable<OverviewSnapshot['architecture']>>(token,
            '/api/tasks/' + task.id + '/draft-build/architecture'),
          api<{ snapshot: OverviewSnapshot['draft'] }>(token,
            '/api/tasks/' + task.id + '/draft-build'),
          api<{ plan: OverviewSnapshot['plan'] }>(token,
            '/api/tasks/' + task.id + '/implementation-plan'),
          api<{ snapshot: OverviewSnapshot['engineering'] }>(token,
            '/api/tasks/' + task.id + '/engineering-runs/latest'),
        ]);
        if (!closed) {
          setData({
            specification: interview.specification,
            architecture, draft: archive.snapshot, plan: plans.plan,
            engineering: engineering.snapshot,
          });
          setError('');
        }
      } catch {
        if (!closed) setError('Unable to refresh the saved project summary. Your work is unchanged.');
      } finally {
        loading = false;
      }
    }
    void load();
    const interval = window.setInterval(() => void load(), 8000);
    return () => { closed = true; window.clearInterval(interval); };
  }, [token, task.id]);

  const spec = data?.specification;
  const architecture = data?.architecture;
  const plan = data?.plan;
  const run = data?.engineering;
  const job = run?.job;
  const completed = run?.tasks.filter(item => item.state === 'Completed').length ?? 0;
  const currentTask = run?.tasks.find(item => item.state === 'Running');
  const nextAction = spec?.status !== 'Approved' ? 'Review specification' :
    !architecture?.readyForSourceGeneration ? 'Review architecture' :
      plan?.status !== 'Approved' || !plan.current ? 'Review implementation plan' :
        job?.state === 'Running' ? 'Watch engineering' : 'Open Engineering Studio';
  const engineeringLabel = !job ? 'Not started' :
    job.state === 'Generated' ? 'Source generated · unverified' :
      job.state === 'Failed' ? 'Failed · inspect partial source' :
        job.state === 'Interrupted' ? 'Interrupted · inspect saved source' :
          job.state === 'Running' ? 'Engineering in progress' : job.state;

  return <section className="wb-overview" aria-label="Project overview">
    <div className="wb-overview-hero">
      <div>
        <p className="wb-kicker">MISSION CONTROL / PROJECT OVERVIEW</p>
        <h2>{task.title}</h2>
        <p className="wb-objective">{task.objective}</p>
      </div>
      <div className="wb-overview-hero-status">
        <span className="wb-overview-status-label">PROJECT RECORD</span>
        <strong>{task.status}</strong>
        <small>SQLite · local workspace</small>
      </div>
    </div>
    <div className="wb-overview-actions">
      <button type="button" className="primary" onClick={onContinue}>
        {data ? nextAction : 'Continue in Engineering Studio'} <span aria-hidden="true">→</span>
      </button>
      <button type="button" className="outline" onClick={onDiagnostics}>Developer tools</button>
    </div>
    {error && <div className="alert" role="alert">{error}</div>}
    {!data && !error && <p className="hint" role="status">Loading saved project state…</p>}
    {data && <>
      <div className="wb-overview-section-heading">
        <div><span className="wb-kicker">APPROVAL PATH</span><h3>Engineering readiness</h3></div>
        <span className="wb-overview-caption">Persisted decisions · no estimated progress</span>
      </div>
      <div className="wb-status-grid">
        <article className="wb-status-card" data-state={spec?.status === 'Approved' ? 'complete' :
          spec?.status === 'Draft' ? 'attention' : 'pending'}>
          <span>01 / PRODUCT OWNER</span>
          <strong>{spec ? 'Specification v' + spec.version : 'Discovery not completed'}</strong>
          <small>{spec ? spec.status : 'Interview and requirements awaiting review'}</small>
        </article>
        <article className="wb-status-card" data-state={
          architecture?.readyForSourceGeneration ? 'complete' : 'pending'}>
          <span>02 / SOLUTION ARCHITECT</span>
          <strong>{architecture?.readyForSourceGeneration ? 'Architecture approved' : 'Review required'}</strong>
          <small>{architecture?.status ?? 'Not available'}
            {architecture?.blockingTopics.length
              ? ' · ' + architecture.blockingTopics.join(', ') : ''}</small>
        </article>
        <article className="wb-status-card" data-state={
          plan?.status === 'Approved' && plan.current ? 'complete' :
            plan?.status === 'Proposed' ? 'attention' : 'pending'}>
          <span>03 / TECHNICAL LEAD</span>
          <strong>{plan ? plan.status + ' · revision ' + plan.revision : 'No implementation plan'}</strong>
          <small>{plan ? plan.outline.tasks.length + ' planned work items' +
            (plan.current ? '' : ' · not current') :
            'An approved architecture is required first'}</small>
        </article>
        <article className="wb-status-card" data-state={
          job?.state === 'Running' ? 'active' :
            job?.state === 'Generated' ? 'complete' :
              job?.state === 'Failed' || job?.state === 'Interrupted' ? 'blocked' : 'pending'}>
          <span>04 / SOFTWARE ENGINEER</span>
          <strong>{engineeringLabel}</strong>
          <small>{run ? completed + ' / ' + run.tasks.length + ' saved tasks completed · ' +
            run.files.length + ' source files' : 'No MAF engineering run saved'}</small>
        </article>
      </div>
      <div className="wb-overview-lower">
        <section className="wb-overview-operations" aria-label="Engineering activity">
          <div className="wb-overview-section-heading">
            <div><span className="wb-kicker">ENGINEERING RUN</span><h3>Current operation</h3></div>
            {job?.state === 'Running' && <span className="wb-operating-indicator">
              <span aria-hidden="true" />Running</span>}
          </div>
          {!job && <p className="wb-muted">No MAF engineering run yet. Open Engineering
            Studio to review the plan and initiate source generation with explicit approvals.</p>}
          {job && <>
            <p className="wb-operation-title">{currentTask?.title ??
              (job.state === 'Running' ? 'Awaiting the next persisted task update' :
                'Most recent engineering run: ' + job.state)}</p>
            <p className="wb-muted">{job.phase}{job.errorCode ? ' · ' + job.errorCode : ''}</p>
            {run && run.tasks.length > 0 && <div className="wb-task-counter">
              <strong>{completed}</strong><span>of {run.tasks.length} assignments completed
                {job.state === 'Generated' ? ' · source only' : ''}</span>
            </div>}
            {run && run.events.length > 0 && <ol className="wb-overview-events">
              {run.events.slice(-3).reverse().map((event, index) => <li
                key={event.createdUtc + ':' + event.kind + ':' + index}>
                <span>{event.kind}</span><p>{event.detail}</p>
              </li>)}
            </ol>}
          </>}
        </section>
        <section className="wb-overview-operations" aria-label="Source and delivery">
          <div className="wb-overview-section-heading">
            <div><span className="wb-kicker">ARTIFACT EVIDENCE</span><h3>Source & delivery</h3></div>
          </div>
          <p className="wb-operation-title">{run?.files.length
            ? run.files.length + ' saved MAF source files' : 'No MAF source files saved'}</p>
          {run && run.files.length > 0 && <ul className="wb-overview-files">
            {run.files.slice(0, 4).map(file => <li key={file.path}>{file.path}</li>)}
            {run.files.length > 4 && <li>+ {run.files.length - 4} additional files</li>}
          </ul>}

        </section>
      </div>
    </>}
    <section className="wb-guardrail" aria-label="Verification status">
      <strong>Source is not a verified application</strong>
      <p>Engineering tasks and ZIP exports record generated files, not a successful
        compilation, test run, or security review. QA currently reports static
        findings only; independently verified builds require separate evidence.</p>
    </section>
  </section>;
}
