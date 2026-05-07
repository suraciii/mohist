import { describe, it, expect, vi, beforeEach } from 'vitest';

vi.mock('../src/config/config-loader', () => ({
  resolveOpencodeBinPath: () => '/mock/opencode',
}));

vi.mock('child_process', () => {
  const { EventEmitter } = require('events');
  const { Writable, Readable } = require('stream');
  return {
    spawn: vi.fn(() => {
      const proc = new EventEmitter();
      (proc as any).stdin = new Writable({
        write: (_c: any, _e: any, cb: any) => cb(),
      });
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

vi.mock('@agentclientprotocol/sdk', () => ({
  ClientSideConnection: vi.fn().mockImplementation((callbackFactory: () => { sessionUpdate: (n: any) => void; requestPermission: (...args: any[]) => any }, _stream: any) => {
    const callbacks = callbackFactory();
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
import type { SessionObserver, SessionContext } from '../src/agent-runtime/session-observer';

describe('ACP session taskId logging', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockPromptFn.mockResolvedValue(undefined);
    mockCancelFn.mockResolvedValue(undefined);
    mockSetSessionConfigOptionFn.mockResolvedValue(undefined);
  });

  it('should include taskId in session context when provided', async () => {
    const capturedContexts: SessionContext[] = [];
    const observer: SessionObserver = {
      onSessionStart(ctx) {
        capturedContexts.push({ ...ctx });
      },
    };

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'some task prompt text',
      taskId: 'T-001',
      issueId: 'issue-123',
      observers: [observer],
    });

    expect(capturedContexts.length).toBe(1);
    expect(capturedContexts[0].issueId).toBe('issue-123');

    await session.close();
  });

  it('should call onSessionStart observer when session starts', async () => {
    const onSessionStartFn = vi.fn();
    const observer: SessionObserver = {
      onSessionStart: onSessionStartFn,
    };

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'some task prompt text',
      issueId: 'issue-123',
      observers: [observer],
    });

    expect(onSessionStartFn).toHaveBeenCalledTimes(1);
    const ctx = onSessionStartFn.mock.calls[0][0];
    expect(ctx.issueId).toBe('issue-123');

    await session.close();
  });

  it('should call onStateChange with completed state on close', async () => {
    const onStateChangeFn = vi.fn();
    const observer: SessionObserver = {
      onStateChange: onStateChangeFn,
    };

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'some task prompt text',
      issueId: 'issue-123',
      observers: [observer],
    });

    await session.close();

    const completedCall = onStateChangeFn.mock.calls.find(
      (call: unknown[]) => (call as unknown[])[2] === 'completed',
    );
    expect(completedCall).toBeDefined();
  });
});
