import type { SessionNotification } from '@agentclientprotocol/sdk';
import type { EventBus } from '../services/event-bus';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { SessionStreamLogRepo } from '../db/session-stream-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import { Log } from '../util/log';

const log = Log.create({ service: 'session-observer' });

export interface SessionObserver {
  onSessionStart?(ctx: SessionContext): void;
  onTextChunk?(ctx: SessionContext, text: string): void;
  onToolCall?(ctx: SessionContext, event: ToolCallEvent): void;
  onSessionEvent?(ctx: SessionContext, eventType: string, data: unknown): void;
  onStateChange?(ctx: SessionContext, from: SessionState, to: SessionState): void;
  onRawNotification?(ctx: SessionContext, notification: SessionNotification): void;
}

export interface SessionContext {
  readonly issueId: string;
  readonly issueNumber: number | undefined;
  readonly projectId: string;
  readonly executionId: string | undefined;
  readonly acpSessionId: string;
  readonly coderSessionId: string | undefined;
  readonly stage: string | undefined;
  readonly model: string | undefined;
  readonly processPid: number | undefined;
}

export type SessionState = 'initializing' | 'running' | 'completed' | 'failed' | 'timeout' | 'cancelled' | 'closed';

export interface ToolCallEvent {
  toolName: string;
  state: 'started' | 'completed';
  toolCallId: string;
  title?: string;
  rawInput?: unknown;
  rawOutput?: unknown;
}

export interface WorkflowSessionObserverDeps {
  eventBus?: EventBus;
  workflowLogRepo?: WorkflowLogRepo;
  sessionStreamLogRepo?: SessionStreamLogRepo;
  coderSessionRepo?: CoderSessionRepo;
  throttleMs?: number;
  taskDescription?: string;
  title?: string;
  stage?: string;
}

export class WorkflowSessionObserver implements SessionObserver {
  private eventBus: EventBus | undefined;
  private workflowLogRepo: WorkflowLogRepo | undefined;
  private sessionStreamLogRepo: SessionStreamLogRepo | undefined;
  private coderSessionRepo: CoderSessionRepo | undefined;
  private throttleMs: number;
  private taskDescription: string;
  private title: string | undefined;
  private stage: string | undefined;
  private lastTextChunkTime = 0;
  private coderToolCallCounter = 0;
  private coderToolCallIds = new Map<string, string[]>();
  private _coderSessionId: string | undefined;

  constructor(deps: WorkflowSessionObserverDeps) {
    this.eventBus = deps.eventBus;
    this.workflowLogRepo = deps.workflowLogRepo;
    this.sessionStreamLogRepo = deps.sessionStreamLogRepo;
    this.coderSessionRepo = deps.coderSessionRepo;
    this.throttleMs = deps.throttleMs ?? 100;
    this.taskDescription = deps.taskDescription ?? '';
    this.title = deps.title;
    this.stage = deps.stage;
  }

  get coderSessionId(): string | undefined {
    return this._coderSessionId;
  }

  onSessionStart(ctx: SessionContext): void {
    const sseIssueId = ctx.issueNumber ? String(ctx.issueNumber) : (ctx.issueId ?? '');

    if (this.coderSessionRepo && ctx.issueId) {
      try {
        const coderSession = this.coderSessionRepo.insert({
          issueId: ctx.issueId,
          acpSessionId: ctx.acpSessionId,
          executionId: ctx.executionId,
          taskDescription: this.taskDescription,
          stage: this.stage ?? ctx.stage,
          title: this.title,
          processPid: ctx.processPid ?? null,
        });
        this._coderSessionId = coderSession.id;
        log.info('coder_session row created', { coderSessionId: this._coderSessionId, acpSessionId: ctx.acpSessionId });
      } catch (err) {
        log.error('Failed to create coder_session row', { error: err instanceof Error ? err.message : String(err) });
      }
    }

    if (this.eventBus && this._coderSessionId) {
      try {
        this.eventBus.emit('coder_session_started', {
          issueId: sseIssueId,
          projectId: ctx.projectId ?? '',
          coderSessionId: this._coderSessionId,
          acpSessionId: ctx.acpSessionId,
          executionId: ctx.executionId,
          model: ctx.model,
          stage: this.stage ?? ctx.stage,
          taskDescription: this.taskDescription,
          title: this.title ?? null,
        });
      } catch (err) {
        log.error('Failed to emit coder_session_started', { error: err instanceof Error ? err.message : String(err) });
      }
    }
  }

  onTextChunk(ctx: SessionContext, text: string): void {
    if (!this.eventBus || !ctx.executionId) return;
    const sseIssueId = String(ctx.issueNumber ?? ctx.issueId ?? '');
    const now = Date.now();
    if (this.throttleMs === 0 || now - this.lastTextChunkTime >= this.throttleMs) {
      this.eventBus.emit('coder_text_chunk', {
        issueId: sseIssueId,
        projectId: ctx.projectId ?? '',
        executionId: ctx.executionId,
        acpSessionId: ctx.acpSessionId,
        text,
      });
      this.lastTextChunkTime = now;
    }
  }

  onToolCall(ctx: SessionContext, event: ToolCallEvent): void {
    if (!this.eventBus || !ctx.executionId) return;
    const sseIssueId = String(ctx.issueNumber ?? ctx.issueId ?? '');
    this.eventBus.emit('coder_tool_call', {
      issueId: sseIssueId,
      projectId: ctx.projectId ?? '',
      executionId: ctx.executionId,
      acpSessionId: ctx.acpSessionId,
      toolName: event.toolName,
      state: event.state,
      toolCallId: event.toolCallId,
      title: event.title,
      rawInput: event.rawInput,
      rawOutput: event.rawOutput,
    });
  }

  onSessionEvent(ctx: SessionContext, eventType: string, data: unknown): void {
    try {
      const update = data as Record<string, unknown>;
      const SESSION_STREAM_EVENT_TYPES = new Set([
        'agent_thought_chunk',
        'agent_message_chunk',
        'tool_call',
        'tool_call_update',
        'user_message_chunk',
      ]);

      if (SESSION_STREAM_EVENT_TYPES.has(eventType)) {
        this.sessionStreamLogRepo?.insert(
          ctx.issueId ?? '',
          ctx.acpSessionId,
          eventType,
          update,
        );
      } else if (this.workflowLogRepo) {
        this.workflowLogRepo.insert(
          ctx.issueId ?? '',
          ctx.acpSessionId || null,
          eventType,
          update,
        );
      }
    } catch {}
  }

  onStateChange(_ctx: SessionContext, _from: SessionState, to: SessionState): void {
    if (!this.coderSessionRepo || !this._coderSessionId) return;

    if (to === 'completed' || to === 'failed' || to === 'timeout' || to === 'cancelled') {
      try {
        this.coderSessionRepo.updateStatus(this._coderSessionId, to);
      } catch (err) {
        log.error('Failed to update coder_session status', { error: err instanceof Error ? err.message : String(err) });
      }
    }
  }

  onRawNotification(_ctx: SessionContext, _notification: SessionNotification): void {
  }

  nextToolCallId(acpSessionId: string, toolName: string, state: 'started' | 'completed'): string {
    if (state === 'started') {
      const toolCallId = `${acpSessionId}-${toolName}-${this.coderToolCallCounter++}`;
      const key = `${acpSessionId}-${toolName}`;
      const list = this.coderToolCallIds.get(key) ?? [];
      list.push(toolCallId);
      this.coderToolCallIds.set(key, list);
      return toolCallId;
    } else {
      const key = `${acpSessionId}-${toolName}`;
      const list = this.coderToolCallIds.get(key) ?? [];
      const toolCallId = list.shift() ?? `${acpSessionId}-${toolName}-${this.coderToolCallCounter++}`;
      if (list.length > 0) {
        this.coderToolCallIds.set(key, list);
      } else {
        this.coderToolCallIds.delete(key);
      }
      return toolCallId;
    }
  }

  writeSessionLog(
    issueId: string | undefined,
    eventType: string,
    data: Record<string, unknown>,
  ): void {
    if (!this.workflowLogRepo || !issueId) return;
    try {
      this.workflowLogRepo.insert(issueId, null, eventType, data);
    } catch (e) {
      log.warn('workflowLogRepo.insert failed', { eventType, issueId, error: e instanceof Error ? e.message : String(e) });
    }
  }
}
