import { useEffect, useState } from 'react';
import { api } from './api';

export interface AgentRoleSelection {
  role: string;
  model: string;
  overridden: boolean;
  source: string;
}

const readable: Record<string, string> = {
  ProductOwner: 'Product Owner',
  SolutionArchitect: 'Solution Architect',
  TechnicalLead: 'Technical Lead',
  SoftwareEngineer: 'Software Engineer',
  LeadSoftwareEngineer: 'Lead Software Engineer',
  QaEngineer: 'QA Engineer',
};

export function AgentRoleModels({ token, taskId, onClose, onSaved }: {
  token: string; taskId: string; onClose: () => void; onSaved?: () => void;
}) {
  const [assignments, setAssignments] = useState<AgentRoleSelection[] | null>(null);
  const [installed, setInstalled] = useState<string[]>([]);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [refresh, setRefresh] = useState(0);

  useEffect(() => {
    let closed = false;
    setAssignments(null);
    setError('');
    void Promise.all([
      api<{ agents: AgentRoleSelection[] }>(token,
        '/api/tasks/' + taskId + '/agent-models'),
      api<{ models: string[] }>(token, '/api/models'),
    ]).then(([choices, models]) => {
      if (closed) return;
      setAssignments(choices.agents);
      setInstalled(models.models);
    }).catch(cause => {
      if (!closed) setError(cause instanceof Error ?
        cause.message : 'Cannot load locally installed models.');
    });
    return () => { closed = true; };
  }, [token, taskId, refresh]);

  async function assign(role: string, chosen: string) {
    if (busy) return;
    setBusy(role); setError(''); setNotice('');
    try {
      const next = await api<AgentRoleSelection>(token,
        '/api/tasks/' + taskId + '/agent-models/' + role, {
          model: chosen === '__default__' ? null : chosen,
        });
      setAssignments(previous => previous?.map(item =>
        item.role === role ? next : item) ?? null);
      setNotice(readable[role] + ' will use ' + next.model + ' on new work.');
      onSaved?.();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message :
        'Could not save model assignment.');
    } finally { setBusy(''); }
  }

  return <section className="ca-role-models" aria-label="Per-agent local model assignments">
    <div className="ca-role-models-header">
      <div>
        <span className="ca-studio-eyebrow">LOCAL OLLAMA / TEAM ASSIGNMENTS</span>
        <h2>Choose each specialist's model</h2>
        <p>Different roles can use different downloaded models. Changes apply to
          <strong> new work only</strong>; existing runs and approvals are unchanged.</p>
      </div>
      <button type="button" className="outline" onClick={onClose}>Close models</button>
    </div>
    <div className="ca-role-models-tools">
      <button type="button" className="outline" disabled={!!busy}
        onClick={() => setRefresh(n => n + 1)}>
        Refresh local Ollama models
      </button>
      <span>{installed.length} installed model{installed.length === 1 ? '' : 's'}</span>
    </div>
    {error && <p className="alert" role="alert">{error}</p>}
    {notice && <p className="success" role="status">{notice}</p>}
    {!assignments && !error && <p className="ca-studio-muted" role="status">
      Reading saved model assignments…</p>}
    {assignments && <div className="ca-role-model-grid">
      {assignments.map(choice => {
        const available = installed.includes(choice.model);
        return <div className="ca-role-model-card" key={choice.role}>
          <label htmlFor={'agent-model-' + choice.role}>
            <strong>{readable[choice.role] ?? choice.role}</strong>
            <small>{choice.source}</small>
          </label>
          {choice.role === 'ProductOwner'
            ? <p className="ca-role-model-current">Interview model: <code>{choice.model}</code>
              <small>{choice.source === 'Pinned interview model'
                ? 'Selected and pinned in Product Discovery.'
                : 'Select this role\'s model when you start Product Discovery.'}</small>
            </p>
            : <select id={'agent-model-' + choice.role}
                aria-label={readable[choice.role] + ' model'}
                value={choice.overridden ? choice.model : '__default__'}
                disabled={!!busy || !installed.length}
                onChange={event => void assign(choice.role, event.target.value)}>
                <option value="__default__">Default: {choice.role === 'LeadSoftwareEngineer' ? 'qwen3-coder:30b (provisional)' :
                  choice.role === 'SoftwareEngineer' || choice.role === 'QaEngineer'
                    ? 'qwen2.5-coder:14b' : 'approved specification model'}</option>
                {installed.map(model => <option value={model} key={model}>{model}</option>)}
              </select>}
          <small className={available ? 'ca-role-model-ok' : 'ca-role-model-missing'}>
            {available ? 'Installed locally' : 'Not installed — cannot start new work'}
          </small>
        </div>;
      })}
    </div>}
    {assignments?.some(item => !installed.includes(item.model)) && <p
      className="ca-role-model-install">
      Install the missing selected models in local Ollama, or choose an already installed model.
      The reviewer default is provisional and has not been benchmarked.
      No model is downloaded or switched automatically.
    </p>}
  </section>;
}
