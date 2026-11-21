import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { ConversationWorkroom, type WorkroomMessage } from '../../src/ConversationWorkroom';
import { InterviewAnswerComposer } from '../../src/InterviewAnswerComposer';
import { DecisionInboxView, buildDecisionInbox } from '../../src/OwnerDecisionInbox';
import { SourceArtifactExplorer } from '../../src/SourceArtifactExplorer';
import { JourneyStage, JourneyStatusStrip, journeySteps, type JourneyId }
  from '../../src/ProjectJourney';
import '../../src/styles.css';
import '../../src/journey.css';
import '../../src/mission-control.css';
import '../../src/conversation-workroom.css';
import '../../src/owner-decision-inbox.css';
import '../../src/source-explorer.css';

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
          content: '# Oddville\\nLocal Ollama only' },
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
            .join('\\n') },
      ]}/>
    <output aria-label="Last submitted answer">{submitted}</output>
  </main>;
}

createRoot(document.getElementById('root')!).render(<BrowserScenario/>);
