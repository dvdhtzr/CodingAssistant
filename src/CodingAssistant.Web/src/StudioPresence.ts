import type { AgentIdentity } from './AgentIdentityPanel';
import type { AgentRoleSelection } from './AgentRoleModels';
import type { StudioEvidence } from './EngineeringStudio';
import type { JourneyId } from './ProjectJourney';
import { workAgentStatuses } from './WorkTeamStrip';

/** A cross-ledger view of SAVED events, never a synthetic conversation. */
export interface StudioLedgerEntry {
  id: string;
  at: string;
  roleIds: string[];
  title: string;
  detail: string;
  originalKind: string;
  historical: boolean;
  stage: JourneyId;
}

export interface StudioAgentPresence {
  agent: AgentIdentity;
  active: boolean;
  state: 'running' | 'stopped' | 'recorded' | 'unstarted';
  status: string;
  assignment: string;
  modelForNextRun: string | null;
  modelForCurrentRun: string | null;
  handoffCount: number;
  lastRecordedKind: string | null;
}

const stageOf: Record<string, JourneyId> = {
  ProductOwner: 'discovery', SolutionArchitect: 'architecture',
  TechnicalLead: 'plan', SoftwareEngineer: 'generation', QaEngineer: 'delivery',
};
const roleName: Record<string, string> = {
  ProductOwner: 'Product Owner', SolutionArchitect: 'Solution Architect',
  TechnicalLead: 'Technical Lead', SoftwareEngineer: 'Software Engineer',
  QaEngineer: 'QA Engineer', HumanOwner: 'You',
};

const namedEvent: Record<string, string> = {
  EngineeringRunAdmitted: 'Engineering work assigned',
  EngineeringRunResumed: 'Engineering resumed from saved source',
  EngineeringTaskStarted: 'Engineer started an assignment',
  EngineeringTaskCompleted: 'Engineer saved a source revision',
  EngineeringRunFailed: 'Engineering run stopped',
  EngineeringRunInterrupted: 'Engineering run interrupted',
  EngineerAtlasInspected: 'Engineer inspected Code Atlas',
  QaReviewAdmitted: 'QA source review started',
  QaStaticReportSaved: 'QA saved an independent review',
  QaReviewFailed: 'QA review stopped',
  QaAtlasInspected: 'QA inspected Code Atlas',
  RepairRunAdmitted: 'Approved repair started',
  ArchitectureProposalSaved: 'Architect submitted a recommendation',
  EngineeringTasksAssigned: 'Technical Lead handed off work',
};

function friendlyEvent(kind: string) {
  return namedEvent[kind] ??
    kind.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_-]/g, ' ');
}

/** Host receipts ordered by persisted UTC. A snapshot is not an activity receipt. */
export function studioRecordedActivity(saved: StudioEvidence | null): StudioLedgerEntry[] {
  if (!saved) return [];
  const history: StudioLedgerEntry[] = [
    ...saved.handoffs.map((event, index) => ({
      id: 'handoff:' + event.createdUtc + ':' + event.kind + ':' + index,
      at: event.createdUtc,
      roleIds: [event.fromAgent, event.toAgent],
      title: (roleName[event.fromAgent] ?? event.fromAgent) + ' → ' +
        (roleName[event.toAgent] ?? event.toAgent),
      detail: friendlyEvent(event.kind) +
        (event.workItemId ? ' · ' + event.workItemId : ''),
      originalKind: event.kind,
      historical: !event.current,
      stage: stageOf[event.fromAgent] ?? stageOf[event.toAgent] ?? 'discovery',
    })),
    ...(saved.engineering?.events ?? []).map(event => ({
      id: 'engineering:' + saved.engineering!.job.id + ':' + event.sequence,
      at: event.createdUtc,
      roleIds: ['SoftwareEngineer'],
      title: friendlyEvent(event.kind),
      detail: event.detail,
      originalKind: event.kind,
      historical: false,
      stage: 'generation' as JourneyId,
    })),
    ...(saved.qa?.events ?? []).map(event => ({
      id: 'qa:' + saved.qa!.job.id + ':' + event.sequence,
      at: event.createdUtc,
      roleIds: ['QaEngineer'],
      title: friendlyEvent(event.kind),
      detail: event.detail,
      originalKind: event.kind,
      historical: saved.qa?.job.engineeringRunId !== saved.engineering?.job.id ||
        saved.qa?.job.planSha256 !== saved.engineering?.job.planSha256,
      stage: 'delivery' as JourneyId,
    })),
  ];
  // Ordering conveys recency, NOT causal dependence between the separate stores.
  return history.sort((a, b) => b.at.localeCompare(a.at) || a.id.localeCompare(b.id));
}

/** Do not infer activity for the PO/Architect/Lead from their defined identity. */
export function studioAgentPresence(saved: StudioEvidence | null,
  models: readonly AgentRoleSelection[] | null): StudioAgentPresence[] {
  if (!saved) return [];
  const status = workAgentStatuses(saved);
  return status.map(({ agent, active, detail }) => {
    const handoffs = saved.handoffs.filter(event =>
      event.fromAgent === agent.id || event.toAgent === agent.id);
    const recent = handoffs.slice().sort((a, b) =>
      b.createdUtc.localeCompare(a.createdUtc))[0];
    const currentQa = saved.qa?.job.engineeringRunId === saved.engineering?.job.id &&
      saved.qa?.job.planSha256 === saved.engineering?.job.planSha256 ? saved.qa : null;
    const currentRepair = saved.repair?.cycle.engineeringRunId === saved.engineering?.job.id ? saved.repair : null;
    const job = agent.id === 'SoftwareEngineer' ? saved.engineering?.job :
      agent.id === 'QaEngineer' ? currentQa?.job : null;
    const model = models?.find(item => item.role === agent.id)?.model ?? null;
    const stopped = job?.state === 'Failed' || job?.state === 'Interrupted' ||
      agent.id === 'SoftwareEngineer' &&
      (currentRepair?.cycle.state === 'Failed' ||
       currentRepair?.cycle.state === 'Interrupted');
    const state = active ? 'running' : stopped ? 'stopped' :
      job || recent ? 'recorded' : 'unstarted';
    const assignment = agent.id === 'SoftwareEngineer' ?
      active ? detail : job ?
        saved.engineering?.tasks.filter(item => item.state === 'Completed').length +
        ' of ' + saved.engineering?.tasks.length + ' assigned task(s) saved' :
        'No source work recorded'
      : agent.id === 'QaEngineer' ? active ?
        saved.qa?.reports.length + ' file(s) inspected · source-only QA' :
        job ? currentQa?.reports.length + ' saved file report(s)' :
          'No source review recorded'
      : recent ? 'Last recorded: ' + friendlyEvent(recent.kind) :
        'No recorded handoff yet';
    return {
      agent, active, state,
      status: active ? 'Working' : stopped ? 'Needs inspection' :
        job ? job.state : recent ? 'Handoff recorded' : 'No work recorded',
      assignment,
      modelForNextRun: model,
      modelForCurrentRun: active ? job?.model ?? null : null,
      handoffCount: handoffs.length,
      lastRecordedKind: recent?.kind ?? null,
    };
  });
}
