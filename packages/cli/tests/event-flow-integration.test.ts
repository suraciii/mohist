import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { EventBus, type EventMap } from '../src/services/event-bus';
import { ToolRegistry, Tool } from '../src/agent-runtime/tool';
import { SessionManager } from '../src/agent-runtime/session';
import { runAgentLoop } from '../src/agent-runtime/agent-loop';
import { z } from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';

async function* asyncIteratorFromParts(parts: any[]) {
  for (const part of parts) {
    yield part;
  }
}

function createMockStreamTextResult(parts: any[]) {
  const fullStream = asyncIteratorFromParts(parts);
  return {
    fullStream,
    text: Promise.resolve('done'),
    steps: Promise.resolve([{ response: { messages: [] } }]),
    finishReason: Promise.resolve('stop'),
  };
}

const mockStreamText = vi.hoisted(() => vi.fn());

vi.mock('ai', async (importOriginal) => {
  const actual = await importOriginal() as any;
  return {
    ...actual,
    streamText: mockStreamText,
  };
});

const acpMockState = vi.hoisted(() => ({
  handlerObj: null as any,
  sessionId: 'sess-test-001',
  shouldFail: false,
  failError: '',
}));

vi.mock('child_process', () => {
  const { EventEmitter } = require('events');
  const { PassThrough } = require('stream');
  return {
    spawn: vi.fn(() => {
      const proc = new EventEmitter();
      (proc as any).pid = 12345;
      (proc as any).stdin = new PassThrough();
      (proc as any).stdout = new PassThrough();
      (proc as any).stderr = new PassThrough();
      (proc as any).kill = vi.fn();
      (proc as any).disconnected = false;
      setTimeout(() => proc.emit('spawn'), 0);
      return proc;
    }),
  };
});

vi.mock('@agentclientprotocol/sdk', () => ({
  ClientSideConnection: vi.fn().mockImplementation((handler: any) => {
    acpMockState.handlerObj = typeof handler === 'function' ? handler({}) : handler;
    return {
      initialize: vi.fn().mockResolvedValue({ protocolVersion: '2025-01-01' }),
      newSession: vi.fn().mockResolvedValue({ sessionId: acpMockState.sessionId }),
      prompt: vi.fn().mockImplementation(async () => {
        if (acpMockState.shouldFail) {
          return {};
        }
        if (acpMockState.handlerObj?.sessionUpdate) {
          await acpMockState.handlerObj.sessionUpdate({
            update: {
              sessionUpdate: 'agent_message_chunk',
              content: { text: 'coder thinking...' },
            },
          });
          await acpMockState.handlerObj.sessionUpdate({
            update: {
              sessionUpdate: 'tool_call',
              toolCall: { toolName: 'read_file', status: 'started' },
            },
          });
          await acpMockState.handlerObj.sessionUpdate({
            update: {
              sessionUpdate: 'tool_call',
              toolCall: { toolName: 'read_file', status: 'completed' },
            },
          });
        }
        return {};
      }),
      cancel: vi.fn().mockResolvedValue({}),
    };
  }),
  ndJsonStream: vi.fn().mockReturnValue({}),
  PROTOCOL_VERSION: '2025-01-01',
}));

describe('EventBus basics', () => {
  it('should emit and receive events', () => {
    const bus = new EventBus();
    const received: any[] = [];
    bus.on('agent_text_chunk', (data) => received.push(data));

    bus.emit('agent_text_chunk', {
      issueId: 'i1',
      projectId: 'p1',
      text: 'hello',
      stepIndex: 0,
    });

    expect(received).toHaveLength(1);
    expect(received[0].text).toBe('hello');
  });

  it('should not crash when emitting with no listeners', () => {
    const bus = new EventBus();
    expect(() =>
      bus.emit('agent_text_chunk', {
        issueId: 'i1',
        projectId: 'p1',
        text: 'hi',
        stepIndex: 0,
      })
    ).not.toThrow();
  });

  it('should support off to remove listener', () => {
    const bus = new EventBus();
    const received: any[] = [];
    const listener = (data: any) => received.push(data);

    bus.on('agent_text_chunk', listener);
    bus.emit('agent_text_chunk', { issueId: 'i1', projectId: 'p1', text: 'a', stepIndex: 0 });
    bus.off('agent_text_chunk', listener);
    bus.emit('agent_text_chunk', { issueId: 'i1', projectId: 'p1', text: 'b', stepIndex: 0 });

    expect(received).toHaveLength(1);
    expect(received[0].text).toBe('a');
  });
});

describe('runAgentLoop event emission', () => {
  let bus: EventBus;
  let events: Record<string, any[]>;
  let sessionManager: SessionManager;
  let session: any;

  beforeEach(() => {
    bus = new EventBus();
    events = {
      agent_text_chunk: [],
      main_tool_call: [],
    };

    bus.on('agent_text_chunk', (d) => events.agent_text_chunk.push(d));
    bus.on('main_tool_call', (d) => events.main_tool_call.push(d));

    sessionManager = new SessionManager();
    session = sessionManager.create(1);

    mockStreamText.mockReset();
  });

  it('should emit agent_text_chunk with correct stepIndex', async () => {
    mockStreamText.mockReturnValue(
      createMockStreamTextResult([
        { type: 'text-delta', text: 'Hello ' },
        { type: 'text-delta', text: 'world' },
      ])
    );

    const registry = new ToolRegistry();

    await runAgentLoop(session, sessionManager, registry, {} as any, {
      eventBus: bus,
      eventContext: { issueId: 'i1', projectId: 'p1' },
    });

    expect(events.agent_text_chunk).toHaveLength(2);
    expect(events.agent_text_chunk[0]).toEqual({
      issueId: 'i1',
      projectId: 'p1',
      text: 'Hello ',
      stepIndex: 0,
    });
    expect(events.agent_text_chunk[1]).toEqual({
      issueId: 'i1',
      projectId: 'p1',
      text: 'world',
      stepIndex: 0,
    });
  });

  it('should increment stepIndex on tool-call events', async () => {
    const registry = new ToolRegistry();
    registry.register(
      Tool.define('test_tool', {
        description: 'A test tool',
        parameters: z.object({ x: z.string() }),
        execute: async () => 'ok',
      })
    );

    mockStreamText.mockReturnValue(
      createMockStreamTextResult([
        { type: 'text-delta', text: 'thinking...' },
        { type: 'tool-call', toolCallId: 'tc_1', toolName: 'test_tool', input: { x: 'a' } },
        { type: 'tool-result', toolCallId: 'tc_1', toolName: 'test_tool', output: 'ok' },
        { type: 'text-delta', text: 'after tool' },
      ])
    );

    await runAgentLoop(session, sessionManager, registry, {} as any, {
      eventBus: bus,
      eventContext: { issueId: 'i1', projectId: 'p1' },
    });

    const textChunks = events.agent_text_chunk;
    expect(textChunks[0].stepIndex).toBe(0);
    expect(textChunks[1].stepIndex).toBe(1);
  });

  it('should emit main_tool_call started/completed', async () => {
    const registry = new ToolRegistry();
    registry.register(
      Tool.define('read_file', {
        description: 'Read file',
        parameters: z.object({ path: z.string() }),
        execute: async () => 'file content',
      })
    );

    mockStreamText.mockReturnValue(
      createMockStreamTextResult([
        { type: 'tool-call', toolCallId: 'tc_1', toolName: 'read_file', input: { path: '/tmp/a' } },
        { type: 'tool-result', toolCallId: 'tc_1', toolName: 'read_file', output: 'file content' },
      ])
    );

    await runAgentLoop(session, sessionManager, registry, {} as any, {
      eventBus: bus,
      eventContext: { issueId: 'i1', projectId: 'p1' },
    });

    expect(events.main_tool_call).toHaveLength(2);
    const started = events.main_tool_call[0];
    const completed = events.main_tool_call[1];

    expect(started.state).toBe('started');
    expect(started.toolName).toBe('read_file');
    expect(started.executionId).toBeTruthy();
    expect(started.args).toBe(JSON.stringify({ path: '/tmp/a' }));

    expect(completed.state).toBe('completed');
    expect(completed.toolName).toBe('read_file');
    expect(completed.executionId).toBe(started.executionId);
    expect(completed.result).toBe('file content');
  });

  it('should emit main_tool_call failed on tool-error', async () => {
    const registry = new ToolRegistry();

    mockStreamText.mockReturnValue(
      createMockStreamTextResult([
        { type: 'tool-call', toolCallId: 'tc_err', toolName: 'bad_tool', input: {} },
        { type: 'tool-error', toolCallId: 'tc_err', toolName: 'bad_tool', error: 'Something broke' },
      ])
    );

    await runAgentLoop(session, sessionManager, registry, {} as any, {
      eventBus: bus,
      eventContext: { issueId: 'i1', projectId: 'p1' },
    });

    const failed = events.main_tool_call.find((e) => e.state === 'failed');
    expect(failed).toBeDefined();
    expect(failed.toolName).toBe('bad_tool');
    expect(failed.error).toBe('Something broke');
    expect(failed.executionId).toBeTruthy();
  });

  it('should not emit events when no eventBus provided', async () => {
    const registry = new ToolRegistry();

    mockStreamText.mockReturnValue(
      createMockStreamTextResult([
        { type: 'text-delta', text: 'hello' },
        { type: 'tool-call', toolCallId: 'tc_1', toolName: 'x', input: {} },
        { type: 'tool-result', toolCallId: 'tc_1', toolName: 'x', output: 'ok' },
      ])
    );

    const freshBus = new EventBus();
    const received: any[] = [];
    freshBus.on('agent_text_chunk', () => received.push('text'));
    freshBus.on('main_tool_call', () => received.push('tool'));

    await runAgentLoop(session, sessionManager, registry, {} as any);

    expect(received).toHaveLength(0);
  });

  it('should set executionId on ToolRegistry during tool-call and clear after tool-result', async () => {
    const registry = new ToolRegistry();
    const registrySnapshots: { event: string; executionId: string | null }[] = [];

    const capturingBus = new EventBus();
    capturingBus.on('main_tool_call', (d) => {
      registrySnapshots.push({
        event: d.state,
        executionId: registry.getCurrentExecutionId(),
      });
    });

    mockStreamText.mockReturnValue(
      createMockStreamTextResult([
        { type: 'tool-call', toolCallId: 'tc_1', toolName: 'my_tool', input: {} },
        { type: 'tool-result', toolCallId: 'tc_1', toolName: 'my_tool', output: 'ok' },
      ])
    );

    await runAgentLoop(session, sessionManager, registry, {} as any, {
      eventBus: capturingBus,
      eventContext: { issueId: 'i1', projectId: 'p1' },
    });

    const startedSnapshot = registrySnapshots.find((s) => s.event === 'started');
    expect(startedSnapshot?.executionId).toBeTruthy();

    expect(registry.getCurrentExecutionId()).toBeNull();
  });
});

describe('ToolRegistry executionId slot', () => {
  it('should store and retrieve executionId', () => {
    const reg = new ToolRegistry();
    expect(reg.getCurrentExecutionId()).toBeNull();

    reg.setCurrentExecutionId('exec-123');
    expect(reg.getCurrentExecutionId()).toBe('exec-123');

    reg.clearCurrentExecutionId();
    expect(reg.getCurrentExecutionId()).toBeNull();
  });

  it('should fallback when executionId slot is empty', async () => {
    const bus = new EventBus();
    const emitted: any[] = [];
    bus.on('main_tool_call', (d) => emitted.push(d));

    const registry = new ToolRegistry();
    const sessionManager = new SessionManager();
    const session = sessionManager.create(1);

    mockStreamText.mockReset();
    mockStreamText.mockReturnValue(
      createMockStreamTextResult([
        { type: 'tool-result', toolCallId: 'tc_orphan', toolName: 'orphan_tool', output: 'orphan result' },
      ])
    );

    await runAgentLoop(session, sessionManager, registry, {} as any, {
      eventBus: bus,
      eventContext: { issueId: 'i1', projectId: 'p1' },
    });

    const completed = emitted.find((e) => e.state === 'completed');
    expect(completed).toBeDefined();
    expect(completed.executionId).toBeTruthy();
    expect(typeof completed.executionId).toBe('string');
    expect(completed.executionId.length).toBeGreaterThan(0);
  });

  it('should handle concurrent tool calls with independent executionIds', async () => {
    const bus = new EventBus();
    const emitted: any[] = [];
    bus.on('main_tool_call', (d) => emitted.push(d));

    const registry = new ToolRegistry();
    const sessionManager = new SessionManager();
    const session = sessionManager.create(1);

    mockStreamText.mockReset();
    mockStreamText.mockReturnValue(
      createMockStreamTextResult([
        { type: 'tool-call', toolCallId: 'tc_a', toolName: 'tool_a', input: {} },
        { type: 'tool-call', toolCallId: 'tc_b', toolName: 'tool_b', input: {} },
        { type: 'tool-result', toolCallId: 'tc_a', toolName: 'tool_a', output: 'a' },
        { type: 'tool-result', toolCallId: 'tc_b', toolName: 'tool_b', output: 'b' },
      ])
    );

    await runAgentLoop(session, sessionManager, registry, {} as any, {
      eventBus: bus,
      eventContext: { issueId: 'i1', projectId: 'p1' },
    });

    const startedEvents = emitted.filter((e) => e.state === 'started');
    expect(startedEvents).toHaveLength(2);
    expect(startedEvents[0].executionId).not.toBe(startedEvents[1].executionId);
  });
});

describe('runAcpSession event emission', () => {
  beforeEach(() => {
    acpMockState.shouldFail = false;
    acpMockState.failError = '';
    acpMockState.handlerObj = null;
  });

  it('should emit coder_text_chunk when executionId is present', async () => {
    const { runAcpSession } = await import('../src/agent-runtime/acp-session');
    const bus = new EventBus();
    const chunks: any[] = [];
    bus.on('coder_text_chunk', (d) => chunks.push(d));

    const result = await runAcpSession({
      cwd: '/tmp/test',
      task: 'do something',
      executionId: 'exec-abc',
      eventBus: bus,
      issueId: 'i1',
      projectId: 'p1',
      throttleMs: 0,
    });

    expect(result.success).toBe(true);
    expect(chunks.length).toBeGreaterThanOrEqual(1);
    expect(chunks[0].executionId).toBe('exec-abc');
    expect(chunks[0].acpSessionId).toBe('sess-test-001');
    expect(chunks[0].text).toBe('coder thinking...');
    expect(chunks[0].issueId).toBe('i1');
    expect(chunks[0].projectId).toBe('p1');
  });

  it('should emit coder_tool_call started and completed', async () => {
    const { runAcpSession } = await import('../src/agent-runtime/acp-session');
    const bus = new EventBus();
    const calls: any[] = [];
    bus.on('coder_tool_call', (d) => calls.push(d));

    await runAcpSession({
      cwd: '/tmp/test',
      task: 'do something',
      executionId: 'exec-xyz',
      eventBus: bus,
      throttleMs: 0,
    });

    const started = calls.find((c) => c.state === 'started');
    const completed = calls.find((c) => c.state === 'completed');

    expect(started).toBeDefined();
    expect(started.executionId).toBe('exec-xyz');
    expect(started.toolName).toBe('read_file');

    expect(completed).toBeDefined();
    expect(completed.executionId).toBe('exec-xyz');
    expect(completed.toolName).toBe('read_file');
  });

  it('should not emit events when executionId is missing', async () => {
    const { runAcpSession } = await import('../src/agent-runtime/acp-session');
    const bus = new EventBus();
    const chunks: any[] = [];
    const calls: any[] = [];
    bus.on('coder_text_chunk', (d) => chunks.push(d));
    bus.on('coder_tool_call', (d) => calls.push(d));

    const result = await runAcpSession({
      cwd: '/tmp/test',
      task: 'do something',
      eventBus: bus,
      throttleMs: 0,
    });

    expect(result.success).toBe(true);
    expect(result.text).toBeTruthy();
    expect(chunks).toHaveLength(0);
    expect(calls).toHaveLength(0);
  });

  it('should not crash when eventBus is missing', async () => {
    const { runAcpSession } = await import('../src/agent-runtime/acp-session');

    const result = await runAcpSession({
      cwd: '/tmp/test',
      task: 'do something',
      executionId: 'exec-no-bus',
    });

    expect(result.success).toBe(true);
    expect(result.text).toBeTruthy();
  });
});

describe('ralph executor event emission', () => {
  let tempDir: string;
  let acpSessionMock: any;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ralph-event-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  it('should emit ralph_task_update and ralph_loop_progress events', async () => {
    const mockRunAcpSession = vi.fn().mockResolvedValue({
      text: 'task output',
      success: true,
      acpSessionId: 'sess-ralph-001',
    });

    vi.doMock('../src/agent-runtime/acp-session', () => ({
      runAcpSession: mockRunAcpSession,
    }));

    const { runRalphLoop } = await import('../src/openspec/ralph-executor');
    const bus = new EventBus();
    const taskUpdates: any[] = [];
    const loopProgress: any[] = [];
    bus.on('ralph_task_update', (d) => taskUpdates.push(d));
    bus.on('ralph_loop_progress', (d) => loopProgress.push(d));

    const changeDir = path.join(tempDir, 'change');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.mkdirSync(path.join(changeDir, 'session-memories'), { recursive: true });

    const prd = {
      tasks: [
        { id: 'T-001', order: 1, title: 'Task 1', description: 'desc' },
        { id: 'T-002', order: 2, title: 'Task 2', description: 'desc' },
      ],
    };
    fs.writeFileSync(path.join(changeDir, 'prd.json'), JSON.stringify(prd));

    const change = {
      changePath: changeDir,
      prdPath: path.join(changeDir, 'prd.json'),
      taskStatusPath: path.join(changeDir, 'task-status.json'),
      sessionMemoriesPath: path.join(changeDir, 'session-memories'),
      proposalPath: path.join(changeDir, 'proposal.md'),
      designPath: path.join(changeDir, 'design.md'),
      specsPath: path.join(changeDir, 'specs'),
    };

    await runRalphLoop(change, {
      worktreePath: tempDir,
      projectPath: tempDir,
      eventBus: bus,
      executionId: 'exec-ralph-001',
      issueId: 'i1',
      projectId: 'p1',
    });

    const startedEvents = taskUpdates.filter((e) => e.status === 'started');
    const completedEvents = taskUpdates.filter((e) => e.status === 'completed');
    expect(startedEvents).toHaveLength(2);
    expect(completedEvents).toHaveLength(2);

    expect(startedEvents[0].executionId).toBe('exec-ralph-001');
    expect(startedEvents[0].taskId).toBe('T-001');
    expect(startedEvents[0].taskIndex).toBe(0);
    expect(startedEvents[0].totalTasks).toBe(2);

    expect(loopProgress.length).toBeGreaterThanOrEqual(1);
    expect(loopProgress[0].executionId).toBe('exec-ralph-001');
    expect(loopProgress[0].total).toBe(2);

    vi.doUnmock('../src/agent-runtime/acp-session');
  });

  it('should emit ralph_task_update with failed status on task failure', async () => {
    const mockRunAcpSession = vi.fn().mockResolvedValue({
      text: '',
      success: false,
      error: 'Build failed: test assertion failed',
    });

    vi.doMock('../src/agent-runtime/acp-session', () => ({
      runAcpSession: mockRunAcpSession,
    }));

    const { runRalphLoop } = await import('../src/openspec/ralph-executor');
    const bus = new EventBus();
    const taskUpdates: any[] = [];
    bus.on('ralph_task_update', (d) => taskUpdates.push(d));

    const changeDir = path.join(tempDir, 'change-fail');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.mkdirSync(path.join(changeDir, 'session-memories'), { recursive: true });

    const prd = {
      tasks: [{ id: 'T-001', order: 1, title: 'Fail Task', description: 'desc' }],
    };
    fs.writeFileSync(path.join(changeDir, 'prd.json'), JSON.stringify(prd));

    const change = {
      changePath: changeDir,
      prdPath: path.join(changeDir, 'prd.json'),
      taskStatusPath: path.join(changeDir, 'task-status.json'),
      sessionMemoriesPath: path.join(changeDir, 'session-memories'),
      proposalPath: path.join(changeDir, 'proposal.md'),
      designPath: path.join(changeDir, 'design.md'),
      specsPath: path.join(changeDir, 'specs'),
    };

    await runRalphLoop(
      change,
      {
        worktreePath: tempDir,
        projectPath: tempDir,
        eventBus: bus,
        executionId: 'exec-fail-001',
      },
      { maxRetries: 0 }
    );

    const failed = taskUpdates.find((e) => e.status === 'failed');
    expect(failed).toBeDefined();
    expect(failed.executionId).toBe('exec-fail-001');
    expect(failed.taskId).toBe('T-001');

    vi.doUnmock('../src/agent-runtime/acp-session');
  });

  it('should not crash when eventBus is missing', async () => {
    const mockRunAcpSession = vi.fn().mockResolvedValue({
      text: 'task output',
      success: true,
    });

    vi.doMock('../src/agent-runtime/acp-session', () => ({
      runAcpSession: mockRunAcpSession,
    }));

    const { runRalphLoop } = await import('../src/openspec/ralph-executor');

    const changeDir = path.join(tempDir, 'change-nobus');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.mkdirSync(path.join(changeDir, 'session-memories'), { recursive: true });

    const prd = {
      tasks: [{ id: 'T-001', order: 1, title: 'Task', description: 'desc' }],
    };
    fs.writeFileSync(path.join(changeDir, 'prd.json'), JSON.stringify(prd));

    const change = {
      changePath: changeDir,
      prdPath: path.join(changeDir, 'prd.json'),
      taskStatusPath: path.join(changeDir, 'task-status.json'),
      sessionMemoriesPath: path.join(changeDir, 'session-memories'),
      proposalPath: path.join(changeDir, 'proposal.md'),
      designPath: path.join(changeDir, 'design.md'),
      specsPath: path.join(changeDir, 'specs'),
    };

    const result = await runRalphLoop(change, {
      worktreePath: tempDir,
      projectPath: tempDir,
    });

    expect(result).toBeDefined();

    vi.doUnmock('../src/agent-runtime/acp-session');
  });
});

describe('executionId correlation L0 <-> L1', () => {
  it('L1 coder events should match L0 main_tool_call executionId', async () => {
    const bus = new EventBus();
    const mainCalls: any[] = [];
    const coderChunks: any[] = [];
    const coderCalls: any[] = [];

    bus.on('main_tool_call', (d) => mainCalls.push(d));
    bus.on('coder_text_chunk', (d) => coderChunks.push(d));
    bus.on('coder_tool_call', (d) => coderCalls.push(d));

    const sessionManager = new SessionManager();
    const session = sessionManager.create(1);
    const registry = new ToolRegistry();

    mockStreamText.mockReset();
    mockStreamText.mockReturnValue(
      createMockStreamTextResult([
        { type: 'tool-call', toolCallId: 'tc_spawn', toolName: 'spawn_coder', input: { task: 'code' } },
        { type: 'tool-result', toolCallId: 'tc_spawn', toolName: 'spawn_coder', output: 'done' },
      ])
    );

    await runAgentLoop(session, sessionManager, registry, {} as any, {
      eventBus: bus,
      eventContext: { issueId: 'i1', projectId: 'p1' },
    });

    const l0ExecutionId = mainCalls[0]?.executionId;
    expect(l0ExecutionId).toBeTruthy();

    bus.emit('coder_text_chunk', {
      issueId: 'i1',
      projectId: 'p1',
      executionId: l0ExecutionId,
      acpSessionId: 'sess-1',
      text: 'writing code...',
    });
    bus.emit('coder_tool_call', {
      issueId: 'i1',
      projectId: 'p1',
      executionId: l0ExecutionId,
      acpSessionId: 'sess-1',
      toolName: 'write_file',
      state: 'started',
    });

    expect(coderChunks[0].executionId).toBe(l0ExecutionId);
    expect(coderCalls[0].executionId).toBe(l0ExecutionId);
  });
});

describe('SSE endpoint includes new event types', () => {
  const newEventTypes = [
    'agent_text_chunk',
    'main_tool_call',
    'coder_text_chunk',
    'coder_tool_call',
    'ralph_task_update',
    'ralph_loop_progress',
  ] as const;

  it('should be able to subscribe and emit all new event types on EventBus', () => {
    const bus = new EventBus();

    for (const type of newEventTypes) {
      const received: any[] = [];
      bus.on(type, (d: any) => received.push(d));

      const payload: any = { issueId: 'i1', projectId: 'p1' };
      if (type === 'agent_text_chunk') {
        payload.text = 'hello';
        payload.stepIndex = 0;
      } else if (type === 'main_tool_call') {
        payload.executionId = 'exec-1';
        payload.toolName = 'test';
        payload.state = 'started';
      } else if (type === 'coder_text_chunk') {
        payload.executionId = 'exec-1';
        payload.acpSessionId = 'sess-1';
        payload.text = 'chunk';
      } else if (type === 'coder_tool_call') {
        payload.executionId = 'exec-1';
        payload.acpSessionId = 'sess-1';
        payload.toolName = 'read';
        payload.state = 'started';
      } else if (type === 'ralph_task_update') {
        payload.executionId = 'exec-1';
        payload.taskId = 'T-001';
        payload.taskIndex = 0;
        payload.totalTasks = 3;
        payload.status = 'started';
      } else if (type === 'ralph_loop_progress') {
        payload.executionId = 'exec-1';
        payload.completed = 1;
        payload.failed = 0;
        payload.total = 3;
      }

      bus.emit(type, payload);
      expect(received).toHaveLength(1);
      expect(received[0].issueId).toBe('i1');
    }
  });
});
