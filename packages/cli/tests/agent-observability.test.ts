import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { EventBus } from '../src/services/event-bus';
import { runAgentLoop } from '../src/agent-runtime/agent-loop';
import { SessionManager } from '../src/agent-runtime/session';
import { ToolRegistry } from '../src/agent-runtime/tool';

const mockStreamText = vi.hoisted(() => vi.fn());

vi.mock('ai', async (importOriginal) => {
  const actual = await importOriginal() as any;
  return {
    ...actual,
    streamText: mockStreamText,
  };
});

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

describe('Real-time Agent Observability', () => {
  let eventBus: EventBus;
  let emittedEvents: Array<{ type: string; data: unknown }>;

  beforeEach(() => {
    eventBus = new EventBus();
    emittedEvents = [];
    mockStreamText.mockClear();

    // 监听所有新事件类型
    const eventTypes = [
      'agent_text_chunk',
      'main_tool_call',
    ];

    for (const type of eventTypes) {
      eventBus.on(type as any, (data: unknown) => {
        emittedEvents.push({ type, data });
      });
    }
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  describe('Main Agent Events', () => {
    it('should emit agent_text_chunk for text-delta events', async () => {
      const sessionManager = new SessionManager();
      const toolRegistry = new ToolRegistry();
      const session = sessionManager.create(1);

      const mockText = 'Let me read the workflow...';
      const mockParts = [
        { type: 'text-delta', text: mockText },
      ];

      mockStreamText.mockReturnValue(createMockStreamTextResult(mockParts));

      await runAgentLoop(session, sessionManager, toolRegistry, {} as any, {
        eventBus,
        eventContext: { issueId: 'issue-1', projectId: 'proj-1' },
      });

      const textChunks = emittedEvents.filter(e => e.type === 'agent_text_chunk');
      expect(textChunks).toHaveLength(1);
      expect(textChunks[0].data).toMatchObject({
        issueId: 'issue-1',
        projectId: 'proj-1',
        text: mockText,
        stepIndex: 0,
      });
    });

    it('should emit main_tool_call started and completed events', async () => {
      const sessionManager = new SessionManager();
      const toolRegistry = new ToolRegistry();
      const session = sessionManager.create(1);

      const mockParts = [
        { type: 'tool-call', toolName: 'test_tool', input: { param: 'value' } },
        { type: 'tool-result', toolName: 'test_tool', output: 'tool result' },
      ];

      mockStreamText.mockReturnValue(createMockStreamTextResult(mockParts));

      await runAgentLoop(session, sessionManager, toolRegistry, {} as any, {
        eventBus,
        eventContext: { issueId: 'issue-1', projectId: 'proj-1' },
      });

      const toolCalls = emittedEvents.filter(e => e.type === 'main_tool_call');
      expect(toolCalls).toHaveLength(2);

      const startedEvent = toolCalls.find(e => (e.data as any).state === 'started');
      expect(startedEvent?.data).toMatchObject({
        issueId: 'issue-1',
        projectId: 'proj-1',
        toolName: 'test_tool',
        state: 'started',
        args: JSON.stringify({ param: 'value' }),
      });
      expect(startedEvent?.data).toHaveProperty('executionId');

      const completedEvent = toolCalls.find(e => (e.data as any).state === 'completed');
      expect(completedEvent?.data).toMatchObject({
        issueId: 'issue-1',
        projectId: 'proj-1',
        toolName: 'test_tool',
        state: 'completed',
        result: 'tool result',
      });
      expect(completedEvent?.data).toHaveProperty('executionId');
      expect(completedEvent?.data).toHaveProperty('duration');
    });

    it('should emit main_tool_call failed for tool errors', async () => {
      const sessionManager = new SessionManager();
      const toolRegistry = new ToolRegistry();
      const session = sessionManager.create(1);

      const mockParts = [
        { type: 'tool-call', toolName: 'error_tool', input: {} },
        { type: 'tool-error', toolName: 'error_tool', error: 'Tool execution failed' },
      ];

      mockStreamText.mockReturnValue(createMockStreamTextResult(mockParts));

      await runAgentLoop(session, sessionManager, toolRegistry, {} as any, {
        eventBus,
        eventContext: { issueId: 'issue-1', projectId: 'proj-1' },
      });

      const failedEvent = emittedEvents.find(
        e => e.type === 'main_tool_call' && (e.data as any).state === 'failed'
      );

      expect(failedEvent?.data).toMatchObject({
        issueId: 'issue-1',
        projectId: 'proj-1',
        toolName: 'error_tool',
        state: 'failed',
        error: 'Tool execution failed',
      });
      expect(failedEvent?.data).toHaveProperty('executionId');
      expect(failedEvent?.data).toHaveProperty('duration');
    });

    it('should not emit events when eventBus is not provided', async () => {
      const sessionManager = new SessionManager();
      const toolRegistry = new ToolRegistry();
      const session = sessionManager.create(1);

      const mockParts = [
        { type: 'text-delta', text: 'test' },
      ];

      mockStreamText.mockReturnValue(createMockStreamTextResult(mockParts));

      // Should not throw
      await expect(
        runAgentLoop(session, sessionManager, toolRegistry, {} as any, {})
      ).resolves.not.toThrow();

      expect(emittedEvents).toHaveLength(0);
    });
  });

  describe('Event Correlation', () => {
    it('should use consistent executionId for started and completed events', async () => {
      const sessionManager = new SessionManager();
      const toolRegistry = new ToolRegistry();
      const session = sessionManager.create(1);

      const mockParts = [
        { type: 'tool-call', toolName: 'test_tool', input: {} },
        { type: 'tool-result', toolName: 'test_tool', output: 'result' },
      ];

      mockStreamText.mockReturnValue(createMockStreamTextResult(mockParts));

      await runAgentLoop(session, sessionManager, toolRegistry, {} as any, {
        eventBus,
        eventContext: { issueId: 'issue-1', projectId: 'proj-1' },
      });

      const toolCalls = emittedEvents.filter(e => e.type === 'main_tool_call');
      expect(toolCalls).toHaveLength(2);

      const startedEvent = toolCalls.find(e => (e.data as any).state === 'started');
      const completedEvent = toolCalls.find(e => (e.data as any).state === 'completed');

      expect(startedEvent?.data).toHaveProperty('executionId');
      expect(completedEvent?.data).toHaveProperty('executionId');
      expect((startedEvent?.data as any).executionId).toBe(
        (completedEvent?.data as any).executionId
      );
    });

    it('should generate unique executionId when ToolRegistry slot is empty (fallback)', async () => {
      const sessionManager = new SessionManager();
      const toolRegistry = new ToolRegistry();
      const session = sessionManager.create(1);

      // Simulate case where executionId was not set in ToolRegistry
      const mockParts = [
        { type: 'tool-result', toolName: 'test_tool', output: 'result' },
      ];

      mockStreamText.mockReturnValue(createMockStreamTextResult(mockParts));

      await runAgentLoop(session, sessionManager, toolRegistry, {} as any, {
        eventBus,
        eventContext: { issueId: 'issue-1', projectId: 'proj-1' },
      });

      const completedEvent = emittedEvents.find(e => e.type === 'main_tool_call');
      expect(completedEvent?.data).toHaveProperty('executionId');
      expect((completedEvent?.data as any).executionId).toBeTruthy();
      expect(typeof (completedEvent?.data as any).executionId).toBe('string');
    });
  });

  describe('Step Index Tracking', () => {
    it('should increment stepIndex for multiple tool calls', async () => {
      const sessionManager = new SessionManager();
      const toolRegistry = new ToolRegistry();
      const session = sessionManager.create(1);

      const mockParts = [
        { type: 'tool-call', toolName: 'tool_a', input: {} },
        { type: 'tool-result', toolName: 'tool_a', output: 'result a' },
        { type: 'tool-call', toolName: 'tool_b', input: {} },
        { type: 'tool-result', toolName: 'tool_b', output: 'result b' },
      ];

      mockStreamText.mockReturnValue(createMockStreamTextResult(mockParts));

      await runAgentLoop(session, sessionManager, toolRegistry, {} as any, {
        eventBus,
        eventContext: { issueId: 'issue-1', projectId: 'proj-1' },
      });

      const startedEvents = emittedEvents.filter(
        e => e.type === 'main_tool_call' && (e.data as any).state === 'started'
      );

      expect(startedEvents).toHaveLength(2);
      expect((startedEvents[0].data as any).stepIndex).toBe(1);
      expect((startedEvents[1].data as any).stepIndex).toBe(2);
    });
  });

  describe('Duration Calculation', () => {
    it('should calculate duration for completed tool calls', async () => {
      const sessionManager = new SessionManager();
      const toolRegistry = new ToolRegistry();
      const session = sessionManager.create(1);

      const mockParts = [
        { type: 'tool-call', toolName: 'test_tool', input: {} },
        { type: 'tool-result', toolName: 'test_tool', output: 'result' },
      ];

      mockStreamText.mockReturnValue(createMockStreamTextResult(mockParts));

      const startTime = Date.now();
      await runAgentLoop(session, sessionManager, toolRegistry, {} as any, {
        eventBus,
        eventContext: { issueId: 'issue-1', projectId: 'proj-1' },
      });
      const endTime = Date.now();

      const completedEvent = emittedEvents.find(
        e => e.type === 'main_tool_call' && (e.data as any).state === 'completed'
      );

      expect(completedEvent?.data).toHaveProperty('duration');
      const duration = (completedEvent?.data as any).duration;
      expect(typeof duration).toBe('number');
      expect(duration).toBeGreaterThanOrEqual(0);
      expect(duration).toBeLessThanOrEqual(endTime - startTime + 100); // Allow 100ms buffer
    });

    it('should calculate duration for failed tool calls', async () => {
      const sessionManager = new SessionManager();
      const toolRegistry = new ToolRegistry();
      const session = sessionManager.create(1);

      const mockParts = [
        { type: 'tool-call', toolName: 'error_tool', input: {} },
        { type: 'tool-error', toolName: 'error_tool', error: 'failed' },
      ];

      mockStreamText.mockReturnValue(createMockStreamTextResult(mockParts));

      await runAgentLoop(session, sessionManager, toolRegistry, {} as any, {
        eventBus,
        eventContext: { issueId: 'issue-1', projectId: 'proj-1' },
      });

      const failedEvent = emittedEvents.find(
        e => e.type === 'main_tool_call' && (e.data as any).state === 'failed'
      );

      expect(failedEvent?.data).toHaveProperty('duration');
      expect(typeof (failedEvent?.data as any).duration).toBe('number');
    });
  });
});
