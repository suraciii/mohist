import { describe, it, expect, beforeEach } from 'vitest';
import { assembleSessionTranscript, type SessionTranscript, type SessionTurn, type SessionPart, type ToolPart } from '../src/services/session-transcript-service';
import type { SessionStreamLogEntry } from '../src/db/session-stream-log-repo';
import type { CoderSession } from '../src/db/coder-session-repo';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { SessionStreamLogRepo } from '../src/db/session-stream-log-repo';

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
      expect(toolPart.status).toBe('started');
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
      expect(toolPart.toolCallId).toBe('session-Read-2024-01-01T10:00:01.000Z');
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
      expect(toolPart.toolCallId).toBe('session-Bash-2024-01-01T10:00:01.000Z');
      expect(toolPart.status).toBe('completed');
      expect(toolPart.output).toBe('ok');
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
});
