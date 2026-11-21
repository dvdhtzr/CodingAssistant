import { useEffect, useState } from 'react';
import { api } from './api';

export interface AgentIdentity {
  id: string;
  role: string;
  goal: string;
  backstory: string;
  workingStyle: string;
  allowedSkillScopes: string[];
  canExecuteCode: boolean;
}

/** Only host-owned roster identities. Nothing here implies an agent is active. */
export function AgentRosterCards({ agents }: { agents: readonly AgentIdentity[] }) {
  return <div className="wb-agent-roster-cards">
    {agents.map(agent => <details className="wb-agent-profile"
      data-agent={agent.id} key={agent.id}>
      <summary>
        <span className="wb-agent-profile-mark" aria-hidden="true">
          {agent.role.split(' ').map(part => part[0]).join('').slice(0, 2)}
        </span>
        <span className="wb-agent-profile-title">
          <strong>{agent.role}</strong>
          <small>{agent.goal}</small>
        </span>
        <span className="wb-agent-profile-chevron" aria-hidden="true">⌄</span>
      </summary>
      <div className="wb-agent-profile-detail">
        <div className="wb-agent-profile-field">
          <span>PERSONALITY & WORKING STYLE</span>
          <p>{agent.workingStyle}</p>
        </div>
        <div className="wb-agent-profile-field">
          <span>FICTIONAL PROFESSIONAL BACKSTORY</span>
          <p>{agent.backstory}</p>
        </div>
        <div className="wb-agent-profile-field">
          <span>FUTURE SKILL SCOPES (NOT ENABLED)</span>
          <p>{agent.allowedSkillScopes.length
            ? agent.allowedSkillScopes.join(' · ') : 'None listed'}</p>
        </div>
        <p className="wb-agent-profile-permission">Executable tools:
          <strong> {agent.canExecuteCode ? 'Host-authorized' : 'Not authorized'}</strong>
        </p>
      </div>
    </details>)}
  </div>;
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
    <p className="wb-kicker">ENGINEERING PERSONNEL</p>
    <p className="wb-muted">Five distinct host-defined MAF identities, each with
      separate responsibilities, goals and fictional professional histories.
      These cards are a roster, not a live presence indicator.</p>
    {error && <p className="wb-muted" role="status">Unable to load the saved agent roster.</p>}
    {agents === null && !error && <p className="wb-muted">Loading agent roster…</p>}
    {agents?.length === 0 && <p className="wb-muted">No agents registered.</p>}
    {agents && <AgentRosterCards agents={agents} />}
    <p className="wb-muted">SKILL.md packages are planned; their descriptors do not
      install skills, execute code or grant permissions.</p>
  </section>;
}
