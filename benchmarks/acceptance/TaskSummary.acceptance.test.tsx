// External acceptance test: injected only during post-execution verification.
import React from 'react';
import { afterEach, expect, test, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import * as TaskModule from './TaskSummary';

const Component = TaskModule.TaskSummary ?? TaskModule.default;

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test('loads task counts and shows actual values', async () => {
  const fetcher = vi.fn().mockResolvedValue({
    ok: true,
    json: async () => ({ total: 3, completed: 2 })
  });
  vi.stubGlobal('fetch', fetcher);
  render(<Component />);
  await screen.findByText(/2\s+of\s+3\s+tasks\s+complete/i);
  expect(fetcher).toHaveBeenCalled();
  expect(String(fetcher.mock.calls[0][0])).toContain('/api/tasks/summary');
});

test('displays a network error', async () => {
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('network unavailable')));
  render(<Component />);
  await waitFor(() => {
    expect(screen.getByText(/error|failed|unable|could not|problem/i)).toBeTruthy();
  });
});
