import { useEffect, useState } from 'react';
import { api, type ApiStatus, type RunItem, type TaskItem, type TaskView } from './api';
import { MafHandoffTimeline } from './MafHandoffTimeline';
import { AgentIdentityPanel } from './AgentIdentityPanel';

export type WorkbenchView = 'overview' | 'studio' | 'diagnostics';

// Presentation metadata only. Three planning specialists now invoke MAF,
// but this static list is not live activity telemetry or a dynamic registry.
const initialTeam = [
  { role: 'Product Owner', initials: 'PO', availability: 'MAF · local Ollama (on request)' },
  { role: 'Solution Architect', initials: 'SA', availability: 'MAF · local Ollama (on request)' },
  { role: 'Technical Lead', initials: 'TL', availability: 'MAF · local Ollama (on request)' },
  { role: 'Software Engineer', initials: 'SE', availability: 'MAF · task-by-task source (on request)' },
  { role: 'QA Engineer', initials: 'QA', availability: 'Planned — no live agent' },
  { role: 'Security Reviewer', initials: 'SR', availability: 'Planned — no live agent' },
];

export function WorkbenchSidebar({ tasks, selectedId, view, disabled, onSelectTask,
  onNewProject, onChangeView, onRefresh, onDeleteTask }: {
  tasks: TaskItem[];
  selectedId: string;
  view: WorkbenchView;
  disabled: boolean;
  onSelectTask: (id: string) => void;
  onNewProject: () => void;
  onChangeView: (view: WorkbenchView) => void;
  onRefresh: () => void;
  onDeleteTask: (id: string) => Promise<boolean>;
}) {
  const [confirmingId, setConfirmingId] = useState<string | null>(null);
  return <aside className="wb-sidebar" aria-label="Workspace navigation">
    <button type="button" className="wb-new-project" disabled={disabled}
      onClick={onNewProject}>+ New project</button>
    {selectedId && <nav aria-label="Project views" className="wb-navigation">
      <p className="wb-kicker">WORKSPACE</p>
      {([
        ['overview', 'Overview'],
        ['studio', 'Engineering Studio'],
        ['diagnostics', 'Legacy diagnostics'],
      ] as const).map(([id, label]) =>
        <button type="button" key={id}
          aria-current={view === id ? 'page' : undefined}
          className={'wb-nav-item' + (view === id ? ' wb-selected' : '')}
          onClick={() => onChangeView(id)}>{label}</button>)}
    </nav>}
    <div className="wb-projects">
      <div className="wb-section-title">
        <span className="wb-kicker">SAVED PROJECTS</span>
        <button type="button" className="wb-refresh" disabled={disabled}
          aria-label="Refresh saved projects" onClick={onRefresh}>↻</button>
      </div>
      {tasks.length === 0 && <p className="wb-muted">Start with your own objective.</p>}
      {tasks.map(task => <div className="wb-project-entry" key={task.id}>
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
            onClick={() => setConfirmingId(current => current === task.id ? null : task.id)}>
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
    <div className="wb-team">
      <p className="wb-kicker">ENGINEERING TEAM</p>
      {initialTeam.map(member => <div className="wb-team-member" key={member.role}>
        <span className="wb-avatar" aria-hidden="true">{member.initials}</span>
        <span className="wb-member-copy"><strong>{member.role}</strong>
          <small>{member.availability}</small></span>
      </div>)}
      <p className="wb-muted wb-team-note">Team availability is descriptive. Only saved MAF handoffs are shown; no live agent status is invented.</p>
    </div>
  </aside>;
}

export function WorkbenchInspector({ token, view, taskView, run, status, onContinue }: {
  token: string;
  view: WorkbenchView;
  taskView: TaskView | null;
  run: RunItem | null;
  status: ApiStatus | null;
  onContinue: () => void;
}) {
  return <aside className="wb-inspector" aria-label="Project inspector">
    <p className="wb-kicker">PROJECT INSPECTOR</p>
    <h2>Current context</h2>
    {taskView ? <>
      <div className="wb-inspector-row"><span>Project</span><strong>{taskView.task.title}</strong></div>
      <div className="wb-inspector-row"><span>Project record</span><strong>{taskView.task.status}</strong></div>
      <div className="wb-inspector-row"><span>Current view</span><strong>{
        view === 'studio' ? 'Engineering Studio' :
          view === 'overview' ? 'Overview' : 'Legacy diagnostics'
      }</strong></div>
      {run && <div className="wb-inspector-row"><span>Legacy run state</span>
        <strong>{run.state}</strong></div>}
      {view !== 'studio' && <button type="button" className="wb-inspector-action"
        onClick={onContinue}>Open Engineering Studio</button>}
    </> : <p className="wb-muted">Create or open a project to inspect its work.</p>}
    {taskView && <MafHandoffTimeline token={token} taskId={taskView.task.id} />}
    <AgentIdentityPanel token={token} />
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
  architecture: { status: string; readyForSourceGeneration: boolean; blockingTopics: string[] } | null;
  draft: { job: { state: string; phase: string } } | null;
}

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
    setData(null);
    setError('');
    void Promise.all([
      api<{ specification: OverviewSnapshot['specification'] }>(token,
        '/api/tasks/' + task.id + '/interview'),
      api<NonNullable<OverviewSnapshot['architecture']>>(token,
        '/api/tasks/' + task.id + '/draft-build/architecture'),
      api<{ snapshot: OverviewSnapshot['draft'] }>(token,
        '/api/tasks/' + task.id + '/draft-build'),
    ]).then(([interview, architecture, response]) => {
      if (!closed) setData({
        specification: interview.specification,
        architecture,
        draft: response.snapshot,
      });
    }).catch(() => {
      if (!closed) setError('Unable to load the saved project summary. Your work is unchanged.');
    });
    return () => { closed = true; };
  }, [token, task.id]);

  const spec = data?.specification;
  const architecture = data?.architecture;
  const draft = data?.draft?.job;
  return <section className="wb-overview" aria-label="Project overview">
    <p className="wb-kicker">PROJECT OVERVIEW</p>
    <h2>{task.title}</h2>
    <p className="wb-objective">{task.objective}</p>
    <div className="wb-overview-actions">
      <button type="button" className="primary" onClick={onContinue}>
        Continue in Engineering Studio
      </button>
      <button type="button" className="outline" onClick={onDiagnostics}>Legacy diagnostics</button>
    </div>
    <h3>Project work</h3>
    {error && <div className="alert" role="alert">{error}</div>}
    {!data && !error && <p className="hint" role="status">Loading saved project state…</p>}
    {data && <div className="wb-status-grid">
      <article className="wb-status-card"><span>01 · Product Owner</span>
        <strong>{spec ? 'Specification v' + spec.version : 'Discovery not completed'}</strong>
        <small>{spec ? spec.status : 'Start or continue the Product Owner conversation'}</small>
      </article>
      <article className="wb-status-card"><span>02 · Solution Architect</span>
        <strong>{architecture?.readyForSourceGeneration ? 'Architecture approved' :
          'Review required'}</strong>
        <small>{architecture?.status ?? 'Not available'}{
          architecture?.blockingTopics.length ? ' · ' + architecture.blockingTopics.join(', ') : ''
        }</small>
      </article>
      <article className="wb-status-card"><span>03 · Project artifacts</span>
        <strong>{draft ? draft.state : 'Not generated'}</strong>
        <small>{draft ? draft.phase : 'Source files will appear after approved decisions'}</small>
      </article>
    </div>}
    <section className="wb-guardrail" aria-label="Verification status">
      <strong>Evidence first</strong>
      <p>Source generation and independent execution are different stages. Any project files shown
        in Engineering Studio remain unverified unless separate evaluator evidence says otherwise.</p>
    </section>
  </section>;
}
