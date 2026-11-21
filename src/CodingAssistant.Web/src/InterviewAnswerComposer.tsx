import { useId, useState, type FormEvent } from 'react';

export interface InterviewChoice {
  kind: 'single' | 'multiple';
  options: string[];
}

/** Format a selection as an ordinary owner response for the unchanged API. */
export function formatInterviewAnswer(choice: InterviewChoice | null,
  selected: readonly string[], other: string, freeform: string): string {
  if (!choice) return freeform.trim();
  const chosen = choice.kind === 'single' ? selected.slice(0, 1) : selected;
  const choiceText = chosen.length
    ? (choice.kind === 'single' ? 'Selected option: ' : 'Selected options: ') +
      chosen.join('; ') : '';
  const otherText = other.trim();
  return [choiceText, otherText ? 'Something else: ' + otherText : '']
    .filter(Boolean).join('\n');
}

/**
 * Choices are authored by the local Product Owner model and saved with the
 * question. The owner may always type their own answer instead. We submit an
 * ordinary owner message so existing history, specifications, stale checks,
 * and approval contracts remain unchanged.
 */
export function InterviewAnswerComposer({ choice, busy, onAnswer }: {
  choice: InterviewChoice | null;
  busy: boolean;
  onAnswer: (answer: string) => void;
}) {
  const groupId = useId();
  const [selected, setSelected] = useState<string[]>([]);
  const [other, setOther] = useState('');
  const [freeform, setFreeform] = useState('');
  const guided = choice && (choice.kind === 'single' || choice.kind === 'multiple') &&
    choice.options.length >= 2 && choice.options.length <= 5;
  const prepared = formatInterviewAnswer(guided ? choice : null,
    selected, other, freeform);
  const valid = prepared.length > 0 && prepared.length <= 2400;

  function toggle(option: string, checked: boolean) {
    if (choice?.kind === 'single') {
      setSelected(checked ? [option] : []);
      if (checked) setOther('');
    } else {
      setSelected(current => checked ? [...current, option] :
        current.filter(value => value !== option));
    }
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!busy && valid) onAnswer(prepared);
  }

  return <form className="interview-answer-composer" onSubmit={submit}
    aria-label="Answer the Product Owner">
    {guided ? <>
      <fieldset className="interview-choice-fieldset" disabled={busy}>
        <legend>{choice.kind === 'single'
          ? 'Choose one answer, or write your own'
          : 'Choose all that apply, or add your own answer'}</legend>
        <div className="interview-choice-options">
          {choice.options.map((option, index) => <label key={index}
            className={'interview-choice-option' +
              (selected.includes(option) ? ' interview-choice-selected' : '')}>
            <input type={choice.kind === 'single' ? 'radio' : 'checkbox'}
              name={choice.kind === 'single' ? groupId : undefined}
              value={option} checked={selected.includes(option)}
              onChange={event => toggle(option, event.target.checked)} />
            <span className="interview-choice-letter" aria-hidden="true">
              {String.fromCharCode(65 + index)}
            </span>
            <span>{option}</span>
          </label>)}
        </div>
      </fieldset>
      <label className="interview-other-label" htmlFor={groupId + '-other'}>
        Something else <small>— or provide additional detail</small>
        <input id={groupId + '-other'} type="text" value={other}
          disabled={busy} maxLength={2400}
          placeholder="Type your own answer…"
          onChange={event => {
            setOther(event.target.value);
            if (choice.kind === 'single' && event.target.value.trim()) setSelected([]);
          }} />
      </label>
    </> : <label className="interview-freeform-label">Your answer or additional requirements
      <textarea value={freeform} maxLength={2400} disabled={busy}
        onChange={event => setFreeform(event.target.value)}
        placeholder="Describe what matters, or ask the Product Owner to explain a choice…" />
    </label>}
    <div className="interview-composer-footer">
      <span className="interview-composer-hint">{guided
        ? 'Your selection is saved as your answer. Nothing is approved automatically.'
        : 'Free-form answers are always welcome.'}</span>
      <button className="primary" disabled={busy || !valid}>
        {busy ? 'Recording answer…' : 'Send answer →'}
      </button>
    </div>
    {prepared.length > 2400 && <p role="alert" className="hint">
      Shorten the answer to 2,400 characters.</p>}
  </form>;
}
