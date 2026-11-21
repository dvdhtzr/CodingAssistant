import React from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import './styles.css';
import './workbench.css';
import './journey.css';

const target = document.getElementById('root');
if (!target) throw new Error('Missing Mission Control mount');
createRoot(target).render(<React.StrictMode><App /></React.StrictMode>);
