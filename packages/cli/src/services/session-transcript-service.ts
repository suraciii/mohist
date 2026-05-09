import type { SessionStreamLogEntry } from '../db/session-stream-log-repo';
import type { CoderSession } from '../db/coder-session-repo';

export type PromptKind = 'initial' | 'task' | 'retry' | 'followup' | 'recovery' | 'legacy-missing';

export interface FileChangeSummary {
  path: string;
  operation: 'created' | 'modified' | 'deleted' | 'moved';
  additions?: number;
  deletions?: number;
  oldPath?: string;
  rawDetail?: string;
}

export interface TranscriptWarning {
  code: string;
  message: string;
}

export interface SessionMetadata {
  sessionId: string;
  coderSessionId: string;
  issueId: string;
  acpSessionId: string;
  executionId: string | null;
  title: string | null;
  status: string;
  statusKind?: 'loading' | 'live' | 'finalizing' | 'completed' | 'failed' | 'stale';
  model: string | null;
  stage: string | null;
  createdAt: string;
  completedAt: string | null;
  cwd?: string | null;
  worktree?: string | null;
  firstPromptSentAt?: string | null;
  lastActivityAt?: string | null;
  eventCount?: number;
  toolCount?: number;
  turnCount?: number;
  changedFiles?: FileChangeSummary[];
  warnings?: TranscriptWarning[];
  hasUnknownTools?: boolean;
}

export interface SessionTranscript {
  session: SessionMetadata;
  turns: SessionTurn[];
  incomplete: boolean;
}

export interface PromptSummary {
  title?: string;
  subtitle?: string;
  outputPath?: string;
  contextFiles?: string[];
  kind: PromptKind;
  rawText?: string;
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
    summary?: PromptSummary;
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
    normalizedName?: string;
    displayTitle?: string;
    displaySubtitle?: string;
    category?: string;
    toolName: string;
    status: 'pending' | 'running' | 'completed' | 'failed' | 'cancelled';
    title?: string;
    target?: string;
    input?: string;
    output?: string;
    error?: string;
    startedAt: string;
    completedAt?: string | null;
    rawInput?: string;
    rawOutput?: string;
    metadata?: Record<string, unknown>;
    changedFiles?: FileChangeSummary[];
    warnings?: TranscriptWarning[];
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
  id: string;
  eventType: string;
  data: Record<string, unknown>;
  createdAt: string;
}

const TERMINAL_STATUSES = new Set(['completed', 'failed', 'timeout', 'cancelled']);

const EVENT_PRIORITY: Record<string, number> = {
  mohist_prompt: 0,
  agent_thought_chunk: 1,
  agent_message_chunk: 1,
  tool_call: 2,
  tool_call_update: 3,
  acp_session_recovery_started: 4,
  acp_session_recovery_failed: 4,
  acp_session_recovery_succeeded: 4,
  acp_session_timeout: 5,
  acp_session_aborted: 5,
  cancel: 5,
  acp_session_completed: 5,
};

function stringifyPayload(value: unknown): string | undefined {
  if (value === undefined) return undefined;
  return typeof value === 'string' ? value : JSON.stringify(value);
}

function getObject(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null ? value as Record<string, unknown> : null;
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
  status?: string;
  title?: string;
  input?: string;
  output?: string;
  error?: string;
  createdAt: string;
  rawInput?: string;
  rawOutput?: string;
  metadata?: Record<string, unknown>;
}

interface ToolCallUpdateData {
  toolCallId: string;
  toolName?: string;
  status?: string;
  title?: string;
  input?: string;
  output?: string;
  error?: string;
  createdAt: string;
  rawInput?: string;
  rawOutput?: string;
  metadata?: Record<string, unknown>;
}

function syntheticToolCallId(data: Record<string, unknown>, fallbackCreatedAt: string): string {
  const sessionId = typeof data.sessionId === 'string' ? data.sessionId
    : typeof data.acpSessionId === 'string' ? data.acpSessionId
      : 'session';
  const toolName = typeof data.toolName === 'string' ? data.toolName
    : typeof data.name === 'string' ? data.name
      : 'unknown';
  const sequence = typeof data.sequence === 'number' || typeof data.sequence === 'string'
    ? String(data.sequence)
    : typeof data.createdAt === 'string' ? data.createdAt.replace(/[^0-9]/g, '').slice(0, 12)
      : fallbackCreatedAt.replace(/[^0-9]/g, '').slice(0, 12);
  return `${sessionId}-${toolName}-${sequence}`;
}

function inferNormalizedToolName(d: Record<string, unknown>): string {
  if (typeof d.toolName === 'string' && d.toolName && d.toolName !== 'unknown' && isKnownToolName(d.toolName)) return d.toolName;
  if (typeof d.name === 'string' && d.name && isKnownToolName(d.name)) return d.name;
  if (typeof d.title === 'string' && d.title) {
    const titleLower = d.title.toLowerCase();
    if (['apply_patch', 'edit', 'write', 'read', 'glob', 'grep', 'bash', 'list', 'search'].some(t => titleLower.includes(t))) {
      return d.title;
    }
  }
  const rawInput = d.rawInput ?? d.input;
  if (rawInput) {
    if (typeof rawInput === 'object' && rawInput !== null) {
      const inputObj = rawInput as Record<string, unknown>;
      if (inputObj.patchText !== undefined) return 'apply_patch';
      if (inputObj.command !== undefined || inputObj.script !== undefined) return 'bash';
      if (inputObj.pattern !== undefined || inputObj.query !== undefined || inputObj.search !== undefined) {
        if (inputObj.file_path !== undefined) return 'grep';
        return 'search';
      }
      if (inputObj.file_path !== undefined || inputObj.path !== undefined) return 'read';
      if (inputObj.pattern !== undefined) return 'glob';
      if (inputObj.todos !== undefined) return 'todowrite';
    } else if (typeof rawInput === 'string') {
      try {
        const parsed = JSON.parse(rawInput);
        if (parsed && typeof parsed === 'object' && parsed !== null) {
          const inputObj = parsed as Record<string, unknown>;
          if (inputObj.patchText !== undefined) return 'apply_patch';
          if (inputObj.command !== undefined || inputObj.script !== undefined) return 'bash';
          if (inputObj.pattern !== undefined || inputObj.query !== undefined || inputObj.search !== undefined) {
            if (inputObj.file_path !== undefined) return 'grep';
            return 'search';
          }
          if (inputObj.file_path !== undefined || inputObj.path !== undefined) return 'read';
          if (inputObj.pattern !== undefined) return 'glob';
          if (inputObj.todos !== undefined) return 'todowrite';
        }
      } catch {
      }
    }
  }
  const rawOutput = d.rawOutput ?? d.output;
  if (rawOutput) {
    if (typeof rawOutput === 'string') {
      try {
        const parsed = JSON.parse(rawOutput);
        if (parsed && typeof parsed === 'object' && parsed !== null) {
          const outputObj = parsed as Record<string, unknown>;
          if (outputObj.metadata && typeof outputObj.metadata === 'object') {
            const meta = (outputObj.metadata as Record<string, unknown>);
            if (typeof meta.toolName === 'string') return meta.toolName;
            if (typeof meta.name === 'string') return meta.name;
            if (typeof meta.title === 'string') return meta.title;
          }
        }
      } catch {
      }
    } else if (typeof rawOutput === 'object' && rawOutput !== null) {
      const outputObj = rawOutput as Record<string, unknown>;
      if (outputObj.metadata && typeof outputObj.metadata === 'object') {
        const meta = (outputObj.metadata as Record<string, unknown>);
        if (typeof meta.toolName === 'string') return meta.toolName;
        if (typeof meta.name === 'string') return meta.name;
        if (typeof meta.title === 'string') return meta.title;
      }
    }
  }
  if (typeof d.metadata === 'object' && d.metadata !== null) {
    const meta = d.metadata as Record<string, unknown>;
    if (typeof meta.toolName === 'string') return meta.toolName;
    if (typeof meta.name === 'string') return meta.name;
    if (typeof meta.title === 'string') return meta.title;
  }
  return 'unknown';
}

function isKnownToolName(toolName: string): boolean {
  const lower = toolName.toLowerCase();
  return [
    'apply_patch', 'edit', 'write', 'write_file',
    'read', 'read_file', 'glob', 'grep', 'bash', 'shell',
    'list', 'search', 'membrowse', 'memread', 'memsearch',
    'todowrite', 'todo',
  ].includes(lower);
}

function parseToolCallStart(data: Record<string, unknown>, fallbackCreatedAt: string): ToolCallStartData | null {
  if (typeof data !== 'object' || data === null) return null;
  const d = getObject(data.toolCall) ?? data;
  const toolCallId = typeof d.toolCallId === 'string' ? d.toolCallId
    : typeof d.id === 'string' ? d.id
      : typeof d.callId === 'string' ? d.callId
        : syntheticToolCallId(d, fallbackCreatedAt);
  const toolName = typeof d.toolName === 'string' ? d.toolName : (typeof d.name === 'string' ? d.name : 'unknown');
  const status = typeof d.status === 'string' ? d.status : undefined;
  const title = typeof d.title === 'string' ? d.title : undefined;
  const rawInput = d.rawInput ?? d.input;
  const input = stringifyPayload(rawInput);
  const rawOutput = d.rawOutput ?? d.output;
  const output = stringifyPayload(rawOutput);
  const error = typeof d.error === 'string' ? d.error : undefined;
  const metadata = getObject(d.metadata) ?? (rawOutput && typeof rawOutput === 'object' ? getObject((rawOutput as Record<string, unknown>).metadata) : null);
  return { toolCallId, toolName, status, title, input, output, error, createdAt: String(d.createdAt ?? fallbackCreatedAt), rawInput: typeof rawInput === 'string' ? rawInput : input, rawOutput: typeof rawOutput === 'string' ? rawOutput : output, metadata: metadata ?? undefined };
}

function parseToolCallUpdate(data: Record<string, unknown>, fallbackCreatedAt: string): ToolCallUpdateData | null {
  if (typeof data !== 'object' || data === null) return null;
  const d = getObject(data.toolCall) ?? data;
  const toolCallId = typeof d.toolCallId === 'string' ? d.toolCallId
    : typeof d.id === 'string' ? d.id
      : typeof d.callId === 'string' ? d.callId
        : syntheticToolCallId(d, fallbackCreatedAt);
  const status = typeof d.status === 'string' ? d.status : undefined;
  const toolName = typeof d.toolName === 'string' ? d.toolName : undefined;
  const title = typeof d.title === 'string' ? d.title : undefined;
  const rawInput = d.rawInput ?? d.input;
  const input = stringifyPayload(rawInput);
  const rawOutput = d.rawOutput ?? d.output;
  const output = stringifyPayload(rawOutput);
  const error = typeof d.error === 'string' ? d.error : undefined;
  const metadata = getObject(d.metadata) ?? (rawOutput && typeof rawOutput === 'object' ? getObject((rawOutput as Record<string, unknown>).metadata) : null);
  return { toolCallId, toolName, status, title: title ?? toolName, input, output, error, createdAt: String(d.createdAt ?? fallbackCreatedAt), rawInput: typeof rawInput === 'string' ? rawInput : input, rawOutput: typeof rawOutput === 'string' ? rawOutput : output, metadata: metadata ?? undefined };
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

function getToolCategory(toolName: string): string | undefined {
  const lower = toolName.toLowerCase();
  if (['read', 'read_file', 'glob', 'grep', 'search', 'list', 'membrowse', 'memread', 'memsearch'].includes(lower)) {
    return 'context';
  }
  if (['apply_patch', 'edit', 'write', 'write_file'].includes(lower)) {
    return 'file-change';
  }
  if (['bash', 'shell'].includes(lower)) {
    return 'execution';
  }
  if (['todowrite', 'todo'].includes(lower)) {
    return 'planning';
  }
  return undefined;
}

function inferDisplayTitle(d: Record<string, unknown>): string | undefined {
  if (typeof d.title === 'string' && d.title) return d.title;
  if (typeof d.toolName === 'string' && d.toolName) return d.toolName;
  if (typeof d.name === 'string' && d.name) return d.name;
  return undefined;
}

function parseApplyPatch(patchText: string): FileChangeSummary[] {
  const changes: FileChangeSummary[] = [];
  const addRegex = /^(?:\*\*\*\s+)?Add File:\s*(.+)/;
  const updateRegex = /^(?:\*\*\*\s+)?Update File:\s*(.+)/;
  const deleteRegex = /^(?:\*\*\*\s+)?Delete File:\s*(.+)/;
  const moveRegex = /^(?:\*\*\*\s+)?Move to:\s*(.+)/;
  const oldPathRegex = /^(?:\*\*\*\s+)?OldPath:\s*(.+)/;

  const lines = patchText.split('\n');
  let currentOp: 'created' | 'modified' | 'deleted' | 'moved' | null = null;
  let currentPath: string | null = null;
  let oldPath: string | null = null;
  let additions = 0;
  let deletions = 0;

  const pushCurrent = () => {
    if (currentPath && currentOp) {
      changes.push({ path: currentPath, operation: currentOp, additions, deletions, oldPath: oldPath ?? undefined });
    }
  };

  for (const line of lines) {
    const addMatch = line.match(addRegex);
    const updateMatch = line.match(updateRegex);
    const deleteMatch = line.match(deleteRegex);
    const moveMatch = line.match(moveRegex);
    const oldPathMatch = line.match(oldPathRegex);

    if (addMatch) {
      pushCurrent();
      currentOp = 'created';
      currentPath = addMatch[1].trim();
      additions = 0;
      deletions = 0;
      oldPath = null;
    } else if (updateMatch) {
      pushCurrent();
      currentOp = 'modified';
      currentPath = updateMatch[1].trim();
      additions = 0;
      deletions = 0;
      oldPath = null;
    } else if (deleteMatch) {
      pushCurrent();
      currentOp = 'deleted';
      currentPath = deleteMatch[1].trim();
      additions = 0;
      deletions = 0;
      oldPath = null;
    } else if (moveMatch) {
      pushCurrent();
      currentOp = 'moved';
      currentPath = moveMatch[1].trim();
      additions = 0;
      deletions = 0;
    } else if (oldPathMatch) {
      pushCurrent();
      currentOp = null;
      currentPath = null;
      additions = 0;
      deletions = 0;
      oldPath = oldPathMatch[1].trim();
    } else if (line.startsWith('+') && !line.startsWith('+++')) {
      additions++;
    } else if (line.startsWith('-') && !line.startsWith('---')) {
      deletions++;
    }
  }

  if (currentPath && currentOp) {
    changes.push({ path: currentPath, operation: currentOp, additions, deletions, oldPath: oldPath ?? undefined });
  }

  return changes;
}

function parseEditWriteChanges(toolName: string, input: string | undefined): FileChangeSummary[] {
  if (!input) return [];
  const lower = toolName.toLowerCase();
  if (!['edit', 'write', 'write_file'].includes(lower)) return [];

  try {
    const parsed = JSON.parse(input);
    if (typeof parsed !== 'object' || parsed === null) return [];

    const filePath = parsed.file_path ?? parsed.path;
    if (typeof filePath !== 'string') return [];

    let operation: 'created' | 'modified' = 'modified';
    if (lower === 'write' || lower === 'write_file') {
      const content = parsed.content;
      if (content === '' || content === null || content === undefined) {
        operation = 'created';
      }
    }

    let additions = 0;
    let deletions = 0;

    if (parsed.old_string || parsed.new_string) {
      const oldStr = typeof parsed.old_string === 'string' ? parsed.old_string : '';
      const newStr = typeof parsed.new_string === 'string' ? parsed.new_string : '';
      const oldLines = oldStr.split('\n').length;
      const newLines = newStr.split('\n').length;
      additions = newLines;
      deletions = oldLines;
    } else if (parsed.additions !== undefined || parsed.deletions !== undefined) {
      additions = typeof parsed.additions === 'number' ? parsed.additions : 0;
      deletions = typeof parsed.deletions === 'number' ? parsed.deletions : 0;
    }

    return [{
      path: filePath.split('/').pop() ?? filePath,
      operation,
      additions: additions || undefined,
      deletions: deletions || undefined,
    }];
  } catch {
    return [];
  }
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
  private currentEventId: string = 'session';
  private partIndexByEventId: Map<string, number> = new Map();
  private syntheticToolIdCounter = 0;
  private pendingToolNames = new Map<string, string>();
  private pendingToolByTitle = new Map<string, string>();
  private eventCount = 0;
  private toolCount = 0;
  private warnings: TranscriptWarning[] = [];
  private hasUnknownTools = false;
  private allChangedFiles: FileChangeSummary[] = [];
  private lastActivityAt: string | null = null;

  constructor(session: CoderSession) {
    this.session = {
      sessionId: session.id,
      coderSessionId: session.id,
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
    this.eventCount = events.length;

    for (const entry of orderedEvents) {
      const data = typeof entry.data === 'string' ? JSON.parse(entry.data) : entry.data;
      this.processEvent({
        id: entry.id,
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

    this.session.lastActivityAt = this.lastActivityAt ?? undefined;
    this.session.eventCount = this.eventCount;
    this.session.toolCount = this.toolCount;
    this.session.turnCount = this.turns.length;
    this.session.changedFiles = this.allChangedFiles.length > 0 ? this.allChangedFiles : undefined;
    this.session.warnings = this.warnings.length > 0 ? this.warnings : undefined;
    this.session.hasUnknownTools = this.hasUnknownTools;

    return {
      session: this.session,
      turns: this.turns,
      incomplete: this.incomplete,
    };
  }

  private sortEvents(events: SessionStreamLogEntry[]): SessionStreamLogEntry[] {
    return events.map((event, index) => ({ event, index })).sort((a, b) => {
      const eventA = a.event;
      const eventB = b.event;
      const timeA = new Date(eventA.createdAt).getTime();
      const timeB = new Date(eventB.createdAt).getTime();
      if (timeA !== timeB) return timeA - timeB;
      const priorityA = EVENT_PRIORITY[eventA.eventType] ?? 3;
      const priorityB = EVENT_PRIORITY[eventB.eventType] ?? 3;
      if (priorityA !== priorityB) return priorityA - priorityB;
      return a.index - b.index;
    }).map(({ event }) => event);
  }

  private processEvent(event: RawEvent): void {
    const { eventType, data, createdAt } = event;
    this.currentEventId = event.id;
    this.lastActivityAt = createdAt;

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
      this.ensureToolCallId(data);
      const start = parseToolCallStart(data, createdAt);
      if (start) {
        this.toolCount++;
        this.handleToolCallStart(start);
        if (start.status === 'completed' || start.status === 'failed' || start.output !== undefined || start.error !== undefined) {
          this.handleToolCallUpdate({
            toolCallId: start.toolCallId,
            status: start.status,
            title: start.title,
            input: start.input,
            output: start.output,
            error: start.error,
            createdAt: start.createdAt,
            rawInput: start.rawInput,
            rawOutput: start.rawOutput,
            metadata: start.metadata,
          });
        }
      }
      return;
    }

    if (eventType === 'tool_call_update') {
      this.ensureToolCallId(data);
      const update = parseToolCallUpdate(data, createdAt);
      if (update) {
        this.handleToolCallUpdate(update);
      }
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
        this.handleError('failed', String(data.error), createdAt, false);
      }
      return;
    }
  }

  private handleMohistPrompt(data: Record<string, unknown>, createdAt: string): void {
    this.closeOpenTurn(createdAt);

    const parsed = parseMohistPromptEvent(data);
    if (!parsed) return;

    this.hasReceivedPrompt = true;
    if (!this.session.firstPromptSentAt) {
      this.session.firstPromptSentAt = parsed.sentAt;
    }
    const turnId = this.nextId('turn');

    const summary: PromptSummary = {
      kind: parsed.kind,
      rawText: parsed.text,
    };

    if (parsed.title) {
      summary.title = parsed.title;
    }

    if (parsed.text) {
      const contractMatch = parsed.text.match(/<contract>([\s\S]*?)<\/contract>/i);
      if (contractMatch) {
        const contract = contractMatch[1].trim();
        summary.outputPath = contract.split('\n')[0].trim();
        summary.subtitle = `Output: ${summary.outputPath}`;
      }

      const roleMatch = parsed.text.match(/<role>([\s\S]*?)<\/role>/i);
      if (roleMatch && !summary.title) {
        const role = roleMatch[1].trim();
        if (role.length < 80) {
          summary.title = role;
        }
      }

      const contextMatch = parsed.text.match(/<context_files>([\s\S]*?)<\/context_files>/i);
      if (contextMatch) {
        const files = contextMatch[1].trim().split('\n').map(f => f.trim()).filter(f => f);
        if (files.length > 0) {
          summary.contextFiles = files.slice(0, 5);
        }
      }
    }

    this.currentTurn = {
      id: turnId,
      startedAt: parsed.sentAt,
      completedAt: null,
      user: {
        role: 'mohist',
        text: parsed.text,
        kind: parsed.kind,
        sentAt: parsed.sentAt,
        summary,
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
        id: this.nextId('text'),
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
        id: this.nextId('reasoning'),
        type: 'reasoning',
        text,
        startedAt: createdAt,
        completedAt: null,
      };
      this.activeParts.reasoningPart = reasoningPart;
      this.currentTurn!.assistant.push(reasoningPart);
    }
  }

  private ensureToolCallId(data: Record<string, unknown>): void {
    const d = getObject(data.toolCall) ?? data;
    const hasId = typeof d.toolCallId === 'string' || typeof d.id === 'string' || typeof d.callId === 'string';
    if (hasId) return;
    const toolName = typeof d.toolName === 'string' ? d.toolName : (typeof d.name === 'string' ? d.name : 'unknown');
    const title = typeof d.title === 'string' ? d.title : undefined;
    const key = toolName;
    const pendingId = this.pendingToolNames.get(key);
    const titleKey = title ? `${toolName}:${title}` : undefined;
    const pendingByTitle = titleKey ? this.pendingToolByTitle.get(titleKey) : undefined;
    if (pendingByTitle) {
      this.pendingToolNames.delete(key);
      this.pendingToolByTitle.delete(titleKey!);
      this.setToolCallIdOnData(data, pendingByTitle);
    } else if ((d.status === 'completed' || d.status === 'failed' || d.status === 'cancelled') && pendingId) {
      this.pendingToolNames.delete(key);
      this.setToolCallIdOnData(data, pendingId);
    } else {
      const newId = `synthetic-${this.syntheticToolIdCounter++}`;
      this.setToolCallIdOnData(data, newId);
      if (d.status !== 'completed' && d.status !== 'failed' && d.status !== 'cancelled') {
        this.pendingToolNames.set(key, newId);
        if (titleKey) {
          this.pendingToolByTitle.set(titleKey, newId);
        }
      }
    }
  }

  private setToolCallIdOnData(data: Record<string, unknown>, id: string): void {
    const toolCall = data.toolCall;
    if (typeof toolCall === 'object' && toolCall !== null) {
      (toolCall as Record<string, unknown>).toolCallId = id;
    } else {
      data.toolCallId = id;
    }
  }

  private computeToolNormalization(d: Record<string, unknown>): { normalizedName: string; displayTitle?: string; displaySubtitle?: string; category?: string } {
    let normalizedName = inferNormalizedToolName(d);
    const displayTitle = inferDisplayTitle(d);
    const category = getToolCategory(normalizedName);
    return { normalizedName, displayTitle, displaySubtitle: undefined, category };
  }

  private handleToolCallStart(start: ToolCallStartData): void {
    if (!this.currentTurn) {
      this.ensureActiveTurn(start.createdAt);
    }

    const existing = this.toolPartsById.get(start.toolCallId);
    if (existing) {
      if (start.title !== undefined) existing.tool.title = start.title;
      if (start.input !== undefined) existing.tool.input = start.input;
      if (existing.tool.target === undefined && start.input) {
        existing.tool.target = deriveToolTarget(start.toolName, start.input);
      }
      return;
    }

    let parsedRawInput: Record<string, unknown> | undefined;
    if (start.rawInput) {
      try {
        parsedRawInput = JSON.parse(start.rawInput);
      } catch {
        parsedRawInput = undefined;
      }
    }
    let parsedRawOutput: Record<string, unknown> | undefined;
    if (start.rawOutput) {
      try {
        parsedRawOutput = JSON.parse(start.rawOutput);
      } catch {
        parsedRawOutput = undefined;
      }
    }

    const d: Record<string, unknown> = { toolName: start.toolName, title: start.title };
    if (parsedRawInput) {
      d.rawInput = parsedRawInput;
    }
    if (parsedRawOutput) {
      d.rawOutput = parsedRawOutput;
    }
    const { normalizedName, displayTitle, category } = this.computeToolNormalization(d);

    if (normalizedName === 'unknown' && start.toolName !== 'unknown') {
      this.hasUnknownTools = true;
      this.warnings.push({ code: 'UNKNOWN_TOOL', message: `Could not normalize tool name from: ${start.toolName}` });
    }

    const toolPart: ToolPart = {
      id: this.nextId('tool'),
      type: 'tool',
      tool: {
        toolCallId: start.toolCallId,
        normalizedName,
        displayTitle: displayTitle ?? start.title,
        category,
        toolName: start.toolName,
        status: 'running',
        title: start.title,
        target: deriveToolTarget(start.toolName, start.input),
        input: start.input,
        startedAt: start.createdAt,
        completedAt: null,
        rawInput: start.rawInput,
        rawOutput: start.rawOutput,
        metadata: start.metadata,
      },
    };

    const changedFiles = this.extractChangedFiles(normalizedName, start.input, start.rawInput);
    if (changedFiles.length > 0) {
      toolPart.tool.changedFiles = changedFiles;
      this.allChangedFiles.push(...changedFiles);
    }

    this.toolPartsById.set(start.toolCallId, toolPart);
    this.currentTurn!.assistant.push(toolPart);
  }

  private extractChangedFiles(toolName: string, input: string | undefined, rawInput: string | undefined): FileChangeSummary[] {
    const lower = toolName.toLowerCase();

    if (lower === 'apply_patch' || toolName === 'apply_patch') {
      const patchText = this.extractPatchText(input, rawInput);
      if (patchText) {
        return parseApplyPatch(patchText);
      }
    }

    if (!input) return [];
    return parseEditWriteChanges(toolName, input);
  }

  private extractPatchText(input: string | undefined, rawInput: string | undefined): string | null {
    if (!input && !rawInput) return null;
    const source = input ?? rawInput;
    if (!source) return null;

    try {
      const parsed = JSON.parse(source);
      if (typeof parsed.patchText === 'string') return parsed.patchText;
      if (typeof parsed.patch === 'string') return parsed.patch;
      if (typeof parsed === 'string') return parsed;
    } catch {
      if (typeof source === 'string' && (source.includes('Add File:') || source.includes('Update File:') || source.includes('Delete File:') || source.includes('Move to:'))) {
        return source;
      }
    }
    return null;
  }

  private handleToolCallUpdate(update: ToolCallUpdateData): void {
    const existing = this.toolPartsById.get(update.toolCallId);
    if (existing) {
      if (update.status) {
        existing.tool.status = update.status === 'completed' ? 'completed'
          : update.status === 'failed' ? 'failed'
          : update.status === 'cancelled' ? 'cancelled'
          : update.status === 'running' ? 'running'
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

      if (existing.tool.rawOutput === undefined && update.rawOutput) {
        existing.tool.rawOutput = update.rawOutput;
      }
      if (update.metadata) {
        existing.tool.metadata = { ...existing.tool.metadata, ...update.metadata };
      }

      const changedFiles = this.extractChangedFiles(existing.tool.normalizedName ?? existing.tool.toolName, update.input, update.rawInput);
      if (changedFiles.length > 0) {
        existing.tool.changedFiles = [...(existing.tool.changedFiles ?? []), ...changedFiles];
        this.allChangedFiles.push(...changedFiles);
      }
    } else {
      if (!this.currentTurn) {
        this.ensureActiveTurn(update.createdAt);
      }

      let parsedRawInput: Record<string, unknown> | undefined;
      if (update.rawInput) {
        try {
          parsedRawInput = JSON.parse(update.rawInput);
        } catch {
          parsedRawInput = undefined;
        }
      }
      let parsedRawOutput: Record<string, unknown> | undefined;
      if (update.rawOutput) {
        try {
          parsedRawOutput = JSON.parse(update.rawOutput);
        } catch {
          parsedRawOutput = undefined;
        }
      }

      const d: Record<string, unknown> = { toolName: update.toolName ?? 'unknown', title: update.title };
      if (parsedRawInput) {
        d.rawInput = parsedRawInput;
      }
      if (parsedRawOutput) {
        d.rawOutput = parsedRawOutput;
      }
      const { normalizedName, displayTitle, category } = this.computeToolNormalization(d);

      if (normalizedName === 'unknown' && update.toolName !== 'unknown' && update.toolName !== undefined) {
        this.hasUnknownTools = true;
        this.warnings.push({ code: 'UNKNOWN_TOOL', message: `Could not normalize tool name from: ${update.toolName}` });
      }

      const toolPart: ToolPart = {
        id: this.nextId('tool'),
        type: 'tool',
        tool: {
          toolCallId: update.toolCallId,
          normalizedName,
          displayTitle: displayTitle ?? update.title,
          category,
          toolName: update.toolName ?? 'unknown',
          status: update.status === 'completed' ? 'completed'
            : update.status === 'failed' ? 'failed'
            : update.status === 'cancelled' ? 'cancelled'
            : update.status === 'running' ? 'running'
            : 'pending',
          title: update.title,
          input: update.input,
          output: update.output,
          error: update.error,
          startedAt: update.createdAt,
          completedAt: update.status ? update.createdAt : null,
          rawInput: update.rawInput,
          rawOutput: update.rawOutput,
          metadata: update.metadata,
        },
      };

      if (toolPart.tool.target === undefined && toolPart.tool.input) {
        toolPart.tool.target = deriveToolTarget(toolPart.tool.toolName, toolPart.tool.input);
      }

      const changedFiles = this.extractChangedFiles(normalizedName, update.input, update.rawInput);
      if (changedFiles.length > 0) {
        toolPart.tool.changedFiles = changedFiles;
        this.allChangedFiles.push(...changedFiles);
      }

      this.toolPartsById.set(update.toolCallId, toolPart);
      this.currentTurn!.assistant.push(toolPart);
    }
  }

  private handleError(kind: ErrorPart['kind'], message: string, createdAt: string, closeTerminal = true): void {
    if (!this.currentTurn) {
      this.ensureActiveTurn(createdAt);
    }

    const errorPart: ErrorPart = {
      id: this.nextId('error'),
      type: 'error',
      message,
      kind,
      at: createdAt,
    };
    this.currentTurn!.assistant.push(errorPart);

    if (closeTerminal && (kind === 'timeout' || kind === 'failed' || kind === 'cancelled')) {
      this.closeOpenTurn(createdAt);
    }
  }

  private ensureActiveTurn(createdAt: string): void {
    if (this.currentTurn) return;

    if (!this.hasReceivedPrompt) {
      this.hasReceivedPrompt = false;
    }

    const turnId = this.nextId('turn');
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

    const turnId = this.nextId('turn');
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

  private nextId(prefix: string): string {
    const current = this.partIndexByEventId.get(this.currentEventId) ?? 0;
    this.partIndexByEventId.set(this.currentEventId, current + 1);
    return `${prefix}-${this.currentEventId}-${current}`;
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
