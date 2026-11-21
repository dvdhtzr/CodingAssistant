import { useEffect, useState } from 'react';
import { api } from './api';

interface AgentIdentity {
  id: string;
  role: string;
  goal: string;
  backstory: string;
  workingStyle: string;
  allowedSkillScopes: string[];
  canExecuteCode: boolean;
}

export function AgentIdentityPanel({ token }: { token: string }) {
  const [agents, setAgents] = useState<AgentIdentity[] | null>(null);
  const [error, setError] = useState(false);
  useEffect(() => {
    let closed = false;
    setAgents(null);
    setError(false);
    void api<{ agents: AgentIdentity[] }>(token, '/api/agents/roster')
      .then(value => { if (!closed) setAgents(value.agents); })
      .catch(() => { if (!closed) setError(true); });
    return () => { closed = true; };
  }, [token]);

  return <section className="wb-agent-identities" aria-label="Independent agent identities">
    <p className="wb-kicker">AGENT IDENTITIES</p>
    <p className="wb-muted">Each specialist has a separate MAF identity,
      professional history, goals and responsibilities.</p>
    {error && <p className="wb-muted">Unable to load agent identities.</p>}
    {agents === null && !error && <p className="wb-muted">Loading agent roster…</p>}
    {agents?.map(agent => <details key={agent.id}>
      <summary>{agent.role}</summary>
      <p><strong>Goal:</strong> {agent.goal}</p>
      <p><strong>Backstory:</strong> {agent.backstory}</p>
      <p><strong>Working style:</strong> {agent.workingStyle}</p>
      <p><strong>Future skill scopes:</strong> {agent.allowedSkillScopes.join(', ')}</p>
      <p><strong>Executable tools:</strong> {agent.canExecuteCode ? 'Available' :
        'Not authorized'}</p>
    </details>)}
    <p className="wb-muted">SKILL.md packages are planned; these scopes do not
      install or activate any skills.</p>
  </section>;
}
