import React from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import './styles.css';
import './workbench.css';
import './journey.css';
import './mission-control.css';
import './agent-presence.css';
import './conversation-workroom.css';
import './owner-decision-inbox.css';
import './source-explorer.css';
import './team-workroom.css';
import './verification-timeline.css';
import './design-system.css';

// Theme foundation: dark-first, with an opt-in light palette honored once a
// stored preference exists. A visible toggle ships after screens are migrated.
try {
  const stored = localStorage.getItem('ca-theme');
  document.documentElement.dataset.theme = stored === 'light' ? 'light' : 'dark';
} catch {
  document.documentElement.dataset.theme = 'dark';
}

const target = document.getElementById('root');
if (!target) throw new Error('Missing Mission Control mount');
createRoot(target).render(<React.StrictMode><App /></React.StrictMode>);
