import { useEffect, useState } from 'react';
import { ProjectDraftPanel } from './ProjectDraftPanel';
import { api, type TaskItem } from './api';

interface InterviewMessage {
  sequence: number;
  role: 'owner' | 'assistant';
  content: string;
  createdUtc: string;
}
interface InterviewSpecification {
  version: number;
  model: string;
  content: string;
  sha256: string;
  status: 'Draft' | 'Approved' | 'Superseded';
  createdUtc: string;
  approvedUtc: string | null;
}
interface InterviewSnapshot {
  taskId: string;
  model: string | null;
  lastSequence: number;
  readyForSpecification: boolean;
  messages: InterviewMessage[];
  specification: InterviewSpecification | null;
}
type InterviewBusy = 'start' | 'answer' | 'draft' | 'approve' | '';

export function InterviewStudio({ token, task }: {
  token: string;
  task: TaskItem;
}) {
  const [snapshot, setSnapshot] = useState<InterviewSnapshot | null>(null);
  const [chosenModel, setChosenModel] = useState('qwen3.5:4b');
  const [answer, setAnswer] = useState('');
  const [busy, setBusy] = useState<InterviewBusy>('');
  const [error, setError] = useState('');
  const [approveChecked, setApproveChecked] = useState(false);
  const [elapsed, setElapsed] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setSnapshot(null);
    setError('');
    setAnswer('');
    setApproveChecked(false);
    void api<InterviewSnapshot>(token, `/api/tasks/${task.id}/interview`)
      .then(data => {
        if (!cancelled) {
          setSnapshot(data);
          if (data.model) setChosenModel(data.model);
        }
      })
      .catch(() => { if (!cancelled) setError('Unable to load the saved interview.'); });
    return () => { cancelled = true; };
  }, [token, task.id]);

  useEffect(() => {
    if (!busy) return;
    const start = Date.now();
    setElapsed(0);
    const timer = window.setInterval(() => {
      setElapsed(Math.floor((Date.now() - start) / 1000));
    }, 1000);
    return () => window.clearInterval(timer);
  }, [busy]);

  async function submit(
    operation: InterviewBusy,
    work: () => Promise<InterviewSnapshot>,
  ) {
    if (busy) return;
    setBusy(operation);
    setError('');
    try {
      const updated = await work();
      setSnapshot(updated);
      setApproveChecked(false);
      if (operation === 'answer') setAnswer('');
    } catch (cause) {
      const code = cause instanceof Error ? cause.message : 'Unknown error';
      setError(code.includes('STALE_INTERVIEW')
        ? 'Your interview changed elsewhere. Reload the page to review it before continuing.'
        : code.includes('MODEL_NOT_INSTALLED')
          ? 'That local model is not installed. Select an installed Ollama model.'
          : code.includes('LOCAL_MODEL_UNAVAILABLE')
            ? 'The local model is unavailable or timed out. Your saved answers are unchanged; retry.'
            : 'Could not complete this step: ' + code);
    } finally {
      setBusy('');
    }
  }

  const spec = snapshot?.specification;
  const ownerAnswers = snapshot?.messages.filter(item => item.role === 'owner').length ?? 0;

  return <section className="panel interview-studio">
    <div className="interview-title">
      <div>
        <span className="pill">New baseline · conversation-first</span>
        <h2>1. Discuss your project with AI</h2>
        <p className="hint">Your objective: <strong>{task.objective}</strong></p>
      </div>
      <span className="pill">{snapshot?.lastSequence ? 'Interview saved' : 'Not started'}</span>
    </div>

    {!snapshot && !error && <p className="hint">Loading your saved conversation…</p>}
    {error && <div className="alert" role="alert">{error}</div>}

    {snapshot && <>
      {!snapshot.lastSequence && <div className="interview-start">
        <p>The AI will ask relevant questions about <em>your</em> project.
          There is no predefined questionnaire or fixed C# fixture.</p>
        <label>Local AI model
          <select value={chosenModel} disabled={!!busy}
            onChange={event => setChosenModel(event.target.value)}>
            <option value="qwen3.5:4b">Qwen 3.5 4B</option>
            <option value="qwen2.5-coder:3b">Qwen 2.5 Coder 3B (if installed)</option>
          </select>
        </label>
        <button className="primary" disabled={!!busy}
          onClick={() => void submit('start', () =>
            api<InterviewSnapshot>(token, `/api/tasks/${task.id}/interview/turn`, {
              expectedSequence: 0, model: chosenModel,
            }))}>
          Begin AI interview
        </button>
      </div>}

      {!!snapshot.lastSequence && <>
        <ol className="interview-transcript" aria-label="Saved interview">
          {snapshot.messages.map(message => <li
            className={message.role === 'owner' ? 'owner-message' : 'assistant-message'}
            key={message.sequence}>
            <div className="interview-speaker">
              {message.role === 'owner' ? 'You' : 'CodingAssistant'}
              <small>{new Date(message.createdUtc).toLocaleTimeString()}</small>
            </div>
            <div className="interview-message">{message.content}</div>
          </li>)}
        </ol>
        <p className="hint">Model: {snapshot.model} · {ownerAnswers} answer(s) recorded.
          {snapshot.readyForSpecification
            ? ' The AI believes there is enough information to draft a specification.'
            : ' Continue the interview, or draft a first specification when you are ready.'}
        </p>
        {ownerAnswers < 12 && <form onSubmit={event => {
          event.preventDefault();
          void submit('answer', () => api<InterviewSnapshot>(
            token, `/api/tasks/${task.id}/interview/turn`, {
              expectedSequence: snapshot.lastSequence,
              message: answer,
              model: snapshot.model,
            }));
        }}>
          <label>Your answer or additional requirements
            <textarea value={answer} maxLength={2400} required
              onChange={event => setAnswer(event.target.value)}
              placeholder="Describe what matters, or ask the AI to explain a choice…" />
          </label>
          <button className="primary" disabled={!!busy || !answer.trim()}>
            Send answer
          </button>
        </form>}
      </>}

      {!!busy && <div className="activity" role="status" aria-live="polite">
        <div className="activity-heading">
          <span className="activity-dot" aria-hidden="true" />
          <strong>{busy === 'draft' ? 'Drafting your specification' :
            busy === 'approve' ? 'Recording specification approval' :
            'Waiting for the local model'}</strong>
          <span className="pill">{elapsed}s</span>
        </div>
        <div className="activity-track" aria-hidden="true"><div className="activity-scan" /></div>
        <p className="hint">The timer is not confirmed model progress.
          The API will save the result only after a complete, valid response.</p>
      </div>}

      <div className="specification-stage">
        <h2>2. Review and approve the specification</h2>
        <p className="hint">The specification will reflect this interview, not a hard-coded
          application template. You control when to draft and approve it.</p>
        <button type="button" disabled={!!busy || ownerAnswers < 1}
          onClick={() => void submit('draft', () => api<InterviewSnapshot>(
            token, `/api/tasks/${task.id}/interview/specification`, {
              expectedSequence: snapshot.lastSequence,
            }))}>
          {spec ? 'Revise specification from interview' : 'Draft detailed specification'}
        </button>
        {spec && <div className="spec-review">
          <div className="actions">
            <strong>Specification v{spec.version}</strong>
            <span className="pill">{spec.status}</span>
          </div>
          <p className="hint">Immutable SHA-256: <code>{spec.sha256}</code></p>
          <pre className="specification-text">{spec.content}</pre>
          {spec.status === 'Draft' && <>
            <label className="spec-consent">
              <input type="checkbox" checked={approveChecked}
                onChange={event => setApproveChecked(event.target.checked)} />
              I've reviewed this specification and approve it as the intended build contract.
            </label>
            <button className="primary"
              disabled={!!busy || !approveChecked}
              onClick={() => void submit('approve', () => api<InterviewSnapshot>(
                token, `/api/tasks/${task.id}/interview/approve`, {
                  version: spec.version, sha256: spec.sha256,
                }))}>
              Approve this specification
            </button>
          </>}
          {spec.status === 'Approved' && <p className="success">
            ✓ Specification approved and saved. This does not start coding.
          </p>}
          {spec.status === 'Superseded' && <p className="hint">
            The interview changed. Draft a new specification before approval.</p>}
        </div>}
      </div>

      <ProjectDraftPanel token={token} taskId={task.id} spec={spec ?? null} />
    </>}
  </section>;
}
