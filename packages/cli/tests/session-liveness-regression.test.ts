import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

vi.mock('../src/config/config-loader', () => ({
  resolveOpencodeBinPath: () => '/mock/opencode',
}));

vi.mock('child_process', () => {
  const { EventEmitter } = require('events');
  const { Writable, Readable } = require('stream');
  return {
    spawn: vi.fn(() => {
      const proc = new EventEmitter();
      (proc as any).stdin = new Writable({ write: (_c: any, _e: any, cb: any) => cb() });
      (proc as any).stdout = new Readable({ read() {} });
      (proc as any).kill = vi.fn();
      (proc as any).pid = 12345;
      return proc;
    }),
    execFile: vi.fn(),
  };
});

const mockPromptFn = vi.fn();
const mockCancelFn = vi.fn();
const mockSetSessionConfigOptionFn = vi.fn();
let globalSessionUpdateFn: ((notification: any) => void) | undefined;

vi.mock('@agentclientprotocol/sdk', () => ({
  ClientSideConnection: vi.fn().mockImplementation((callbackFactory: () => { sessionUpdate: (n: any) => void; requestPermission: (...args: any[]) => any }, _stream: any) => {
    const callbacks = callbackFactory();
    globalSessionUpdateFn = callbacks.sessionUpdate;
    return {
      initialize: vi.fn().mockResolvedValue({ protocolVersion: '0.1' }),
      newSession: vi.fn().mockResolvedValue({ sessionId: 'test-session-123' }),
      prompt: mockPromptFn,
      cancel: mockCancelFn,
      setSessionConfigOption: mockSetSessionConfigOptionFn,
    };
  }),
  ndJsonStream: vi.fn().mockReturnValue({
    readable: { cancel: vi.fn().mockResolvedValue(undefined) },
    writable: { abort: vi.fn().mockResolvedValue(undefined) },
  }),
  PROTOCOL_VERSION: '0.1',
}));

import { AgentSession } from '../src/agent-runtime/agent-session';
import type { SessionObserver, LivenessUpdate } from '../src/agent-runtime/session-observer';
import { DatabaseManager } from '../src/db/database';
import { StateManager } from '../src/server/state-manager';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { EventBus } from '../src/services/event-bus';
import { WorkflowSessionObserver } from '../src/services/session-observers';
import { formatSessionState, type CoderSessionResponse } from '../src/cli/commands/issue';

function emitAgentMessageChunk(text: string): void {
  globalSessionUpdateFn?.({
    update: {
      sessionUpdate: 'agent_message_chunk',
      content: { text },
    },
  });
}

const QUIET_THRESHOLD_MS = 100;
const PROBE_TIMEOUT_MS = 50;

describe('Session liveness end-to-end regression', () => {
  let db: DatabaseManager;
  let stateManager: StateManager;
  let coderSessionRepo: any;
  let issueService: IssueService;
  let projectService: ProjectService;
  let projectId: string;

  beforeEach(async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    mockPromptFn.mockReset();
    mockCancelFn.mockReset();
    mockSetSessionConfigOptionFn.mockResolvedValue(undefined);
    globalSessionUpdateFn = undefined;

    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);
    coderSessionRepo = stateManager.getCoderSessionRepo();

    const projectRepo = stateManager.getProjectRepo();
    const configRepo = stateManager.getConfigRepo();
    const issueRepo = stateManager.getIssueRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();

    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);

    const project = await projectService.create({ name: 'TestProject', path: '/test/path' });
    projectId = project.id;
    projectService.setCurrent(project);
  });

  afterEach(() => {
    vi.useRealTimers();
    globalSessionUpdateFn = undefined;
    db.close();
  });

  function createIssue(stage: string = 'build', status: string = 'active') {
    const issue = issueService.create({ projectId, title: 'Test issue' });
    if (stage !== 'backlog' || status !== 'active') {
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.update(issue.id, { stage: stage as any, status: status as any });
      return issueRepo.findById(issue.id)!;
    }
    return issue;
  }

  it('ACP data updates lastDataAt and does not trigger probe before threshold', async () => {
    const stateChanges: Array<{ from: string; to: string }> = [];
    const observer: SessionObserver = {
      onStateChange(_ctx, from, to) {
        stateChanges.push({ from, to });
      },
    };

    mockPromptFn.mockImplementation(() => {
      emitAgentMessageChunk('data 1');
      emitAgentMessageChunk('data 2');
      return Promise.resolve();
    });

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      livenessQuietThresholdMs: QUIET_THRESHOLD_MS,
      probeTimeoutMs: PROBE_TIMEOUT_MS,
      observers: [observer],
    });

    await session.execute('test');

    const probingChanges = stateChanges.filter(c => c.to === 'probing');
    expect(probingChanges).toHaveLength(0);

    await session.close();
  });

  it('quiet running session enters probing and records probeSentAt/probeDeadlineAt', async () => {
    const stateChanges: Array<{ from: string; to: string }> = [];
    const livenessUpdates: LivenessUpdate[] = [];

    const observer: SessionObserver = {
      onStateChange(_ctx, from, to) {
        stateChanges.push({ from, to });
      },
      onLivenessUpdate(_ctx, update) {
        livenessUpdates.push(update);
      },
    };

    let promptCallCount = 0;
    mockPromptFn.mockImplementation(() => {
      promptCallCount++;
      return new Promise(() => {});
    });

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      livenessQuietThresholdMs: QUIET_THRESHOLD_MS,
      probeTimeoutMs: PROBE_TIMEOUT_MS,
      observers: [observer],
    });

    const executePromise = session.execute('test');

    await vi.advanceTimersByTimeAsync(QUIET_THRESHOLD_MS + 50);

    const probingChange = stateChanges.find(c => c.to === 'probing');
    expect(probingChange).toBeDefined();
    expect(probingChange!.from).toBe('running');

    const probingUpdate = livenessUpdates.find(u => u.status === 'probing');
    expect(probingUpdate).toBeDefined();
    expect(probingUpdate!.probeSentAt).not.toBeNull();
    expect(probingUpdate!.probeDeadlineAt).not.toBeNull();

    expect(promptCallCount).toBeGreaterThanOrEqual(2);

    const probeDeadline = new Date(probingUpdate!.probeDeadlineAt!).getTime();
    const probeSent = new Date(probingUpdate!.probeSentAt!).getTime();
    expect(probeDeadline - probeSent).toBeGreaterThanOrEqual(PROBE_TIMEOUT_MS - 10);

    await session.close().catch(() => {});
    await executePromise.catch(() => {});
  });

  it('probing session returns to running when new data arrives', async () => {
    const stateChanges: Array<{ from: string; to: string }> = [];
    const livenessUpdates: LivenessUpdate[] = [];

    const observer: SessionObserver = {
      onStateChange(_ctx, from, to) {
        stateChanges.push({ from, to });
      },
      onLivenessUpdate(_ctx, update) {
        livenessUpdates.push(update);
      },
    };

    mockPromptFn.mockImplementation(() => new Promise(() => {}));

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      livenessQuietThresholdMs: QUIET_THRESHOLD_MS,
      probeTimeoutMs: PROBE_TIMEOUT_MS,
      observers: [observer],
    });

    const executePromise = session.execute('test');

    await vi.advanceTimersByTimeAsync(QUIET_THRESHOLD_MS + 10);

    expect(stateChanges.some(c => c.to === 'probing')).toBe(true);

    emitAgentMessageChunk('recovery data');

    await vi.advanceTimersByTimeAsync(10);

    const recoveryChange = stateChanges.find(c => c.from === 'probing' && c.to === 'running');
    expect(recoveryChange).toBeDefined();

    const runningAfterProbe = livenessUpdates.filter(u => u.status === 'running');
    expect(runningAfterProbe.length).toBeGreaterThanOrEqual(1);

    await vi.advanceTimersByTimeAsync(QUIET_THRESHOLD_MS + PROBE_TIMEOUT_MS + 100);
    const result = await executePromise;
    expect(result.success).toBe(false);

    await session.close().catch(() => {});
  }, 10000);

  it('probe timeout marks session failed with failureReason and returns session_failed', async () => {
    const stateChanges: Array<{ from: string; to: string }> = [];
    const livenessUpdates: LivenessUpdate[] = [];

    const observer: SessionObserver = {
      onStateChange(_ctx, from, to) {
        stateChanges.push({ from, to });
      },
      onLivenessUpdate(_ctx, update) {
        livenessUpdates.push(update);
      },
    };

    mockPromptFn.mockImplementation(() => new Promise(() => {}));

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      livenessQuietThresholdMs: QUIET_THRESHOLD_MS,
      probeTimeoutMs: PROBE_TIMEOUT_MS,
      observers: [observer],
    });

    const executePromise = session.execute('test');

    await vi.advanceTimersByTimeAsync(QUIET_THRESHOLD_MS + 50);
    expect(stateChanges.some(c => c.to === 'probing')).toBe(true);

    await vi.advanceTimersByTimeAsync(PROBE_TIMEOUT_MS + 50);

    const result = await executePromise;

    expect(result.success).toBe(false);
    expect(result.failureKind).toBe('session_failed');
    expect(result.failureReason).toBeDefined();
    expect(result.error).toContain('liveness probe');

    const failedUpdate = livenessUpdates.find(u => u.status === 'failed');
    expect(failedUpdate).toBeDefined();
    expect(failedUpdate!.failureReason).not.toBeNull();

    const failedStateChange = stateChanges.find(c => c.to === 'failed');
    expect(failedStateChange).toBeDefined();

    await session.close().catch(() => {});
  });

  it('issue.stage and issue.status are unchanged by session probing and session failure', async () => {
    const issue = createIssue('build', 'active');
    const originalStage = issue.stage;
    const originalStatus = issue.status;

    const stateChanges: Array<{ from: string; to: string }> = [];
    const observer: SessionObserver = {
      onStateChange(_ctx, from, to) {
        stateChanges.push({ from, to });
      },
    };

    mockPromptFn.mockImplementation(() => new Promise(() => {}));

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: issue.id,
      projectId: projectId,
      executionId: 'exec-1',
      livenessQuietThresholdMs: QUIET_THRESHOLD_MS,
      probeTimeoutMs: PROBE_TIMEOUT_MS,
      observers: [observer],
    });

    const executePromise = session.execute('test');

    await vi.advanceTimersByTimeAsync(QUIET_THRESHOLD_MS + 50);

    expect(stateChanges.some(c => c.to === 'probing')).toBe(true);

    const reloadedAfterProbe = issueService.getById(issue.id);
    expect(reloadedAfterProbe!.stage).toBe(originalStage);
    expect(reloadedAfterProbe!.status).toBe(originalStatus);

    await vi.advanceTimersByTimeAsync(PROBE_TIMEOUT_MS + 50);

    const result = await executePromise;
    expect(result.success).toBe(false);
    expect(result.failureKind).toBe('session_failed');

    const reloadedAfterFail = issueService.getById(issue.id);
    expect(reloadedAfterFail!.stage).toBe(originalStage);
    expect(reloadedAfterFail!.status).toBe(originalStatus);

    await session.close().catch(() => {});
  });

  it('API plus CLI/Web-facing mapping produces all four session state labels', () => {
    expect(formatSessionState(null)).toContain('No active session');

    expect(formatSessionState({
      id: 's1', acpSessionId: 'a1', status: 'running',
      createdAt: new Date().toISOString(),
      lastDataAt: null, probeSentAt: null, probeDeadlineAt: null, failureReason: null,
    })).toContain('Running');

    expect(formatSessionState({
      id: 's2', acpSessionId: 'a2', status: 'probing',
      createdAt: new Date().toISOString(),
      lastDataAt: null, probeSentAt: null, probeDeadlineAt: null, failureReason: null,
    })).toContain('Checking session');

    expect(formatSessionState({
      id: 's3', acpSessionId: 'a3', status: 'failed',
      createdAt: new Date().toISOString(),
      lastDataAt: null, probeSentAt: null, probeDeadlineAt: null, failureReason: null,
    })).toContain('Session failed');

    expect(formatSessionState({
      id: 's4', acpSessionId: 'a4', status: 'completed',
      createdAt: new Date().toISOString(),
      lastDataAt: null, probeSentAt: null, probeDeadlineAt: null, failureReason: null,
    })).toContain('No active session');

    const webStatusLabelMap = (status: string): string => {
      if (status === 'running') return 'Running';
      if (status === 'probing') return 'Checking session';
      if (status === 'failed') return 'Session failed';
      if (status === 'completed') return 'Completed';
      if (status === 'cancelled') return 'Cancelled';
      return status;
    };

    expect(webStatusLabelMap('running')).toBe('Running');
    expect(webStatusLabelMap('probing')).toBe('Checking session');
    expect(webStatusLabelMap('failed')).toBe('Session failed');
    expect(webStatusLabelMap('completed')).toBe('Completed');

    const apiStateMap = (status: string | null): string => {
      if (status === 'failed') return 'Session failed';
      if (status === 'probing') return 'Checking session';
      if (status === 'running') return 'Running';
      return 'No active session';
    };

    expect(apiStateMap('running')).toBe('Running');
    expect(apiStateMap('probing')).toBe('Checking session');
    expect(apiStateMap('failed')).toBe('Session failed');
    expect(apiStateMap(null)).toBe('No active session');
    expect(apiStateMap('completed')).toBe('No active session');

    const forbidden = ['healthy', 'quiet', 'stale', 'hung-suspected', 'recoverable'];
    for (const label of ['Running', 'Checking session', 'Session failed', 'No active session']) {
      for (const word of forbidden) {
        expect(label.toLowerCase()).not.toContain(word);
      }
    }
  });

  it('full liveness lifecycle: running -> probing -> running -> probing -> failed with persistence', async () => {
    const eventBus = new EventBus();
    const emittedEvents: Array<{ event: string; data: any }> = [];
    const observedLivenessUpdates: LivenessUpdate[] = [];
    eventBus.on('coder_session_status_changed', (data: any) => {
      emittedEvents.push({ event: 'coder_session_status_changed', data });
    });

    const wfObserver = new WorkflowSessionObserver({
      eventBus,
      coderSessionRepo,
    });

    const stateChanges: Array<{ from: string; to: string }> = [];
    const observer: SessionObserver = {
      onSessionStart(ctx) {
        wfObserver.onSessionStart(ctx);
      },
      onLivenessUpdate(ctx, update) {
        observedLivenessUpdates.push(update);
        wfObserver.onLivenessUpdate(ctx, update);
      },
      onStateChange(ctx, from, to) {
        stateChanges.push({ from, to });
        wfObserver.onStateChange(ctx, from, to);
      },
    };

    const issue = createIssue('build', 'active');

    mockPromptFn.mockImplementation(() => new Promise(() => {}));

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: issue.id,
      projectId: projectId,
      executionId: 'exec-lifecycle',
      livenessQuietThresholdMs: QUIET_THRESHOLD_MS,
      probeTimeoutMs: PROBE_TIMEOUT_MS,
      observers: [observer],
    });

    const coderSessionId = (wfObserver as any)._coderSessionId;
    expect(coderSessionId).toBeDefined();

    const executePromise = session.execute('test');

    await vi.advanceTimersByTimeAsync(QUIET_THRESHOLD_MS + 10);

    let persisted = coderSessionRepo.findById(coderSessionId);
    expect(persisted.status).toBe('probing');
    expect(persisted.probeSentAt).not.toBeNull();
    expect(persisted.probeDeadlineAt).not.toBeNull();

    const observedProbeUpdate = observedLivenessUpdates.find(u => u.status === 'probing');
    expect(observedProbeUpdate).toBeDefined();
    expect(persisted.probeSentAt).toBe(observedProbeUpdate!.probeSentAt);
    expect(persisted.probeDeadlineAt).toBe(observedProbeUpdate!.probeDeadlineAt);

    const probingEvents = emittedEvents.filter(e => e.data.status === 'probing');
    expect(probingEvents.length).toBeGreaterThanOrEqual(1);
    const emittedProbeEvent = probingEvents[probingEvents.length - 1].data;
    expect(persisted.probeSentAt).toBe(emittedProbeEvent.probeSentAt);
    expect(persisted.probeDeadlineAt).toBe(emittedProbeEvent.probeDeadlineAt);

    emitAgentMessageChunk('recovery');

    await vi.advanceTimersByTimeAsync(10);

    expect(stateChanges.some(c => c.from === 'probing' && c.to === 'running')).toBe(true);

    persisted = coderSessionRepo.findById(coderSessionId);
    expect(persisted.status).toBe('running');
    expect(persisted.lastDataAt).not.toBeNull();

    await vi.advanceTimersByTimeAsync(QUIET_THRESHOLD_MS + 10);

    persisted = coderSessionRepo.findById(coderSessionId);
    expect(persisted.status).toBe('probing');

    await vi.advanceTimersByTimeAsync(PROBE_TIMEOUT_MS + 50);

    const result = await executePromise;
    expect(result.success).toBe(false);
    expect(result.failureKind).toBe('session_failed');

    persisted = coderSessionRepo.findById(coderSessionId);
    expect(persisted.status).toBe('failed');
    expect(persisted.failureReason).not.toBeNull();
    expect(persisted.completedAt).not.toBeNull();

    const reloadedIssue = issueService.getById(issue.id);
    expect(reloadedIssue!.stage).toBe('build');
    expect(reloadedIssue!.status).toBe('active');

    const failedEvents = emittedEvents.filter(e => e.data.status === 'failed');
    expect(failedEvents.length).toBeGreaterThanOrEqual(1);
    expect(failedEvents[failedEvents.length - 1].data.failureReason).toBeDefined();

    await session.close().catch(() => {});
  });

  it('persistence layer correctly tracks liveness fields through the lifecycle', async () => {
    const issue = createIssue();

    const session = coderSessionRepo.insert({
      issueId: issue.id,
      acpSessionId: 'acp-persist-test',
      stage: 'build',
    });

    expect(session.status).toBe('running');
    expect(session.lastDataAt).not.toBeNull();
    expect(session.probeSentAt).toBeNull();
    expect(session.probeDeadlineAt).toBeNull();
    expect(session.failureReason).toBeNull();

    const afterData = coderSessionRepo.markDataReceived(session.id);
    expect(afterData.lastDataAt).not.toBeNull();
    expect(afterData.status).toBe('running');

    const probeSentAt = new Date().toISOString();
    const deadline = new Date(Date.now() + PROBE_TIMEOUT_MS).toISOString();
    const afterProbing = coderSessionRepo.markProbing(session.id, probeSentAt, deadline);
    expect(afterProbing.status).toBe('probing');
    expect(afterProbing.probeSentAt).toBe(probeSentAt);
    expect(afterProbing.probeDeadlineAt).toBe(deadline);

    const afterRecovery = coderSessionRepo.markDataReceived(session.id);
    expect(afterRecovery.status).toBe('running');
    expect(afterRecovery.lastDataAt).not.toBeNull();

    const probeSentAt2 = new Date().toISOString();
    const deadline2 = new Date(Date.now() + PROBE_TIMEOUT_MS).toISOString();
    coderSessionRepo.markProbing(session.id, probeSentAt2, deadline2);

    const afterFailed = coderSessionRepo.markFailed(session.id, 'probe_timeout');
    expect(afterFailed.status).toBe('failed');
    expect(afterFailed.failureReason).toBe('probe_timeout');
    expect(afterFailed.completedAt).not.toBeNull();

    const reloadedIssue = issueService.getById(issue.id);
    expect(reloadedIssue!.stage).toBe('build');
    expect(reloadedIssue!.status).toBe('active');
  });

  it('cancellation remains distinct from session failure', async () => {
    const stateChanges: Array<{ from: string; to: string }> = [];
    const observer: SessionObserver = {
      onStateChange(_ctx, from, to) {
        stateChanges.push({ from, to });
      },
    };

    mockPromptFn.mockImplementation(() => new Promise(() => {}));

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      livenessQuietThresholdMs: 99999999,
      probeTimeoutMs: 99999999,
      observers: [observer],
    });

    const abortController = new AbortController();
    const executePromise = session.execute('test', { signal: abortController.signal });

    await vi.advanceTimersByTimeAsync(50);
    abortController.abort();

    const result = await executePromise;
    expect(result.success).toBe(false);
    expect(result.failureKind).toBe('cancelled');
    expect(result.failureKind).not.toBe('session_failed');

    const failedChange = stateChanges.find(c => c.to === 'failed');
    expect(failedChange).toBeUndefined();

    const cancelledChange = stateChanges.find(c => c.to === 'cancelled');
    expect(cancelledChange).toBeDefined();

    await session.close().catch(() => {});
  });

  it('probe send failure returns session_failed immediately', async () => {
    const livenessUpdates: LivenessUpdate[] = [];
    const stateChanges: Array<{ from: string; to: string }> = [];
    const observer: SessionObserver = {
      onLivenessUpdate(_ctx, update) {
        livenessUpdates.push(update);
      },
      onStateChange(_ctx, from, to) {
        stateChanges.push({ from, to });
      },
    };

    let promptCallCount = 0;
    mockPromptFn.mockImplementation(() => {
      promptCallCount++;
      if (promptCallCount === 1) {
        return new Promise(() => {});
      }
      return Promise.reject(new Error('Probe send failed: connection reset'));
    });

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      livenessQuietThresholdMs: QUIET_THRESHOLD_MS,
      probeTimeoutMs: PROBE_TIMEOUT_MS,
      observers: [observer],
    });

    const executePromise = session.execute('test');

    await vi.advanceTimersByTimeAsync(QUIET_THRESHOLD_MS + 50);
    await vi.advanceTimersByTimeAsync(1);

    const result = await executePromise;

    expect(result.success).toBe(false);
    expect(result.failureKind).toBe('session_failed');
    expect(result.failureReason).toContain('Probe send failed');

    const failedUpdate = livenessUpdates.find(u => u.status === 'failed');
    expect(failedUpdate).toBeDefined();
    expect(failedUpdate!.failureReason).toContain('Probe send failed');

    const failedChange = stateChanges.find(c => c.from === 'probing' && c.to === 'failed');
    expect(failedChange).toBeDefined();

    await session.close().catch(() => {});
  }, 10000);

  it('close preserves failed terminal state after session failure', async () => {
    const stateChanges: Array<{ from: string; to: string }> = [];
    const observer: SessionObserver = {
      onStateChange(_ctx, from, to) {
        stateChanges.push({ from, to });
      },
    };

    let promptCallCount = 0;
    mockPromptFn.mockImplementation(() => {
      promptCallCount++;
      if (promptCallCount === 1) {
        return new Promise(() => {});
      }
      return Promise.reject(new Error('Probe send failed: connection reset'));
    });

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      livenessQuietThresholdMs: QUIET_THRESHOLD_MS,
      probeTimeoutMs: PROBE_TIMEOUT_MS,
      observers: [observer],
    });

    const executePromise = session.execute('test');

    await vi.advanceTimersByTimeAsync(QUIET_THRESHOLD_MS + 50);
    await vi.advanceTimersByTimeAsync(1);

    const result = await executePromise;
    expect(result.failureKind).toBe('session_failed');

    await session.close();

    expect(stateChanges.some(c => c.to === 'failed')).toBe(true);
    expect(stateChanges.some(c => c.to === 'completed')).toBe(false);
  }, 10000);
});
