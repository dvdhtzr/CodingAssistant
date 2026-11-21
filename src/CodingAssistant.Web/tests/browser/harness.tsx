import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { ConversationWorkroom, type WorkroomMessage } from '../../src/ConversationWorkroom';
import { InterviewAnswerComposer } from '../../src/InterviewAnswerComposer';
import { JourneyStage, JourneyStatusStrip, journeySteps, type JourneyId }
  from '../../src/ProjectJourney';
import '../../src/styles.css';
import '../../src/journey.css';
import '../../src/mission-control.css';
import '../../src/conversation-workroom.css';

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
    <output aria-label="Last submitted answer">{submitted}</output>
  </main>;
}

createRoot(document.getElementById('root')!).render(<BrowserScenario/>);
