import React from 'react';
import { createRoot } from 'react-dom/client';
import { EngineeringStudio } from '../../src/EngineeringStudio';
import '../../src/styles.css';
import '../../src/workbench.css';
import '../../src/journey.css';
import '../../src/mission-control.css';
import '../../src/agent-presence.css';
import '../../src/conversation-workroom.css';
import '../../src/owner-decision-inbox.css';
import '../../src/source-explorer.css';
import '../../src/code-atlas.css';
import '../../src/team-workroom.css';
import '../../src/verification-timeline.css';
import '../../src/design-system.css';
import '../../src/engineering-studio.css';

const task = { id: 'a'.repeat(32), title: 'Historic',
  objective: 'A small local history website', status: 'Active',
  currentVersion: 1, createdUtc: '2026-11-23T12:00:00Z' };
createRoot(document.getElementById('root')!).render(
  <EngineeringStudio task={task} token={'f'.repeat(64)}
    onTeam={() => { document.body.dataset.teamNavigation = 'requested'; }}/>
);
