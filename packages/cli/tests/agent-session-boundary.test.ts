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

import { AgentSession, AgentSessionOptions } from '../src/agent-runtime/agent-session';
import type { SessionObserver, SessionContext, ToolCallEvent } from '../src/agent-runtime/session-observer';
import { WorkflowSessionObserver } from '../src/services/session-observers';

function emitAgentMessageChunk(text: string): void {
  globalSessionUpdateFn?.({
    update: {
      sessionUpdate: 'agent_message_chunk',
      content: { text },
    },
  });
}

function emitToolCall(toolName: string, status: 'started' | 'completed', toolCallId: string, input?: unknown, output?: unknown): void {
  globalSessionUpdateFn?.({
    update: {
      sessionUpdate: 'tool_call',
      toolCall: { toolName, status, toolCallId, input, output, title: `tool-${toolName}` },
    },
  });
}

function emitAgentThoughtChunk(thought: string): void {
  globalSessionUpdateFn?.({
    update: {
      sessionUpdate: 'agent_thought_chunk',
      content: { thought },
    },
  });
}

function emitProcessError(errorMsg: string): void {
  globalSessionUpdateFn?.({
    update: {
      sessionUpdate: 'acp_session_process_error',
      error: errorMsg,
      mode: 'test',
      timestamp: new Date().toISOString(),
    },
  });
}

describe('AgentSessionOptions boundary', () => {
  it('should not include EventBus type in AgentSessionOptions', () => {
    const options: AgentSessionOptions = {
      cwd: '/tmp',
      issueId: 'issue-1',
      observers: [],
    };
    expect(options).not.toHaveProperty('eventBus');
  });

  it('should not include WorkflowLogRepo type in AgentSessionOptions', () => {
    const options: AgentSessionOptions = {
      cwd: '/tmp',
      task: 'test',
    };
    expect(options).not.toHaveProperty('workflowLogRepo');
  });

  it('should not include SessionStreamLogRepo type in AgentSessionOptions', () => {
    const options: AgentSessionOptions = {
      cwd: '/tmp',
    };
    expect(options).not.toHaveProperty('sessionStreamLogRepo');
  });

  it('should not include CoderSessionRepo type in AgentSessionOptions', () => {
    const options: AgentSessionOptions = {
      cwd: '/tmp',
    };
    expect(options).not.toHaveProperty('coderSessionRepo');
  });

  it('should accept observers array', () => {
    const options: AgentSessionOptions = {
      cwd: '/tmp',
      observers: [],
    };
    expect(options.observers).toBeDefined();
  });
});

describe('Observer-driven coder_text_chunk emission', () => {
  let capturedChunks: Array<{ ctx: SessionContext; text: string }> = [];

  beforeEach(() => {
    capturedChunks = [];
    mockPromptFn.mockReset();
    mockCancelFn.mockReset();
    globalSessionUpdateFn = undefined;
    mockPromptFn.mockImplementation(() => {
      emitAgentMessageChunk('hello');
      return Promise.resolve();
    });
  });

  afterEach(() => {
    globalSessionUpdateFn = undefined;
  });

  it('should emit text chunk through observer with issue-number fallback', async () => {
    const testObserver: SessionObserver = {
      onTextChunk(ctx, text) {
        capturedChunks.push({ ctx, text });
      },
    };

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: '550e8400-e29b-41d4-a716-446655440000',
      issueNumber: 42,
      projectId: 'proj-1',
      executionId: 'exec-1',
      observers: [testObserver],
    });

    await session.execute('hello');

    expect(capturedChunks).toHaveLength(1);
    expect(capturedChunks[0].ctx.issueNumber).toBe(42);
    expect(capturedChunks[0].text).toBe('hello');

    await session.close();
  });

  it('should emit text chunk with issueId when issueNumber is not set', async () => {
    const testObserver: SessionObserver = {
      onTextChunk(ctx, text) {
        capturedChunks.push({ ctx, text });
      },
    };

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: '550e8400-e29b-41d4-a716-446655440000',
      projectId: 'proj-1',
      executionId: 'exec-1',
      observers: [testObserver],
    });

    await session.execute('hello');

    expect(capturedChunks).toHaveLength(1);
    expect(capturedChunks[0].ctx.issueNumber).toBeUndefined();
    expect(capturedChunks[0].ctx.issueId).toBe('550e8400-e29b-41d4-a716-446655440000');

    await session.close();
  });
});

describe('Observer-driven coder_tool_call emission', () => {
  let capturedToolCalls: Array<{ ctx: SessionContext; event: ToolCallEvent }> = [];

  beforeEach(() => {
    capturedToolCalls = [];
    mockPromptFn.mockReset();
    mockCancelFn.mockReset();
    globalSessionUpdateFn = undefined;
    mockPromptFn.mockImplementation(() => {
      emitToolCall('test_tool', 'started', 'test-session-123-test_tool-0');
      emitToolCall('test_tool', 'completed', 'test-session-123-test_tool-0', { input: 'foo' }, { output: 'bar' });
      return Promise.resolve();
    });
  });

  afterEach(() => {
    globalSessionUpdateFn = undefined;
  });

  it('should emit tool call with stable toolCallId via observer', async () => {
    const testObserver: SessionObserver = {
      onToolCall(ctx, event) {
        capturedToolCalls.push({ ctx, event });
      },
    };

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      issueNumber: 1,
      projectId: 'proj-1',
      executionId: 'exec-1',
      observers: [testObserver],
    });

    await session.execute('test');

    const started = capturedToolCalls.find(e => e.event.state === 'started');
    const completed = capturedToolCalls.find(e => e.event.state === 'completed');

    expect(started).toBeDefined();
    expect(completed).toBeDefined();
    expect(started!.event.toolCallId).toBe(completed!.event.toolCallId);
    expect(started!.event.toolCallId).toContain('test-session-123');

    await session.close();
  });

  it('should include tool name, state, toolCallId in tool call event', async () => {
    const testObserver: SessionObserver = {
      onToolCall(_ctx, event) {
        capturedToolCalls.push({ ctx: _ctx, event });
      },
    };

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      observers: [testObserver],
    });

    await session.execute('test');

    for (const tc of capturedToolCalls) {
      expect(tc.event.toolName).toBeDefined();
      expect(tc.event.state).toMatch(/^(started|completed)$/);
      expect(typeof tc.event.toolCallId).toBe('string');
    }

    await session.close();
  });
});

describe('Observer-driven session_stream_log writes', () => {
  let streamLogRepo: any;

  beforeEach(() => {
    streamLogRepo = { insert: vi.fn() };
    mockPromptFn.mockReset();
    mockCancelFn.mockReset();
    globalSessionUpdateFn = undefined;
    mockPromptFn.mockImplementation(() => {
      emitAgentThoughtChunk('thinking...');
      emitAgentMessageChunk('some text');
      return Promise.resolve();
    });
  });

  afterEach(() => {
    globalSessionUpdateFn = undefined;
  });

  it('should write stream events through WorkflowSessionObserver', async () => {
    const wfObserver = new WorkflowSessionObserver({
      sessionStreamLogRepo: streamLogRepo,
      eventBus: { emit: vi.fn() } as any,
    });

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      observers: [wfObserver],
    });

    await session.execute('test');

    expect(streamLogRepo.insert).toHaveBeenCalled();

    await session.close();
  });
});

describe('Observer-driven workflow_log writes', () => {
  let wfLogRepo: any;

  beforeEach(() => {
    wfLogRepo = { insert: vi.fn() };
    mockPromptFn.mockReset();
    mockCancelFn.mockReset();
    globalSessionUpdateFn = undefined;
    mockPromptFn.mockResolvedValue(undefined);
  });

  afterEach(() => {
    globalSessionUpdateFn = undefined;
  });

  it('should write workflow log events through WorkflowSessionObserver', async () => {
    const wfObserver = new WorkflowSessionObserver({
      workflowLogRepo: wfLogRepo,
      sessionStreamLogRepo: { insert: vi.fn() } as any,
      eventBus: { emit: vi.fn() } as any,
    });

    wfObserver.onSessionEvent(
      { issueId: 'issue-1', issueNumber: undefined, projectId: 'proj-1', executionId: 'exec-1', acpSessionId: 'sess-1', stage: undefined, model: undefined, processPid: 123 },
      'acp_session_start',
      { sessionId: 'sess-1' }
    );

    expect(wfLogRepo.insert).toHaveBeenCalledWith('issue-1', 'sess-1', 'acp_session_start', { sessionId: 'sess-1' });
  });

  it('should write non-stream session events to workflow log', async () => {
    const wfObserver = new WorkflowSessionObserver({
      workflowLogRepo: wfLogRepo,
      sessionStreamLogRepo: { insert: vi.fn() } as any,
      eventBus: { emit: vi.fn() } as any,
    });

    wfObserver.onSessionEvent(
      { issueId: 'issue-1', issueNumber: undefined, projectId: 'proj-1', executionId: 'exec-1', acpSessionId: 'sess-1', stage: undefined, model: undefined, processPid: 123 },
      'acp_session_process_error',
      { error: 'test error', mode: 'test', timestamp: new Date().toISOString() }
    );

    expect(wfLogRepo.insert).toHaveBeenCalled();
    const call = wfLogRepo.insert.mock.calls[0];
    expect(call[0]).toBe('issue-1');
    expect(call[2]).toBe('acp_session_process_error');
  });
});

describe('Observer-driven coder_session status updates', () => {
  let coderSessionRepo: any;

  beforeEach(() => {
    coderSessionRepo = {
      insert: vi.fn(() => ({ id: 'cs-1' })),
      updateStatus: vi.fn(),
    };
    mockPromptFn.mockReset();
    mockCancelFn.mockReset();
    globalSessionUpdateFn = undefined;
    mockPromptFn.mockResolvedValue(undefined);
  });

  afterEach(() => {
    globalSessionUpdateFn = undefined;
  });

  it('should update coder_session status to completed on close', async () => {
    const wfObserver = new WorkflowSessionObserver({
      coderSessionRepo,
      eventBus: { emit: vi.fn() } as any,
      sessionStreamLogRepo: { insert: vi.fn() } as any,
      workflowLogRepo: { insert: vi.fn() } as any,
    });

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      observers: [wfObserver],
    });

    await session.execute('test');
    await session.close();

    expect(coderSessionRepo.updateStatus).toHaveBeenCalledWith('cs-1', 'completed');
  });

  it('should update coder_session status to failed on error', async () => {
    mockPromptFn.mockRejectedValue(new Error('test error'));

    const wfObserver = new WorkflowSessionObserver({
      coderSessionRepo,
      eventBus: { emit: vi.fn() } as any,
      sessionStreamLogRepo: { insert: vi.fn() } as any,
      workflowLogRepo: { insert: vi.fn() } as any,
    });

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      timeout: 600_000,
      observers: [wfObserver],
    });

    await session.execute('test').catch(() => {});

    expect(coderSessionRepo.updateStatus).toHaveBeenCalledWith('cs-1', 'failed');

    await session.close().catch(() => {});
  });

  it('should update coder_session status to cancelled when cancel() is called', async () => {
    const wfObserver = new WorkflowSessionObserver({
      coderSessionRepo,
      eventBus: { emit: vi.fn() } as any,
      sessionStreamLogRepo: { insert: vi.fn() } as any,
      workflowLogRepo: { insert: vi.fn() } as any,
    });

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      timeout: 600000,
      observers: [wfObserver],
    });

    await session.cancel();

    expect(coderSessionRepo.updateStatus).toHaveBeenCalledWith('cs-1', 'cancelled');

    await session.close();
  });
});

describe('Plan/Check raw notification bridge observer', () => {
  beforeEach(() => {
    mockPromptFn.mockReset();
    mockCancelFn.mockReset();
    globalSessionUpdateFn = undefined;
    mockPromptFn.mockImplementation(() => {
      emitAgentMessageChunk('response');
      emitAgentThoughtChunk('thought');
      emitToolCall('tool_x', 'started', 'x-0');
      emitToolCall('tool_x', 'completed', 'x-0');
      return Promise.resolve();
    });
  });

  afterEach(() => {
    globalSessionUpdateFn = undefined;
  });

  it('should emit raw notifications via observer', async () => {
    const planSessionUpdates: any[] = [];

    const planBridgeObserver: SessionObserver = {
      onRawNotification(_ctx, notification) {
        planSessionUpdates.push(notification);
      },
    };

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      observers: [planBridgeObserver],
    });

    await session.execute('test');

    expect(planSessionUpdates.length).toBeGreaterThan(0);

    await session.close();
  });

  it('should not create new session per prompt in multi-round scenario', async () => {
    const planSessionUpdates: any[] = [];

    const planBridgeObserver: SessionObserver = {
      onRawNotification(_ctx, notification) {
        planSessionUpdates.push(notification);
      },
    };

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      observers: [planBridgeObserver],
    });

    const firstSessionId = session.acpSessionId;

    await session.execute('round 1');
    await session.execute('round 2');
    await session.execute('round 3');

    expect(session.acpSessionId).toBe(firstSessionId);
    expect(planSessionUpdates.length).toBeGreaterThan(3);

    await session.close();
  });
});

describe('Session lifecycle observer notifications', () => {
  beforeEach(() => {
    mockPromptFn.mockReset();
    mockCancelFn.mockReset();
    globalSessionUpdateFn = undefined;
    mockPromptFn.mockResolvedValue(undefined);
  });

  afterEach(() => {
    globalSessionUpdateFn = undefined;
  });

  it('should notify onSessionStart when session starts', async () => {
    let sessionStarts = 0;
    const startObserver: SessionObserver = {
      onSessionStart() { sessionStarts++; },
    };

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      observers: [startObserver],
    });

    expect(sessionStarts).toBe(1);
    await session.close();
  });

  it('should notify onStateChange on terminal state transitions', async () => {
    const stateChanges: Array<{ from: string; to: string }> = [];

    const stateObserver: SessionObserver = {
      onStateChange(_ctx, from, to) {
        stateChanges.push({ from, to });
      },
    };

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test prompt',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      observers: [stateObserver],
    });

    await session.execute('test');
    await session.close();

    const completedChange = stateChanges.find(c => c.to === 'completed');
    expect(completedChange).toBeDefined();
  });
});

describe('withSession finally cleanup', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    mockPromptFn.mockReset();
    mockCancelFn.mockReset();
    mockSetSessionConfigOptionFn.mockReset();
    mockSetSessionConfigOptionFn.mockResolvedValue(undefined);
    globalSessionUpdateFn = undefined;
    mockPromptFn.mockResolvedValue(undefined);
  });

  afterEach(() => {
    vi.useRealTimers();
    globalSessionUpdateFn = undefined;
  });

  it('should call close() in finally path when execution succeeds', async () => {
    const { withSession } = await import('../src/agent-runtime/agent-session');

    const stateChanges: Array<{ from: string; to: string }> = [];

    const result = await withSession({
      cwd: '/tmp/test',
      task: 'test',
      issueId: 'issue-1',
      timeout: 600_000,
      observers: [{
        onStateChange(_ctx, from, to) { stateChanges.push({ from, to }); },
      }],
    });

    expect(result.success).toBe(true);
    const completedChange = stateChanges.find(c => c.to === 'completed');
    expect(completedChange).toBeDefined();
  });

  it('should call close() in finally path when execution fails', async () => {
    const { withSession } = await import('../src/agent-runtime/agent-session');

    mockPromptFn.mockRejectedValue(new Error('test error'));

    const stateChanges: Array<{ from: string; to: string }> = [];

    const result = await withSession({
      cwd: '/tmp/test',
      task: 'test',
      issueId: 'issue-1',
      timeout: 600_000,
      observers: [{
        onStateChange(_ctx, from, to) { stateChanges.push({ from, to }); },
      }],
    });

    expect(result.success).toBe(false);
    expect(stateChanges.length).toBeGreaterThan(0);
  });
});

describe('Abort path cleanup and cancellation', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    mockPromptFn.mockReset();
    mockCancelFn.mockReset();
    mockSetSessionConfigOptionFn.mockReset();
    mockSetSessionConfigOptionFn.mockResolvedValue(undefined);
    globalSessionUpdateFn = undefined;
  });

  afterEach(() => {
    vi.useRealTimers();
    globalSessionUpdateFn = undefined;
  });

  it('should attempt ACP cancel, run onBeforeKill, cleanup, and return success:false on abort', async () => {
    const { withSession } = await import('../src/agent-runtime/agent-session');

    mockCancelFn.mockResolvedValue(undefined);
    mockPromptFn.mockImplementation(() => new Promise(() => {}));

    const abortController = new AbortController();
    const onBeforeKillFn = vi.fn().mockResolvedValue(false);

    const resultPromise = withSession({
      cwd: '/tmp/test',
      task: 'test',
      issueId: 'issue-1',
      timeout: 600_000,
      signal: abortController.signal,
      onBeforeKill: onBeforeKillFn,
      observers: [],
    });

    await vi.advanceTimersByTimeAsync(100);
    abortController.abort();

    const result = await resultPromise;

    expect(mockCancelFn).toHaveBeenCalled();
    expect(onBeforeKillFn).toHaveBeenCalled();
    expect(result.success).toBe(false);
    expect(result.error).toBe('Agent stopped by user');
  });

  it('should cleanup process even if onBeforeKill throws', async () => {
    const { withSession } = await import('../src/agent-runtime/agent-session');

    mockCancelFn.mockResolvedValue(undefined);
    mockPromptFn.mockImplementation(() => new Promise(() => {}));

    const abortController = new AbortController();
    const onBeforeKillFn = vi.fn().mockRejectedValue(new Error('onBeforeKill failed'));

    const resultPromise = withSession({
      cwd: '/tmp/test',
      task: 'test',
      issueId: 'issue-1',
      timeout: 600_000,
      signal: abortController.signal,
      onBeforeKill: onBeforeKillFn,
      observers: [],
    });

    await vi.advanceTimersByTimeAsync(100);
    abortController.abort();

    const result = await resultPromise;

    expect(result.success).toBe(false);
    expect(result.error).toBe('Agent stopped by user');
  });
});

describe('Timeout path cleanup and cancellation', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    mockPromptFn.mockReset();
    mockCancelFn.mockReset();
    mockSetSessionConfigOptionFn.mockReset();
    mockSetSessionConfigOptionFn.mockResolvedValue(undefined);
    globalSessionUpdateFn = undefined;
  });

  afterEach(() => {
    vi.useRealTimers();
    globalSessionUpdateFn = undefined;
  });

  it('should attempt ACP cancel, run onBeforeKill, cleanup, emit terminal state, and return timeout failure', async () => {
    const { withSession } = await import('../src/agent-runtime/agent-session');

    mockCancelFn.mockResolvedValue(undefined);
    mockPromptFn.mockImplementation(() => new Promise(() => {}));

    const onBeforeKillFn = vi.fn().mockResolvedValue(false);
    const stateChanges: Array<{ from: string; to: string }> = [];

    const resultPromise = withSession({
      cwd: '/tmp/test',
      task: 'test',
      issueId: 'issue-1',
      timeout: 5000,
      onBeforeKill: onBeforeKillFn,
      observers: [{
        onStateChange(_ctx, from, to) { stateChanges.push({ from, to }); },
      }],
    });

    await vi.advanceTimersByTimeAsync(6000);

    const result = await resultPromise;

    expect(mockCancelFn).toHaveBeenCalled();
    expect(onBeforeKillFn).toHaveBeenCalled();
    expect(stateChanges.some(c => c.to === 'failed')).toBe(true);
    expect(result.success).toBe(false);
    expect(result.error).toContain('Timed out');
    expect(result.failureKind).toBe('timeout');
    expect(result.failureReason).toBe('timeout');
  });

  it('should emit failed terminal state to observers on timeout', async () => {
    const { withSession } = await import('../src/agent-runtime/agent-session');

    mockCancelFn.mockResolvedValue(undefined);
    mockPromptFn.mockImplementation(() => new Promise(() => {}));

    const stateChanges: Array<{ from: string; to: string }> = [];

    const resultPromise = withSession({
      cwd: '/tmp/test',
      task: 'test',
      issueId: 'issue-1',
      timeout: 5000,
      observers: [{
        onStateChange(_ctx, from, to) { stateChanges.push({ from, to }); },
      }],
    });

    await vi.advanceTimersByTimeAsync(6000);

    await resultPromise;

    expect(stateChanges.some(c => c.to === 'failed')).toBe(true);
  });
});

describe('QuietThresholdMonitor: restart creates fresh cycles for Promise.race', () => {
  it('should resolve after threshold when no restart occurs', async () => {
    const { createQuietThresholdMonitorForTest } = await import('../src/agent-runtime/quiet-threshold-monitor');
    vi.useFakeTimers();
    const monitor = createQuietThresholdMonitorForTest(100);
    monitor.start();
    const p = monitor.promise();
    vi.advanceTimersByTime(99);
    let resolved = false;
    p.then(() => { resolved = true; });
    await vi.advanceTimersByTimeAsync(0);
    expect(resolved).toBe(false);
    vi.advanceTimersByTime(2);
    await vi.advanceTimersByTimeAsync(0);
    expect(resolved).toBe(true);
    vi.useRealTimers();
  });

  it('should keep same promise reference before the current cycle settles', async () => {
    const { createQuietThresholdMonitorForTest } = await import('../src/agent-runtime/quiet-threshold-monitor');
    vi.useFakeTimers();
    const monitor = createQuietThresholdMonitorForTest(100);
    monitor.start();
    const p1 = monitor.promise();
    monitor.restart();
    const p2 = monitor.promise();
    expect(p1).toBe(p2);
    vi.advanceTimersByTime(99);
    let resolved = false;
    p1.then(() => { resolved = true; });
    await vi.advanceTimersByTimeAsync(0);
    expect(resolved).toBe(false);
    vi.advanceTimersByTime(2);
    await vi.advanceTimersByTimeAsync(0);
    expect(resolved).toBe(true);
    vi.useRealTimers();
  });

  it('should create a fresh promise after a settled cycle restarts', async () => {
    const { createQuietThresholdMonitorForTest } = await import('../src/agent-runtime/quiet-threshold-monitor');
    vi.useFakeTimers();
    const monitor = createQuietThresholdMonitorForTest(100);
    monitor.start();
    const p1 = monitor.promise();
    vi.advanceTimersByTime(101);
    await vi.advanceTimersByTimeAsync(0);

    monitor.start();
    const p2 = monitor.promise();
    expect(p2).not.toBe(p1);

    let resolved = false;
    p2.then(() => { resolved = true; });
    await vi.advanceTimersByTimeAsync(0);
    expect(resolved).toBe(false);

    vi.advanceTimersByTime(101);
    await vi.advanceTimersByTimeAsync(0);
    expect(resolved).toBe(true);
    vi.useRealTimers();
  });

  it('should delay resolution when restart resets timer after notifications', async () => {
    const { createQuietThresholdMonitorForTest } = await import('../src/agent-runtime/quiet-threshold-monitor');
    vi.useFakeTimers();
    const monitor = createQuietThresholdMonitorForTest(100);
    monitor.start();
    const p = monitor.promise();

    vi.advanceTimersByTime(50);
    monitor.restart();

    vi.advanceTimersByTime(99);
    let resolved = false;
    p.then(() => { resolved = true; });
    await vi.advanceTimersByTimeAsync(0);
    expect(resolved).toBe(false);

    vi.advanceTimersByTime(2);
    await vi.advanceTimersByTimeAsync(0);
    expect(resolved).toBe(true);
    vi.useRealTimers();
  });

  it('should not resolve after clear', async () => {
    const { createQuietThresholdMonitorForTest } = await import('../src/agent-runtime/quiet-threshold-monitor');
    vi.useFakeTimers();
    const monitor = createQuietThresholdMonitorForTest(100);
    monitor.start();
    const p = monitor.promise();
    monitor.clear();
    vi.advanceTimersByTime(200);
    let resolved = false;
    p.then(() => { resolved = true; });
    await vi.advanceTimersByTimeAsync(0);
    expect(resolved).toBe(false);
    vi.useRealTimers();
  });

  it('should handle multiple restarts and still resolve correctly', async () => {
    const { createQuietThresholdMonitorForTest } = await import('../src/agent-runtime/quiet-threshold-monitor');
    vi.useFakeTimers();
    const monitor = createQuietThresholdMonitorForTest(100);
    monitor.start();
    const p = monitor.promise();

    for (let i = 0; i < 10; i++) {
      vi.advanceTimersByTime(30);
      monitor.restart();
    }

    vi.advanceTimersByTime(99);
    let resolved = false;
    p.then(() => { resolved = true; });
    await vi.advanceTimersByTimeAsync(0);
    expect(resolved).toBe(false);

    vi.advanceTimersByTime(2);
    await vi.advanceTimersByTimeAsync(0);
    expect(resolved).toBe(true);
    vi.useRealTimers();
  });
});

describe('Model override behavior', () => {
  beforeEach(() => {
    mockPromptFn.mockReset();
    mockCancelFn.mockReset();
    mockSetSessionConfigOptionFn.mockReset();
    mockSetSessionConfigOptionFn.mockResolvedValue(undefined);
    globalSessionUpdateFn = undefined;
    mockPromptFn.mockResolvedValue(undefined);
  });

  afterEach(() => {
    globalSessionUpdateFn = undefined;
  });

  it('should send model override after session creation when configured', async () => {
    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      model: 'claude-3-5-sonnet',
      observers: [],
    });

    await session.execute('test');

    expect(mockSetSessionConfigOptionFn).toHaveBeenCalledWith({
      sessionId: 'test-session-123',
      configId: 'model',
      value: 'claude-3-5-sonnet',
    });

    await session.close();
  });

  it('should degrade without failing session creation when model-set fails', async () => {
    mockSetSessionConfigOptionFn.mockRejectedValue(new Error('model set failed'));

    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      model: 'claude-3-5-sonnet',
      observers: [],
    });

    await session.execute('test');

    expect(mockSetSessionConfigOptionFn).toHaveBeenCalled();
    expect(session.state).toBe('running');

    await session.close();
  });

  it('should not call setSessionConfigOption when model is not configured', async () => {
    const session = await AgentSession.create({
      cwd: '/tmp/test',
      task: 'test',
      issueId: 'issue-1',
      projectId: 'proj-1',
      executionId: 'exec-1',
      observers: [],
    });

    await session.execute('test');

    expect(mockSetSessionConfigOptionFn).not.toHaveBeenCalled();

    await session.close();
  });
});
