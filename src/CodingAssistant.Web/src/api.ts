export interface ApiStatus {
  phase: string;
  model: string;
  executionEnabled: boolean;
  localOnly: boolean;
}
export interface TaskItem {
  id: string;
  title: string;
  objective: string;
  status: string;
  currentVersion: number;
  createdUtc: string;
}
export interface PlanItem {
  taskId: string;
  version: number;
  summary: string;
  model: string;
  scope: string;
  planHash: string;
  status: string;
  executionEnabled: boolean;
}
export interface TaskView { task: TaskItem; plan: PlanItem | null; }
export interface HtmlArtifact {
  id: string;
  taskId: string;
  model: string;
  html: string;
  sha256: string;
  createdUtc: string;
}
export interface HtmlArtifactResponse {
  available: boolean;
  artifact: HtmlArtifact | null;
}

export interface RunItem {
  id: string;
  taskId: string;
  planVersion: number;
  planHash: string;
  state: string;
  planCurrent: boolean;
  executionEnabled: boolean;
  lastSequence: number;
  createdUtc: string;
  updatedUtc: string;
}
export interface RunEvent {
  runId: string;
  sequence: number;
  kind: string;
  state: string;
  createdUtc: string;
}
export interface EventPage {
  events: RunEvent[];
  nextCursor: number;
}
export interface ExecutionView {
  runId: string;
  planHash: string;
  state: string;
  plan: {
    fixture: string;
    model: string;
    model_digest: string;
    writable_files: string[];
    image_id: string;
    evaluator_image_id: string;
    no_cloud: boolean;
    no_git_remote: boolean;
    control: { run_id: string; plan_hash: string };
    [key: string]: unknown;
  };
}
export interface RunResult {
  runId: string;
  planHash: string;
  status: 'GATEWAY_APPROVED_CODING_PASS' | 'BLOCKED' | 'NOT_APPROVED';
  phase: string;
  reasonCode: string | null;
  evaluatorVerified: boolean | null;
  sourceChanged: boolean | null;
  inferenceRequests: number | null;
  patch: string;
  patchOmitted: boolean;
}
export interface RunResultResponse {
  available: boolean;
  result: RunResult | null;
}

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly code: string) {
    super(`HTTP ${status}: ${code}`);
  }
}

/** Same-origin bearer authentication. No token ever appears in an URL, body or store. */
export async function api<T>(
  token: string, path: string, body?: Record<string, unknown>,
): Promise<T> {
  if (!/^[0-9a-f]{64}$/.test(token)) throw new Error('Owner token must be 64 lowercase hex characters.');
  if (!path.startsWith('/api/') || path.includes('..') || path.includes('//')) {
    throw new Error('Refusing non-local API endpoint.');
  }
  const response = await fetch(path, {
    method: body === undefined ? 'GET' : 'POST',
    cache: 'no-store',
    credentials: 'omit',
    referrerPolicy: 'no-referrer',
    headers: {
      Authorization: `Bearer ${token}`,
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    const data: unknown = await response.json().catch(() => null);
    const code = typeof data === 'object' && data !== null && 'error' in data &&
      typeof data.error === 'string' && /^[A-Z_]{2,60}$/.test(data.error)
      ? data.error : 'REQUEST_REJECTED';
    throw new ApiError(response.status, code);
  }
  return response.json() as Promise<T>;
}
/** Authenticated destructive operation. Never place credentials in URLs or storage. */
export async function deleteTask(token: string, taskId: string): Promise<void> {
  if (!/^[0-9a-f]{64}$/.test(token))
    throw new Error('Owner token must be 64 lowercase hex characters.');
  if (!isHexId(taskId)) throw new Error('Invalid project identifier.');
  const response = await fetch('/api/tasks/' + taskId, {
    method: 'DELETE',
    cache: 'no-store',
    credentials: 'omit',
    referrerPolicy: 'no-referrer',
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    const data: unknown = await response.json().catch(() => null);
    const code = data && typeof data === 'object' && 'error' in data &&
      typeof data.error === 'string' ? data.error : 'REQUEST_REJECTED';
    throw new ApiError(response.status, code);
  }
}

export function newRequestId(): string {
  return crypto.randomUUID().replaceAll('-', '');
}
export function isHexId(value: string): boolean {
  return /^[0-9a-f]{32}$/.test(value);
}


/** Windows-only same-origin bootstrap. This is intentionally not a cookie:
 * it returns a short-lived browser bearer, never the persisted owner token.
 * Linux returns 404 and keeps the existing explicit-session workflow.
 */
export async function bootstrapLocalSession(): Promise<string | null> {
  const response = await fetch('/api/local-session', {
    method: 'POST',
    mode: 'same-origin',
    credentials: 'omit',
    cache: 'no-store',
    referrerPolicy: 'no-referrer',
    headers: {
      'X-CodingAssistant-Client': 'mission-control',
      'Content-Type': 'application/json',
    },
    body: '{}',
  });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error('Local Windows session could not be established.');
  const payload: unknown = await response.json();
  if (!payload || typeof payload !== 'object' ||
      !('sessionToken' in payload) || typeof payload.sessionToken !== 'string' ||
      !/^[0-9a-f]{64}$/.test(payload.sessionToken)) {
    throw new Error('Local session response was invalid.');
  }
  return payload.sessionToken;
}
