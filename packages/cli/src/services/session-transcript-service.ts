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

export interface MutationFileChange {
  path: string;
  operation: 'created' | 'modified' | 'deleted' | 'moved';
  additions?: number;
  deletions?: number;
  diff?: string;
  content?: string;
  oldPath?: string;
}

export interface ContextToolDetails {
  family: 'context';
  path?: string;
  pattern?: string;
  query?: string;
  include?: string;
  offset?: number;
  limit?: number;
  recursive?: boolean;
  resultSummary?: string;
}

export interface ExecutionToolDetails {
  family: 'execution';
  command?: string;
  cwd?: string;
  timeout?: number;
  exitCode?: number;
  completionStatus?: string;
  outputPreview?: string;
}

export interface PlanningToolDetails {
  family: 'planning';
  items?: Array<{ content: string; status: string }>;
  completedCount?: number;
  totalCount?: number;
  statusSummary?: string;
}

export interface DelegationToolDetails {
  family: 'delegation';
  subagentType?: string;
  subagentName?: string;
  description?: string;
  childSessionId?: string;
  taskId?: string;
}

export interface InteractionToolDetails {
  family: 'interaction';
  question?: string;
  answerCount?: number;
  url?: string;
  query?: string;
  resultPreview?: string;
}

export interface SkillToolDetails {
  family: 'skill';
  skillName?: string;
}

export interface MutationToolDetails {
  family: 'mutation';
  files: MutationFileChange[];
}

export type ToolSemanticDetails =
  | ContextToolDetails
  | ExecutionToolDetails
  | PlanningToolDetails
  | DelegationToolDetails
  | InteractionToolDetails
  | SkillToolDetails
  | MutationToolDetails;

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
  hidden?: boolean;
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
    details?: ToolSemanticDetails;
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

function hasVisibleTodoItems(rawInput: string | undefined): boolean {
  if (!rawInput) return false;
  const parsed = safeParseJson(rawInput);
  if (!parsed) return false;
  return Array.isArray(parsed.todos) && parsed.todos.length > 0;
}

function isSuppressedInternalTool(normalizedName: string, rawInput: string | undefined): boolean {
  if (!['todowrite', 'todo'].includes(normalizedName)) return false;
  return !hasVisibleTodoItems(rawInput);
}

const TERMINAL_STATUSES = new Set(['completed', 'failed', 'cancelled']);

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

function normalizePromptOutputLabel(value: string | undefined): string | undefined {
  if (!value) return undefined;
  const trimmed = value.trim();
  return trimmed.replace(/^output\s*:\s*/i, '').trim();
}

function normalizePromptSummary(summary: PromptSummary): PromptSummary {
  const normalizedSubtitle = normalizePromptOutputLabel(summary.subtitle);
  const normalizedOutputPath = normalizePromptOutputLabel(summary.outputPath);
  if (normalizedSubtitle && normalizedOutputPath && normalizedSubtitle === normalizedOutputPath) {
    delete summary.subtitle;
    summary.outputPath = normalizedOutputPath;
  }
  return summary;
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

function inferNormalizedToolName(d: Record<string, unknown>): { name: string; wasInferred: boolean } {
  const toolName = typeof d.toolName === 'string' ? d.toolName : undefined;
  const name = typeof d.name === 'string' ? d.name : undefined;

  const inferTitleToolFamily = (titleLower: string): string | undefined => {
    if (titleLower.includes('apply_patch')) return 'apply_patch';
    if (titleLower.includes('search_files')) return 'search_files';
    if (titleLower.includes('webfetch')) return 'webfetch';
    if (titleLower.includes('websearch')) return 'websearch';
    if (titleLower.includes('todowrite')) return 'todowrite';
    if (titleLower === 'todo' || titleLower.startsWith('todo:') || titleLower.includes(' todo ')) return 'todo';
    if (titleLower.includes('bash')) return 'bash';
    if (titleLower.includes('shell')) return 'shell';
    if (titleLower.includes('grep')) return 'grep';
    if (titleLower.includes('glob')) return 'glob';
    if (titleLower.includes('read')) return 'read';
    if (titleLower.includes('write')) return 'write';
    if (titleLower.includes('edit')) return 'edit';
    if (titleLower.includes('list')) return 'list';
    if (titleLower.includes('question')) return 'question';
    if (titleLower.includes('search')) return 'search';
    return undefined;
  };

  const inferSemanticToolName = (obj: Record<string, unknown>): string | undefined => {
    const title = typeof obj.title === 'string' ? obj.title : undefined;
    if (title) {
      const titleLower = title.toLowerCase();
      if (titleLower.startsWith('loaded skill:') || titleLower === 'skill' || titleLower.startsWith('skill:')) return 'skill';
      if (titleLower.includes('subagent') || titleLower.includes('delegate') || titleLower.startsWith('task:')) return 'task';
      const inferredFamily = inferTitleToolFamily(titleLower);
      if (inferredFamily) return inferredFamily;
    }

    const skillName = obj.skillName ?? obj.skill ?? obj.name;
    if (typeof skillName === 'string' && skillName && skillName !== toolName && skillName !== name) return 'skill';
    if (obj.subagent_type !== undefined || obj.subagentType !== undefined || obj.task_id !== undefined || obj.taskId !== undefined || obj.childSessionId !== undefined || obj.child_session_id !== undefined) return 'task';
    if (obj.patchText !== undefined) return 'apply_patch';
    if (obj.command !== undefined || obj.script !== undefined) return 'bash';
    if (obj.pattern !== undefined || obj.query !== undefined || obj.search !== undefined) {
      if (obj.file_path !== undefined) return 'grep';
      return 'search';
    }
    if (obj.file_path !== undefined || obj.filePath !== undefined || obj.path !== undefined) return 'read';
    if (obj.todos !== undefined) return 'todowrite';
    return undefined;
  };

  if (toolName && toolName !== 'unknown') return { name: toolName, wasInferred: false };
  if (name && name !== 'unknown') return { name, wasInferred: false };

  if (typeof d.metadata === 'object' && d.metadata !== null) {
    const meta = d.metadata as Record<string, unknown>;
    if (typeof meta.toolName === 'string' && meta.toolName && meta.toolName !== 'unknown') return { name: meta.toolName, wasInferred: true };
    if (typeof meta.name === 'string' && meta.name && meta.name !== 'unknown') return { name: meta.name, wasInferred: true };
    const inferred = inferSemanticToolName(meta);
    if (inferred) return { name: inferred, wasInferred: true };
  }

  const rawInput = d.rawInput ?? d.input;
  if (rawInput) {
    if (typeof rawInput === 'object' && rawInput !== null) {
      const inputObj = rawInput as Record<string, unknown>;
      const inferred = inferSemanticToolName(inputObj);
      if (inferred) return { name: inferred, wasInferred: true };
    } else if (typeof rawInput === 'string') {
      try {
        const parsed = JSON.parse(rawInput);
        if (parsed && typeof parsed === 'object' && parsed !== null) {
          const inputObj = parsed as Record<string, unknown>;
          const inferred = inferSemanticToolName(inputObj);
          if (inferred) return { name: inferred, wasInferred: true };
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
            if (typeof meta.toolName === 'string' && meta.toolName && meta.toolName !== 'unknown') return { name: meta.toolName, wasInferred: true };
            if (typeof meta.name === 'string' && meta.name && meta.name !== 'unknown') return { name: meta.name, wasInferred: true };
            const inferred = inferSemanticToolName(meta);
            if (inferred) return { name: inferred, wasInferred: true };
          }
        }
      } catch {
      }
    } else if (typeof rawOutput === 'object' && rawOutput !== null) {
      const outputObj = rawOutput as Record<string, unknown>;
      if (outputObj.metadata && typeof outputObj.metadata === 'object') {
        const meta = (outputObj.metadata as Record<string, unknown>);
        if (typeof meta.toolName === 'string' && meta.toolName && meta.toolName !== 'unknown') return { name: meta.toolName, wasInferred: true };
        if (typeof meta.name === 'string' && meta.name && meta.name !== 'unknown') return { name: meta.name, wasInferred: true };
        const inferred = inferSemanticToolName(meta);
        if (inferred) return { name: inferred, wasInferred: true };
      }
    }
  }

  const inferred = inferSemanticToolName(d);
  if (inferred) return { name: inferred, wasInferred: true };

  const fallbackName = toolName ?? name ?? 'unknown';
  return { name: fallbackName, wasInferred: false };
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
  const topLevelMetadata = getObject(data.metadata);
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
  const metadata = getObject(d.metadata) ?? topLevelMetadata ?? (rawOutput && typeof rawOutput === 'object' ? getObject((rawOutput as Record<string, unknown>).metadata) : null);
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
  if (['read', 'read_file', 'glob', 'grep', 'search', 'search_files', 'list', 'membrowse', 'memread', 'memsearch'].includes(lower)) {
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
  if (lower === 'task') {
    return 'delegation';
  }
  if (['question', 'webfetch', 'websearch'].includes(lower) || lower.startsWith('web-') || lower.startsWith('web_') || lower.startsWith('context7')) {
    return 'interaction';
  }
  if (lower === 'skill') {
    return 'skill';
  }
  return undefined;
}

function inferDisplayTitle(d: Record<string, unknown>): string | undefined {
  if (typeof d.title === 'string' && d.title) return d.title;
  if (typeof d.toolName === 'string' && d.toolName) return d.toolName;
  if (typeof d.name === 'string' && d.name) return d.name;
  return undefined;
}

function synthesizeWriteDiff(filePath: string, content: string): string {
  if (!content) return '';
  const lines = content.split('\n');
  const newCount = lines.length;
  const header = `diff --git a/${filePath} b/${filePath}\nnew file mode 100644\n--- /dev/null\n+++ b/${filePath} @@ -0,0 +1,${newCount} @@`;
  const diffLines = lines.map(line => '+' + line).join('\n');
  return `${header}\n${diffLines}\n`;
}

function synthesizeEditDiff(filePath: string, oldStr: string | undefined, newStr: string | undefined): string {
  if (oldStr === undefined && newStr === undefined) return '';
  const oldLines = oldStr !== undefined ? oldStr.split('\n') : [''];
  const newLines = newStr !== undefined ? newStr.split('\n') : [''];
  const oldCount = oldLines.length;
  const newCount = newLines.length;
  const header = `diff --git a/${filePath} b/${filePath}\n--- a/${filePath}\n+++ b/${filePath} @@ -1,${oldCount} +1,${newCount} @@`;
  const oldPart = oldLines.map(line => '-' + line).join('\n');
  const newPart = newLines.map(line => '+' + line).join('\n');
  return `${header}\n${oldPart}\n${newPart}\n`;
}

function countDiffLines(diff: string | undefined): { additions?: number; deletions?: number } {
  if (!diff) return {};
  let additions = 0;
  let deletions = 0;
  for (const line of diff.split('\n')) {
    if (line.startsWith('+') && !line.startsWith('+++')) additions++;
    if (line.startsWith('-') && !line.startsWith('---')) deletions++;
  }
  return { additions: additions || undefined, deletions: deletions || undefined };
}

function deriveWriteOperation(input: Record<string, unknown>): 'created' | 'modified' | 'moved' {
  if (input.old_path || input.oldPath) return 'moved';
  const hasBeforeAfter = input.old_string !== undefined || input.oldString !== undefined || input.new_string !== undefined || input.newString !== undefined;
  const hasCounts = input.additions !== undefined || input.deletions !== undefined;
  const priorStateHint = input.existedBefore ?? input.fileExists ?? input.file_exists ?? input.previousContent ?? input.previous_content;
  if (priorStateHint === false) return 'created';
  if (hasBeforeAfter || hasCounts || priorStateHint !== undefined) return 'modified';
  return 'created';
}

function deriveWriteFileChange(input: Record<string, unknown>): MutationFileChange | null {
  const filePath = strVal(input.file_path ?? input.filePath ?? input.path);
  if (!filePath) return null;

  const operation = deriveWriteOperation(input);
  const oldStr = strVal(input.old_string ?? input.oldString);
  const newStr = strVal(input.new_string ?? input.newString);
  const content = typeof input.content === 'string' ? input.content : '';

  let diff: string | undefined;
  let additions: number | undefined;
  let deletions: number | undefined;

  if (oldStr !== undefined || newStr !== undefined) {
    diff = synthesizeEditDiff(filePath, oldStr, newStr);
    ({ additions, deletions } = countDiffLines(diff));
  } else if (typeof input.diff === 'string' && input.diff) {
    diff = input.diff;
    ({ additions, deletions } = countDiffLines(diff));
  } else if (input.additions !== undefined || input.deletions !== undefined) {
    additions = typeof input.additions === 'number' ? input.additions : undefined;
    deletions = typeof input.deletions === 'number' ? input.deletions : undefined;
  }

  if (!diff && content) {
    diff = synthesizeWriteDiff(filePath, content);
    ({ additions, deletions } = countDiffLines(diff));
  }

  return {
    path: filePath,
    operation,
    additions,
    deletions,
    diff: diff || undefined,
    content: content || undefined,
    oldPath: strVal(input.old_path ?? input.oldPath),
  };
}

function buildMutationInputFromMetadata(input: Record<string, unknown> | null, metadata: Record<string, unknown> | undefined): Record<string, unknown> | null {
  if (!metadata) return input;
  const filePath = strVal(input?.file_path ?? input?.filePath ?? input?.path ?? metadata.file_path ?? metadata.filePath ?? metadata.path);
  if (!filePath) return input;
  return {
    ...(input ?? {}),
    file_path: filePath,
    old_path: input?.old_path ?? input?.oldPath ?? metadata.old_path ?? metadata.oldPath,
    old_string: input?.old_string ?? input?.oldString ?? metadata.old_string ?? metadata.oldString ?? metadata.before ?? metadata.previousContent ?? metadata.previous_content,
    new_string: input?.new_string ?? input?.newString ?? metadata.new_string ?? metadata.newString ?? metadata.after ?? metadata.content,
    content: input?.content ?? metadata.content,
    diff: input?.diff ?? metadata.diff,
    additions: input?.additions ?? metadata.additions,
    deletions: input?.deletions ?? metadata.deletions,
    existedBefore: input?.existedBefore ?? input?.fileExists ?? input?.file_exists ?? metadata.existedBefore ?? metadata.fileExists ?? metadata.file_exists,
  };
}

function isPlaceholderPayload(current: string | undefined): boolean {
  if (!current) return true;
  const trimmed = current.trim();
  return trimmed === '' || trimmed === '{}' || trimmed === '[]' || trimmed === 'null' || trimmed === 'undefined';
}

function chooseMoreSpecificPayload(current: string | undefined, next: string | undefined): string | undefined {
  if (!next) return current;
  if (!current || isPlaceholderPayload(current)) return next;
  if (isPlaceholderPayload(next)) return current;
  const currentParsed = safeParseJson(current);
  const nextParsed = safeParseJson(next);
  if (!currentParsed || !nextParsed) return next.length > current.length ? next : current;
  const currentKeys = Object.keys(currentParsed).length;
  const nextKeys = Object.keys(nextParsed).length;
  if (nextKeys > currentKeys) return next;
  if (nextKeys === currentKeys && next.length > current.length) return next;
  return current;
}

function buildUnifiedDiff(toolName: string, input: string | undefined, metadata?: Record<string, unknown>): { changedFiles: FileChangeSummary[]; diff: string } {
  const empty = { changedFiles: [] as FileChangeSummary[], diff: '' };

  if (!input) return empty;

  const lower = toolName.toLowerCase();

  if (lower === 'apply_patch' || toolName === 'apply_patch') {
    try {
      const parsed = JSON.parse(input);
      const patchText = typeof parsed.patchText === 'string' ? parsed.patchText
        : typeof parsed.patch === 'string' ? parsed.patch
        : typeof input === 'string' && input.includes('Add File:') ? input
        : '';
      if (!patchText) return empty;
      return { changedFiles: parseApplyPatch(patchText), diff: patchText };
    } catch {
      return empty;
    }
  }

  if (lower === 'edit' || lower === 'write' || lower === 'write_file') {
    try {
      const parsed = JSON.parse(input);
      if (typeof parsed !== 'object' || parsed === null) return empty;

      if (lower === 'write' || lower === 'write_file') {
        const file = deriveWriteFileChange(buildMutationInputFromMetadata(parsed, metadata) ?? parsed);
        if (!file) return empty;
        return {
          changedFiles: [{
            path: file.path.split('/').pop() ?? file.path,
            operation: file.operation,
            additions: file.additions,
            deletions: file.deletions,
            oldPath: file.oldPath ? (file.oldPath.split('/').pop() ?? file.oldPath) : undefined,
          }],
          diff: file.diff ?? '',
        };
      }

      const enriched = buildMutationInputFromMetadata(parsed, metadata) ?? parsed;
      const filePath = enriched.file_path ?? enriched.filePath ?? enriched.path;
      if (typeof filePath !== 'string') return empty;

      const operation: 'created' | 'modified' | 'moved' = enriched.old_path || enriched.oldPath ? 'moved' : 'modified';

      let additions = 0;
      let deletions = 0;
      let diff = '';

      if (enriched.old_string !== undefined || enriched.oldString !== undefined || enriched.new_string !== undefined || enriched.newString !== undefined) {
        const oldStr = strVal(enriched.old_string ?? enriched.oldString) ?? '';
        const newStr = strVal(enriched.new_string ?? enriched.newString) ?? '';
        diff = synthesizeEditDiff(filePath, oldStr, newStr);
        additions = countDiffLines(diff).additions ?? 0;
        deletions = countDiffLines(diff).deletions ?? 0;
      } else if (typeof enriched.diff === 'string' && enriched.diff) {
        diff = enriched.diff;
        additions = countDiffLines(diff).additions ?? 0;
        deletions = countDiffLines(diff).deletions ?? 0;
      } else if (enriched.additions !== undefined || enriched.deletions !== undefined) {
        additions = typeof enriched.additions === 'number' ? enriched.additions : 0;
        deletions = typeof enriched.deletions === 'number' ? enriched.deletions : 0;
      }

      const changedFiles: FileChangeSummary[] = [{
        path: filePath.split('/').pop() ?? filePath,
        operation,
        additions: additions || undefined,
        deletions: deletions || undefined,
        oldPath: (enriched.old_path ?? enriched.oldPath)?.split('/').pop() ?? (enriched.old_path ?? enriched.oldPath),
      }];

      return { changedFiles, diff };
    } catch {
      return empty;
    }
  }

  return empty;
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

    if (lower === 'write' || lower === 'write_file') {
      const file = deriveWriteFileChange(parsed);
      if (!file) return [];
      return [{
        path: file.path.split('/').pop() ?? file.path,
        operation: file.operation,
        additions: file.additions,
        deletions: file.deletions,
        oldPath: file.oldPath ? (file.oldPath.split('/').pop() ?? file.oldPath) : undefined,
      }];
    }

    const filePath = parsed.file_path ?? parsed.path;
    if (typeof filePath !== 'string') return [];

    let operation: 'created' | 'modified' | 'moved' = 'modified';
    if (lower === 'write' || lower === 'write_file') {
      const content = parsed.content;
      if (content === '' || content === null || content === undefined) {
        operation = 'created';
      }
    }

    if (parsed.old_path || parsed.oldPath) {
      operation = 'moved';
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
      oldPath: (parsed.old_path ?? parsed.oldPath)?.split('/').pop() ?? (parsed.old_path ?? parsed.oldPath),
    }];
  } catch {
    return [];
  }
}

function strVal(v: unknown): string | undefined {
  return typeof v === 'string' ? v : undefined;
}

function numVal(v: unknown): number | undefined {
  return typeof v === 'number' ? v : undefined;
}

function boolVal(v: unknown): boolean | undefined {
  return typeof v === 'boolean' ? v : undefined;
}

const OUTPUT_PREVIEW_LIMIT = 500;
const RESULT_PREVIEW_LIMIT = 200;

function truncatePreview(text: string | undefined, limit: number = OUTPUT_PREVIEW_LIMIT): string | undefined {
  if (!text) return undefined;
  const trimmed = text.trim();
  if (trimmed.length <= limit) return trimmed;
  return trimmed.slice(0, limit) + '...';
}

function safeParseJson(str: string | undefined): Record<string, unknown> | null {
  if (!str) return null;
  try {
    const parsed = JSON.parse(str);
    if (typeof parsed === 'object' && parsed !== null) return parsed as Record<string, unknown>;
    return null;
  } catch {
    return null;
  }
}

function isContextFamily(name: string): boolean {
  const lower = name.toLowerCase();
  return ['read', 'read_file', 'glob', 'grep', 'search', 'search_files', 'list', 'membrowse', 'memread', 'memsearch'].includes(lower);
}

function isExecutionFamily(name: string): boolean {
  const lower = name.toLowerCase();
  return ['bash', 'shell'].includes(lower);
}

function isPlanningFamily(name: string): boolean {
  const lower = name.toLowerCase();
  return ['todowrite', 'todo'].includes(lower);
}

function isDelegationFamily(name: string): boolean {
  return name.toLowerCase() === 'task';
}

function isInteractionFamily(name: string): boolean {
  const lower = name.toLowerCase();
  if (['question', 'webfetch', 'websearch'].includes(lower)) return true;
  if (lower.startsWith('web-') || lower.startsWith('web_')) return true;
  if (lower.startsWith('context7')) return true;
  return false;
}

function isSkillFamily(name: string): boolean {
  return name.toLowerCase() === 'skill';
}

function isMutationFamily(name: string): boolean {
  const lower = name.toLowerCase();
  return ['apply_patch', 'edit', 'write', 'write_file'].includes(lower);
}

function buildContextDetails(rawInput: string | undefined, rawOutput: string | undefined): ContextToolDetails {
  const input = safeParseJson(rawInput);
  const details: ContextToolDetails = { family: 'context' };
  if (input) {
    details.path = strVal(input.file_path ?? input.filePath ?? input.path ?? input.uri);
    details.pattern = strVal(input.pattern ?? input.glob);
    details.query = strVal(input.query ?? input.search ?? input.search_query);
    details.include = strVal(input.include ?? input.file_pattern ?? input.filePattern);
    details.offset = numVal(input.offset);
    details.limit = numVal(input.limit);
    details.recursive = boolVal(input.recursive);
  }
  const output = safeParseJson(rawOutput);
  if (rawOutput) {
    if (output) {
      if (Array.isArray(output.files)) {
        details.resultSummary = `${output.files.length} file(s) matched`;
      } else if (Array.isArray(output.results)) {
        details.resultSummary = `${output.results.length} result(s)`;
      } else if (Array.isArray(output.locations)) {
        details.resultSummary = `${output.locations.length} location(s)`;
      } else if (typeof output.content === 'string') {
        details.resultSummary = truncatePreview(output.content, RESULT_PREVIEW_LIMIT);
      } else if (typeof output.text === 'string') {
        details.resultSummary = truncatePreview(output.text, RESULT_PREVIEW_LIMIT);
      } else {
        details.resultSummary = truncatePreview(rawOutput, RESULT_PREVIEW_LIMIT);
      }
    } else {
      details.resultSummary = truncatePreview(rawOutput, RESULT_PREVIEW_LIMIT);
    }
  }
  return details;
}

function buildExecutionDetails(rawInput: string | undefined, rawOutput: string | undefined, error: string | undefined): ExecutionToolDetails {
  const input = safeParseJson(rawInput);
  const details: ExecutionToolDetails = { family: 'execution' };
  if (input) {
    details.command = strVal(input.command ?? input.script);
    details.cwd = strVal(input.cwd ?? input.workdir ?? input.workingDir);
    details.timeout = numVal(input.timeout);
  }
  const output = safeParseJson(rawOutput);
  if (output) {
    details.exitCode = numVal(output.exitCode ?? output.exit_code ?? output.code);
    if (typeof output.stdout === 'string') {
      details.outputPreview = truncatePreview(output.stdout);
    } else if (typeof output.output === 'string') {
      details.outputPreview = truncatePreview(output.output);
    }
  }
  if (!details.outputPreview && rawOutput) {
    details.outputPreview = truncatePreview(rawOutput);
  }
  if (error) {
    details.completionStatus = 'failed';
  } else if (rawOutput !== undefined) {
    details.completionStatus = 'completed';
  }
  return details;
}

function buildPlanningDetails(rawInput: string | undefined): PlanningToolDetails {
  const input = safeParseJson(rawInput);
  const details: PlanningToolDetails = { family: 'planning' };
  if (input && Array.isArray(input.todos)) {
    const items = input.todos as Array<Record<string, unknown>>;
    details.items = items.map(item => ({
      content: typeof item.content === 'string' ? item.content : String(item.content ?? ''),
      status: typeof item.status === 'string' ? item.status : 'unknown',
    }));
    details.totalCount = details.items.length;
    details.completedCount = details.items.filter(i => i.status === 'completed').length;
    const statusCounts: Record<string, number> = {};
    for (const item of details.items) {
      statusCounts[item.status] = (statusCounts[item.status] ?? 0) + 1;
    }
    details.statusSummary = Object.entries(statusCounts).map(([s, c]) => `${c} ${s}`).join(', ');
  }
  return details;
}

function buildDelegationDetails(rawInput: string | undefined, metadata: Record<string, unknown> | undefined): DelegationToolDetails {
  const input = safeParseJson(rawInput);
  const details: DelegationToolDetails = { family: 'delegation' };
  if (input) {
    details.subagentType = strVal(input.subagent_type ?? input.agentType ?? input.type);
    details.subagentName = strVal(input.subagent_name ?? input.agentName ?? input.name);
    details.description = strVal(input.description ?? input.prompt ?? input.task ?? input.command);
    details.taskId = strVal(input.task_id ?? input.taskId);
  }
  if (metadata) {
    details.childSessionId = strVal(metadata.childSessionId ?? metadata.sessionId ?? metadata.child_session_id);
    if (!details.subagentType) details.subagentType = strVal(metadata.subagentType);
    if (!details.description) details.description = strVal(metadata.description);
  }
  return details;
}

function buildInteractionDetails(normalizedName: string, rawInput: string | undefined, rawOutput: string | undefined): InteractionToolDetails {
  const input = safeParseJson(rawInput);
  const details: InteractionToolDetails = { family: 'interaction' };
  if (normalizedName.toLowerCase() === 'question') {
    if (input) {
      if (typeof input.question === 'string') details.question = input.question;
      if (Array.isArray(input.questions)) {
        details.question = (input.questions as string[]).join('; ');
        details.answerCount = input.questions.length;
      }
    }
  } else {
    if (input) {
      details.url = strVal(input.url ?? input.URI);
      details.query = strVal(input.query ?? input.search_query ?? input.searchQuery ?? input.search);
    }
  }
  const output = safeParseJson(rawOutput);
  if (output) {
    if (Array.isArray(output.answers)) {
      details.answerCount = output.answers.length;
      details.resultPreview = truncatePreview(JSON.stringify(output.answers), 300);
    } else if (typeof output.content === 'string') {
      details.resultPreview = truncatePreview(output.content, 300);
    } else if (typeof output.text === 'string') {
      details.resultPreview = truncatePreview(output.text, 300);
    } else if (typeof output.summary === 'string') {
      details.resultPreview = truncatePreview(output.summary, 300);
    }
  }
  if (!details.resultPreview && rawOutput) {
    details.resultPreview = truncatePreview(rawOutput, 300);
  }
  return details;
}

function buildSkillDetails(title: string | undefined, rawInput: string | undefined, metadata: Record<string, unknown> | undefined): SkillToolDetails {
  const details: SkillToolDetails = { family: 'skill' };
  if (title) {
    const match = title.match(/(?:Loaded skill:?\s*)(.+)/i) || title.match(/(?:skill:?\s*)(.+)/i);
    if (match) details.skillName = match[1].trim();
  }
  if (!details.skillName) {
    const input = safeParseJson(rawInput);
    if (input) details.skillName = strVal(input.name ?? input.skillName ?? input.skill);
  }
  if (!details.skillName && metadata) {
    details.skillName = strVal(metadata.skillName ?? metadata.name);
  }
  if (!details.skillName && title && title.toLowerCase() !== 'skill') {
    details.skillName = title;
  }
  return details;
}

function extractPatchTextFromRaw(rawInput: string | undefined): string | null {
  if (!rawInput) return null;
  const input = safeParseJson(rawInput);
  if (input) {
    if (typeof input.patchText === 'string') return input.patchText;
    if (typeof input.patch === 'string') return input.patch;
  }
  if (rawInput.includes('Add File:') || rawInput.includes('Update File:') || rawInput.includes('Delete File:')) {
    return rawInput;
  }
  return null;
}

function parseApplyPatchToMutationFiles(patchText: string): MutationFileChange[] {
  const files: MutationFileChange[] = [];
  const lines = patchText.split('\n');
  let currentOp: 'created' | 'modified' | 'deleted' | 'moved' | null = null;
  let currentPath: string | null = null;
  let oldPath: string | null = null;
  let additions = 0;
  let deletions = 0;
  let diffLines: string[] = [];
  const pushCurrent = () => {
    if (currentPath && currentOp) {
      files.push({
        path: currentPath,
        operation: currentOp,
        additions: additions || undefined,
        deletions: deletions || undefined,
        diff: diffLines.length > 0 ? diffLines.join('\n') : undefined,
        oldPath: oldPath ?? undefined,
      });
    }
  };
  const resetCurrent = () => { currentOp = null; currentPath = null; oldPath = null; additions = 0; deletions = 0; diffLines = []; };
  for (const line of lines) {
    const addMatch = line.match(/^(?:\*\*\*\s+)?Add File:\s*(.+)/);
    const updateMatch = line.match(/^(?:\*\*\*\s+)?Update File:\s*(.+)/);
    const deleteMatch = line.match(/^(?:\*\*\*\s+)?Delete File:\s*(.+)/);
    const moveMatch = line.match(/^(?:\*\*\*\s+)?Move to:\s*(.+)/);
    const oldPathMatch = line.match(/^(?:\*\*\*\s+)?OldPath:\s*(.+)/);
    if (addMatch) { pushCurrent(); resetCurrent(); currentOp = 'created'; currentPath = addMatch[1].trim(); diffLines.push(line); }
    else if (updateMatch) { pushCurrent(); resetCurrent(); currentOp = 'modified'; currentPath = updateMatch[1].trim(); diffLines.push(line); }
    else if (deleteMatch) { pushCurrent(); resetCurrent(); currentOp = 'deleted'; currentPath = deleteMatch[1].trim(); diffLines.push(line); }
    else if (moveMatch) { pushCurrent(); resetCurrent(); currentOp = 'moved'; currentPath = moveMatch[1].trim(); diffLines.push(line); }
    else if (oldPathMatch) { oldPath = oldPathMatch[1].trim(); diffLines.push(line); }
    else if (line.startsWith('***') && (line.includes('Begin Patch') || line.includes('End Patch'))) { diffLines.push(line); }
    else if (currentOp) {
      if (line.startsWith('+') && !line.startsWith('+++')) additions++;
      else if (line.startsWith('-') && !line.startsWith('---')) deletions++;
      diffLines.push(line);
    }
  }
  pushCurrent();
  return files;
}

function buildMutationDetails(normalizedName: string, rawInput: string | undefined, metadata?: Record<string, unknown>): MutationToolDetails {
  const details: MutationToolDetails = { family: 'mutation', files: [] };
  const lower = normalizedName.toLowerCase();
  if (lower === 'apply_patch') {
    const patchText = extractPatchTextFromRaw(rawInput);
    if (patchText) details.files = parseApplyPatchToMutationFiles(patchText);
  } else if (lower === 'edit') {
    const input = buildMutationInputFromMetadata(safeParseJson(rawInput), metadata);
    if (input) {
      const filePath = strVal(input.file_path ?? input.filePath ?? input.path);
      if (filePath) {
        const oldStr = strVal(input.old_string ?? input.oldString);
        const newStr = strVal(input.new_string ?? input.newString);
        const metadataDiff = strVal(input.diff);
        const diff = metadataDiff ?? ((oldStr !== undefined || newStr !== undefined) ? synthesizeEditDiff(filePath, oldStr, newStr) : undefined);
        const counts = diff ? countDiffLines(diff) : { additions: undefined, deletions: undefined };
        details.files.push({
          path: filePath,
          operation: (input.old_path || input.oldPath) ? 'moved' : 'modified',
          additions: counts.additions ?? (typeof input.additions === 'number' ? input.additions : undefined),
          deletions: counts.deletions ?? (typeof input.deletions === 'number' ? input.deletions : undefined),
          diff: diff || undefined,
          content: strVal(input.content),
          oldPath: strVal(input.old_path ?? input.oldPath),
        });
      }
    }
  } else if (lower === 'write' || lower === 'write_file') {
    const input = buildMutationInputFromMetadata(safeParseJson(rawInput), metadata);
    if (input) {
      const file = deriveWriteFileChange(input);
      if (file) details.files.push(file);
    }
  }
  return details;
}

function mutationDetailsToChangedFiles(details: ToolSemanticDetails | undefined): FileChangeSummary[] {
  if (!details || details.family !== 'mutation') return [];
  return details.files.map(file => ({
    path: file.path.split('/').pop() ?? file.path,
    operation: file.operation,
    additions: file.additions,
    deletions: file.deletions,
    oldPath: file.oldPath ? (file.oldPath.split('/').pop() ?? file.oldPath) : undefined,
    rawDetail: file.diff ?? file.content,
  }));
}

function buildSemanticDetails(normalizedName: string, rawInput: string | undefined, rawOutput: string | undefined, title: string | undefined, error: string | undefined, metadata: Record<string, unknown> | undefined): ToolSemanticDetails | undefined {
  if (isContextFamily(normalizedName)) return buildContextDetails(rawInput, rawOutput);
  if (isExecutionFamily(normalizedName)) return buildExecutionDetails(rawInput, rawOutput, error);
  if (isPlanningFamily(normalizedName)) return buildPlanningDetails(rawInput);
  if (isDelegationFamily(normalizedName)) return buildDelegationDetails(rawInput, metadata);
  if (isInteractionFamily(normalizedName)) return buildInteractionDetails(normalizedName, rawInput, rawOutput);
  if (isSkillFamily(normalizedName)) return buildSkillDetails(title, rawInput, metadata);
  if (isMutationFamily(normalizedName)) return buildMutationDetails(normalizedName, rawInput, metadata);
  return undefined;
}

interface ActiveParts {
  textPart: TextPart | null;
  reasoningPart: ReasoningPart | null;
}

interface PendingNoIdTool {
  id: string;
  nameKey: string;
  correlationKey?: string;
}

export class SessionTranscriptAssembler {
  private session: SessionMetadata;
  private turns: SessionTurn[] = [];
  private currentTurn: SessionTurn | null = null;
  private activeParts: ActiveParts = { textPart: null, reasoningPart: null };
  private toolPartsById: Map<string, ToolPart> = new Map();
  private toolIdAliasProviderToLocal: Map<string, string> = new Map();
  private toolIdAliasLocalToProvider: Map<string, string> = new Map();
  private incomplete: boolean = false;
  private hasReceivedPrompt: boolean = false;
  private currentEventId: string = 'session';
  private partIndexByEventId: Map<string, number> = new Map();
  private syntheticToolIdCounter = 0;
  private pendingNoIdToolsByName = new Map<string, PendingNoIdTool[]>();
  private pendingNoIdToolsByCorrelation = new Map<string, PendingNoIdTool[]>();
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
        summary: normalizePromptSummary(summary),
      },
      assistant: [],
    };
    this.turns.push(this.currentTurn);
    this.activeParts = { textPart: null, reasoningPart: null };
    this.toolPartsById.clear();
    this.clearPendingNoIdTools();
  }

  private handleTextChunk(text: string, createdAt: string): void {
    this.ensureActiveTurn(createdAt);
    this.closeActiveReasoningPart(createdAt);

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

  private closeActiveReasoningPart(createdAt: string): void {
    if (this.activeParts.reasoningPart) {
      this.activeParts.reasoningPart.completedAt = createdAt;
      this.activeParts.reasoningPart = null;
    }
  }

  private closeActiveTextPart(createdAt: string): void {
    if (this.activeParts.textPart) {
      this.activeParts.textPart.completedAt = createdAt;
      this.activeParts.textPart = null;
    }
  }

  private closeOpenStreamingParts(createdAt: string): void {
    this.closeActiveTextPart(createdAt);
    this.closeActiveReasoningPart(createdAt);
  }

  private handleReasoningChunk(text: string, createdAt: string): void {
    this.ensureActiveTurn(createdAt);
    this.closeActiveTextPart(createdAt);

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
    const { name: normalizedName } = inferNormalizedToolName(d);
    const nameKey = normalizedName.toLowerCase();
    const rawInput = d.rawInput ?? d.input;
    const input = stringifyPayload(rawInput);
    const target = deriveToolTarget(normalizedName !== 'unknown' ? normalizedName : toolName, input);
    const correlationValue = title ?? target;
    const correlationKey = correlationValue ? `${nameKey}:${correlationValue}` : undefined;
    const terminal = d.status === 'completed' || d.status === 'failed' || d.status === 'cancelled';
    const pendingByCorrelation = correlationKey ? this.shiftPendingNoIdToolByCorrelation(correlationKey) : undefined;

    if (pendingByCorrelation) {
      this.setToolCallIdOnData(data, pendingByCorrelation.id);
    } else if (terminal) {
      const candidates = this.pendingNoIdToolsByName.get(nameKey) ?? [];
      if (!correlationKey && candidates.length === 1) {
        const candidate = this.shiftPendingNoIdToolByName(nameKey)!;
        this.warnings.push({ code: 'AMBIGUOUS_TOOL_CORRELATION', message: `Merged no-id ${toolName} update by name only; target or title was missing.` });
        this.setToolCallIdOnData(data, candidate.id);
      } else {
        if (!correlationKey || candidates.length > 1) {
          this.warnings.push({ code: 'AMBIGUOUS_TOOL_CORRELATION', message: `Could not safely correlate no-id ${toolName} update; target or title was missing or ambiguous.` });
        }
        this.setToolCallIdOnData(data, `synthetic-${this.syntheticToolIdCounter++}`);
      }
    } else {
      const newId = `synthetic-${this.syntheticToolIdCounter++}`;
      this.setToolCallIdOnData(data, newId);
      this.pushPendingNoIdTool({ id: newId, nameKey, correlationKey });
    }
  }

  private pushPendingNoIdTool(candidate: PendingNoIdTool): void {
    this.pendingNoIdToolsByName.set(candidate.nameKey, [...(this.pendingNoIdToolsByName.get(candidate.nameKey) ?? []), candidate]);
    if (candidate.correlationKey) {
      this.pendingNoIdToolsByCorrelation.set(candidate.correlationKey, [...(this.pendingNoIdToolsByCorrelation.get(candidate.correlationKey) ?? []), candidate]);
    }
  }

  private shiftPendingNoIdToolByCorrelation(correlationKey: string): PendingNoIdTool | undefined {
    const queue = this.pendingNoIdToolsByCorrelation.get(correlationKey);
    if (!queue) return undefined;
    const candidate = queue.shift();
    if (!candidate) return undefined;
    if (queue.length === 0) this.pendingNoIdToolsByCorrelation.delete(correlationKey);
    this.removePendingNoIdTool(candidate);
    return candidate;
  }

  private shiftPendingNoIdToolByName(nameKey: string): PendingNoIdTool | undefined {
    const queue = this.pendingNoIdToolsByName.get(nameKey);
    if (!queue) return undefined;
    const candidate = queue.shift();
    if (!candidate) return undefined;
    if (queue.length === 0) this.pendingNoIdToolsByName.delete(nameKey);
    if (candidate.correlationKey) {
      const correlated = this.pendingNoIdToolsByCorrelation.get(candidate.correlationKey) ?? [];
      const remaining = correlated.filter(item => item !== candidate);
      if (remaining.length > 0) this.pendingNoIdToolsByCorrelation.set(candidate.correlationKey, remaining);
      else this.pendingNoIdToolsByCorrelation.delete(candidate.correlationKey);
    }
    return candidate;
  }

  private removePendingNoIdTool(candidate: PendingNoIdTool): void {
    const byName = this.pendingNoIdToolsByName.get(candidate.nameKey) ?? [];
    const remainingByName = byName.filter(item => item !== candidate);
    if (remainingByName.length > 0) this.pendingNoIdToolsByName.set(candidate.nameKey, remainingByName);
    else this.pendingNoIdToolsByName.delete(candidate.nameKey);
  }

  private clearPendingNoIdTools(): void {
    this.pendingNoIdToolsByName.clear();
    this.pendingNoIdToolsByCorrelation.clear();
  }

  private setToolCallIdOnData(data: Record<string, unknown>, id: string): void {
    const toolCall = data.toolCall;
    if (typeof toolCall === 'object' && toolCall !== null) {
      (toolCall as Record<string, unknown>).toolCallId = id;
    } else {
      data.toolCallId = id;
    }
  }

  private correlateUpdateToSyntheticTool(update: ToolCallUpdateData): string | undefined {
    const updateNameKey = (update.toolName ?? 'unknown').toLowerCase();
    const updateTitle = update.title ?? undefined;
    const updateTarget = deriveToolTarget(update.toolName ?? 'unknown', update.input);
    const updateCorrelationKey = updateTitle ?? updateTarget;

    if (!updateCorrelationKey) return undefined;

    for (const [localId, toolPart] of this.toolPartsById) {
      if (toolPart.tool.status === 'completed' || toolPart.tool.status === 'failed') continue;

      const partNameKey = (toolPart.tool.normalizedName ?? toolPart.tool.toolName ?? 'unknown').toLowerCase();
      if (partNameKey !== updateNameKey) continue;

      const partCorrelationKey = toolPart.tool.title ?? toolPart.tool.target;
      if (partCorrelationKey === updateCorrelationKey) {
        return localId;
      }
    }

    const candidates: Array<{ localId: string; score: number }> = [];
    for (const [localId, toolPart] of this.toolPartsById) {
      if (toolPart.tool.status === 'completed' || toolPart.tool.status === 'failed') continue;

      const partNameKey = (toolPart.tool.normalizedName ?? toolPart.tool.toolName ?? 'unknown').toLowerCase();
      if (partNameKey !== updateNameKey) continue;

      let score = 0;
      if (toolPart.tool.title === updateTitle) score += 3;
      if (toolPart.tool.target === updateTarget) score += 2;
      if (score === 0) continue;

      candidates.push({ localId, score });
    }

    if (candidates.length === 1) {
      return candidates[0].localId;
    }

    if (candidates.length > 1) {
      candidates.sort((a, b) => b.score - a.score);
      if (candidates[0].score > candidates[1].score) {
        return candidates[0].localId;
      }
    }

    return undefined;
  }

  private computeToolNormalization(d: Record<string, unknown>): { normalizedName: string; displayTitle?: string; displaySubtitle?: string; category?: string } {
    const { name, wasInferred } = inferNormalizedToolName(d);
    const displayTitle = inferDisplayTitle(d);
    const category = getToolCategory(name);
    const shouldMarkUnknown = !wasInferred && name === 'unknown';
    return { normalizedName: shouldMarkUnknown ? 'unknown' : name, displayTitle, displaySubtitle: undefined, category };
  }

  private refreshToolSemantics(tool: ToolPart['tool'], sourceName: string | undefined): void {
    const parsedRawInput = safeParseJson(tool.rawInput);
    const parsedRawOutput = safeParseJson(tool.rawOutput);
    const normalizationInput: Record<string, unknown> = {
      toolName: sourceName ?? tool.toolName,
      name: sourceName ?? tool.toolName,
      title: tool.title,
      metadata: tool.metadata,
    };
    if (parsedRawInput) normalizationInput.rawInput = parsedRawInput;
    if (parsedRawOutput) normalizationInput.rawOutput = parsedRawOutput;
    const previousName = tool.normalizedName ?? tool.toolName;
    const { normalizedName, displayTitle, category } = this.computeToolNormalization(normalizationInput);
    tool.normalizedName = normalizedName;
    tool.displayTitle = displayTitle ?? tool.title;
    tool.category = category;
    tool.target = deriveToolTarget(normalizedName !== 'unknown' ? normalizedName : (sourceName ?? tool.toolName), tool.input);
    tool.details = buildSemanticDetails(normalizedName, tool.rawInput, tool.rawOutput, tool.title, tool.error, tool.metadata);
    const semanticChangedFiles = mutationDetailsToChangedFiles(tool.details);
    if (semanticChangedFiles.length > 0) {
      tool.changedFiles = semanticChangedFiles;
    }

    const changedFiles = this.extractChangedFiles(normalizedName, tool.input, tool.rawInput);
    if (changedFiles.length > 0 && semanticChangedFiles.length === 0) {
      tool.changedFiles = changedFiles;
    }

    const { changedFiles: unifiedFiles, diff } = buildUnifiedDiff(normalizedName, tool.input ?? tool.rawInput, tool.metadata);
    if ((!tool.changedFiles || tool.changedFiles.length === 0) && unifiedFiles.length > 0) {
      tool.changedFiles = unifiedFiles;
    }
    if (diff) {
      tool.metadata = { ...tool.metadata, diff };
    }

    if (previousName === 'unknown' && normalizedName !== 'unknown') {
      this.hasUnknownTools = this.toolPartsById.size > 0 && [...this.toolPartsById.values()].some(part => (part.tool.normalizedName ?? part.tool.toolName) === 'unknown');
      this.warnings = this.warnings.filter(w => !(w.code === 'UNKNOWN_TOOL' && w.message.includes(tool.title ?? tool.target ?? sourceName ?? tool.toolCallId)));
    } else if (normalizedName === 'unknown') {
      this.recordUnknownTool(sourceName ?? tool.toolName, tool.displayTitle ?? tool.title, tool.target);
    }
  }

  private recordUnknownTool(sourceName: string | undefined, displayTitle: string | undefined, target: string | undefined): void {
    this.hasUnknownTools = true;
    const fallback = displayTitle ?? target ?? sourceName ?? this.currentEventId;
    this.warnings.push({ code: 'UNKNOWN_TOOL', message: `Could not normalize tool from: ${fallback}` });
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
    if (start.metadata) {
      d.metadata = start.metadata;
    }
    if (parsedRawInput) {
      d.rawInput = parsedRawInput;
    }
    if (parsedRawOutput) {
      d.rawOutput = parsedRawOutput;
    }
    const { normalizedName, displayTitle, category } = this.computeToolNormalization(d);

    const target = deriveToolTarget(start.toolName, start.input);
    if (normalizedName === 'unknown') {
      this.recordUnknownTool(start.toolName, displayTitle ?? start.title, target);
    }

    const suppressed = isSuppressedInternalTool(normalizedName, start.rawInput ?? start.input);
    const toolPart: ToolPart = {
      id: this.nextId('tool'),
      type: 'tool',
      hidden: suppressed || undefined,
      tool: {
        toolCallId: start.toolCallId,
        normalizedName,
        displayTitle: displayTitle ?? start.title,
        category,
        toolName: start.toolName,
        status: 'running',
        title: start.title,
        target,
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

    const { changedFiles: fc, diff } = buildUnifiedDiff(normalizedName, start.input, start.metadata);
    if (fc.length > 0 && !toolPart.tool.changedFiles) {
      toolPart.tool.changedFiles = fc;
      this.allChangedFiles.push(...fc);
    }
    if (diff) {
      toolPart.tool.metadata = { ...toolPart.tool.metadata, diff };
    }

    toolPart.tool.details = buildSemanticDetails(normalizedName, start.rawInput, start.rawOutput, start.title, undefined, start.metadata);

    this.toolPartsById.set(start.toolCallId, toolPart);
    if (start.toolCallId.startsWith('synthetic-')) {
      this.toolIdAliasLocalToProvider.set(start.toolCallId, start.toolCallId);
    } else {
      this.toolIdAliasProviderToLocal.set(start.toolCallId, start.toolCallId);
      this.toolIdAliasLocalToProvider.set(start.toolCallId, start.toolCallId);
    }
    this.closeOpenStreamingParts(start.createdAt);
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
    let resolvedLocalId: string | undefined = this.toolIdAliasProviderToLocal.get(update.toolCallId);
    if (!resolvedLocalId) {
      resolvedLocalId = this.toolPartsById.has(update.toolCallId) ? update.toolCallId : undefined;
    }

    if (!resolvedLocalId) {
      const correlatedId = this.correlateUpdateToSyntheticTool(update);
      if (correlatedId) {
        resolvedLocalId = correlatedId;
        this.toolIdAliasProviderToLocal.set(update.toolCallId, correlatedId);
      }
    }

    const existing = resolvedLocalId ? this.toolPartsById.get(resolvedLocalId) : undefined;
    if (existing && resolvedLocalId) {
      if (resolvedLocalId !== update.toolCallId) {
        existing.tool.toolCallId = update.toolCallId;
        this.toolIdAliasLocalToProvider.set(resolvedLocalId, update.toolCallId);
      }
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
      existing.tool.rawInput = chooseMoreSpecificPayload(existing.tool.rawInput, update.rawInput);
      existing.tool.rawOutput = chooseMoreSpecificPayload(existing.tool.rawOutput, update.rawOutput);
      if (update.metadata) {
        existing.tool.metadata = { ...existing.tool.metadata, ...update.metadata };
      }

      this.refreshToolSemantics(existing.tool, update.toolName ?? existing.tool.toolName);
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
      if (update.metadata) {
        d.metadata = update.metadata;
      }
      if (parsedRawInput) {
        d.rawInput = parsedRawInput;
      }
      if (parsedRawOutput) {
        d.rawOutput = parsedRawOutput;
      }
      const { normalizedName, displayTitle, category } = this.computeToolNormalization(d);

      const target = deriveToolTarget(update.toolName ?? normalizedName, update.input);
      if (normalizedName === 'unknown') {
        this.recordUnknownTool(update.toolName, displayTitle ?? update.title, target);
      }

      const suppressed = isSuppressedInternalTool(normalizedName, update.rawInput ?? update.input);
      const toolPart: ToolPart = {
        id: this.nextId('tool'),
        type: 'tool',
        hidden: suppressed || undefined,
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

      toolPart.tool.target = target;

      const changedFiles = this.extractChangedFiles(normalizedName, update.input, update.rawInput);
      if (changedFiles.length > 0) {
        toolPart.tool.changedFiles = changedFiles;
        this.allChangedFiles.push(...changedFiles);
      }

      const { changedFiles: fc, diff } = buildUnifiedDiff(normalizedName, update.input, update.metadata);
      if (fc.length > 0 && !toolPart.tool.changedFiles) {
        toolPart.tool.changedFiles = fc;
        this.allChangedFiles.push(...fc);
      }
      if (diff) {
        toolPart.tool.metadata = { ...toolPart.tool.metadata, diff };
      }

      toolPart.tool.details = buildSemanticDetails(normalizedName, update.rawInput, update.rawOutput, update.title, update.error, update.metadata);

      this.toolPartsById.set(update.toolCallId, toolPart);
      this.closeOpenStreamingParts(update.createdAt);
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
    this.closeOpenStreamingParts(createdAt);
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
