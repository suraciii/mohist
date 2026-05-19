import {
  ClientSideConnection,
  PROTOCOL_VERSION,
} from '@agentclientprotocol/sdk';
import type {
  SessionNotification,
  RequestPermissionRequest,
  RequestPermissionResponse,
} from '@agentclientprotocol/sdk';
import { AcpProcess } from './acp-process';
import { createQuietThresholdMonitor, type QuietThresholdMonitor } from './quiet-threshold-monitor';
import type { SessionObserver, SessionContext, SessionState, ToolCallEvent, MohistPromptEvent } from './session-observer';
import { SessionStateMachine } from './session-state';
import { Log } from '../util/log';

const log = Log.create({ service: 'agent-session' });

export type PromptKind = 'initial' | 'task' | 'retry' | 'followup' | 'recovery';

export interface ExecutePromptOptions {
  kind?: PromptKind;
  title?: string;
  signal?: AbortSignal;
}

export interface AgentSessionOptions {
  cwd: string;
  task?: string;
  taskId?: string;
  timeout?: number;
  issueId?: string;
  projectId?: string;
  executionId?: string;
  issueNumber?: number;
  onSessionUpdate?: (notification: SessionNotification) => void;
  opencodeBinPath?: string;
  signal?: AbortSignal;
  observers?: SessionObserver[];
  onProcessSpawned?: (proc: import('child_process').ChildProcess) => void;
  stage?: string;
  model?: string;
  onBeforeKill?: (cwd: string) => Promise<boolean>;
  title?: string;
  livenessQuietThresholdMs?: number;
  probeTimeoutMs?: number;
}

export interface AcpSessionResult {
  text: string;
  success: boolean;
  error?: string;
  acpSessionId?: string;
  wipCommitted?: boolean;
  failureKind?: 'session_failed' | 'timeout' | 'cancelled';
  failureReason?: string;
}

const DEFAULT_TIMEOUT = 30 * 60 * 1000;
const PER_ROUND_TIMEOUT = 15 * 60 * 1000;
const MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024;
const DEFAULT_LIVENESS_QUIET_THRESHOLD_MS = 5 * 60 * 1000;
const DEFAULT_PROBE_TIMEOUT_MS = 30 * 1000;

export function truncateAgentText(text: string): string {
  if (text.length <= MAX_AGENT_TEXT_LENGTH) return text;
  const keepLength = Math.floor(MAX_AGENT_TEXT_LENGTH / 2);
  const head = text.slice(0, keepLength);
  const tail = text.slice(-keepLength);
  const truncated = text.length - MAX_AGENT_TEXT_LENGTH;
  return `${head}\n\n...[truncated ${truncated} characters]...\n\n${tail}`;
}

function createTimeout(ms: number): Promise<'timeout'> {
  return new Promise<'timeout'>((resolve) => setTimeout(() => resolve('timeout'), ms));
}

class ToolCallIdGenerator {
  private counters = new Map<string, number>();
  private ids = new Map<string, string[]>();
  private toolNamesById = new Map<string, string>();

  nextToolCallId(acpSessionId: string, toolName: string, state: 'started' | 'completed'): string {
    if (state === 'started') {
      const toolCallId = `${acpSessionId}-${toolName}-${this.counters.get(acpSessionId) ?? 0}`;
      this.counters.set(acpSessionId, (this.counters.get(acpSessionId) ?? 0) + 1);
      this.rememberStartedToolCallId(acpSessionId, toolName, toolCallId);
      return toolCallId;
    } else {
      const toolCallId = this.takeStartedToolCallId(acpSessionId, toolName) ?? `${acpSessionId}-${toolName}-${this.counters.get(acpSessionId) ?? 0}`;
      return toolCallId;
    }
  }

  rememberStartedToolCallId(acpSessionId: string, toolName: string, toolCallId: string): void {
    const key = `${acpSessionId}-${toolName}`;
    const list = this.ids.get(key) ?? [];
    if (!list.includes(toolCallId)) list.push(toolCallId);
    this.ids.set(key, list);
    this.toolNamesById.set(toolCallId, toolName);
  }

  takeStartedToolCallId(acpSessionId: string, toolName: string): string | undefined {
    const key = `${acpSessionId}-${toolName}`;
    const list = this.ids.get(key) ?? [];
    const toolCallId = list.shift();
    if (list.length > 0) {
      this.ids.set(key, list);
    } else {
      this.ids.delete(key);
    }
    return toolCallId;
  }

  getToolName(id: string): string | undefined {
    return this.toolNamesById.get(id);
  }

  setToolNameForId(id: string, toolName: string): void {
    this.toolNamesById.set(id, toolName);
  }
}

interface NormalizedToolCall {
  toolCallId: string;
  toolName: string;
  status: string;
  title?: string;
  input?: unknown;
  output?: unknown;
  outputMetadata?: Record<string, unknown>;
  metadata?: Record<string, unknown>;
}

function knownName(name: string | undefined): string | undefined {
  return name && name !== 'unknown' ? name : undefined;
}

function inferToolName(update: Record<string, unknown>, toolCall: Record<string, unknown> | undefined): string {
  const nestedName = knownName(toolCall?.toolName as string | undefined)
    ?? knownName(toolCall?.name as string | undefined);
  if (nestedName) return nestedName;

  const topLevelName = knownName(update.toolName as string | undefined)
    ?? knownName(update.name as string | undefined);
  if (topLevelName) return topLevelName;

  for (const value of [toolCall?.metadata, update.metadata, toolCall?.output, toolCall?.input, update.output, update.input]) {
    if (isRecord(value)) {
      const metaName = value.toolName as string | undefined ?? value.name as string | undefined;
      if (metaName) return metaName;
    }
  }

  const titleName = inferToolNameFromTitle(toolCall?.title as string | undefined ?? update.title as string | undefined);
  if (titleName) return titleName;

  for (const value of [toolCall?.input, update.input, toolCall?.output, update.output]) {
    const payloadName = inferToolNameFromPayload(value);
    if (payloadName) return payloadName;
  }

  const status = (toolCall?.status as string) ?? '';
  if (status === 'completed') {
    const output = toolCall?.output;
    if (isRecord(output)) {
      const outName = output.toolName as string | undefined ?? output.name as string | undefined;
      if (outName) return outName;
    }
  }

  return 'unknown';
}

function inferToolNameFromTitle(title: string | undefined): string | undefined {
  if (!title) return undefined;
  const lower = title.toLowerCase();
  if (lower.includes('apply_patch') || lower.includes('patch')) return 'apply_patch';
  if (lower.includes('bash') || lower.includes('command')) return 'bash';
  if (lower.includes('todo')) return 'todowrite';
  if (lower.includes('grep')) return 'grep';
  if (lower.includes('glob')) return 'glob';
  if (lower.includes('search')) return 'search';
  if (lower.includes('read')) return 'read';
  if (lower.includes('write')) return 'write';
  if (lower.includes('edit')) return 'edit';
  return undefined;
}

function inferToolNameFromPayload(payload: unknown): string | undefined {
  if (!isRecord(payload)) return undefined;
  if (typeof payload.patchText === 'string' || typeof payload.patch === 'string') return 'apply_patch';
  if (typeof payload.command === 'string' || typeof payload.script === 'string') return 'bash';
  if (Array.isArray(payload.todos)) return 'todowrite';
  if (typeof payload.pattern === 'string') return 'glob';
  if (typeof payload.query === 'string' || typeof payload.search === 'string') return 'search';
  if (
    typeof payload.file_path === 'string'
    || typeof payload.filePath === 'string'
    || typeof payload.path === 'string'
  ) {
    const contentKeys = ['content', 'oldStr', 'newStr', 'old_string', 'new_string'];
    if (contentKeys.some(key => payload[key] !== undefined)) return 'edit';
    return 'read';
  }
  for (const value of Object.values(payload)) {
    const nestedName = inferToolNameFromPayload(value);
    if (nestedName) return nestedName;
  }
  return undefined;
}

function extractProviderId(update: Record<string, unknown>, toolCall: Record<string, unknown> | undefined): string | undefined {
  return toolCall?.toolCallId as string | undefined
    ?? toolCall?.id as string | undefined
    ?? toolCall?.callId as string | undefined
    ?? update.toolCallId as string | undefined
    ?? update.id as string | undefined
    ?? update.callId as string | undefined;
}

function mapStatusToState(status: string): 'started' | 'completed' {
  return status === 'completed' ? 'completed' : 'started';
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function normalizeToolCallNotification(
  update: Record<string, unknown>,
  eventType: string,
  sessionId: string,
  wfObserver: SessionObserver | undefined,
  idGenerator: ToolCallIdGenerator,
): NormalizedToolCall {
  let toolCall = update.toolCall as Record<string, unknown> | undefined;
  const isUpdate = eventType === 'tool_call_update';

  if (!toolCall) {
    toolCall = {};
    update.toolCall = toolCall;
  }

  const topLevelOutput = update.output;
  const topLevelMetadata = update.metadata as Record<string, unknown> | undefined;
  if (update.status !== undefined && toolCall.status === undefined) toolCall.status = update.status;
  if (update.title !== undefined && toolCall.title === undefined) toolCall.title = update.title;
  if (update.input !== undefined && toolCall.input === undefined) toolCall.input = update.input;
  if (topLevelOutput !== undefined && toolCall.output === undefined) toolCall.output = topLevelOutput;
  if (topLevelMetadata && toolCall.metadata === undefined) toolCall.metadata = topLevelMetadata;
  if (topLevelMetadata && isRecord(toolCall.output) && toolCall.output.metadata === undefined) {
    toolCall.output.metadata = topLevelMetadata;
  }

  const providerId = extractProviderId(update, toolCall);
  const inferredName = inferToolName(update, toolCall);
  const status = (toolCall?.status as string) ?? (isUpdate ? 'completed' : '');
  const state = mapStatusToState(status);

  let toolCallId: string;
  let storedName: string | undefined;
  if (providerId) {
    toolCallId = providerId;
    storedName = idGenerator.getToolName(providerId);
  } else if (wfObserver) {
    toolCallId = wfObserver.nextToolCallId!(sessionId, inferredName, state);
  } else {
    toolCallId = idGenerator.nextToolCallId(sessionId, inferredName, state);
  }

  toolCall.toolCallId = toolCallId;

  let finalName = knownName(toolCall.toolName as string | undefined) ?? inferredName;
  if (!finalName || finalName === 'unknown') {
    if (storedName) {
      finalName = storedName;
    }
  }
  if (finalName && finalName !== 'unknown') {
    idGenerator.setToolNameForId(toolCallId, finalName);
    if (state === 'started' && providerId) {
      idGenerator.rememberStartedToolCallId(sessionId, finalName, toolCallId);
      wfObserver?.rememberStartedToolCallId?.(sessionId, finalName, toolCallId);
    }
  }
  toolCall.toolName = finalName;

  if (status && !toolCall.status) {
    toolCall.status = status;
  }

  return {
    toolCallId,
    toolName: finalName,
    status,
    title: toolCall.title as string | undefined,
    input: toolCall.input,
    output: toolCall.output,
    metadata: toolCall.metadata as Record<string, unknown> | undefined,
    outputMetadata: toolCall.metadata as Record<string, unknown> | undefined
      ?? (isRecord(toolCall.output) ? toolCall.output.metadata as Record<string, unknown> | undefined : undefined),
  };
}

function buildRawNotificationObserver(
  onSessionUpdate: (notification: SessionNotification) => void,
): SessionObserver {
  return { onRawNotification(_ctx, n) { onSessionUpdate(n); } };
}

export class AgentSession {
  private _options: AgentSessionOptions;
  private _acpProcess: AcpProcess;
  private _connection!: ClientSideConnection;
  private _observers: SessionObserver[];
  private _wfObserver: SessionObserver | undefined;
  private _stateMachine: SessionStateMachine;
  private _sessionId = '';
  private _agentText = '';
  private _agentTextTruncated = false;
  private _closed = false;
  private _sessionStartTime: number;
  private _toolCallIdGenerator = new ToolCallIdGenerator();
  private _lastDataAt: number;
  private _probeSentAt: number | null = null;
  private _probeDeadlineAt: number | null = null;
  private _activeProbe = false;
  private _probeSendFailure: Error | null = null;
  private _resolveProbeSendFailure: (() => void) | null = null;
  private _failureReason: string | null = null;
  private _livenessQuietThresholdMs: number;
  private _probeTimeoutMs: number;
  private _resetQuietTimer: (() => void) | null = null;

  get state(): SessionState { return this._stateMachine.current; }
  get acpSessionId(): string { return this._sessionId; }

  canClose(): boolean {
    return this._stateMachine.current === 'running'
      || this._stateMachine.current === 'probing'
      || this._stateMachine.current === 'initializing';
  }

  private constructor(
    options: AgentSessionOptions,
    acpProcess: AcpProcess,
    observers: SessionObserver[],
  ) {
    this._options = options;
    this._acpProcess = acpProcess;
    this._observers = observers;
    this._wfObserver = observers.find(o => typeof o.nextToolCallId === 'function');
    this._sessionStartTime = Date.now();
    this._lastDataAt = this._sessionStartTime;
    this._stateMachine = new SessionStateMachine('initializing');
    this._livenessQuietThresholdMs = options.livenessQuietThresholdMs ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS;
    this._probeTimeoutMs = options.probeTimeoutMs ?? DEFAULT_PROBE_TIMEOUT_MS;
  }

  private setupConnection(): void {
    const self = this;
    this._connection = new ClientSideConnection(
      () => ({
        sessionUpdate: async (notification: SessionNotification) => {
          try {
            self.handleSessionUpdate(notification);
          } catch (err) {
            log.error('sessionUpdate error', { error: err instanceof Error ? err.message : String(err) });
          }
        },
        requestPermission: async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
          const allow = params.options.find((o) => o.kind === 'allow_once' || o.kind === 'allow_always');
          if (allow) return { outcome: { outcome: 'selected', optionId: allow.optionId } };
          return { outcome: { outcome: 'cancelled' } };
        },
      }),
      this._acpProcess.stream,
    );
  }

  private refreshLastDataAt(options: { notifyRunning?: boolean } = {}): void {
    this._lastDataAt = Date.now();
    if (this._stateMachine.current === 'probing') {
      this._activeProbe = false;
      this._probeDeadlineAt = null;
      try {
        this._stateMachine.transition('running');
      } catch (err) {
        log.warn('stateMachine transition to running from probing failed', { error: err instanceof Error ? err.message : String(err) });
      }
      const ctx = this.makeCtx();
      for (const obs of this._observers) {
        try { obs.onStateChange?.(ctx, 'probing', 'running'); } catch (err) {
          log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
        }
      }
      this.notifyLivenessUpdate(ctx, 'running');
    } else if (options.notifyRunning && this._stateMachine.current === 'running') {
      this.notifyLivenessUpdate(this.makeCtx(), 'running');
    }
    if (this._resetQuietTimer && this._stateMachine.current === 'running') {
      this._resetQuietTimer();
    }
  }

  private notifyLivenessUpdate(ctx: SessionContext, status: SessionState, extra?: Partial<{ lastDataAt: string | null; probeSentAt: string | null; probeDeadlineAt: string | null; failureReason: string | null }>): void {
    for (const obs of this._observers) {
      try {
        obs.onLivenessUpdate?.(ctx, {
          status,
          lastDataAt: extra?.lastDataAt ?? new Date(this._lastDataAt).toISOString(),
          probeSentAt: extra?.probeSentAt ?? (this._probeSentAt ? new Date(this._probeSentAt).toISOString() : null),
          probeDeadlineAt: extra?.probeDeadlineAt ?? (this._probeDeadlineAt ? new Date(this._probeDeadlineAt).toISOString() : null),
          failureReason: extra?.failureReason ?? this._failureReason,
        });
      } catch (err) {
        log.error('onLivenessUpdate observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }
  }

  private transitionToFailed(reason?: string | null): void {
    if (!this._failureReason) {
      this._failureReason = reason ?? 'unknown';
    }
    try {
      this._stateMachine.transition('failed');
    } catch (err) {
      log.warn('stateMachine transition to failed failed', { error: err instanceof Error ? err.message : String(err) });
    }
    const ctx = this.makeCtx();
    this.notifyLivenessUpdate(ctx, 'failed');
  }

  private createQuietThresholdMonitor(): QuietThresholdMonitor {
    return createQuietThresholdMonitor(this._livenessQuietThresholdMs);
  }

  private createPromptResult(roundStartIndex: number, overrides: Omit<Partial<AcpSessionResult>, 'text'> = {}): AcpSessionResult {
    return {
      text: this._agentText.slice(roundStartIndex),
      success: overrides.success ?? false,
      acpSessionId: overrides.acpSessionId ?? this._sessionId,
      ...overrides,
    };
  }

  private createAbortPromise(signal: AbortSignal | undefined): Promise<'aborted'> {
    if (!signal) {
      return new Promise<'aborted'>(() => {});
    }

    return new Promise<'aborted'>((resolve) => {
      if (signal.aborted) {
        resolve('aborted');
        return;
      }
      const onAbort = () => resolve('aborted');
      signal.addEventListener('abort', onAbort, { once: true });
    });
  }

  private createProbeDeadlinePromise(): Promise<'probe_deadline'> {
    if (!this._probeDeadlineAt) {
      return new Promise<'probe_deadline'>(() => {});
    }

    return new Promise<'probe_deadline'>((resolve) => {
      const ms = this._probeDeadlineAt! - Date.now();
      if (ms <= 0) {
        resolve('probe_deadline');
        return;
      }
      setTimeout(() => resolve('probe_deadline'), ms);
    });
  }

  private shouldProbeForQuietThreshold(): boolean {
    return Date.now() - this._lastDataAt >= this._livenessQuietThresholdMs
      && this._stateMachine.current === 'running';
  }

  private async handleQuietThreshold(quietThresholdMonitor: QuietThresholdMonitor): Promise<void> {
    if (this.shouldProbeForQuietThreshold()) {
      quietThresholdMonitor.clear();
      await this.startProbe();
      quietThresholdMonitor.start();
      return;
    }

    quietThresholdMonitor.restart();
  }

  private async waitForPromptProgress(
    promptPromise: Promise<unknown>,
    timeout: number,
    abortPromise: Promise<'aborted'>,
    quietThresholdMonitor: QuietThresholdMonitor,
  ): Promise<unknown | 'timeout' | 'aborted' | 'probe_deadline' | 'quiet_threshold' | 'probe_send_failed'> {
    return Promise.race([
      promptPromise,
      createTimeout(timeout),
      abortPromise,
      this._acpProcess.exitFailure,
      this.createProbeDeadlinePromise(),
      quietThresholdMonitor.promise(),
      this.createProbeSendFailurePromise(),
    ]);
  }

  private async monitorPromptExecution(
    promptPromise: Promise<unknown>,
    roundStartIndex: number,
    timeout: number,
    abortPromise: Promise<'aborted'>,
    quietThresholdMonitor: QuietThresholdMonitor,
  ): Promise<AcpSessionResult> {
    while (true) {
      const result = await this.waitForPromptProgress(promptPromise, timeout, abortPromise, quietThresholdMonitor);

      if (result === 'quiet_threshold') {
        await this.handleQuietThreshold(quietThresholdMonitor);
        continue;
      }

      if (result === 'probe_deadline') {
        const deadlineResult = await this.handleProbeDeadline(roundStartIndex, quietThresholdMonitor);
        if (deadlineResult) return deadlineResult;
        continue;
      }

      if (result === 'probe_send_failed') {
        return this.handleProbeSendFailure(roundStartIndex, quietThresholdMonitor);
      }

      if (result === 'aborted') {
        return this.handleAbort(roundStartIndex, quietThresholdMonitor);
      }

      if (result === 'timeout') {
        return this.handleTimeout(roundStartIndex, timeout, quietThresholdMonitor);
      }

      quietThresholdMonitor.clear();
      this.refreshLastDataAt({ notifyRunning: true });
      return this.createPromptResult(roundStartIndex, { success: true });
    }
  }

  private async handleExecuteError(
    err: unknown,
    roundStartIndex: number,
  ): Promise<AcpSessionResult> {
    const duration = Date.now() - this._sessionStartTime;
    log.error('ACP execute failed', {
      sessionId: this._sessionId, elapsedMs: duration, error: err instanceof Error ? err.message : String(err),
    });
    this._failureReason = err instanceof Error ? err.message : String(err);
    this.transitionToFailed();
    const failCtx = this.makeCtx();
    for (const obs of this._observers) {
      try { obs.onStateChange?.(failCtx, 'running', 'failed'); } catch (obsErr) {
        log.error('onStateChange observer failed', { error: obsErr instanceof Error ? obsErr.message : String(obsErr) });
      }
    }
    let wipCommitted = false;
    if (this._options.onBeforeKill) {
      try { wipCommitted = await this._options.onBeforeKill(this._options.cwd); } catch (killErr) {
        log.warn('onBeforeKill failed on error', { error: killErr instanceof Error ? killErr.message : String(killErr) });
      }
    }
    await this._acpProcess.cleanup();
    const message = err instanceof Error ? err.message : String(err);
    return this.createPromptResult(roundStartIndex, { success: false, error: message, wipCommitted, failureKind: 'session_failed', failureReason: this._failureReason });
  }

  private async handleProbeDeadline(roundStartIndex: number, quietThresholdMonitor: QuietThresholdMonitor): Promise<AcpSessionResult | null> {
    if (!this._activeProbe || this._closed) {
      this._probeDeadlineAt = null;
      return null;
    }

    quietThresholdMonitor.clear();
    this._failureReason = 'probe_timeout';
    this.transitionToFailed();
    const failCtx = this.makeCtx();
    for (const obs of this._observers) {
      try { obs.onStateChange?.(failCtx, 'probing', 'failed'); } catch (err) {
        log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }
    await this._acpProcess.cleanup();
    this._closed = true;
    return this.createPromptResult(roundStartIndex, {
      success: false,
      error: 'Session liveness probe timed out',
      failureKind: 'session_failed',
      failureReason: this._failureReason,
    });
  }

  private createProbeSendFailurePromise(): Promise<'probe_send_failed'> {
    if (this._probeSendFailure) {
      return Promise.resolve('probe_send_failed');
    }
    return new Promise<'probe_send_failed'>((resolve) => {
      this._resolveProbeSendFailure = () => resolve('probe_send_failed');
    });
  }

  private async handleProbeSendFailure(roundStartIndex: number, quietThresholdMonitor: QuietThresholdMonitor): Promise<AcpSessionResult> {
    quietThresholdMonitor.clear();
    const error = this._probeSendFailure;
    this._probeSendFailure = null;
    this._failureReason = error?.message ?? 'probe_send_failed';
    this.transitionToFailed();
    const failCtx = this.makeCtx();
    for (const obs of this._observers) {
      try { obs.onStateChange?.(failCtx, 'probing', 'failed'); } catch (err) {
        log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }
    await this._acpProcess.cleanup();
    this._closed = true;
    return this.createPromptResult(roundStartIndex, {
      success: false,
      error: this._failureReason,
      failureKind: 'session_failed',
      failureReason: this._failureReason,
    });
  }

  private async handleAbort(roundStartIndex: number, quietThresholdMonitor: QuietThresholdMonitor): Promise<AcpSessionResult> {
    quietThresholdMonitor.clear();
    log.info('ACP prompt aborted by signal', { sessionId: this._sessionId });
    try { this._stateMachine.transition('cancelled'); } catch (err) {
      log.warn('stateMachine transition to cancelled failed', { error: err instanceof Error ? err.message : String(err) });
    }
    const cancelCtx = this.makeCtx();
    for (const obs of this._observers) {
      try { obs.onStateChange?.(cancelCtx, 'running', 'cancelled'); } catch (err) {
        log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }
    try { await this._connection.cancel({ sessionId: this._sessionId }); } catch (err) {
      log.warn('cancel failed on abort', { sessionId: this._sessionId, error: err instanceof Error ? err.message : String(err) });
    }
    let wipCommitted = false;
    if (this._options.onBeforeKill) {
      try { wipCommitted = await this._options.onBeforeKill(this._options.cwd); } catch (err) {
        log.warn('onBeforeKill failed on abort', { error: err instanceof Error ? err.message : String(err) });
      }
    }
    await this._acpProcess.cleanup();
    this._closed = true;
    return this.createPromptResult(roundStartIndex, {
      success: false,
      error: 'Agent stopped by user',
      wipCommitted,
      failureKind: 'cancelled',
    });
  }

  private async handleTimeout(roundStartIndex: number, timeout: number, quietThresholdMonitor: QuietThresholdMonitor): Promise<AcpSessionResult> {
    quietThresholdMonitor.clear();
    const duration = Date.now() - this._sessionStartTime;
    log.error('ACP prompt timed out', { sessionId: this._sessionId, timeout, elapsedMs: duration });
    this._failureReason = 'timeout';
    this.transitionToFailed('timeout');
    const failCtx = this.makeCtx();
    for (const obs of this._observers) {
      try { obs.onStateChange?.(failCtx, 'running', 'failed'); } catch (err) {
        log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }
    try { await this._connection.cancel({ sessionId: this._sessionId }); } catch (err) {
      log.warn('cancel failed on timeout', { sessionId: this._sessionId, error: err instanceof Error ? err.message : String(err) });
    }
    let wipCommitted = false;
    if (this._options.onBeforeKill) {
      try { wipCommitted = await this._options.onBeforeKill(this._options.cwd); } catch (err) {
        log.warn('onBeforeKill failed on timeout', { error: err instanceof Error ? err.message : String(err) });
      }
    }
    this._closed = true;
    await this._acpProcess.cleanup();
    return this.createPromptResult(roundStartIndex, {
      success: false,
      error: `Timed out after ${timeout / 1000}s`,
      wipCommitted,
      failureKind: 'timeout',
      failureReason: this._failureReason,
    });
  }

  private async startProbe(): Promise<void> {
    this._probeSentAt = Date.now();
    this._probeDeadlineAt = this._probeSentAt + this._probeTimeoutMs;
    this._activeProbe = true;

    const fromState = this._stateMachine.current;
    try {
      this._stateMachine.transition('probing');
    } catch (err) {
      log.warn('stateMachine transition to probing failed', { error: err instanceof Error ? err.message : String(err) });
    }

    const ctx = this.makeCtx();
    for (const obs of this._observers) {
      try { obs.onStateChange?.(ctx, fromState, 'probing'); } catch (err) {
        log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }
    this.notifyLivenessUpdate(ctx, 'probing');

    const probeText = 'If this session is still alive, briefly report the current step and continue from existing context. Do not restart completed work.';

    let probePromise: Promise<unknown>;
    try {
      probePromise = this._connection.prompt({
        sessionId: this._sessionId,
        prompt: [{ type: 'text', text: probeText }],
      });
    } catch (err) {
      log.warn('probe prompt failed', { sessionId: this._sessionId, error: err instanceof Error ? err.message : String(err) });
      this._probeSendFailure = err instanceof Error ? err : new Error(String(err));
      this._resolveProbeSendFailure?.();
      this._resolveProbeSendFailure = null;
      return;
    }

    probePromise.then(() => {
      if (this._activeProbe && !this._closed) {
        this.refreshLastDataAt({ notifyRunning: true });
      }
    }).catch((err) => {
      log.warn('probe prompt failed', { sessionId: this._sessionId, error: err instanceof Error ? err.message : String(err) });
      this._probeSendFailure = err instanceof Error ? err : new Error(String(err));
      this._resolveProbeSendFailure?.();
      this._resolveProbeSendFailure = null;
    });
  }

  private handleSessionUpdate(notification: SessionNotification): void {
    this.refreshLastDataAt();
    const update = notification.update;
    const eventType = update.sessionUpdate;

    if (eventType === 'agent_thought_chunk') {
      const contentBlock = update.content;
      const thoughtText = (contentBlock?.type === 'text' ? contentBlock.text : null);
      if (thoughtText) {
        const ctx = this.makeCtx();
        for (const obs of this._observers) {
          try { obs.onThoughtChunk?.(ctx, thoughtText); } catch (err) {
            log.error('onThoughtChunk observer failed', { error: err instanceof Error ? err.message : String(err) });
          }
        }
      }
    } else if (
      eventType === 'agent_message_chunk' &&
      update.content &&
      'text' in update.content
    ) {
      const textChunk = (update.content as { text: string }).text;
      if (!this._agentTextTruncated) {
        this._agentText += textChunk;
        if (this._agentText.length > MAX_AGENT_TEXT_LENGTH) {
          this._agentText = truncateAgentText(this._agentText);
          this._agentTextTruncated = true;
        }
      }
      const ctx = this.makeCtx();
      for (const obs of this._observers) {
        try { obs.onTextChunk?.(ctx, textChunk); } catch (err) {
          log.error('onTextChunk observer failed', { error: err instanceof Error ? err.message : String(err) });
        }
      }
    }

    if (eventType === 'tool_call' || eventType === 'tool_call_update') {
      const toolData = update as Record<string, unknown>;
      const normalized = normalizeToolCallNotification(
        toolData,
        eventType,
        this._sessionId,
        this._wfObserver,
        this._toolCallIdGenerator,
      );
      const state = mapStatusToState(normalized.status);
      const ctx = this.makeCtx();
      const event: ToolCallEvent = {
        toolName: normalized.toolName,
        state,
        toolCallId: normalized.toolCallId,
        title: normalized.title,
        rawInput: state === 'started' ? normalized.input : undefined,
        rawOutput: state === 'completed' ? normalized.output : undefined,
        rawOutputMetadata: state === 'completed' ? normalized.outputMetadata : undefined,
        metadata: normalized.metadata,
        status: normalized.status || undefined,
      };
      for (const obs of this._observers) {
        try { obs.onToolCall?.(ctx, event); } catch (err) {
          log.error('onToolCall observer failed', { toolName: event.toolName, error: err instanceof Error ? err.message : String(err) });
        }
      }
    }

    {
      const ctx = this.makeCtx();
      for (const obs of this._observers) {
        try { obs.onSessionEvent?.(ctx, eventType, update); } catch (err) {
          log.error('onSessionEvent observer failed', { eventType, error: err instanceof Error ? err.message : String(err) });
        }
      }
      for (const obs of this._observers) {
        try { obs.onRawNotification?.(ctx, notification); } catch (err) {
          log.error('onRawNotification observer failed', { error: err instanceof Error ? err.message : String(err) });
        }
      }
    }
  }

  private makeCtx(): SessionContext {
    return {
      issueId: this._options.issueId ?? '',
      issueNumber: this._options.issueNumber,
      projectId: this._options.projectId ?? '',
      executionId: this._options.executionId,
      acpSessionId: this._sessionId,
      stage: this._options.stage,
      model: this._options.model,
      processPid: this._acpProcess.process.pid ?? undefined,
    };
  }

  private buildPromptData(prompt: string, kind: string, sentAt: string, title?: string): MohistPromptEvent {
    const outputPath = this.extractOutputPath(prompt);
    const contextFiles = this.extractContextFiles(prompt);
    return {
      role: 'mohist',
      text: prompt,
      kind,
      sentAt,
      executionId: this._options.executionId,
      stage: this._options.stage,
      title: title ?? this._options.title,
      issueId: this._options.issueId,
      acpSessionId: this._sessionId,
      outputPath,
      contextFiles,
    };
  }

  private extractOutputPath(prompt: string): string | undefined {
    const match = prompt.match(/<contract>([\s\S]*?)<\/contract>/i);
    if (match) {
      return match[1].trim().split('\n')[0].trim();
    }
    return undefined;
  }

  private extractContextFiles(prompt: string): string[] | undefined {
    const match = prompt.match(/<context[-_]files>([\s\S]*?)<\/context[-_]files>/i);
    if (!match) return undefined;

    const files = match[1]
      .trim()
      .split('\n')
      .map(line => line.trim())
      .filter(line => line && !line.startsWith('<!--'))
      .map(line => {
        const atRef = line.match(/^@(\S+)/);
        if (atRef) return atRef[1];
        const xmlRef = line.match(/<file\s+path="([^"]+)"/i);
        if (xmlRef) return xmlRef[1];
        return line;
      })
      .filter(Boolean);
    return files.length > 0 ? files.slice(0, 5) : undefined;
  }

  static async create(options: AgentSessionOptions): Promise<AgentSession> {
    const {
      cwd,
      timeout = PER_ROUND_TIMEOUT,
      issueId,
      model,
    } = options;

    const observers: SessionObserver[] = [];
    if (options.onSessionUpdate) {
      observers.push(buildRawNotificationObserver(options.onSessionUpdate));
    }
    observers.push(...(options.observers ?? []));

    log.info('Spawning opencode acp subprocess for agent session', {
      cwd, timeout, issueId: issueId?.slice(0, 8), taskId: options.taskId, promptPreview: (options.task ?? '').slice(0, 100),
    });

    const acpProcess = new AcpProcess({
      cwd,
      opencodeBinPath: options.opencodeBinPath,
      onError: (err) => {
        const ctx = { issueId: issueId ?? '', acpSessionId: '', stage: options.stage, model: options.model, processPid: acpProcess.process.pid ?? undefined, executionId: options.executionId, projectId: options.projectId ?? '', issueNumber: options.issueNumber };
        for (const obs of observers) {
          try { obs.onSessionEvent?.(ctx, 'acp_session_process_error', { error: err.message, mode: 'agent-session', timestamp: new Date().toISOString() }); } catch (e) { /* ignore */ }
        }
      },
      onExit: ({ exitCode, phase }) => {
        const ctx = { issueId: issueId ?? '', acpSessionId: '', stage: options.stage, model: options.model, processPid: acpProcess.process.pid ?? undefined, executionId: options.executionId, projectId: options.projectId ?? '', issueNumber: options.issueNumber };
        for (const obs of observers) {
          try { obs.onSessionEvent?.(ctx, 'acp_session_process_exit', { exitCode, phase, mode: 'agent-session', timestamp: new Date().toISOString() }); } catch (e) { /* ignore */ }
        }
      },
    });

    if (options.onProcessSpawned) {
      options.onProcessSpawned(acpProcess.process);
    }

    const session = new AgentSession(options, acpProcess, observers);
    session.setupConnection();

    try {
      const initResult = await Promise.race([
        session._connection.initialize({
          protocolVersion: PROTOCOL_VERSION,
          clientInfo: { name: 'mohist', version: '0.1.0' },
        }),
        createTimeout(timeout),
        acpProcess.spawnFailure,
      ]).catch(async (err: unknown) => {
        await acpProcess.cleanup();
        throw err;
      });

      acpProcess.markInitialized();

      if (initResult === 'timeout') {
        const duration = Date.now() - session._sessionStartTime;
        log.error('ACP initialize timed out', { timeout, elapsedMs: duration });
        if (options.onBeforeKill) {
          try { await options.onBeforeKill(options.cwd); } catch (err) {
            log.warn('onBeforeKill failed on init timeout', { error: err instanceof Error ? err.message : String(err) });
          }
        }
        await acpProcess.cleanup();
        throw new Error('[INIT_TIMEOUT] ACP initialize timed out');
      }

      log.info('ACP initialized, creating session');
      session.refreshLastDataAt();

      const sessionResult = await Promise.race([
        session._connection.newSession({ cwd, mcpServers: [] }),
        createTimeout(timeout),
      ]);

      if (sessionResult === 'timeout') {
        const duration = Date.now() - session._sessionStartTime;
        log.error('ACP newSession timed out', { timeout, elapsedMs: duration });
        if (options.onBeforeKill) {
          try { await options.onBeforeKill(options.cwd); } catch (err) {
            log.warn('onBeforeKill failed on newSession timeout', { error: err instanceof Error ? err.message : String(err) });
          }
        }
        await acpProcess.cleanup();
        throw new Error('[NEWSESSION_TIMEOUT] ACP newSession timed out');
      }

      session._sessionId = sessionResult.sessionId;
      log.info('ACP session created', { sessionId: session._sessionId });
      session.refreshLastDataAt();

      if (model) {
        try {
          await session._connection.setSessionConfigOption({
            sessionId: session._sessionId, configId: 'model', value: model,
          });
          session.refreshLastDataAt();
          log.info('ACP session model set', { sessionId: session._sessionId, model });
        } catch (err) {
          session._options = { ...session._options, model: undefined };
          log.warn('setSessionConfigOption for model failed', {
            sessionId: session._sessionId, model, error: err instanceof Error ? err.message : String(err),
          });
        }
      }
    } catch (err) {
      throw err;
    }

    session._stateMachine.transition('running');
    const startCtx = session.makeCtx();
    for (const obs of session._observers) {
      try { obs.onSessionStart?.(startCtx); } catch (err) {
        log.error('onSessionStart observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }

    return session;
  }

  async execute(prompt: string, meta?: ExecutePromptOptions): Promise<AcpSessionResult> {
    if (this._closed) {
      return { text: '', success: false, error: 'Session is closed' };
    }

    const roundStartIndex = this._agentText.length;
    const timeout = this._options.timeout ?? PER_ROUND_TIMEOUT;

    const kind = meta?.kind ?? 'task';
    const sentAt = new Date().toISOString();

    const wfObs2 = this._wfObserver;
    if (wfObs2) {
      const promptData = this.buildPromptData(prompt, kind, sentAt, meta?.title);
      wfObs2.writeMohistPrompt!(this.makeCtx(), promptData);
    }

    const quietThresholdMonitor = this.createQuietThresholdMonitor();
    const abortPromise = this.createAbortPromise(meta?.signal ?? this._options.signal);

    this._resetQuietTimer = () => quietThresholdMonitor.restart();
    quietThresholdMonitor.start();

    try {
      const promptPromise = this._connection.prompt({
        sessionId: this._sessionId,
        prompt: [{ type: 'text', text: prompt }],
      });

      return await this.monitorPromptExecution(
        promptPromise,
        roundStartIndex,
        timeout,
        abortPromise,
        quietThresholdMonitor,
      );
    } catch (err) {
      quietThresholdMonitor.clear();
      return this.handleExecuteError(err, roundStartIndex);
    } finally {
      this._resetQuietTimer = null;
    }
  }

  async cancel(): Promise<void> {
    if (this._closed) return;
    try { await this._connection.cancel({ sessionId: this._sessionId }); } catch (err) {
      log.warn('cancel failed', { sessionId: this._sessionId, error: err instanceof Error ? err.message : String(err) });
    }
    try { this._stateMachine.transition('cancelled'); } catch (err) {
      log.warn('stateMachine transition to cancelled failed', { error: err instanceof Error ? err.message : String(err) });
    }
    const ctx = this.makeCtx();
    for (const obs of this._observers) {
      try { obs.onStateChange?.(ctx, 'running', 'cancelled'); } catch (err) {
        log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }
  }

  async close(): Promise<void> {
    if (this._closed) return;
    this._closed = true;
    const duration = Date.now() - this._sessionStartTime;
    log.info('Agent session closed', { sessionId: this._sessionId, elapsedMs: duration });
    const fromState = this._stateMachine.current;
    if (!this.canClose()) {
      try { this._stateMachine.transition('closed'); } catch (err) {
        log.warn('stateMachine transition to closed failed', { error: err instanceof Error ? err.message : String(err) });
      }
      await this._acpProcess.cleanup();
      return;
    }
    try { this._stateMachine.transition('completed'); } catch (err) {
      log.warn('stateMachine transition to completed failed', { error: err instanceof Error ? err.message : String(err) });
    }
    const ctx = this.makeCtx();
    for (const obs of this._observers) {
      try { obs.onStateChange?.(ctx, fromState, 'completed'); } catch (err) {
        log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }
    await this._acpProcess.cleanup();
  }
}

export function withSession(
  options: AgentSessionOptions,
): Promise<AcpSessionResult>;
export function withSession<T>(
  options: AgentSessionOptions,
  fn: (session: AgentSession) => Promise<T>,
): Promise<T>;
export async function withSession<T = AcpSessionResult>(
  options: AgentSessionOptions,
  fn?: (session: AgentSession) => Promise<T>,
): Promise<AcpSessionResult | T> {
  let session: AgentSession;
  try {
    session = await AgentSession.create({
      ...options,
      timeout: options.timeout ?? DEFAULT_TIMEOUT,
    });
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    return { text: '', success: false, error: message };
  }
  try {
    if (fn) return await fn(session);
    return await session.execute(options.task ?? '', { kind: 'initial' });
  } finally {
    if (session.canClose()) {
      await session.close();
    }
  }
}
