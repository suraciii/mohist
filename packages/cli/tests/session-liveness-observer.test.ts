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
import { WorkflowSessionObserver } from '../src/services/session-observers';
import { VALID_TRANSITIONS } from '../src/agent-runtime/session-state';

describe('SessionState with probing', () => {
  it('should include probing as valid transition from running', () => {
    expect(VALID_TRANSITIONS.running).toContain('probing');
  });

  it('should include running as valid transition from probing', () => {
    expect(VALID_TRANSITIONS.probing).toContain('running');
  });

  it('should treat probing as non-terminal state', () => {
    expect(VALID_TRANSITIONS.probing).not.toContain('closed');
    expect(VALID_TRANSITIONS.running).not.toContain('closed');
  });
});

describe('WorkflowSessionObserver onLivenessUpdate', () => {
  let mockEventBus: any;
  let mockCoderSessionRepo: any;

  beforeEach(() => {
    mockEventBus = { emit: vi.fn() };
    mockCoderSessionRepo = {
      markDataReceived: vi.fn(),
      markProbing: vi.fn(),
      markFailed: vi.fn(),
    };
  });

  afterEach(() => {
    globalSessionUpdateFn = undefined;
  });

  it('should call markDataReceived when status is running with lastDataAt', () => {
    const observer = new WorkflowSessionObserver({
      eventBus: mockEventBus,
      coderSessionRepo: mockCoderSessionRepo,
    });
    (observer as any)._coderSessionId = 'cs-1';

    const update: LivenessUpdate = {
      status: 'running',
      lastDataAt: '2024-01-01T00:00:00.000Z',
    };

    observer.onLivenessUpdate({ issueId: 'issue-1', issueNumber: 42, projectId: 'proj-1', acpSessionId: 'acp-1', executionId: 'exec-1', stage: undefined, model: undefined, processPid: undefined }, update);

    expect(mockCoderSessionRepo.markDataReceived).toHaveBeenCalledWith('cs-1');
  });

  it('should call markProbing when status is probing with emitted probe timestamps', () => {
    const observer = new WorkflowSessionObserver({
      eventBus: mockEventBus,
      coderSessionRepo: mockCoderSessionRepo,
    });
    (observer as any)._coderSessionId = 'cs-1';

    const update: LivenessUpdate = {
      status: 'probing',
      probeSentAt: '2024-01-01T00:00:30.000Z',
      probeDeadlineAt: '2024-01-01T00:01:00.000Z',
    };

    observer.onLivenessUpdate({ issueId: 'issue-1', issueNumber: 42, projectId: 'proj-1', acpSessionId: 'acp-1', executionId: 'exec-1', stage: undefined, model: undefined, processPid: undefined }, update);

    expect(mockCoderSessionRepo.markProbing).toHaveBeenCalledWith(
      'cs-1',
      '2024-01-01T00:00:30.000Z',
      '2024-01-01T00:01:00.000Z'
    );
  });

  it('should call markFailed when status is failed with failureReason', () => {
    const observer = new WorkflowSessionObserver({
      eventBus: mockEventBus,
      coderSessionRepo: mockCoderSessionRepo,
    });
    (observer as any)._coderSessionId = 'cs-1';

    const update: LivenessUpdate = {
      status: 'failed',
      failureReason: 'Probe timeout',
    };

    observer.onLivenessUpdate({ issueId: 'issue-1', issueNumber: 42, projectId: 'proj-1', acpSessionId: 'acp-1', executionId: 'exec-1', stage: undefined, model: undefined, processPid: undefined }, update);

    expect(mockCoderSessionRepo.markFailed).toHaveBeenCalledWith('cs-1', 'Probe timeout');
  });

  it('should emit coder_session_status_changed event with all liveness fields', () => {
    const observer = new WorkflowSessionObserver({
      eventBus: mockEventBus,
      coderSessionRepo: mockCoderSessionRepo,
    });
    (observer as any)._coderSessionId = 'cs-1';

    const update: LivenessUpdate = {
      status: 'probing',
      lastDataAt: '2024-01-01T00:00:00.000Z',
      probeSentAt: '2024-01-01T00:00:30.000Z',
      probeDeadlineAt: '2024-01-01T00:01:00.000Z',
    };

    observer.onLivenessUpdate({ issueId: 'issue-1', issueNumber: 42, projectId: 'proj-1', acpSessionId: 'acp-1', executionId: 'exec-1', stage: undefined, model: undefined, processPid: undefined }, update);

    expect(mockEventBus.emit).toHaveBeenCalledWith('coder_session_status_changed', expect.objectContaining({
      issueId: '42',
      projectId: 'proj-1',
      coderSessionId: 'cs-1',
      acpSessionId: 'acp-1',
      status: 'probing',
      lastDataAt: '2024-01-01T00:00:00.000Z',
      probeSentAt: '2024-01-01T00:00:30.000Z',
      probeDeadlineAt: '2024-01-01T00:01:00.000Z',
    }));
  });

  it('should emit coder_session_status_changed with failureReason when failed', () => {
    const observer = new WorkflowSessionObserver({
      eventBus: mockEventBus,
      coderSessionRepo: mockCoderSessionRepo,
    });
    (observer as any)._coderSessionId = 'cs-1';

    const update: LivenessUpdate = {
      status: 'failed',
      failureReason: 'Protocol disconnected',
    };

    observer.onLivenessUpdate({ issueId: 'issue-1', issueNumber: 42, projectId: 'proj-1', acpSessionId: 'acp-1', executionId: 'exec-1', stage: undefined, model: undefined, processPid: undefined }, update);

    expect(mockEventBus.emit).toHaveBeenCalledWith('coder_session_status_changed', expect.objectContaining({
      status: 'failed',
      failureReason: 'Protocol disconnected',
    }));
  });

  it('should not throw when coderSessionRepo operations fail', () => {
    mockCoderSessionRepo.markDataReceived.mockImplementation(() => { throw new Error('db error'); });

    const observer = new WorkflowSessionObserver({
      eventBus: mockEventBus,
      coderSessionRepo: mockCoderSessionRepo,
    });
    (observer as any)._coderSessionId = 'cs-1';

    const update: LivenessUpdate = { status: 'running', lastDataAt: '2024-01-01T00:00:00.000Z' };

    expect(() => {
      observer.onLivenessUpdate({ issueId: 'issue-1', issueNumber: 42, projectId: 'proj-1', acpSessionId: 'acp-1', executionId: 'exec-1', stage: undefined, model: undefined, processPid: undefined }, update);
    }).not.toThrow();
  });

  it('should not throw when eventBus emit fails', () => {
    mockEventBus.emit.mockImplementation(() => { throw new Error('emit error'); });

    const observer = new WorkflowSessionObserver({
      eventBus: mockEventBus,
      coderSessionRepo: mockCoderSessionRepo,
    });
    (observer as any)._coderSessionId = 'cs-1';

    const update: LivenessUpdate = { status: 'running', lastDataAt: '2024-01-01T00:00:00.000Z' };

    expect(() => {
      observer.onLivenessUpdate({ issueId: 'issue-1', issueNumber: 42, projectId: 'proj-1', acpSessionId: 'acp-1', executionId: 'exec-1', stage: undefined, model: undefined, processPid: undefined }, update);
    }).not.toThrow();
  });

  it('should use issueId as fallback when issueNumber is not available', () => {
    const observer = new WorkflowSessionObserver({
      eventBus: mockEventBus,
      coderSessionRepo: mockCoderSessionRepo,
    });
    (observer as any)._coderSessionId = 'cs-1';

    const update: LivenessUpdate = { status: 'probing', probeDeadlineAt: '2024-01-01T00:01:00.000Z' };

    observer.onLivenessUpdate({ issueId: 'issue-uuid-123', issueNumber: undefined, projectId: 'proj-1', acpSessionId: 'acp-1', executionId: 'exec-1', stage: undefined, model: undefined, processPid: undefined }, update);

    expect(mockEventBus.emit).toHaveBeenCalledWith('coder_session_status_changed', expect.objectContaining({
      issueId: 'issue-uuid-123',
    }));
  });
});

describe('Observer liveness callback for running, probing, and failed transitions', () => {
  it('should not throw when observer onLivenessUpdate is not defined', async () => {
    const partialObserver: SessionObserver = {
      onSessionStart() {},
    };

    mockPromptFn.mockResolvedValue(undefined);

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      observers: [partialObserver],
    });

    await session.execute('test');

    expect(session.state).toBeDefined();

    await session.close();
  });
});
