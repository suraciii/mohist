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
import type { SessionObserver } from '../src/agent-runtime/session-observer';

function emitAgentMessageChunk(text: string): void {
  globalSessionUpdateFn?.({
    update: {
      sessionUpdate: 'agent_message_chunk',
      content: { text },
    },
  });
}

describe('Session liveness metadata in AcpSessionResult', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    mockPromptFn.mockReset();
    mockCancelFn.mockReset();
    mockSetSessionConfigOptionFn.mockResolvedValue(undefined);
    globalSessionUpdateFn = undefined;
  });

  afterEach(() => {
    vi.useRealTimers();
    globalSessionUpdateFn = undefined;
  });

  it('should set failureKind session_failed on execute error', async () => {
    const stateObserver: SessionObserver = {};

    mockPromptFn.mockRejectedValue(new Error('connection lost'));

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      livenessQuietThresholdMs: 99999999,
      probeTimeoutMs: 99999999,
      observers: [stateObserver],
    });

    const result = await session.execute('test').catch(() => ({ success: true }));

    expect(result.success).toBe(false);
    expect(result.failureKind).toBe('session_failed');

    await session.close().catch(() => {});
  });

  it('should include failureKind timeout in result on timeout', async () => {
    const { withSession } = await import('../src/agent-runtime/agent-session');

    mockCancelFn.mockResolvedValue(undefined);
    mockPromptFn.mockImplementation(() => new Promise(() => {}));

    const resultPromise = withSession({
      cwd: '/tmp/test',
      task: 'test',
      issueId: 'issue-1',
      timeout: 5000,
      observers: [],
    });

    await vi.advanceTimersByTimeAsync(6000);

    const result = await resultPromise;
    expect(result.success).toBe(false);
    expect(result.failureKind).toBe('timeout');
    expect(result.failureReason).toBe('timeout');
  });

  it('should fail immediately when the probe send rejects', async () => {
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
      livenessQuietThresholdMs: 100,
      probeTimeoutMs: 5000,
      observers: [],
    });

    const executePromise = session.execute('test');

    await vi.advanceTimersByTimeAsync(150);

    const result = await executePromise;
    expect(result.success).toBe(false);
    expect(result.failureKind).toBe('session_failed');
    expect(result.failureReason).toContain('Probe send failed');

    await session.close().catch(() => {});
  });

  it('should include failureKind cancelled in result on abort via ExecutePromptOptions.signal', async () => {
    const stateObserver: SessionObserver = {};

    mockPromptFn.mockImplementation(() => new Promise(() => {}));

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      livenessQuietThresholdMs: 99999999,
      probeTimeoutMs: 99999999,
      observers: [stateObserver],
    });

    const abortController = new AbortController();
    const executePromise = session.execute('test', { signal: abortController.signal });

    await vi.advanceTimersByTimeAsync(100);
    abortController.abort();

    const result = await executePromise;
    expect(result.success).toBe(false);
    expect(result.failureKind).toBe('cancelled');
    expect(result.error).toBe('Agent stopped by user');

    await session.close().catch(() => {});
  });

  it('should not probe when data arrives before quiet threshold', async () => {
    const stateChanges: Array<{ from: string; to: string }> = [];

    const stateObserver: SessionObserver = {
      onStateChange(_ctx, from, to) {
        stateChanges.push({ from, to });
      },
    };

    mockPromptFn.mockImplementation(() => {
      emitAgentMessageChunk('hello');
      return Promise.resolve();
    });

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      livenessQuietThresholdMs: 10000,
      probeTimeoutMs: 5000,
      observers: [stateObserver],
    });

    await session.execute('test');

    await vi.advanceTimersByTimeAsync(5000);

    const probingChange = stateChanges.find(c => c.to === 'probing');
    expect(probingChange).toBeUndefined();

    await session.close();
  });
});
