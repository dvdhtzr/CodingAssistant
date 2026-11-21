import type { ReactNode } from 'react';

export interface WorkroomMessage {
  sequence: number;
  role: 'owner' | 'assistant';
  content: string;
  createdUtc: string;
}

function Speaker({ message, children }: {
  message: WorkroomMessage;
  children: ReactNode;
}) {
  return <div className="interview-speaker">
    <span>{message.role === 'owner' ? 'You' : 'Product Owner'}</span>
    <time dateTime={message.createdUtc}>
      {new Date(message.createdUtc).toLocaleTimeString()}
    </time>
    {children}
  </div>;
}

/**
 * The most recent saved conversation turn stays in sight beside the existing
 * owner answer composer. Previous turns remain intact in a disclosure instead
 * of pushing the active question out of view. No API writes or simulated turns.
 */
export function ConversationWorkroom({ messages }: {
  messages: readonly WorkroomMessage[];
}) {
  if (!messages.length) return null;
  const latest = messages[messages.length - 1];
  const history = messages.slice(0, -1);
  const hasQuestion = latest.role === 'assistant';

  return <section className="conversation-workroom"
    aria-label="Product Owner conversation workroom">
    <div className="conversation-current" aria-live="polite">
      <div className="conversation-current-heading">
        <span className="wb-kicker">{hasQuestion
          ? 'CURRENT PRODUCT OWNER QUESTION' : 'LAST SAVED OWNER ANSWER'}</span>
        <span className="conversation-current-number">
          Turn {latest.sequence}
        </span>
      </div>
      <Speaker message={latest}>{null}</Speaker>
      <div className="conversation-current-content">{latest.content}</div>
    </div>
    {history.length > 0 && <details className="conversation-history">
      <summary>
        <span className="conversation-history-icon" aria-hidden="true">▸</span>
        <span>Earlier conversation</span>
        <small>{history.length} saved message{history.length === 1 ? '' : 's'}</small>
      </summary>
      <ol className="interview-transcript" aria-label="Earlier Product Owner conversation"
        tabIndex={0}>
        {history.map(message => <li
          className={message.role === 'owner' ? 'owner-message' : 'assistant-message'}
          key={message.sequence}>
          <Speaker message={message}>{null}</Speaker>
          <div className="interview-message">{message.content}</div>
        </li>)}
      </ol>
    </details>}
  </section>;
}
