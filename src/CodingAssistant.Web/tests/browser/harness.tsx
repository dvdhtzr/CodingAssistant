import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { ConversationWorkroom, type WorkroomMessage } from '../../src/ConversationWorkroom';
import { InterviewAnswerComposer } from '../../src/InterviewAnswerComposer';
import { InterviewStudio } from '../../src/InterviewStudio';
import { DecisionInboxView, buildDecisionInbox } from '../../src/OwnerDecisionInbox';
import { SourceArtifactExplorer } from '../../src/SourceArtifactExplorer';
import { TeamWorkroomView, type TeamEvidence } from '../../src/TeamWorkroom';
import { VerificationTimelineView } from '../../src/VerificationTimeline';
import { VerificationScopeView, type VerificationScope }
  from '../../src/VerificationScope';
import { JourneyStage, JourneyStatusStrip, journeySteps, type JourneyId }
  from '../../src/ProjectJourney';
import '../../src/styles.css';
import '../../src/journey.css';
import '../../src/mission-control.css';
import '../../src/conversation-workroom.css';
import '../../src/owner-decision-inbox.css';
import '../../src/source-explorer.css';
import '../../src/team-workroom.css';
import '../../src/verification-timeline.css';

const seed: WorkroomMessage[] = Array.from({ length: 12 }, (_, index) => ({
  sequence: index + 1,
  role: index % 2 === 0 ? 'assistant' : 'owner',
  content: (index % 2 === 0 ? 'Product Owner question ' : 'Owner answer ') +
    (index + 1) + ': ' + 'Explain what happened in Oddville. '.repeat(9),
  createdUtc: '2026-11-21T14:00:00Z',
}));
seed.push({
  sequence: 13, role: 'assistant',
  content: 'How should Oddville residents remember each other? Choose all that apply.',
  createdUtc: '2026-11-21T14:02:00Z',
});
const steps = journeySteps.map(step =>
  step.id === 'discovery' ? { ...step, tone: 'active' as const, status: '6 answers saved' } :
  step.id === 'specification' ? { ...step, tone: 'attention' as const,
    status: 'Review required' } : step);

const teamFixture: TeamEvidence = {
  agents: [
    { id: 'ProductOwner', role: 'Product Owner',
      goal: 'Protect accepted requirements', workingStyle: 'Focused questions',
      backstory: 'Fictional product veteran', allowedSkillScopes: ['product-discovery'],
      canExecuteCode: false },
    { id: 'SolutionArchitect', role: 'Solution Architect',
      goal: 'Choose reliable architecture', workingStyle: 'Compare tradeoffs',
      backstory: 'Fictional solution designer',
      allowedSkillScopes: ['architecture-review'], canExecuteCode: false },
    { id: 'TechnicalLead', role: 'Technical Lead',
      goal: 'Plan vertical slices', workingStyle: 'Pragmatic sequencing',
      backstory: 'Fictional delivery lead',
      allowedSkillScopes: ['task-decomposition'], canExecuteCode: false },
    { id: 'SoftwareEngineer', role: 'Software Engineer',
      goal: 'Implement approved tasks', workingStyle: 'Source-first engineering',
      backstory: 'Fictional local developer',
      allowedSkillScopes: ['source-implementation'], canExecuteCode: false },
    { id: 'QaEngineer', role: 'QA Engineer',
      goal: 'Inspect source independently', workingStyle: 'Skeptical review',
      backstory: 'Fictional QA specialist',
      allowedSkillScopes: ['static-code-review'], canExecuteCode: false },
  ],
  events: [
    { kind: 'SpecificationHandoff',
      fromAgent: 'ProductOwner', toAgent: 'SolutionArchitect',
      specificationVersion: 1, revision: 0, model: 'qwen3.5:9b',
      current: false, createdUtc: '2026-11-21T14:00:00Z' },
    { kind: 'EngineeringTaskAssigned',
      fromAgent: 'TechnicalLead', toAgent: 'SoftwareEngineer',
      specificationVersion: 2, revision: 1, model: 'qwen3.5:9b',
      workItemId: 'W1', current: true, createdUtc: '2026-11-21T15:00:00Z' },
    { kind: 'QaReviewRequested',
      fromAgent: 'SoftwareEngineer', toAgent: 'QaEngineer',
      specificationVersion: 2, revision: 1, model: 'qwen3.5:9b',
      current: true, createdUtc: '2026-11-21T15:02:00Z' },
  ],
  engineering: {
    job: {
      id: 'c'.repeat(32), taskId: 'a'.repeat(32), planRevision: 1,
      planSha256: 'b'.repeat(64), specificationVersion: 2,
      specificationSha256: 'a'.repeat(64), architectureSha256: 'd'.repeat(64),
      model: 'qwen3.5:9b', state: 'Generated', phase: 'SourceOnlyGenerated',
      errorCode: null, createdUtc: '2026-11-21T15:00:00Z',
      updatedUtc: '2026-11-21T15:03:00Z',
    },
    tasks: [
      { id: 'W1', ordinal: 0, title: 'Build residents',
        state: 'Completed', startedUtc: '2026-11-21T15:00:00Z',
        completedUtc: '2026-11-21T15:02:00Z', summary: 'Source saved' },
      { id: 'W2', ordinal: 1, title: 'Build dashboard',
        state: 'Pending', startedUtc: null, completedUtc: null, summary: null },
    ],
    events: [], files: [],
  },
  qa: {
    job: { id: 'e'.repeat(32), state: 'Reviewed',
      engineeringRunId: 'f'.repeat(32), planSha256: 'b'.repeat(64) },
    sourceState: 'Generated', reports: [{
      path: 'src/Models/Resident.cs', reviewedUtc: '2026-11-21T15:05:00Z',
    }],
  },
};

const scopeFixture: VerificationScope = {
  schemaVersion: 1, taskId: 'a'.repeat(32), engineeringRunId: 'c'.repeat(32),
  planRevision: 1, planSha256: 'b'.repeat(64),
  specificationVersion: 2, specificationSha256: 'a'.repeat(64),
  architectureSha256: 'd'.repeat(64), model: 'qwen3.5:9b',
  sourceFingerprint: 'e'.repeat(64), scopeSha256: 'f'.repeat(64),
  status: 'SCOPE_CANDIDATE', executionEnabled: false,
  verificationStatus: 'GENERATED_NOT_TESTED', blockers: [],
  files: [{ path: 'index.html', sha256: '1'.repeat(64), utf8Bytes: 240 },
    { path: 'app.js', sha256: '2'.repeat(64), utf8Bytes: 181 }],
};

function BrowserScenario() {
  const [active, setActive] = useState<JourneyId>('discovery');
  const [submitted, setSubmitted] = useState('');
  return <main className="journey-shell" style={{ maxWidth: 1120, margin: '20px auto' }}>
    <JourneyStatusStrip title="Oddville browser fixture" activeId={active}
      steps={steps} onNavigate={setActive}/>
    <DecisionInboxView
      items={buildDecisionInbox({
        specification: { version: 2, sha256: 'a'.repeat(64), status: 'Approved' },
        ownerAnswers: 12, readyForSpecification: true,
        decisions: [
          { topic: 'technology-stack', revision: 1, specificationSha256: 'a'.repeat(64),
            specificationCurrent: true, status: 'Proposed' },
          { topic: 'architecture-style', revision: 1, specificationSha256: 'a'.repeat(64),
            specificationCurrent: true, status: 'Approved' },
        ],
        plan: null,
      })}
      onNavigate={setActive}/>
    {steps.map((step, index) => <JourneyStage key={step.id} step={step}
      index={index} open={active === step.id}
      onOpen={id => setActive(previous => previous === id ? null : id)}>
      {step.id === 'discovery' ? <section className="interview-studio">
        <ConversationWorkroom messages={seed}/>
        <InterviewAnswerComposer choice={{
          kind: 'multiple',
          options: ['Personality evolves', 'Persistent memories', 'Town events'],
        }} busy={false} onAnswer={setSubmitted}/>
      </section> : <p>Stage content: {step.title}. Existing controls stay mounted.</p>}
    </JourneyStage>)}
    <TeamWorkroomView evidence={teamFixture} onNavigate={setActive}/>
    <VerificationTimelineView evidence={{
      engineering: { ...teamFixture.engineering!,
        events: [{ sequence: 1, kind: 'SourceFileSaved', detail: 'Saved index.html',
          createdUtc: '2026-11-21T15:03:00Z' }] },
      qa: { ...teamFixture.qa!, job: { ...teamFixture.qa!.job,
        sourceSha256: 'a'.repeat(64), model: 'qwen3.5:9b', errorCode: null,
        createdUtc: '2026-11-21T15:04:00Z' },
        events: [{ sequence: 1, kind: 'QaReviewAdmitted',
          detail: 'Static review accepted', createdUtc: '2026-11-21T15:04:00Z' }] },
      plan: { revision: 1, planSha256: 'b'.repeat(64),
        status: 'Approved', current: true },
      profile: { id: 'static-web', version: '0.1.0',
        qualificationStatus: 'UNQUALIFIED', executableViaRegistry: false,
        requiredIndependentTests: ['Linked asset integrity', 'Chromium interactions'] },
      registryExecutionAvailable: false,
      preflight: { runId: 'c'.repeat(32), sourceState: 'Generated',
        sourceSha256: 'f'.repeat(64), status: 'POTENTIAL_ISSUES',
        verificationStatus: 'GENERATED_NOT_TESTED',
        htmlFileCount: 1, referencedAssets: 0, findings: [
          { code: 'NO_LINKED_JAVASCRIPT', severity: 'Warning',
            path: 'index.html', detail: 'Saved JavaScript not linked from HTML' },
        ] },
    }}/>
    <VerificationScopeView scope={scopeFixture}/>
    <VerificationScopeView scope={{ ...scopeFixture, status: 'BLOCKED',
      blockers: ['MISSING_ROOT_INDEX_HTML', 'SOURCE_FILE_HASH_MISMATCH'],
      scopeSha256: null, sourceFingerprint: null, files: [] }}/>
    <SourceArtifactExplorer
      source="MAF Software Engineer"
      state="Generated" runId={'f'.repeat(32)} isCurrent
      assignments={[
        { id: 'T1', ordinal: 0, title: 'Build residents' },
        { id: 'T2', ordinal: 1, title: 'Build town dashboard' },
      ]}
      files={[
        { path: 'README.md', purpose: 'Setup instructions',
          sha256: 'a'.repeat(64), lastWorkItemId: 'T1',
          updatedUtc: '2026-11-21T15:00:00Z',
          content: '# Oddville\nLocal Ollama only' },
        { path: 'src/Models/Resident.cs', purpose: 'Resident domain',
          sha256: 'b'.repeat(64), lastWorkItemId: 'T1',
          updatedUtc: '2026-11-21T15:01:00Z',
          content: 'public class Resident { }' },
        { path: 'src/Views/Overview.tsx', purpose: 'Town dashboard',
          sha256: 'c'.repeat(64), lastWorkItemId: 'T2',
          updatedUtc: '2026-11-21T15:02:00Z',
          content: 'export const title = "<script>alert(1)</script>";' },
        { path: 'src/LongPayload.ts', purpose: 'Long source preview',
          sha256: 'd'.repeat(64), lastWorkItemId: 'T2',
          updatedUtc: '2026-11-21T15:03:00Z',
          content: Array.from({ length: 810 }, (_, i) => 'const n' + i + ' = ' + i + ';')
            .join('\n') },
      ]}/>
    <output aria-label="Last submitted answer">{submitted}</output>
  </main>;
}

function CompleteInterviewScenario() {
  const task = {
    id: 'a'.repeat(32), title: 'Podcast analytics',
    objective: 'Build a podcast audience demographics dashboard',
    status: 'Active', currentVersion: 1,
    createdUtc: '2026-11-22T17:00:00Z',
  };
  return <InterviewStudio token={'f'.repeat(64)} task={task}/>;
}

createRoot(document.getElementById('root')!).render(
  window.location.search.includes('specification-acceptance')
    ? <CompleteInterviewScenario/>
    : <BrowserScenario/>);
