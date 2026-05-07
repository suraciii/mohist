import type { SessionStreamLogEntry } from '../db/session-stream-log-repo';
import type { CoderSession } from '../db/coder-session-repo';

export type PromptKind = 'initial' | 'task' | 'retry' | 'followup' | 'recovery' | 'legacy-missing';

export interface SessionMetadata {
  sessionId: string;
  issueId: string;
  acpSessionId: string;
  executionId: string | null;
  title: string | null;
  status: string;
  model: string | null;
  stage: string | null;
  createdAt: string;
  completedAt: string | null;
}

export interface SessionTranscript {
  session: SessionMetadata;
  turns: SessionTurn[];
  incomplete: boolean;
}

export interface SessionTurn {
  id: string;
  startedAt: string;
  completedAt: string | null;
  incomplete?: boolean;
  user: {
    role: 'mohist';
    text: string;
    kind: PromptKind;
    sentAt: string;
  };
  assistant: SessionPart[];
}

export type SessionPart =
  | TextPart
  | ReasoningPart
  | ToolPart
  | ErrorPart;

export interface TextPart {
  id: string;
  type: 'text';
  text: string;
  startedAt: string;
  completedAt: string | null;
}

export interface ReasoningPart {
  id: string;
  type: 'reasoning';
  text: string;
  startedAt: string;
  completedAt: string | null;
}

export interface ToolPart {
  id: string;
  type: 'tool';
  tool: {
    toolCallId: string;
    toolName: string;
    status: 'started' | 'completed' | 'failed';
    title?: string;
    target?: string;
    input?: string;
    output?: string;
    error?: string;
    startedAt: string;
    completedAt?: string | null;
  };
}

export interface ErrorPart {
  id: string;
  type: 'error';
  message: string;
  kind: 'timeout' | 'failed' | 'cancelled' | 'recovery';
  at: string;
}

interface RawEvent {
  eventType: string;
  data: Record<string, unknown>;
  createdAt: string;
}

const TERMINAL_STATUSES = new Set(['completed', 'failed', 'timeout', 'cancelled']);

function generateId(): string {
  return `part-${Date.now()}-${Math.random().toString(36).slice(2, 9)}`;
}

function parseMohistPromptEvent(data: Record<string, unknown>): { text: string; kind: PromptKind; sentAt: string; title?: string } | null {
  if (typeof data !== 'object' || data === null) return null;
  const d = data as Record<string, unknown>;
  if (d.role !== 'mohist') return null;
  const text = typeof d.text === 'string' ? d.text : '';
  const kind = typeof d.kind === 'string' ? d.kind as PromptKind : 'task';
  const sentAt = typeof d.sentAt === 'string' ? d.sentAt : String(d.createdAt ?? new Date().toISOString());
  const title = typeof d.title === 'string' ? d.title : undefined;
  return { text, kind, sentAt, title };
}

function parseAgentMessageChunk(data: Record<string, unknown>): string | null {
  if (typeof data !== 'object' || data === null) return null;
  const d = data as Record<string, unknown>;
  if (d.content && typeof d.content === 'object' && d.content !== null) {
    const content = d.content as Record<string, unknown>;
    if (typeof content.text === 'string') return content.text;
  }
  if (typeof d.text === 'string') return d.text;
  return null;
}

function parseAgentThoughtChunk(data: Record<string, unknown>): string | null {
  return parseAgentMessageChunk(data);
}

interface ToolCallStartData {
  toolCallId: string;
  toolName: string;
  title?: string;
  input?: string;
  createdAt: string;
}

interface ToolCallUpdateData {
  toolCallId: string;
  status?: string;
  title?: string;
  input?: string;
  output?: string;
  error?: string;
  createdAt: string;
}

function parseToolCallStart(data: Record<string, unknown>): ToolCallStartData | null {
  if (typeof data !== 'object' || data === null) return null;
  const d = data as Record<string, unknown>;
  const toolCallId = typeof d.toolCallId === 'string' ? d.toolCallId : '';
  if (!toolCallId) return null;
  const toolName = typeof d.toolName === 'string' ? d.toolName : (typeof d.name === 'string' ? d.name : 'unknown');
  const title = typeof d.title === 'string' ? d.title : undefined;
  const rawInput = d.rawInput ?? d.input;
  const input = rawInput !== undefined ? (typeof rawInput === 'string' ? rawInput : JSON.stringify(rawInput)) : undefined;
  return { toolCallId, toolName, title, input, createdAt: String(d.createdAt ?? new Date().toISOString()) };
}

function parseToolCallUpdate(data: Record<string, unknown>): ToolCallUpdateData | null {
  if (typeof data !== 'object' || data === null) return null;
  const d = data as Record<string, unknown>;
  const toolCallId = typeof d.toolCallId === 'string' ? d.toolCallId : '';
  if (!toolCallId) return null;
  const status = typeof d.status === 'string' ? d.status : undefined;
  const title = typeof d.title === 'string' ? d.title : undefined;
  const rawInput = d.rawInput ?? d.input;
  const input = rawInput !== undefined ? (typeof rawInput === 'string' ? rawInput : JSON.stringify(rawInput)) : undefined;
  const rawOutput = d.rawOutput ?? d.output;
  const output = rawOutput !== undefined ? (typeof rawOutput === 'string' ? rawOutput : JSON.stringify(rawOutput)) : undefined;
  const error = typeof d.error === 'string' ? d.error : undefined;
  return { toolCallId, status, title, input, output, error, createdAt: String(d.createdAt ?? new Date().toISOString()) };
}

function deriveToolTarget(toolName: string, input: string | undefined): string | undefined {
  if (!input) return undefined;
  try {
    const parsed = JSON.parse(input);
    if (typeof parsed !== 'object' || parsed === null) return undefined;
    const lower = toolName.toLowerCase();
    if (['read', 'read_file', 'write', 'write_file', 'edit'].includes(lower)) {
      const fp = parsed.file_path ?? parsed.filePath ?? parsed.path;
      if (typeof fp === 'string' && fp) return fp.split('/').pop() ?? fp;
    }
    if (lower === 'bash') {
      const cmd = parsed.command ?? parsed.script;
      if (typeof cmd === 'string' && cmd) return cmd.length > 60 ? cmd.slice(0, 57) + '...' : cmd;
    }
    if (['glob', 'search_files', 'grep', 'search'].includes(lower)) {
      const pat = parsed.pattern ?? parsed.query ?? parsed.search;
      if (typeof pat === 'string' && pat) return pat;
    }
  } catch {
    return undefined;
  }
  return undefined;
}

interface ActiveParts {
  textPart: TextPart | null;
  reasoningPart: ReasoningPart | null;
}

export class SessionTranscriptAssembler {
  private session: SessionMetadata;
  private turns: SessionTurn[] = [];
  private currentTurn: SessionTurn | null = null;
  private activeParts: ActiveParts = { textPart: null, reasoningPart: null };
  private toolPartsById: Map<string, ToolPart> = new Map();
  private incomplete: boolean = false;
  private hasReceivedPrompt: boolean = false;

  constructor(session: CoderSession) {
    this.session = {
      sessionId: session.id,
      issueId: session.issueId,
      acpSessionId: session.acpSessionId,
      executionId: session.executionId,
      title: session.title,
      status: session.status,
      model: session.model,
      stage: session.stage,
      createdAt: session.createdAt,
      completedAt: session.completedAt,
    };
  }

  assemble(events: SessionStreamLogEntry[]): SessionTranscript {
    const orderedEvents = this.sortEvents(events);

    for (const entry of orderedEvents) {
      const data = typeof entry.data === 'string' ? JSON.parse(entry.data) : entry.data;
      this.processEvent({
        eventType: entry.eventType,
        data: data as Record<string, unknown>,
        createdAt: entry.createdAt,
      });
    }

    this.finalizeTerminalState();

    if (!this.hasReceivedPrompt && this.currentTurn === null && this.turns.length === 0) {
      this.createLegacyTurn();
    }

    if (TERMINAL_STATUSES.has(this.session.status)) {
      this.closeOpenTurn(this.session.completedAt ?? new Date().toISOString());
    }

    return {
      session: this.session,
      turns: this.turns,
      incomplete: this.incomplete,
    };
  }

  private sortEvents(events: SessionStreamLogEntry[]): SessionStreamLogEntry[] {
    return [...events].sort((a, b) => {
      const timeA = new Date(a.createdAt).getTime();
      const timeB = new Date(b.createdAt).getTime();
      if (timeA !== timeB) return timeA - timeB;
      return a.id.localeCompare(b.id);
    });
  }

  private processEvent(event: RawEvent): void {
    const { eventType, data, createdAt } = event;

    if (eventType === 'mohist_prompt') {
      this.handleMohistPrompt(data, createdAt);
      return;
    }

    if (eventType === 'agent_message_chunk') {
      const text = parseAgentMessageChunk(data);
      if (text) this.handleTextChunk(text, createdAt);
      return;
    }

    if (eventType === 'agent_thought_chunk') {
      const text = parseAgentThoughtChunk(data);
      if (text) this.handleReasoningChunk(text, createdAt);
      return;
    }

    if (eventType === 'tool_call') {
      const start = parseToolCallStart(data);
      if (start) this.handleToolCallStart(start);
      return;
    }

    if (eventType === 'tool_call_update') {
      const update = parseToolCallUpdate(data);
      if (update) this.handleToolCallUpdate(update);
      return;
    }

    if (eventType === 'acp_session_timeout') {
      this.handleError('timeout', 'Session timed out', createdAt);
      return;
    }

    if (eventType === 'acp_session_aborted' || eventType === 'cancel') {
      this.handleError('cancelled', 'Session was cancelled', createdAt);
      return;
    }

    if (eventType === 'acp_session_recovery_started') {
      this.handleError('recovery', 'Session recovery started', createdAt);
      return;
    }

    if (eventType === 'acp_session_recovery_succeeded') {
      this.handleError('recovery', 'Session recovered', createdAt);
      return;
    }

    if (eventType === 'acp_session_recovery_failed') {
      this.handleError('failed', 'Session recovery failed', createdAt);
      return;
    }

    if (eventType === 'acp_session_completed') {
      const success = data.success === false ? false : true;
      if (!success && data.error) {
        this.handleError('failed', String(data.error), createdAt);
      }
      return;
    }
  }

  private handleMohistPrompt(data: Record<string, unknown>, createdAt: string): void {
    this.closeOpenTurn(createdAt);

    const parsed = parseMohistPromptEvent(data);
    if (!parsed) return;

    this.hasReceivedPrompt = true;
    const turnId = generateId();
    this.currentTurn = {
      id: turnId,
      startedAt: parsed.sentAt,
      completedAt: null,
      user: {
        role: 'mohist',
        text: parsed.text,
        kind: parsed.kind,
        sentAt: parsed.sentAt,
      },
      assistant: [],
    };
    this.turns.push(this.currentTurn);
    this.activeParts = { textPart: null, reasoningPart: null };
    this.toolPartsById.clear();
  }

  private handleTextChunk(text: string, createdAt: string): void {
    this.ensureActiveTurn(createdAt);

    if (this.activeParts.textPart) {
      this.activeParts.textPart.text += text;
    } else {
      const textPart: TextPart = {
        id: generateId(),
        type: 'text',
        text,
        startedAt: createdAt,
        completedAt: null,
      };
      this.activeParts.textPart = textPart;
      this.currentTurn!.assistant.push(textPart);
    }
  }

  private handleReasoningChunk(text: string, createdAt: string): void {
    this.ensureActiveTurn(createdAt);

    if (this.activeParts.reasoningPart) {
      this.activeParts.reasoningPart.text += text;
    } else {
      const reasoningPart: ReasoningPart = {
        id: generateId(),
        type: 'reasoning',
        text,
        startedAt: createdAt,
        completedAt: null,
      };
      this.activeParts.reasoningPart = reasoningPart;
      this.currentTurn!.assistant.push(reasoningPart);
    }
  }

  private handleToolCallStart(start: ToolCallStartData): void {
    if (!this.currentTurn) {
      this.ensureActiveTurn(start.createdAt);
    }

    const toolPart: ToolPart = {
      id: generateId(),
      type: 'tool',
      tool: {
        toolCallId: start.toolCallId,
        toolName: start.toolName,
        status: 'started',
        title: start.title,
        target: deriveToolTarget(start.toolName, start.input),
        input: start.input,
        startedAt: start.createdAt,
        completedAt: null,
      },
    };
    this.toolPartsById.set(start.toolCallId, toolPart);
    this.currentTurn!.assistant.push(toolPart);
  }

  private handleToolCallUpdate(update: ToolCallUpdateData): void {
    const existing = this.toolPartsById.get(update.toolCallId);
    if (existing) {
      if (update.status) {
        existing.tool.status = update.status === 'completed' ? 'completed'
          : update.status === 'failed' ? 'failed'
          : existing.tool.status;
      }
      if (update.title !== undefined) existing.tool.title = update.title;
      if (update.input !== undefined) existing.tool.input = update.input;
      if (update.output !== undefined) existing.tool.output = update.output;
      if (update.error !== undefined) existing.tool.error = update.error;
      if (update.status === 'completed' || update.status === 'failed') {
        existing.tool.completedAt = update.createdAt;
      }
      if (existing.tool.target === undefined && existing.tool.input) {
        existing.tool.target = deriveToolTarget(existing.tool.toolName, existing.tool.input);
      }
    } else {
      const toolPart: ToolPart = {
        id: generateId(),
        type: 'tool',
        tool: {
          toolCallId: update.toolCallId,
          toolName: 'unknown',
          status: update.status === 'completed' ? 'completed'
            : update.status === 'failed' ? 'failed'
            : 'started',
          output: update.output,
          error: update.error,
          startedAt: update.createdAt,
          completedAt: update.status ? update.createdAt : null,
        },
      };
      this.toolPartsById.set(update.toolCallId, toolPart);
      if (this.currentTurn) {
        this.currentTurn.assistant.push(toolPart);
      }
    }
  }

  private handleError(kind: ErrorPart['kind'], message: string, createdAt: string): void {
    if (!this.currentTurn) {
      this.ensureActiveTurn(createdAt);
    }

    const errorPart: ErrorPart = {
      id: generateId(),
      type: 'error',
      message,
      kind,
      at: createdAt,
    };
    this.currentTurn!.assistant.push(errorPart);

    if (kind === 'timeout' || kind === 'failed' || kind === 'cancelled') {
      this.closeOpenTurn(createdAt);
    }
  }

  private ensureActiveTurn(createdAt: string): void {
    if (this.currentTurn) return;

    if (!this.hasReceivedPrompt) {
      this.hasReceivedPrompt = false;
    }

    const turnId = generateId();
    this.currentTurn = {
      id: turnId,
      startedAt: createdAt,
      completedAt: null,
      incomplete: true,
      user: {
        role: 'mohist',
        text: 'Prompt was not recorded for this historical session',
        kind: 'legacy-missing',
        sentAt: createdAt,
      },
      assistant: [],
    };
    this.turns.push(this.currentTurn);
    this.activeParts = { textPart: null, reasoningPart: null };
    this.incomplete = true;
  }

  private createLegacyTurn(): void {
    if (this.hasReceivedPrompt) return;

    const turnId = generateId();
    const createdAt = this.session.createdAt;
    this.currentTurn = {
      id: turnId,
      startedAt: createdAt,
      completedAt: null,
      incomplete: true,
      user: {
        role: 'mohist',
        text: 'Prompt was not recorded for this historical session',
        kind: 'legacy-missing',
        sentAt: createdAt,
      },
      assistant: [],
    };

    for (const toolPart of this.toolPartsById.values()) {
      this.currentTurn.assistant.push(toolPart);
    }
    this.toolPartsById.clear();

    this.turns.push(this.currentTurn);
    this.incomplete = true;
  }

  private closeOpenTurn(completedAt: string): void {
    if (this.currentTurn && this.currentTurn.completedAt === null) {
      const completedTime = new Date(completedAt).getTime();
      const startedTime = new Date(this.currentTurn.startedAt).getTime();
      if (completedTime >= startedTime) {
        this.currentTurn.completedAt = completedAt;
        if (this.activeParts.textPart) {
          this.activeParts.textPart.completedAt = completedAt;
          this.activeParts.textPart = null;
        }
        if (this.activeParts.reasoningPart) {
          this.activeParts.reasoningPart.completedAt = completedAt;
          this.activeParts.reasoningPart = null;
        }
      }
      this.currentTurn = null;
    }
  }

  private finalizeTerminalState(): void {
    if (TERMINAL_STATUSES.has(this.session.status)) {
      this.closeOpenTurn(this.session.completedAt ?? new Date().toISOString());
    }
  }
}

export function assembleSessionTranscript(
  session: CoderSession,
  events: SessionStreamLogEntry[],
): SessionTranscript {
  const assembler = new SessionTranscriptAssembler(session);
  return assembler.assemble(events);
}
