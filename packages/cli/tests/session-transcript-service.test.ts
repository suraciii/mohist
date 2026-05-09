import { describe, it, expect, beforeEach } from 'vitest';
import { assembleSessionTranscript, type SessionTranscript, type SessionTurn, type SessionPart, type ToolPart, type FileChangeSummary, type TranscriptWarning } from '../src/services/session-transcript-service';
import type { SessionStreamLogEntry } from '../src/db/session-stream-log-repo';
import type { CoderSession } from '../src/db/coder-session-repo';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { SessionStreamLogRepo } from '../src/db/session-stream-log-repo';
import { WorkflowSessionObserver, type SessionContext, type MohistPromptEvent } from '../src/agent-runtime/session-observer';

function makeSession(overrides: Partial<CoderSession> = {}): CoderSession {
  return {
    id: 'session-1',
    issueId: 'issue-1',
    acpSessionId: 'acp-session-1',
    executionId: 'exec-1',
    taskDescription: 'Test task',
    status: 'running',
    createdAt: '2024-01-01T10:00:00.000Z',
    completedAt: null,
    model: 'claude',
    coderType: null,
    stage: 'design',
    title: 'Test Session',
    processPid: null,
    ...overrides,
  };
}

function makeEvent(eventType: string, data: Record<string, unknown>, createdAt: string = '2024-01-01T10:00:00.000Z'): SessionStreamLogEntry {
  return {
    id: `event-${Math.random().toString(36).slice(2)}`,
    sessionId: 'session-1',
    issueId: 'issue-1',
    eventType,
    data: JSON.stringify(data),
    createdAt,
  };
}

function makePromptEvent(text: string, kind: string = 'task', sentAt: string = '2024-01-01T10:00:00.000Z'): SessionStreamLogEntry {
  return makeEvent('mohist_prompt', {
    role: 'mohist',
    text,
    kind,
    sentAt,
    executionId: 'exec-1',
    stage: 'design',
  }, sentAt);
}

function makeTextChunk(text: string, createdAt: string = '2024-01-01T10:00:01.000Z'): SessionStreamLogEntry {
  return makeEvent('agent_message_chunk', {
    content: { text },
    createdAt,
  }, createdAt);
}

function makeThoughtChunk(text: string, createdAt: string = '2024-01-01T10:00:01.000Z'): SessionStreamLogEntry {
  return makeEvent('agent_thought_chunk', {
    content: { text },
    createdAt,
  }, createdAt);
}

function makeToolCallStart(toolCallId: string, toolName: string, title?: string, input?: string, createdAt: string = '2024-01-01T10:00:02.000Z'): SessionStreamLogEntry {
  return makeEvent('tool_call', {
    toolCallId,
    toolName,
    title,
    input,
    status: 'started',
    createdAt,
  }, createdAt);
}

function makeToolCallUpdate(toolCallId: string, status: string, output?: string, error?: string, createdAt: string = '2024-01-01T10:00:03.000Z'): SessionStreamLogEntry {
  return makeEvent('tool_call_update', {
    toolCallId,
    status,
    output,
    error,
    createdAt,
  }, createdAt);
}

function makeTimeoutEvent(createdAt: string = '2024-01-01T10:00:05.000Z'): SessionStreamLogEntry {
  return makeEvent('acp_session_timeout', {
    phase: 'prompt',
    mode: 'agent-session',
    timestamp: createdAt,
  }, createdAt);
}

function makeCancelledEvent(createdAt: string = '2024-01-01T10:00:05.000Z'): SessionStreamLogEntry {
  return makeEvent('acp_session_aborted', {
    mode: 'agent-session',
    timestamp: createdAt,
  }, createdAt);
}

function makeCompletedEvent(success: boolean = true, error?: string, createdAt: string = '2024-01-01T10:00:05.000Z'): SessionStreamLogEntry {
  return makeEvent('acp_session_completed', {
    success,
    error,
    mode: 'agent-session',
    timestamp: createdAt,
  }, createdAt);
}

function makeRecoveryEvent(kind: 'started' | 'succeeded' | 'failed', createdAt: string = '2024-01-01T10:00:04.000Z'): SessionStreamLogEntry {
  const eventTypeMap = {
    started: 'acp_session_recovery_started',
    succeeded: 'acp_session_recovery_succeeded',
    failed: 'acp_session_recovery_failed',
  };
  return makeEvent(eventTypeMap[kind], {
    timestamp: createdAt,
  }, createdAt);
}

describe('SessionTranscriptAssembler', () => {
  describe('prompt boundaries', () => {
    it('should open a new turn when mohist_prompt event is received', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello, implement feature X', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('I will implement feature X now.', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(1);
      expect(transcript.turns[0].user.role).toBe('mohist');
      expect(transcript.turns[0].user.text).toBe('Hello, implement feature X');
      expect(transcript.turns[0].user.kind).toBe('task');
    });

    it('should close previous turn when new mohist_prompt event opens a new turn', () => {
      const session = makeSession({ status: 'running' });
      const events = [
        makePromptEvent('First prompt', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response to first', '2024-01-01T10:00:01.000Z'),
        makePromptEvent('Second prompt', 'retry', '2024-01-01T10:00:05.000Z'),
        makeTextChunk('Response to second', '2024-01-01T10:00:06.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(2);
      expect(transcript.turns[0].completedAt).toBe('2024-01-01T10:00:05.000Z');
      expect(transcript.turns[1].user.kind).toBe('retry');
      expect(transcript.turns[1].completedAt).toBeNull();
    });

    it('should record prompt kind correctly for different prompt types', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Initial task', 'initial', '2024-01-01T10:00:00.000Z'),
        makePromptEvent('Retry prompt', 'retry', '2024-01-01T10:01:00.000Z'),
        makePromptEvent('Followup prompt', 'followup', '2024-01-01T10:02:00.000Z'),
        makePromptEvent('Recovery prompt', 'recovery', '2024-01-01T10:03:00.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].user.kind).toBe('initial');
      expect(transcript.turns[1].user.kind).toBe('retry');
      expect(transcript.turns[2].user.kind).toBe('followup');
      expect(transcript.turns[3].user.kind).toBe('recovery');
    });
  });

  describe('retry/follow-up boundaries', () => {
    it('should create separate turns for retry prompts', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Initial request', 'initial', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Initial response', '2024-01-01T10:00:01.000Z'),
        makePromptEvent('Please try again', 'retry', '2024-01-01T10:01:00.000Z'),
        makeTextChunk('Retry response', '2024-01-01T10:01:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(2);
      expect(transcript.turns[0].user.kind).toBe('initial');
      expect(transcript.turns[1].user.kind).toBe('retry');
    });
  });

  describe('text and reasoning accumulation', () => {
    it('should accumulate text chunks into a single text part', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Hello ', '2024-01-01T10:00:01.000Z'),
        makeTextChunk('world', '2024-01-01T10:00:02.000Z'),
        makeTextChunk('!', '2024-01-01T10:00:03.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      expect(transcript.turns[0].assistant[0].type).toBe('text');
      expect((transcript.turns[0].assistant[0] as any).text).toBe('Hello world!');
    });

    it('should accumulate thought chunks into a single reasoning part', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeThoughtChunk('Thinking... ', '2024-01-01T10:00:01.000Z'),
        makeThoughtChunk('Let me analyze...', '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      expect(transcript.turns[0].assistant[0].type).toBe('reasoning');
      expect((transcript.turns[0].assistant[0] as any).text).toBe('Thinking... Let me analyze...');
    });

    it('should maintain separate text and reasoning parts', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeThoughtChunk('Thinking about this...', '2024-01-01T10:00:01.000Z'),
        makeTextChunk('Here is my answer.', '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(2);
      expect(transcript.turns[0].assistant[0].type).toBe('reasoning');
      expect(transcript.turns[0].assistant[1].type).toBe('text');
    });

    it('should set completedAt on text part when turn closes', () => {
      const session = makeSession({ status: 'completed', completedAt: '2024-01-01T10:00:10.000Z' });
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant[0].type).toBe('text');
      expect((transcript.turns[0].assistant[0] as any).completedAt).toBe('2024-01-01T10:00:10.000Z');
    });
  });

  describe('tool merge', () => {
    it('should create tool part on tool_call start event', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Read a file', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-1', 'Read', 'src/index.ts', '{"file_path":"src/index.ts"}', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      expect(transcript.turns[0].assistant[0].type).toBe('tool');
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.toolCallId).toBe('tc-1');
      expect(toolPart.toolName).toBe('Read');
      expect(toolPart.status).toBe('running');
      expect(toolPart.title).toBe('src/index.ts');
      expect(toolPart.target).toBe('index.ts');
    });

    it('should merge tool_call_update into existing tool part by toolCallId', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Read a file', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-1', 'Read', undefined, '{"file_path":"src/index.ts"}', '2024-01-01T10:00:01.000Z'),
        makeToolCallUpdate('tc-1', 'completed', 'file contents here', undefined, '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.status).toBe('completed');
      expect(toolPart.output).toBe('file contents here');
      expect(toolPart.completedAt).toBe('2024-01-01T10:00:02.000Z');
    });

    it('should handle failed tool status', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Run command', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-1', 'Bash', 'npm install', 'npm install', '2024-01-01T10:00:01.000Z'),
        makeToolCallUpdate('tc-1', 'failed', undefined, 'Command failed with exit code 1', '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.status).toBe('failed');
      expect(toolPart.error).toBe('Command failed with exit code 1');
    });

    it('should create tool part if update arrives before start', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Do something', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallUpdate('tc-1', 'completed', 'result', undefined, '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.toolCallId).toBe('tc-1');
      expect(toolPart.status).toBe('completed');
    });

    it('should create legacy tool part if update arrives without a prompt or start event', () => {
      const session = makeSession();
      const events = [
        makeToolCallUpdate('tc-1', 'completed', 'result', undefined, '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(1);
      expect(transcript.turns[0].user.kind).toBe('legacy-missing');
      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.toolCallId).toBe('tc-1');
      expect(toolPart.status).toBe('completed');
      expect(toolPart.output).toBe('result');
    });

    it('should replay nested ACP tool_call without a toolCallId using a deterministic synthetic id', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Run a tool', 'task', '2024-01-01T10:00:00.000Z'),
        makeEvent('tool_call', {
          toolCall: {
            toolName: 'Read',
            title: 'src/index.ts',
            input: { file_path: 'src/index.ts' },
            status: 'started',
            createdAt: '2024-01-01T10:00:01.000Z',
          },
        }, '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.toolCallId).toBe('synthetic-0');
      expect(toolPart.toolName).toBe('Read');
      expect(toolPart.input).toBe('{"file_path":"src/index.ts"}');
    });

    it('should replay nested ACP completed tool_call without a toolCallId', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Run a tool', 'task', '2024-01-01T10:00:00.000Z'),
        makeEvent('tool_call', {
          toolCall: {
            toolName: 'Bash',
            input: { command: 'npm test' },
            output: 'ok',
            status: 'completed',
            createdAt: '2024-01-01T10:00:01.000Z',
          },
        }, '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.toolCallId).toBe('synthetic-0');
      expect(toolPart.status).toBe('completed');
      expect(toolPart.output).toBe('ok');
    });

    it('should merge two no-id ACP tool_call events (started + completed) into one tool part', () => {
      const session = makeSession();
      const sharedToolCallId = 'session-Read-0';
      const events = [
        makePromptEvent('Run a tool', 'task', '2024-01-01T10:00:00.000Z'),
        makeEvent('tool_call', {
          toolCall: {
            toolCallId: sharedToolCallId,
            toolName: 'Read',
            title: 'src/index.ts',
            input: { file_path: 'src/index.ts' },
            status: 'started',
            createdAt: '2024-01-01T10:00:01.000Z',
          },
        }, '2024-01-01T10:00:01.000Z'),
        makeEvent('tool_call', {
          toolCall: {
            toolCallId: sharedToolCallId,
            toolName: 'Read',
            title: 'src/index.ts',
            output: 'file contents here',
            status: 'completed',
            createdAt: '2024-01-01T10:00:02.000Z',
          },
        }, '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.toolCallId).toBe(sharedToolCallId);
      expect(toolPart.toolName).toBe('Read');
      expect(toolPart.status).toBe('completed');
      expect(toolPart.input).toBe('{"file_path":"src/index.ts"}');
      expect(toolPart.output).toBe('file contents here');
    });

    it('should merge two no-id ACP tool_call events (started + completed) without any toolCallId into one tool part', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Run a tool', 'task', '2024-01-01T10:00:00.000Z'),
        makeEvent('tool_call', {
          toolCall: {
            toolName: 'Read',
            title: 'src/index.ts',
            input: { file_path: 'src/index.ts' },
            status: 'started',
            createdAt: '2024-01-01T10:00:01.000Z',
          },
        }, '2024-01-01T10:00:01.000Z'),
        makeEvent('tool_call', {
          toolCall: {
            toolName: 'Read',
            title: 'src/index.ts',
            output: 'file contents here',
            status: 'completed',
            createdAt: '2024-01-01T10:00:02.000Z',
          },
        }, '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.toolName).toBe('Read');
      expect(toolPart.status).toBe('completed');
      expect(toolPart.input).toBe('{"file_path":"src/index.ts"}');
      expect(toolPart.output).toBe('file contents here');
    });

    it('should set target from input when not explicitly set', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Glob files', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-1', 'Glob', undefined, '{"pattern":"**/*.ts"}', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.target).toBe('**/*.ts');
    });
  });

  describe('terminal events', () => {
    it('should close turn on timeout event', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response', '2024-01-01T10:00:01.000Z'),
        makeTimeoutEvent('2024-01-01T10:00:05.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].completedAt).toBe('2024-01-01T10:00:05.000Z');
      const errorPart = transcript.turns[0].assistant.find(p => p.type === 'error') as any;
      expect(errorPart.kind).toBe('timeout');
      expect(errorPart.message).toBe('Session timed out');
    });

    it('should close turn on cancelled event', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response', '2024-01-01T10:00:01.000Z'),
        makeCancelledEvent('2024-01-01T10:00:05.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].completedAt).toBe('2024-01-01T10:00:05.000Z');
      const errorPart = transcript.turns[0].assistant.find(p => p.type === 'error') as any;
      expect(errorPart.kind).toBe('cancelled');
    });

    it('should close turn on completed status with failed error', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response', '2024-01-01T10:00:01.000Z'),
        makeCompletedEvent(false, 'Something went wrong', '2024-01-01T10:00:05.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      const errorPart = transcript.turns[0].assistant.find(p => p.type === 'error') as any;
      expect(errorPart.kind).toBe('failed');
      expect(errorPart.message).toBe('Something went wrong');
    });

    it('should close turn when session status is terminal', () => {
      const session = makeSession({ status: 'completed', completedAt: '2024-01-01T10:00:10.000Z' });
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].completedAt).toBe('2024-01-01T10:00:10.000Z');
    });

    it('should handle recovery events as non-terminal errors', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response', '2024-01-01T10:00:01.000Z'),
        makeRecoveryEvent('started', '2024-01-01T10:00:03.000Z'),
        makeRecoveryEvent('succeeded', '2024-01-01T10:00:04.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant.filter(p => p.type === 'error')).toHaveLength(2);
      expect(transcript.turns[0].completedAt).toBeNull();
    });
  });

  describe('legacy fallback', () => {
    it('should create synthetic turn when there are assistant events but no prompt', () => {
      const session = makeSession();
      const events = [
        makeTextChunk('Legacy response', '2024-01-01T10:00:01.000Z'),
        makeToolCallStart('tc-1', 'Read', 'file.txt', '{}', '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(1);
      expect(transcript.turns[0].user.kind).toBe('legacy-missing');
      expect(transcript.turns[0].user.text).toBe('Prompt was not recorded for this historical session');
      expect(transcript.turns[0].incomplete).toBe(true);
      expect(transcript.incomplete).toBe(true);
    });

    it('should not create legacy turn if there are prompts', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Real prompt', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Real response', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(1);
      expect(transcript.turns[0].user.kind).toBe('task');
      expect(transcript.incomplete).toBe(false);
    });

    it('should include tool events in legacy turn', () => {
      const session = makeSession();
      const events = [
        makeToolCallStart('tc-1', 'Bash', 'ls', 'ls', '2024-01-01T10:00:01.000Z'),
        makeToolCallUpdate('tc-1', 'completed', 'file1.txt\nfile2.txt', undefined, '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(1);
      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.toolName).toBe('Bash');
      expect(toolPart.status).toBe('completed');
    });
  });

  describe('event ordering', () => {
    it('should order events by createdAt timestamp', () => {
      const session = makeSession();
      const events = [
        makeTextChunk('Late response', '2024-01-01T10:00:05.000Z'),
        makePromptEvent('Early prompt', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Early response', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(1);
      expect((transcript.turns[0].assistant[0] as any).text).toBe('Early responseLate response');
    });

    it('should preserve input order for same-timestamp events instead of sorting by UUID id', () => {
      const session = makeSession();
      const events = [
        { id: 'zzzz-uuid-like', sessionId: 'session-1', issueId: 'issue-1', eventType: 'mohist_prompt', data: JSON.stringify({ role: 'mohist', text: 'Prompt', kind: 'task', sentAt: '2024-01-01T10:00:00.000Z' }), createdAt: '2024-01-01T10:00:00.000Z' },
        { id: 'evt-prompt', sessionId: 'session-1', issueId: 'issue-1', eventType: 'mohist_prompt', data: JSON.stringify({ role: 'mohist', text: 'Prompt', kind: 'task', sentAt: '2024-01-01T10:00:00.000Z' }), createdAt: '2024-01-01T10:00:00.000Z' },
        { id: '0000-uuid-like', sessionId: 'session-1', issueId: 'issue-1', eventType: 'agent_message_chunk', data: JSON.stringify({ content: { text: 'Assistant' } }), createdAt: '2024-01-01T10:00:00.000Z' },
      ];

      const transcript = assembleSessionTranscript(session, events as any);

      expect(transcript.turns).toHaveLength(2);
      expect(transcript.turns[1].user.text).toBe('Prompt');
      expect((transcript.turns[1].assistant[0] as any).text).toBe('Assistant');
    });

    it('should preserve same-priority same-timestamp message chunk input order', () => {
      const session = makeSession();
      const sameTime = '2024-01-01T10:00:00.000Z';
      const events = [
        makePromptEvent('Prompt', 'task', sameTime),
        { id: 'zzzz-uuid-like', sessionId: 'session-1', issueId: 'issue-1', eventType: 'agent_message_chunk', data: JSON.stringify({ content: { text: 'first ' } }), createdAt: sameTime },
        { id: '0000-uuid-like', sessionId: 'session-1', issueId: 'issue-1', eventType: 'agent_message_chunk', data: JSON.stringify({ content: { text: 'second' } }), createdAt: sameTime },
      ];

      const transcript = assembleSessionTranscript(session, events as any);

      expect((transcript.turns[0].assistant[0] as any).text).toBe('first second');
    });

    it('should read same-second rows in SQLite insertion order', () => {
      const db = new DatabaseManager({ inMemory: true });
      initializeDatabase(db);
      const repo = new SessionStreamLogRepo(db);
      try {
        db.run(`INSERT INTO projects (id, name, path, created_at, updated_at) VALUES (?, ?, ?, datetime('now'), datetime('now'))`, ['project-1', 'Project', '/tmp/project']);
        db.run(`INSERT INTO issues (id, project_id, number, title, status, created_at, updated_at) VALUES (?, ?, ?, ?, ?, datetime('now'), datetime('now'))`, ['issue-1', 'project-1', 1, 'Issue', 'open']);
        db.run(
          `INSERT INTO session_stream_log (id, session_id, issue_id, event_type, data, created_at) VALUES (?, ?, ?, ?, ?, ?)`,
          ['zzzz-uuid-like', 'session-1', 'issue-1', 'mohist_prompt', JSON.stringify({ role: 'mohist', text: 'Prompt', kind: 'task', sentAt: '2024-01-01T10:00:00.000Z' }), '2024-01-01 10:00:00'],
        );
        db.run(
          `INSERT INTO session_stream_log (id, session_id, issue_id, event_type, data, created_at) VALUES (?, ?, ?, ?, ?, ?)`,
          ['0000-uuid-like', 'session-1', 'issue-1', 'agent_message_chunk', JSON.stringify({ content: { text: 'Assistant' } }), '2024-01-01 10:00:00'],
        );

        const rows = repo.findBySessionId('session-1');
        expect(rows.map((row) => row.id)).toEqual(['zzzz-uuid-like', '0000-uuid-like']);
      } finally {
        db.close();
      }
    });
  });

  describe('transcript metadata', () => {
    it('should include session metadata in transcript', () => {
      const session = makeSession({
        id: 'my-session-id',
        issueId: 'my-issue-id',
        acpSessionId: 'my-acp-session',
        executionId: 'my-exec',
        title: 'My Session Title',
        status: 'completed',
        model: 'claude-3',
        stage: 'implementation',
        completedAt: '2024-01-01T12:00:00.000Z',
      });
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.session.sessionId).toBe('my-session-id');
      expect(transcript.session.issueId).toBe('my-issue-id');
      expect(transcript.session.acpSessionId).toBe('my-acp-session');
      expect(transcript.session.title).toBe('My Session Title');
      expect(transcript.session.status).toBe('completed');
      expect(transcript.session.model).toBe('claude-3');
      expect(transcript.session.stage).toBe('implementation');
      expect(transcript.session.completedAt).toBe('2024-01-01T12:00:00.000Z');
    });

    it('should set incomplete false when all prompts are recorded', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.incomplete).toBe(false);
    });
  });

  describe('multiple turns with various content', () => {
    it('should handle complex multi-turn session', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('First task', 'initial', '2024-01-01T10:00:00.000Z'),
        makeThoughtChunk('Thinking...', '2024-01-01T10:00:01.000Z'),
        makeTextChunk('First response', '2024-01-01T10:00:02.000Z'),
        makeToolCallStart('tc-1', 'Read', 'file1.txt', '{"file_path":"file1.txt"}', '2024-01-01T10:00:03.000Z'),
        makeToolCallUpdate('tc-1', 'completed', 'contents', undefined, '2024-01-01T10:00:04.000Z'),
        makePromptEvent('Second task', 'followup', '2024-01-01T10:01:00.000Z'),
        makeTextChunk('Second response', '2024-01-01T10:01:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(2);

      expect(transcript.turns[0].user.kind).toBe('initial');
      expect(transcript.turns[0].completedAt).toBe('2024-01-01T10:01:00.000Z');
      expect(transcript.turns[0].assistant.filter(p => p.type === 'reasoning')).toHaveLength(1);
      expect(transcript.turns[0].assistant.filter(p => p.type === 'text')).toHaveLength(1);
      expect(transcript.turns[0].assistant.filter(p => p.type === 'tool')).toHaveLength(1);

      expect(transcript.turns[1].user.kind).toBe('followup');
      expect(transcript.turns[1].completedAt).toBeNull();
      expect(transcript.turns[1].assistant.filter(p => p.type === 'text')).toHaveLength(1);
    });
  });

  describe('production prompt persistence', () => {
    it('writeMohistPrompt persists prompt retrievable by acpSessionId', () => {
      const db = new DatabaseManager({ inMemory: true });
      initializeDatabase(db);
      const repo = new SessionStreamLogRepo(db);
      try {
        db.run(`INSERT INTO projects (id, name, path, created_at, updated_at) VALUES (?, ?, ?, datetime('now'), datetime('now'))`, ['project-1', 'Project', '/tmp/project']);
        db.run(`INSERT INTO issues (id, project_id, number, title, status, created_at, updated_at) VALUES (?, ?, ?, ?, ?, datetime('now'), datetime('now'))`, ['issue-1', 'project-1', 1, 'Issue', 'open']);

        const observer = new WorkflowSessionObserver({ sessionStreamLogRepo: repo });
        const ctx: SessionContext = {
          issueId: 'issue-1',
          issueNumber: 1,
          projectId: 'project-1',
          executionId: 'exec-1',
          acpSessionId: 'acp-test-session',
          coderSessionId: undefined,
          stage: 'build',
          model: 'claude-3',
          processPid: undefined,
        };
        const prompt: MohistPromptEvent = {
          role: 'mohist',
          text: 'Implement task T-001: Add authentication middleware',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
          executionId: 'exec-1',
          stage: 'build',
          issueId: 'issue-1',
          acpSessionId: 'acp-test-session',
        };

        observer.writeMohistPrompt(ctx, prompt);

        const rows = repo.findBySessionId('acp-test-session');
        expect(rows.length).toBeGreaterThanOrEqual(1);
        const promptRow = rows.find(r => r.eventType === 'mohist_prompt');
        expect(promptRow).toBeDefined();
        const data = JSON.parse(promptRow!.data);
        expect(data.text).toBe('Implement task T-001: Add authentication middleware');
        expect(data.kind).toBe('task');
        expect(data.role).toBe('mohist');
      } finally {
        db.close();
      }
    });

    it('writeMohistPrompt persists outputPath and contextFiles metadata', () => {
      const db = new DatabaseManager({ inMemory: true });
      initializeDatabase(db);
      const repo = new SessionStreamLogRepo(db);
      try {
        db.run(`INSERT INTO projects (id, name, path, created_at, updated_at) VALUES (?, ?, ?, datetime('now'), datetime('now'))`, ['project-1', 'Project', '/tmp/project']);
        db.run(`INSERT INTO issues (id, project_id, number, title, status, created_at, updated_at) VALUES (?, ?, ?, ?, ?, datetime('now'), datetime('now'))`, ['issue-1', 'project-1', 1, 'Issue', 'open']);

        const observer = new WorkflowSessionObserver({ sessionStreamLogRepo: repo });
        const ctx: SessionContext = {
          issueId: 'issue-1',
          issueNumber: 1,
          projectId: 'project-1',
          executionId: 'exec-1',
          acpSessionId: 'acp-test-session-2',
          coderSessionId: undefined,
          stage: 'build',
          model: 'claude-3',
          processPid: undefined,
        };
        const prompt: MohistPromptEvent = {
          role: 'mohist',
          text: '<contract>packages/cli/src/index.ts</contract>\n<role>Implement feature</role>\n<context_files>\nsrc/a.ts\nsrc/b.ts\n</context_files>',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
          executionId: 'exec-1',
          stage: 'build',
          issueId: 'issue-1',
          acpSessionId: 'acp-test-session-2',
          outputPath: 'packages/cli/src/index.ts',
          contextFiles: ['src/a.ts', 'src/b.ts'],
        };

        observer.writeMohistPrompt(ctx, prompt);

        const rows = repo.findBySessionId('acp-test-session-2');
        expect(rows.length).toBeGreaterThanOrEqual(1);
        const promptRow = rows.find(r => r.eventType === 'mohist_prompt');
        expect(promptRow).toBeDefined();
        const data = JSON.parse(promptRow!.data);
        expect(data.outputPath).toBe('packages/cli/src/index.ts');
        expect(data.contextFiles).toEqual(['src/a.ts', 'src/b.ts']);
      } finally {
        db.close();
      }
    });
  });

  describe('tool event replay', () => {
    it('should replay tool with rawOutputMetadata preserved after fetch', () => {
      const db = new DatabaseManager({ inMemory: true });
      initializeDatabase(db);
      const repo = new SessionStreamLogRepo(db);
      try {
        db.run(`INSERT INTO projects (id, name, path, created_at, updated_at) VALUES (?, ?, ?, datetime('now'), datetime('now'))`, ['project-1', 'Project', '/tmp/project']);
        db.run(`INSERT INTO issues (id, project_id, number, title, status, created_at, updated_at) VALUES (?, ?, ?, ?, ?, datetime('now'), datetime('now'))`, ['issue-1', 'project-1', 1, 'Issue', 'open']);

        const observer = new WorkflowSessionObserver({ sessionStreamLogRepo: repo });
        const ctx: SessionContext = {
          issueId: 'issue-1',
          issueNumber: 1,
          projectId: 'project-1',
          executionId: 'exec-1',
          acpSessionId: 'acp-test-session-3',
          coderSessionId: undefined,
          stage: 'build',
          model: 'claude-3',
          processPid: undefined,
        };

        const toolData = {
          toolCallId: 'tool-call-1',
          toolName: 'Read',
          title: 'src/index.ts',
          input: JSON.stringify({ file_path: 'src/index.ts' }),
          output: JSON.stringify({ result: 'file contents', metadata: { toolName: 'Read', name: 'Read' } }),
          status: 'completed',
          createdAt: '2024-01-01T10:00:01.000Z',
          rawOutputMetadata: { toolName: 'Read', name: 'Read' },
        };

        observer.onSessionEvent(ctx, 'tool_call', toolData);

        const rows = repo.findBySessionId('acp-test-session-3');
        expect(rows.length).toBeGreaterThanOrEqual(1);
        const toolRow = rows.find(r => r.eventType === 'tool_call');
        expect(toolRow).toBeDefined();
        const data = JSON.parse(toolRow!.data);
        expect(data.toolName).toBe('Read');
        expect(data.title).toBe('src/index.ts');
        expect(data.rawOutputMetadata).toEqual({ toolName: 'Read', name: 'Read' });
        expect(data.status).toBe('completed');
      } finally {
        db.close();
      }
    });

    it('should reconstruct tool title/rawInput/rawOutput from persisted event', () => {
      const session = makeSession();
      const events: SessionStreamLogEntry[] = [
        makePromptEvent('Read a file', 'task', '2024-01-01T10:00:00.000Z'),
        { id: 'evt-tool', sessionId: 'session-1', issueId: 'issue-1', eventType: 'tool_call', data: JSON.stringify({
          toolCallId: 'tc-replay',
          toolName: 'Read',
          title: 'src/index.ts',
          input: JSON.stringify({ file_path: 'src/index.ts' }),
          output: JSON.stringify({ result: 'file contents', metadata: { toolName: 'Read' } }),
          status: 'completed',
          createdAt: '2024-01-01T10:00:01.000Z',
          rawOutputMetadata: { toolName: 'Read' },
        }), createdAt: '2024-01-01T10:00:01.000Z' },
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.title).toBe('src/index.ts');
      expect(toolPart.input).toBe(JSON.stringify({ file_path: 'src/index.ts' }));
      expect(toolPart.output).toBe(JSON.stringify({ result: 'file contents', metadata: { toolName: 'Read' } }));
    });

    it('should preserve terminal status for turn closure after replay', () => {
      const session = makeSession({ status: 'completed', completedAt: '2024-01-01T10:00:10.000Z' });
      const events: SessionStreamLogEntry[] = [
        makePromptEvent('Run command', 'task', '2024-01-01T10:00:00.000Z'),
        { id: 'evt-tool', sessionId: 'session-1', issueId: 'issue-1', eventType: 'tool_call', data: JSON.stringify({
          toolCallId: 'tc-term',
          toolName: 'Bash',
          title: 'npm test',
          status: 'completed',
          output: 'tests passed',
          createdAt: '2024-01-01T10:00:05.000Z',
        }), createdAt: '2024-01-01T10:00:05.000Z' },
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].completedAt).toBe('2024-01-01T10:00:10.000Z');
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.status).toBe('completed');
      expect(toolPart.output).toBe('tests passed');
    });
  });

  describe('same-second event ordering', () => {
    it('should sort prompts before assistant activity within same second', () => {
      const session = makeSession();
      const events: SessionStreamLogEntry[] = [
        { id: 'evt-assistant', sessionId: 'session-1', issueId: 'issue-1', eventType: 'agent_message_chunk', data: JSON.stringify({ content: { text: 'Response' } }), createdAt: '2024-01-01T10:00:00.000Z' },
        { id: 'evt-prompt', sessionId: 'session-1', issueId: 'issue-1', eventType: 'mohist_prompt', data: JSON.stringify({ role: 'mohist', text: 'Prompt', kind: 'task', sentAt: '2024-01-01T10:00:00.000Z' }), createdAt: '2024-01-01T10:00:00.000Z' },
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(1);
      expect(transcript.turns[0].assistant[0].type).toBe('text');
      expect((transcript.turns[0].assistant[0] as any).text).toBe('Response');
    });

    it('should sort tool_call before tool_call_update within same second', () => {
      const session = makeSession();
      const events: SessionStreamLogEntry[] = [
        { id: 'evt-update', sessionId: 'session-1', issueId: 'issue-1', eventType: 'tool_call_update', data: JSON.stringify({ toolCallId: 'tc-1', status: 'completed', output: 'done', createdAt: '2024-01-01T10:00:00.000Z' }), createdAt: '2024-01-01T10:00:00.000Z' },
        { id: 'evt-start', sessionId: 'session-1', issueId: 'issue-1', eventType: 'tool_call', data: JSON.stringify({ toolCallId: 'tc-1', toolName: 'Read', status: 'started', createdAt: '2024-01-01T10:00:00.000Z' }), createdAt: '2024-01-01T10:00:00.000Z' },
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(1);
      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.status).toBe('completed');
      expect(toolPart.output).toBe('done');
    });

    it('should sort terminal events last within same second', () => {
      const session = makeSession();
      const events: SessionStreamLogEntry[] = [
        { id: 'evt-text', sessionId: 'session-1', issueId: 'issue-1', eventType: 'agent_message_chunk', data: JSON.stringify({ content: { text: 'Response' } }), createdAt: '2024-01-01T10:00:01.000Z' },
        { id: 'evt-terminal', sessionId: 'session-1', issueId: 'issue-1', eventType: 'acp_session_timeout', data: JSON.stringify({}), createdAt: '2024-01-01T10:00:01.000Z' },
        { id: 'evt-prompt', sessionId: 'session-1', issueId: 'issue-1', eventType: 'mohist_prompt', data: JSON.stringify({ role: 'mohist', text: 'Prompt', kind: 'task', sentAt: '2024-01-01T10:00:00.000Z' }), createdAt: '2024-01-01T10:00:00.000Z' },
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(1);
      expect(transcript.turns[0].completedAt).toBe('2024-01-01T10:00:01.000Z');
      const errorPart = transcript.turns[0].assistant.find(p => p.type === 'error');
      expect(errorPart).toBeDefined();
    });

    it('should produce deterministic same-second ordering across multiple runs', () => {
      const session = makeSession();
      const events: SessionStreamLogEntry[] = [
        { id: 'uuid-z', sessionId: 'session-1', issueId: 'issue-1', eventType: 'mohist_prompt', data: JSON.stringify({ role: 'mohist', text: 'Prompt', kind: 'task', sentAt: '2024-01-01T10:00:00.000Z' }), createdAt: '2024-01-01T10:00:00.000Z' },
        { id: 'uuid-a', sessionId: 'session-1', issueId: 'issue-1', eventType: 'agent_message_chunk', data: JSON.stringify({ content: { text: 'First' } }), createdAt: '2024-01-01T10:00:00.000Z' },
        { id: 'uuid-b', sessionId: 'session-1', issueId: 'issue-1', eventType: 'agent_message_chunk', data: JSON.stringify({ content: { text: 'Second' } }), createdAt: '2024-01-01T10:00:00.000Z' },
      ];

      const transcript1 = assembleSessionTranscript(session, events);
      const transcript2 = assembleSessionTranscript(session, events);

      const text1 = transcript1.turns[0].assistant.map(p => p.type === 'text' ? p.text : '').join('');
      const text2 = transcript2.turns[0].assistant.map(p => p.type === 'text' ? p.text : '').join('');
      expect(text1).toBe(text2);
      expect(text1).toBe('FirstSecond');
    });
  });

  describe('multi-prompt turns', () => {
    it('should close previous turn when new prompt opens a new turn', () => {
      const session = makeSession({ status: 'running' });
      const events = [
        makePromptEvent('First prompt', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('First response', '2024-01-01T10:00:01.000Z'),
        makePromptEvent('Second prompt', 'retry', '2024-01-01T10:01:00.000Z'),
        makeTextChunk('Second response', '2024-01-01T10:01:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(2);
      expect(transcript.turns[0].completedAt).toBe('2024-01-01T10:01:00.000Z');
      expect(transcript.turns[0].assistant[0].type).toBe('text');
      expect(transcript.turns[1].user.kind).toBe('retry');
      expect(transcript.turns[1].completedAt).toBeNull();
    });

    it('should handle multiple prompts without assistant events between them', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('First', 'initial', '2024-01-01T10:00:00.000Z'),
        makePromptEvent('Second', 'retry', '2024-01-01T10:01:00.000Z'),
        makeTextChunk('Response to second', '2024-01-01T10:01:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(2);
      expect(transcript.turns[0].user.text).toBe('First');
      expect(transcript.turns[1].user.text).toBe('Second');
      expect(transcript.turns[1].assistant[0].type).toBe('text');
    });
  });

  describe('nested and no-id tool merge', () => {
    it('should merge nested toolCall payloads with top-level ids', () => {
      const session = makeSession();
      const events: SessionStreamLogEntry[] = [
        makePromptEvent('Run a tool', 'task', '2024-01-01T10:00:00.000Z'),
        { id: 'evt-1', sessionId: 'session-1', issueId: 'issue-1', eventType: 'tool_call', data: JSON.stringify({
          toolCall: {
            toolCallId: 'tc-nested',
            toolName: 'Read',
            title: 'src/index.ts',
            input: { file_path: 'src/index.ts' },
            status: 'started',
            createdAt: '2024-01-01T10:00:01.000Z',
          },
        }), createdAt: '2024-01-01T10:00:01.000Z' },
        { id: 'evt-2', sessionId: 'session-1', issueId: 'issue-1', eventType: 'tool_call_update', data: JSON.stringify({
          toolCall: {
            toolCallId: 'tc-nested',
            output: 'file contents',
            status: 'completed',
            createdAt: '2024-01-01T10:00:02.000Z',
          },
        }), createdAt: '2024-01-01T10:00:02.000Z' },
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.toolCallId).toBe('tc-nested');
      expect(toolPart.status).toBe('completed');
      expect(toolPart.output).toBe('file contents');
    });

    it('should merge no-id ACP tool_call start/update events by correlation', () => {
      const session = makeSession();
      const events: SessionStreamLogEntry[] = [
        makePromptEvent('Run tools', 'task', '2024-01-01T10:00:00.000Z'),
        { id: 'evt-start', sessionId: 'session-1', issueId: 'issue-1', eventType: 'tool_call', data: JSON.stringify({
          toolCall: {
            toolName: 'Read',
            title: 'file-a.txt',
            input: { file_path: 'file-a.txt' },
            status: 'started',
            createdAt: '2024-01-01T10:00:01.000Z',
          },
        }), createdAt: '2024-01-01T10:00:01.000Z' },
        { id: 'evt-update', sessionId: 'session-1', issueId: 'issue-1', eventType: 'tool_call_update', data: JSON.stringify({
          toolCall: {
            toolName: 'Read',
            title: 'file-a.txt',
            output: 'contents of file-a',
            status: 'completed',
            createdAt: '2024-01-01T10:00:02.000Z',
          },
        }), createdAt: '2024-01-01T10:00:02.000Z' },
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.toolName).toBe('Read');
      expect(toolPart.status).toBe('completed');
      expect(toolPart.input).toBe('{"file_path":"file-a.txt"}');
      expect(toolPart.output).toBe('contents of file-a');
    });

    it('should not merge different tools with same name in same turn', () => {
      const session = makeSession();
      const events: SessionStreamLogEntry[] = [
        makePromptEvent('Run tools', 'task', '2024-01-01T10:00:00.000Z'),
        { id: 'evt-start-1', sessionId: 'session-1', issueId: 'issue-1', eventType: 'tool_call', data: JSON.stringify({
          toolCall: {
            toolName: 'Read',
            title: 'file-a.txt',
            input: { file_path: 'file-a.txt' },
            status: 'started',
            createdAt: '2024-01-01T10:00:01.000Z',
          },
        }), createdAt: '2024-01-01T10:00:01.000Z' },
        { id: 'evt-start-2', sessionId: 'session-1', issueId: 'issue-1', eventType: 'tool_call', data: JSON.stringify({
          toolCall: {
            toolName: 'Read',
            title: 'file-b.txt',
            input: { file_path: 'file-b.txt' },
            status: 'started',
            createdAt: '2024-01-01T10:00:01.500Z',
          },
        }), createdAt: '2024-01-01T10:00:01.500Z' },
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(2);
      const tools = transcript.turns[0].assistant.filter(p => p.type === 'tool').map(t => (t as ToolPart).tool);
      expect(tools).toHaveLength(2);
      expect(tools[0].title).toBe('file-a.txt');
      expect(tools[1].title).toBe('file-b.txt');
    });
  });

  describe('title and input based tool inference', () => {
    it('should infer tool name from title field', () => {
      const session = makeSession();
      const events: SessionStreamLogEntry[] = [
        makePromptEvent('Do something', 'task', '2024-01-01T10:00:00.000Z'),
        makeEvent('tool_call', {
          toolCall: {
            toolName: 'apply_patch',
            title: 'src/index.ts',
            input: { patchText: 'some patch' },
            status: 'started',
            createdAt: '2024-01-01T10:00:01.000Z',
          },
        }, '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.normalizedName).toBe('apply_patch');
      expect(toolPart.category).toBe('file-change');
    });

    it('should infer tool name from input shape when toolName is missing', () => {
      const session = makeSession();
      const events: SessionStreamLogEntry[] = [
        makePromptEvent('Do something', 'task', '2024-01-01T10:00:00.000Z'),
        makeEvent('tool_call', {
          toolCall: {
            name: 'Bash',
            input: { command: 'npm test' },
            status: 'started',
            createdAt: '2024-01-01T10:00:01.000Z',
          },
        }, '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.normalizedName).toBe('Bash');
      expect(toolPart.category).toBe('execution');
    });

    it('should infer tool name from rawInput.patchText for apply_patch', () => {
      const session = makeSession();
      const events: SessionStreamLogEntry[] = [
        makePromptEvent('Patch files', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-apply', 'unknown', 'src/index.ts', JSON.stringify({ patchText: 'Add File: src/index.ts\n+ new content' }), '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.normalizedName).toBe('apply_patch');
    });

    it('should infer tool name from rawOutput.metadata when other fields are missing', () => {
      const session = makeSession();
      const events: SessionStreamLogEntry[] = [
        makePromptEvent('Do something', 'task', '2024-01-01T10:00:00.000Z'),
        makeEvent('tool_call', {
          toolCall: {
            toolName: 'unknown',
            input: {},
            output: { result: 'ok', metadata: { toolName: 'Write', name: 'Write' } },
            status: 'completed',
            createdAt: '2024-01-01T10:00:01.000Z',
          },
        }, '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.normalizedName).toBe('Write');
    });

    it('should mark tool as unknown with warning when inference fails', () => {
      const session = makeSession();
      const events: SessionStreamLogEntry[] = [
        makePromptEvent('Do something', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-unknown', 'completely-unrecognized-tool', undefined, '{}', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.normalizedName).toBe('unknown');
      expect(transcript.session.hasUnknownTools).toBe(true);
      expect(transcript.session.warnings).toBeDefined();
      expect(transcript.session.warnings!.some((w: TranscriptWarning) => w.code === 'UNKNOWN_TOOL')).toBe(true);
    });
  });

  describe('terminal closure', () => {
    it('should close turn on timeout event', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response', '2024-01-01T10:00:01.000Z'),
        makeTimeoutEvent('2024-01-01T10:00:05.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].completedAt).toBe('2024-01-01T10:00:05.000Z');
      const errorPart = transcript.turns[0].assistant.find(p => p.type === 'error') as any;
      expect(errorPart.kind).toBe('timeout');
      expect(errorPart.message).toBe('Session timed out');
    });

    it('should close turn on cancelled event', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response', '2024-01-01T10:00:01.000Z'),
        makeCancelledEvent('2024-01-01T10:00:05.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].completedAt).toBe('2024-01-01T10:00:05.000Z');
      const errorPart = transcript.turns[0].assistant.find(p => p.type === 'error') as any;
      expect(errorPart.kind).toBe('cancelled');
    });

    it('should close turn when session status is terminal', () => {
      const session = makeSession({ status: 'completed', completedAt: '2024-01-01T10:00:10.000Z' });
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].completedAt).toBe('2024-01-01T10:00:10.000Z');
    });

    it('should close turn on failed status with error message', () => {
      const session = makeSession({ status: 'failed', completedAt: '2024-01-01T10:00:10.000Z' });
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response', '2024-01-01T10:00:01.000Z'),
        makeCompletedEvent(false, 'Something went wrong', '2024-01-01T10:00:05.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].completedAt).toBe('2024-01-01T10:00:10.000Z');
      const errorPart = transcript.turns[0].assistant.find(p => p.type === 'error') as any;
      expect(errorPart.kind).toBe('failed');
      expect(errorPart.message).toBe('Something went wrong');
    });
  });

  describe('legacy fallback', () => {
    it('should create synthetic turn when there are assistant events but no prompt', () => {
      const session = makeSession();
      const events = [
        makeTextChunk('Legacy response', '2024-01-01T10:00:01.000Z'),
        makeToolCallStart('tc-1', 'Read', 'file.txt', '{}', '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(1);
      expect(transcript.turns[0].user.kind).toBe('legacy-missing');
      expect(transcript.turns[0].user.text).toBe('Prompt was not recorded for this historical session');
      expect(transcript.turns[0].incomplete).toBe(true);
      expect(transcript.incomplete).toBe(true);
    });

    it('should not create legacy turn if there are prompts', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Real prompt', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Real response', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(1);
      expect(transcript.turns[0].user.kind).toBe('task');
      expect(transcript.incomplete).toBe(false);
    });

    it('should include tool events in legacy turn', () => {
      const session = makeSession();
      const events = [
        makeToolCallStart('tc-1', 'Bash', 'ls', 'ls', '2024-01-01T10:00:01.000Z'),
        makeToolCallUpdate('tc-1', 'completed', 'file1.txt\nfile2.txt', undefined, '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns).toHaveLength(1);
      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.toolName).toBe('Bash');
      expect(toolPart.status).toBe('completed');
    });
  });

  describe('apply_patch summaries', () => {
    it('should parse apply_patch Add File operations', () => {
      const session = makeSession();
      const patchText = `Add File: src/new-file.ts
+ import { something } from './other';
+ export const foo = 1;`;
      const events = [
        makePromptEvent('Patch files', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-patch', 'apply_patch', 'src/new-file.ts', JSON.stringify({ patchText }), '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.changedFiles).toBeDefined();
      expect(toolPart.changedFiles).toHaveLength(1);
      expect(toolPart.changedFiles![0].path).toBe('src/new-file.ts');
      expect(toolPart.changedFiles![0].operation).toBe('created');
      expect(toolPart.changedFiles![0].additions).toBe(2);
    });

    it('should parse real apply_patch envelope headers', () => {
      const session = makeSession();
      const patchText = `*** Begin Patch
*** Add File: src/new-file.ts
+ export const foo = 1;
*** Update File: src/index.ts
- old line
+ new line
*** Delete File: src/obsolete.ts
*** OldPath: src/old-name.ts
*** Move to: src/renamed.ts
+ moved content
*** End Patch`;
      const events = [
        makePromptEvent('Patch files', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-patch', 'apply_patch', 'src/new-file.ts', JSON.stringify({ patchText }), '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.changedFiles).toEqual([
        { path: 'src/new-file.ts', operation: 'created', additions: 1, deletions: 0 },
        { path: 'src/index.ts', operation: 'modified', additions: 1, deletions: 1 },
        { path: 'src/obsolete.ts', operation: 'deleted', additions: 0, deletions: 0 },
        { path: 'src/renamed.ts', operation: 'moved', additions: 1, deletions: 0, oldPath: 'src/old-name.ts' },
      ]);
    });

    it('should parse apply_patch Update File operations', () => {
      const session = makeSession();
      const patchText = `Update File: src/index.ts
- old line
+ new line`;
      const events = [
        makePromptEvent('Patch files', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-patch', 'apply_patch', 'src/index.ts', JSON.stringify({ patchText }), '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.changedFiles).toBeDefined();
      expect(toolPart.changedFiles![0].operation).toBe('modified');
      expect(toolPart.changedFiles![0].additions).toBe(1);
      expect(toolPart.changedFiles![0].deletions).toBe(1);
    });

    it('should parse apply_patch Delete File operations', () => {
      const session = makeSession();
      const patchText = `Delete File: src/obsolete.ts`;
      const events = [
        makePromptEvent('Patch files', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-patch', 'apply_patch', 'src/obsolete.ts', JSON.stringify({ patchText }), '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.changedFiles).toBeDefined();
      expect(toolPart.changedFiles![0].operation).toBe('deleted');
    });

    it('should parse apply_patch Move to operations', () => {
      const session = makeSession();
      const patchText = `Move to: src/renamed.ts
+ new content here`;
      const events = [
        makePromptEvent('Patch files', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-patch', 'apply_patch', 'src/renamed.ts', JSON.stringify({ patchText }), '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.changedFiles).toBeDefined();
      expect(toolPart.changedFiles![0].operation).toBe('moved');
    });

    it('should handle apply_patch with title-only tool identity', () => {
      const session = makeSession();
      const patchText = `Add File: src/brand-new.ts
+ export const value = 42;`;
      const events: SessionStreamLogEntry[] = [
        makePromptEvent('Patch files', 'task', '2024-01-01T10:00:00.000Z'),
        { id: 'evt-patch', sessionId: 'session-1', issueId: 'issue-1', eventType: 'tool_call', data: JSON.stringify({
          toolCall: {
            title: 'apply_patch',
            input: { patchText },
            status: 'started',
            createdAt: '2024-01-01T10:00:01.000Z',
          },
        }), createdAt: '2024-01-01T10:00:01.000Z' },
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].assistant).toHaveLength(1);
      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.changedFiles).toBeDefined();
      expect(toolPart.changedFiles![0].path).toBe('src/brand-new.ts');
      expect(toolPart.changedFiles![0].operation).toBe('created');
    });

    it('should accumulate changed files in session metadata', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Patch files', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-patch1', 'apply_patch', 'src/a.ts', JSON.stringify({ patchText: 'Add File: src/a.ts\n+ content' }), '2024-01-01T10:00:01.000Z'),
        makeToolCallStart('tc-patch2', 'apply_patch', 'src/b.ts', JSON.stringify({ patchText: 'Add File: src/b.ts\n+ content' }), '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.session.changedFiles).toBeDefined();
      expect(transcript.session.changedFiles).toHaveLength(2);
      expect(transcript.session.changedFiles!.some((f: FileChangeSummary) => f.path === 'src/a.ts')).toBe(true);
      expect(transcript.session.changedFiles!.some((f: FileChangeSummary) => f.path === 'src/b.ts')).toBe(true);
    });
  });

  describe('transcript metadata', () => {
    it('should include lastActivityAt from most recent event', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response', '2024-01-01T10:00:05.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.session.lastActivityAt).toBe('2024-01-01T10:00:05.000Z');
    });

    it('should include eventCount in metadata', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Response', '2024-01-01T10:00:01.000Z'),
        makeToolCallStart('tc-1', 'Read', 'file.txt', '{}', '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.session.eventCount).toBe(3);
    });

    it('should include toolCount in metadata', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-1', 'Read', 'file1.txt', '{}', '2024-01-01T10:00:01.000Z'),
        makeToolCallUpdate('tc-1', 'completed', 'ok', undefined, '2024-01-01T10:00:02.000Z'),
        makeToolCallStart('tc-2', 'Bash', 'ls', '{}', '2024-01-01T10:00:03.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.session.toolCount).toBe(2);
    });

    it('should include turnCount in metadata', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('First', 'initial', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('First response', '2024-01-01T10:00:01.000Z'),
        makePromptEvent('Second', 'retry', '2024-01-01T10:01:00.000Z'),
        makeTextChunk('Second response', '2024-01-01T10:01:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.session.turnCount).toBe(2);
    });

    it('should include warnings in transcript when tools could not be normalized', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-unknown', 'some-random-tool', undefined, '{}', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.session.warnings).toBeDefined();
      expect(transcript.session.warnings!.length).toBeGreaterThan(0);
      expect(transcript.session.hasUnknownTools).toBe(true);
    });

    it('should not include warnings when all tools are normalized', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Hello', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-1', 'Read', 'file.txt', '{"file_path":"file.txt"}', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.session.warnings).toBeUndefined();
      expect(transcript.session.hasUnknownTools).toBe(false);
    });
  });

  describe('prompt summary', () => {
    it('should extract output path from contract tag', () => {
      const session = makeSession();
      const events = [
        makePromptEvent(`<contract>packages/cli/src/index.ts</contract>\n<role>Implement feature</role>`, 'task', '2024-01-01T10:00:00.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].user.summary).toBeDefined();
      expect(transcript.turns[0].user.summary!.outputPath).toBe('packages/cli/src/index.ts');
    });

    it('should extract context files from context_files tag', () => {
      const session = makeSession();
      const events = [
        makePromptEvent(`<context_files>\nsrc/a.ts\nsrc/b.ts\ndocs/readme.md\n</context_files>`, 'task', '2024-01-01T10:00:00.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].user.summary).toBeDefined();
      expect(transcript.turns[0].user.summary!.contextFiles).toBeDefined();
      expect(transcript.turns[0].user.summary!.contextFiles).toContain('src/a.ts');
      expect(transcript.turns[0].user.summary!.contextFiles).toContain('src/b.ts');
    });

    it('should preserve rawText in prompt summary', () => {
      const session = makeSession();
      const promptText = `<role>Implement feature X</role>\n<task>Do something</task>`;
      const events = [
        makePromptEvent(promptText, 'task', '2024-01-01T10:00:00.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      expect(transcript.turns[0].user.summary!.rawText).toBe(promptText);
    });
  });

  describe('tool lifecycle normalization', () => {
    it('should produce exactly one ToolPart when tool_call plus tool_call_update share the same call id', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Read a file', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-same-id', 'Read', 'src/index.ts', '{"file_path":"src/index.ts"}', '2024-01-01T10:00:01.000Z'),
        makeToolCallUpdate('tc-same-id', 'completed', 'file contents', undefined, '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      const toolParts = transcript.turns[0].assistant.filter(p => p.type === 'tool') as ToolPart[];
      expect(toolParts).toHaveLength(1);
      expect(toolParts[0].tool.toolCallId).toBe('tc-same-id');
      expect(toolParts[0].tool.status).toBe('completed');
      expect(toolParts[0].tool.output).toBe('file contents');
      expect(toolParts[0].tool.completedAt).toBe('2024-01-01T10:00:02.000Z');
    });

    it('should merge update-only event into pending tool by normalized name plus title', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Read files', 'task', '2024-01-01T10:00:00.000Z'),
        makeEvent('tool_call', {
          toolCall: {
            toolName: 'Read',
            title: 'file-a.txt',
            input: { file_path: 'file-a.txt' },
            status: 'started',
            createdAt: '2024-01-01T10:00:01.000Z',
          },
        }, '2024-01-01T10:00:01.000Z'),
        makeEvent('tool_call_update', {
          toolCall: {
            toolName: 'Read',
            title: 'file-a.txt',
            output: 'contents of file-a',
            status: 'completed',
            createdAt: '2024-01-01T10:00:02.000Z',
          },
        }, '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      const toolParts = transcript.turns[0].assistant.filter(p => p.type === 'tool') as ToolPart[];
      expect(toolParts).toHaveLength(1);
      expect(toolParts[0].tool.status).toBe('completed');
      expect(toolParts[0].tool.output).toBe('contents of file-a');
      expect(toolParts[0].tool.input).toBe('{"file_path":"file-a.txt"}');
    });

    it('should not create orphan unknown running entry for inferable update-only payloads', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Gather context', 'task', '2024-01-01T10:00:00.000Z'),
        makeEvent('tool_call', {
          toolCall: {
            name: 'read',
            title: 'src/config.ts',
            input: { file_path: 'src/config.ts' },
            status: 'started',
            createdAt: '2024-01-01T10:00:01.000Z',
          },
        }, '2024-01-01T10:00:01.000Z'),
        makeEvent('tool_call_update', {
          toolCall: {
            name: 'read',
            title: 'src/config.ts',
            output: { content: 'database url = postgres://...' },
            status: 'completed',
            createdAt: '2024-01-01T10:00:02.000Z',
          },
        }, '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      const toolParts = transcript.turns[0].assistant.filter(p => p.type === 'tool') as ToolPart[];
      expect(toolParts).toHaveLength(1);
      expect(toolParts[0].tool.normalizedName).toBe('read');
      expect(toolParts[0].tool.status).toBe('completed');
    });

    it('should normalize status started to running for display', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Run task', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-1', 'Bash', 'npm test', '{"command":"npm test"}', '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.status).toBe('running');
    });

    it('should normalize status pending when no terminal status is provided', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Do something', 'task', '2024-01-01T10:00:00.000Z'),
        makeEvent('tool_call_update', {
          toolCallId: 'tc-pending',
          toolName: 'Read',
          createdAt: '2024-01-01T10:00:01.000Z',
        }, '2024-01-01T10:00:01.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.status).toBe('pending');
    });

    it('should normalize status cancelled when update provides cancelled status', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Run task', 'task', '2024-01-01T10:00:00.000Z'),
        makeToolCallStart('tc-1', 'Bash', 'npm test', '{"command":"npm test"}', '2024-01-01T10:00:01.000Z'),
        makeToolCallUpdate('tc-1', 'cancelled', undefined, 'User cancelled', '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      const toolPart = (transcript.turns[0].assistant[0] as ToolPart).tool;
      expect(toolPart.status).toBe('cancelled');
      expect(toolPart.error).toBe('User cancelled');
    });

    it('should normalize status running when update provides running status', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Run task', 'task', '2024-01-01T10:00:00.000Z'),
        makeEvent('tool_call', {
          toolCallId: 'tc-run',
          toolName: 'Bash',
          title: 'npm test',
          status: 'started',
          createdAt: '2024-01-01T10:00:01.000Z',
        }, '2024-01-01T10:00:01.000Z'),
        makeEvent('tool_call_update', {
          toolCallId: 'tc-run',
          status: 'running',
          output: 'Running tests...',
          createdAt: '2024-01-01T10:00:02.000Z',
        }, '2024-01-01T10:00:02.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      const toolParts = transcript.turns[0].assistant.filter(p => p.type === 'tool') as ToolPart[];
      expect(toolParts).toHaveLength(1);
      expect(toolParts[0].tool.status).toBe('running');
      expect(toolParts[0].tool.output).toBe('Running tests...');
    });

    it('should not create tool part for internal lifecycle events', () => {
      const session = makeSession();
      const events = [
        makePromptEvent('Run task', 'task', '2024-01-01T10:00:00.000Z'),
        makeTextChunk('Starting work...', '2024-01-01T10:00:01.000Z'),
        makeEvent('acp_session_step', { step: 1, phase: 'execute' }, '2024-01-01T10:00:02.000Z'),
        makeEvent('acp_session_heartbeat', { active: true }, '2024-01-01T10:00:03.000Z'),
        makeEvent('acp_bookkeeping', { kind: 'stats', cpu: 45 }, '2024-01-01T10:00:04.000Z'),
        makeTextChunk('Work complete.', '2024-01-01T10:00:05.000Z'),
      ];

      const transcript = assembleSessionTranscript(session, events);

      const toolParts = transcript.turns[0].assistant.filter(p => p.type === 'tool');
      expect(toolParts).toHaveLength(0);
      const textParts = transcript.turns[0].assistant.filter(p => p.type === 'text');
      expect(textParts).toHaveLength(1);
      expect((textParts[0] as any).text).toBe('Starting work...Work complete.');
    });
  });
});
