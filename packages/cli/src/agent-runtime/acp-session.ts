import { spawn, type ChildProcess } from 'child_process';
import { Writable, Readable } from 'stream';
import {
  ClientSideConnection,
  ndJsonStream,
  PROTOCOL_VERSION,
} from '@agentclientprotocol/sdk';
import type {
  SessionNotification,
  RequestPermissionRequest,
  RequestPermissionResponse,
} from '@agentclientprotocol/sdk';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import type { EventBus } from '../services/event-bus';
import { Log } from '../util/log';
import { resolveOpencodeBinPath, load } from '../config/config-loader';
import { getOpencodeDiscoveryService } from '../services/opencode-discovery-service';

const log = Log.create({ service: 'acp-session' });

function writeSessionLog(
  workflowLogRepo: WorkflowLogRepo | undefined,
  issueId: string | undefined,
  eventType: string,
  data: Record<string, unknown>
): void {
  if (!workflowLogRepo || !issueId) return;
  try {
    workflowLogRepo.insert(issueId, null, eventType, data);
  } catch (e) {
    log.warn('workflowLogRepo.insert failed', { eventType, issueId, error: e instanceof Error ? e.message : String(e) });
  }
}

export interface AcpSessionOptions {
  cwd: string;
  task: string;
  taskId?: string;
  timeout?: number;
  issueId?: string;
  projectId?: string;
  executionId?: string;
  workflowLogRepo?: WorkflowLogRepo;
  eventBus?: EventBus;
  throttleMs?: number;
  coderSessionRepo?: CoderSessionRepo;
  issueNumber?: number;
  onSessionUpdate?: (notification: SessionNotification) => void;
  opencodeBinPath?: string;
  onProcessSpawned?: (proc: ChildProcess) => void;
  onBeforeKill?: (cwd: string) => Promise<boolean>;
  model?: string;
  stage?: string;
  hangIdleMs?: number;
}

export interface AcpSessionResult {
  text: string;
  success: boolean;
  error?: string;
  acpSessionId?: string;
  wipCommitted?: boolean;
}

const DEFAULT_TIMEOUT = 30 * 60 * 1000;
const PER_ROUND_TIMEOUT = 30 * 60 * 1000;
const MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024;
const DEFAULT_HANG_IDLE_MS = 3 * 60 * 1000;
const HANG_CHECK_INTERVAL_MS = 30 * 1000;
const RECOVERY_CANCEL_TIMEOUT_MS = 5 * 1000;
const RECOVERY_WIP_TIMEOUT_MS = 5 * 1000;
const RECOVERY_COOLDOWN_MS = 1 * 1000;
const MAX_RECOVERY_ATTEMPTS = 2;

export function truncateAgentText(text: string): string {
  if (text.length <= MAX_AGENT_TEXT_LENGTH) {
    return text;
  }
  const keepLength = Math.floor(MAX_AGENT_TEXT_LENGTH / 2);
  const head = text.slice(0, keepLength);
  const tail = text.slice(-keepLength);
  const truncated = text.length - MAX_AGENT_TEXT_LENGTH;
  return `${head}\n\n...[truncated ${truncated} characters]...\n\n${tail}`;
}

interface PromptWithRecoveryParams {
  connection: ClientSideConnection;
  sessionId: string;
  promptText: string;
  timeoutMs: number;
  hangIdleMs: number;
  cwd: string;
  issueId?: string;
  projectId?: string;
  executionId?: string;
  sseIssueId: string;
  acpSessionId: string;
  workflowLogRepo?: WorkflowLogRepo;
  eventBus?: EventBus;
  onBeforeKill?: (cwd: string) => Promise<boolean>;
  cleanup: () => Promise<void>;
  getLastEventTime: () => number;
  setLastEventTime: (t: number) => void;
}

async function runPromptWithHangRecovery(
  params: PromptWithRecoveryParams
): Promise<AcpSessionResult> {
  const {
    connection,
    sessionId,
    promptText,
    timeoutMs,
    hangIdleMs,
    cwd,
    issueId,
    projectId,
    executionId,
    sseIssueId,
    acpSessionId,
    workflowLogRepo,
    eventBus,
    onBeforeKill,
    cleanup,
    getLastEventTime,
    setLastEventTime,
  } = params;

  const startTime = Date.now();
  const idleDisabled = hangIdleMs <= 0;
  let recoveryAttemptCount = 0;
  let idleTimer: ReturnType<typeof setInterval> | undefined;
  let hangResolved: ((value: 'hang') => void) | undefined;

  const emitRecoveryStatus = (status: 'detected' | 'recovering' | 'recovered' | 'failed', attempt: number, reason?: string) => {
    if (!eventBus) return;
    try {
      eventBus.emit('coder_recovery_status', {
        issueId: sseIssueId,
        projectId: projectId ?? '',
        executionId: executionId ?? '',
        acpSessionId: acpSessionId || sessionId,
        status,
        attempt,
        reason,
      });
    } catch {}
  };

  const doWipCommit = async (): Promise<boolean> => {
    if (!onBeforeKill) return false;
    try {
      const result = await Promise.race([
        onBeforeKill(cwd),
        new Promise<false>((resolve) => setTimeout(() => resolve(false), RECOVERY_WIP_TIMEOUT_MS)),
      ]);
      return result;
    } catch {
      return false;
    }
  };

  const doCancel = async (): Promise<boolean> => {
    try {
      await Promise.race([
        connection.cancel({ sessionId }),
        new Promise<never>((_, reject) => setTimeout(() => reject(new Error('cancel timeout')), RECOVERY_CANCEL_TIMEOUT_MS)),
      ]);
      return true;
    } catch {
      return false;
    }
  };

  const doCooldown = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, RECOVERY_COOLDOWN_MS));

  const createTimeoutP = (ms: number): Promise<'timeout'> =>
    new Promise((resolve) => setTimeout(() => resolve('timeout'), ms));

  const createIdlePromise = (): Promise<'hang'> =>
    new Promise((resolve) => { hangResolved = resolve; });

  const startIdleMonitor = () => {
    if (idleDisabled) return;
    idleTimer = setInterval(() => {
      const idle = Date.now() - getLastEventTime();
      if (idle > hangIdleMs) {
        if (hangResolved) {
          hangResolved('hang');
          hangResolved = undefined;
        }
        clearInterval(idleTimer!);
        idleTimer = undefined;
      }
    }, HANG_CHECK_INTERVAL_MS);
  };

  const stopIdleMonitor = () => {
    if (idleTimer) {
      clearInterval(idleTimer);
      idleTimer = undefined;
    }
    hangResolved = undefined;
  };

  const recoveryHint = (idleMs: number) =>
    `The previous LLM streaming connection was interrupted (idle for ${idleMs}ms). Your session context is preserved. Please continue from where you left off.`;

  try {
    let currentPrompt = promptText;

    while (true) {
      setLastEventTime(Date.now());
      startIdleMonitor();

      const remaining = Math.max(timeoutMs - (Date.now() - startTime), 0);
      const result = await Promise.race([
        connection.prompt({
          sessionId,
          prompt: [{ type: 'text', text: currentPrompt }],
        }),
        createTimeoutP(remaining),
        createIdlePromise(),
      ]);

      stopIdleMonitor();

      if (result === 'timeout') {
        await cleanup();
        return {
          text: '',
          success: false,
          error: `Timed out after ${timeoutMs / 1000}s`,
          acpSessionId: sessionId,
        };
      }

      if (result !== 'hang') {
        return {
          text: '',
          success: true,
          acpSessionId: sessionId,
        };
      }

      const idleMs = Date.now() - getLastEventTime() + hangIdleMs;
      const attempt = recoveryAttemptCount + 1;

      log.warn('ACP session hang detected, starting recovery', { sessionId, idleMs, attempt });
      writeSessionLog(workflowLogRepo, issueId, 'acp_session_hang_detected', { sessionId, idleMs, attempt });
      emitRecoveryStatus('detected', attempt);

      if (attempt > MAX_RECOVERY_ATTEMPTS) {
        log.error('Max recovery attempts exceeded, killing process', { sessionId, attempt });
        writeSessionLog(workflowLogRepo, issueId, 'acp_session_recovery_failed', { sessionId, attempt, reason: 'max_attempts_exceeded' });
        emitRecoveryStatus('failed', attempt, 'max_attempts_exceeded');
        await cleanup();
        return {
          text: '',
          success: false,
          error: `[HANG_UNRECOVERABLE] max recovery attempts exceeded`,
          acpSessionId: sessionId,
        };
      }

      const wipCommitted = await doWipCommit();

      const cancelOk = await doCancel();
      if (!cancelOk) {
        log.error('Cancel timed out during recovery, falling back to kill', { sessionId, attempt });
        writeSessionLog(workflowLogRepo, issueId, 'acp_session_recovery_failed', { sessionId, attempt, reason: 'cancel_timeout' });
        emitRecoveryStatus('failed', attempt, 'cancel_timeout');
        await cleanup();
        return {
          text: '',
          success: false,
          error: `[HANG_UNRECOVERABLE] cancel timed out`,
          acpSessionId: sessionId,
          wipCommitted,
        };
      }

      await doCooldown();

      recoveryAttemptCount = attempt;
      setLastEventTime(Date.now());

      writeSessionLog(workflowLogRepo, issueId, 'acp_session_recovery_started', { sessionId, attempt });
      emitRecoveryStatus('recovering', attempt);

      currentPrompt = recoveryHint(idleMs);
    }
  } catch (err) {
    stopIdleMonitor();
    const message = err instanceof Error ? err.message : String(err);
    return { text: '', success: false, error: message, acpSessionId: sessionId };
  }
}

function killProc(proc: import('child_process').ChildProcess): void {
  try {
    proc.kill('SIGTERM');
  } catch {
    // already exited
  }
}

export async function runAcpSession(
  options: AcpSessionOptions
): Promise<AcpSessionResult> {
  const {
    cwd,
    task,
    taskId,
    timeout = DEFAULT_TIMEOUT,
    issueId,
    projectId,
    executionId,
    workflowLogRepo,
    eventBus,
    throttleMs = 100,
    coderSessionRepo,
    issueNumber,
    opencodeBinPath,
    onBeforeKill,
    stage,
    hangIdleMs = DEFAULT_HANG_IDLE_MS,
  } = options;

  const sseIssueId = String(issueNumber ?? issueId ?? '');
  let lastTextChunkTime = 0;
  let lastEventTime = Date.now();
  const sessionStartTime = Date.now();

  log.info('Spawning opencode acp subprocess', { cwd, timeout, issueId: issueId?.slice(0, 8), taskId, promptPreview: task.slice(0, 100) });
  writeSessionLog(workflowLogRepo, issueId, 'acp_session_start', { cwd, timeout, issueId: issueId?.slice(0, 8), taskId, promptPreview: task.slice(0, 100), timestamp: new Date().toISOString() });

  const resolvedBinPath = opencodeBinPath || resolveOpencodeBinPath() || 'opencode';

  const proc = spawn(resolvedBinPath, ['acp'], {
    cwd,
    stdio: ['pipe', 'pipe', 'inherit'],
    env: Object.fromEntries(
      Object.entries(process.env).filter(
        ([key]) =>
          key !== 'OPENCODE_SERVER_PASSWORD' &&
          key !== 'OPENCODE_SERVER_USERNAME'
      )
    ),
  });

  options.onProcessSpawned?.(proc);

  let initialized = false;
  let rejectOnSpawn: ((err: Error) => void) | undefined;
  const spawnFailure = new Promise<never>((_, reject) => {
    rejectOnSpawn = reject;
  });

  proc.on('error', (err) => {
    log.error('opencode acp subprocess error', { error: err.message });
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_process_error', { error: err.message, timestamp: new Date().toISOString() });
    if (!initialized && rejectOnSpawn) {
      rejectOnSpawn(new Error(`[SPAWN_FAILED] ${err.message}`));
    }
  });

  proc.on('exit', () => {
    try { proc.stdin.destroy(); } catch {}
    try { proc.stdout.destroy(); } catch {}
  });

  proc.on('exit', (code) => {
    const phase = initialized ? 'running' : 'init';
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_process_exit', { exitCode: code, phase, mode: 'single', timestamp: new Date().toISOString() });
    if (!initialized && code !== 0) {
      log.error('opencode acp subprocess exited before initialize', { exitCode: code });
      if (rejectOnSpawn) {
        rejectOnSpawn(new Error(`[SPAWN_FAILED] opencode process exited before initialize (exit code: ${code ?? 'signal'})`));
      }
    }
  });

  // Close streams immediately on spawn failure to prevent EPIPE errors
  proc.stdin.on('error', () => {});
  proc.stdout.on('error', () => {});

  let procExited = false;
  let agentText = '';
  let agentTextTruncated = false;
  let sessionId = '';
  let coderToolCallCounter = 0;
  const coderToolCallIds = new Map<string, string[]>();

  const ensureKill = () => {
    if (!procExited) {
      procExited = true;
      killProc(proc);
      setTimeout(() => {
        try {
          proc.kill('SIGKILL');
        } catch {
          // already exited
        }
      }, 5000);
    }
  };

  const input = Writable.toWeb(proc.stdin) as WritableStream<Uint8Array>;
  const output = Readable.toWeb(proc.stdout) as ReadableStream<Uint8Array>;
  const stream = ndJsonStream(input, output);

  const cleanup = async () => {
    const cleanupPromise = Promise.allSettled([
      stream.readable.cancel().catch(() => {}),
      stream.writable.abort().catch(() => {}),
    ]);
    let timeoutId: ReturnType<typeof setTimeout> | undefined;
    const timeoutPromise = new Promise<'timeout'>((resolve) => {
      timeoutId = setTimeout(() => {
        log.warn('Cleanup timed out after 5s, forcing kill');
        resolve('timeout');
      }, 5000);
    });
    const result = await Promise.race([cleanupPromise, timeoutPromise]);
    if (timeoutId !== undefined) clearTimeout(timeoutId);
    if (result !== 'timeout') {
      (result as PromiseSettledResult<void>[]).forEach((r, index) => {
        if (r.status === 'rejected') {
          log.error('Cleanup failed', { index, reason: String(r.reason) });
        }
      });
    }
    ensureKill();
  };

  const connection = new ClientSideConnection(
    (_agent) => ({
      sessionUpdate: async (notification: SessionNotification) => {
        try {
          lastEventTime = Date.now();
          const update = notification.update;
          const eventType = update.sessionUpdate;

          if (eventType === 'agent_thought_chunk') {
            // excluded from agentText — only agent_message_chunk contributes
          } else if (
            eventType === 'agent_message_chunk' &&
            update.content &&
            'text' in update.content
          ) {
            const textChunk = (update.content as { text: string }).text;
            if (!agentTextTruncated) {
              agentText += textChunk;
              if (agentText.length > MAX_AGENT_TEXT_LENGTH) {
                agentText = truncateAgentText(agentText);
                agentTextTruncated = true;
              }
            }
            if (eventBus && executionId) {
              const now = Date.now();
              if (throttleMs === 0 || now - lastTextChunkTime >= throttleMs) {
                const chunkPayload: Record<string, unknown> = {
                  issueId: sseIssueId,
                  projectId: projectId ?? '',
                  executionId,
                  acpSessionId: sessionId,
                  text: textChunk,
                };
                if (coderSessionId) chunkPayload.coderSessionId = coderSessionId;
                if (options.model) chunkPayload.model = options.model;
                eventBus.emit('coder_text_chunk', chunkPayload as any);
                lastTextChunkTime = now;
              }
            }
          }

          if (workflowLogRepo) {
            workflowLogRepo.insert(
              issueId ?? '',
              sessionId || null,
              eventType,
              update as unknown as Record<string, unknown>,
            );
          }

          if (
            eventType === 'tool_call' &&
            eventBus &&
            executionId
          ) {
            const toolData = update as Record<string, unknown>;
            const toolCallData = toolData.toolCall as Record<string, unknown> | undefined;
            const toolStatus = (toolCallData?.status as string) ?? '';
            const state = toolStatus === 'completed' ? 'completed' : 'started';
            const toolName = (toolCallData?.toolName as string) ?? '';
            let toolCallId: string;
            if (state === 'started') {
              toolCallId = `${sessionId}-${toolName}-${coderToolCallCounter++}`;
              const key = `${sessionId}-${toolName}`;
              const list = coderToolCallIds.get(key) ?? [];
              list.push(toolCallId);
              coderToolCallIds.set(key, list);
            } else {
              const key = `${sessionId}-${toolName}`;
              const list = coderToolCallIds.get(key) ?? [];
              toolCallId = list.shift() ?? `${sessionId}-${toolName}-${coderToolCallCounter++}`;
              if (list.length > 0) {
                coderToolCallIds.set(key, list);
              } else {
                coderToolCallIds.delete(key);
              }
            }
            const toolPayload: Record<string, unknown> = {
              issueId: sseIssueId,
              projectId: projectId ?? '',
              executionId,
              acpSessionId: sessionId,
              toolName,
              state,
              toolCallId,
              title: (toolCallData?.title as string) ?? undefined,
              rawInput: state === 'started' ? toolCallData?.input : undefined,
              rawOutput: state === 'completed' ? toolCallData?.output : undefined,
            };
            if (coderSessionId) toolPayload.coderSessionId = coderSessionId;
            if (options.model) toolPayload.model = options.model;
            eventBus.emit('coder_tool_call', toolPayload as any);
          }
        } catch (err) {
          log.error('sessionUpdate error', { error: err instanceof Error ? err.message : String(err) });
        }
      },
      requestPermission: async (
        params: RequestPermissionRequest
      ): Promise<RequestPermissionResponse> => {
        const allow = params.options.find(
          (o) => o.kind === 'allow_once' || o.kind === 'allow_always'
        );
        if (allow) {
          return { outcome: { outcome: 'selected', optionId: allow.optionId } };
        }
        return { outcome: { outcome: 'cancelled' } };
      },
    }),
    stream
  );

  const timeoutPromise = new Promise<'timeout'>((resolve) =>
    setTimeout(() => resolve('timeout'), timeout)
  );

  let coderSessionId: string | undefined;

  try {
    const initResult = await Promise.race([
      connection.initialize({
        protocolVersion: PROTOCOL_VERSION,
        clientInfo: { name: 'mohist', version: '0.1.0' },
      }),
      timeoutPromise,
      spawnFailure,
    ]);

    initialized = true;
    rejectOnSpawn = undefined;

    if (initResult === 'timeout') {
      const duration = Date.now() - sessionStartTime;
      log.error('ACP initialize timed out', { timeout, duration });
      writeSessionLog(workflowLogRepo, issueId, 'acp_session_timeout', { phase: 'initialize', timeout, duration, timestamp: new Date().toISOString() });
      await cleanup();
      return { text: agentText, success: false, error: `Timed out during initialize` };
    }

    log.info('ACP initialized, creating session');

    const sessionResult = await Promise.race([
      connection.newSession({
        cwd,
        mcpServers: [],
      }),
      timeoutPromise,
    ]);

    if (sessionResult === 'timeout') {
      const duration = Date.now() - sessionStartTime;
      log.error('ACP newSession timed out', { timeout, duration });
      writeSessionLog(workflowLogRepo, issueId, 'acp_session_timeout', { phase: 'newSession', timeout, duration, timestamp: new Date().toISOString() });
      await cleanup();
      return { text: agentText, success: false, error: `Timed out during newSession` };
    }

    sessionId = sessionResult.sessionId;
    log.info('ACP session created', { sessionId });

    let resolvedModel: string | undefined;

    if (coderSessionRepo && issueId) {
      try {
        resolvedModel = options.model ?? load().model ?? undefined;
        const coderSession = coderSessionRepo.insert({
          issueId,
          acpSessionId: sessionId,
          executionId,
          taskDescription: task.slice(0, 200),
          model: resolvedModel,
          coderType: 'opencode',
          stage: 'build',
        });
        coderSessionId = coderSession.id;
        log.info('coder_session row created', { coderSessionId, acpSessionId: sessionId });

        if (eventBus) {
          eventBus.emit('coder_session_started', {
            issueId: sseIssueId,
            projectId: projectId ?? '',
            coderSessionId,
            acpSessionId: sessionId,
            executionId,
            model: resolvedModel,
            coderType: 'opencode',
            stage: 'build',
            taskDescription: task.slice(0, 200),
          });
        }
      } catch (err) {
        log.error('Failed to create coder_session row', { error: err instanceof Error ? err.message : String(err) });
      }
    }

    const config = load();
    const configuredModel = stage && config.opencode?.stageModels?.[stage]
      ? config.opencode.stageModels[stage]
      : config.opencode?.model ?? null;

    if (configuredModel) {
      try {
        const availableModels = await getOpencodeDiscoveryService().getAvailableModels();
        if (!availableModels.includes(configuredModel)) {
          const errorMsg = `Configured model "${configuredModel}" is not available. Available models: ${availableModels.join(', ')}`;
          log.error('Model validation failed', { configuredModel, availableModels });
          writeSessionLog(workflowLogRepo, issueId, 'model_selection_failed', {
            configuredModel,
            availableModels,
            stage,
            error: errorMsg,
            timestamp: new Date().toISOString(),
          });
          await cleanup();
          return { text: agentText, success: false, error: errorMsg };
        }
      } catch (err) {
        log.warn('Model discovery probe failed, skipping validation', { error: err instanceof Error ? err.message : String(err) });
      }

      try {
        await connection.setSessionConfigOption({
          configId: 'model',
          sessionId,
          value: configuredModel,
        });
        log.info('Model override applied', { model: configuredModel, stage });
        writeSessionLog(workflowLogRepo, issueId, 'model_selected', {
          model: configuredModel,
          stage: stage ?? null,
          source: stage && config.opencode?.stageModels?.[stage] ? 'stageModels' : 'opencode.model',
          sessionId,
          timestamp: new Date().toISOString(),
        });
      } catch (err) {
        log.warn('setSessionConfigOption failed, falling back to default model', {
          configuredModel,
          error: err instanceof Error ? err.message : String(err),
        });
        writeSessionLog(workflowLogRepo, issueId, 'model_fallback', {
          configuredModel,
          stage: stage ?? null,
          reason: err instanceof Error ? err.message : 'setSessionConfigOption failed',
          timestamp: new Date().toISOString(),
        });
      }
    }

    const promptResult = await runPromptWithHangRecovery({
      connection,
      sessionId,
      promptText: task,
      timeoutMs: timeout,
      hangIdleMs,
      cwd,
      issueId,
      projectId,
      executionId,
      sseIssueId,
      acpSessionId: sessionId,
      workflowLogRepo,
      eventBus,
      onBeforeKill,
      cleanup,
      getLastEventTime: () => lastEventTime,
      setLastEventTime: (t: number) => { lastEventTime = t; },
    });

    if (!promptResult.success) {
      if (coderSessionRepo && coderSessionId) {
        try {
          coderSessionRepo.updateStatus(coderSessionId, 'failed');
          if (eventBus) {
            eventBus.emit('coder_session_completed', {
              issueId: sseIssueId,
              projectId: projectId ?? '',
              coderSessionId,
              status: 'failed',
              duration: Math.round((Date.now() - sessionStartTime) / 1000),
            });
          }
        } catch (err) {
          log.error('Failed to update coder_session status', { error: err instanceof Error ? err.message : String(err) });
        }
      }
      writeSessionLog(workflowLogRepo, issueId, 'acp_session_timeout', { phase: 'prompt', sessionId, timeout, duration: Date.now() - sessionStartTime, timestamp: new Date().toISOString() });
      return { text: agentText, success: false, error: promptResult.error, wipCommitted: promptResult.wipCommitted };
    }

    if (coderSessionRepo && coderSessionId) {
      try {
        const updatedSession = coderSessionRepo.updateStatus(coderSessionId, 'completed');
        if (eventBus) {
          const duration = updatedSession.completedAt && updatedSession.createdAt
            ? Math.round((new Date(updatedSession.completedAt).getTime() - new Date(updatedSession.createdAt).getTime()) / 1000)
            : Math.round((Date.now() - sessionStartTime) / 1000);
          eventBus.emit('coder_session_completed', {
            issueId: sseIssueId,
            projectId: projectId ?? '',
            coderSessionId,
            status: 'completed',
            duration,
          });
        }
      } catch (err) {
        log.error('Failed to update coder_session status to completed', { error: err instanceof Error ? err.message : String(err) });
      }
    }

    const successDuration = Date.now() - sessionStartTime;
    log.info('ACP session completed successfully', { sessionId, duration: successDuration });
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_completed', { sessionId, success: true, duration: successDuration, timestamp: new Date().toISOString() });
    return { text: agentText, success: true, acpSessionId: sessionId };
  } catch (err) {
    const failDuration = Date.now() - sessionStartTime;
    log.error('ACP session failed', { sessionId, duration: failDuration, error: err instanceof Error ? err.message : String(err) });
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_completed', { sessionId, success: false, duration: failDuration, error: err instanceof Error ? err.message : String(err), timestamp: new Date().toISOString() });
    if (coderSessionRepo && coderSessionId) {
      try {
        coderSessionRepo.updateStatus(coderSessionId, 'failed');
        if (eventBus) {
          eventBus.emit('coder_session_completed', {
            issueId: sseIssueId,
            projectId: projectId ?? '',
            coderSessionId,
            status: 'failed',
            duration: Math.round(failDuration / 1000),
          });
        }
      } catch (updateErr) {
        log.error('Failed to update coder_session status', { error: updateErr instanceof Error ? updateErr.message : String(updateErr) });
      }
    }
    await cleanup();
    const message = err instanceof Error ? err.message : String(err);
    return { text: agentText, success: false, error: message };
  } finally {
    ensureKill();
  }
}

export interface AcpConnectionOptions {
  cwd: string;
  timeout?: number;
  issueId?: string;
  projectId?: string;
  executionId?: string;
  workflowLogRepo?: WorkflowLogRepo;
  eventBus?: EventBus;
  throttleMs?: number;
  coderSessionRepo?: CoderSessionRepo;
  issueNumber?: number;
  onSessionUpdate?: (notification: SessionNotification) => void;
  opencodeBinPath?: string;
  onProcessSpawned?: (proc: ChildProcess) => void;
  onBeforeKill?: (cwd: string) => Promise<boolean>;
  model?: string;
  stage?: string;
  hangIdleMs?: number;
}

export interface AcpConnection {
  prompt(text: string): Promise<AcpSessionResult>;
  close(): Promise<void>;
  coderSessionId?: string;
  acpSessionId?: string;
}

function createTimeout(ms: number): Promise<'timeout'> {
  return new Promise<'timeout'>((resolve) =>
    setTimeout(() => resolve('timeout'), ms)
  );
}

export async function createAcpConnection(
  options: AcpConnectionOptions
): Promise<AcpConnection> {
  const {
    cwd,
    timeout = PER_ROUND_TIMEOUT,
    issueId,
    projectId,
    executionId,
    workflowLogRepo,
    eventBus,
    throttleMs = 100,
    coderSessionRepo,
    issueNumber,
    onSessionUpdate,
    opencodeBinPath,
    onBeforeKill,
    stage,
    hangIdleMs = DEFAULT_HANG_IDLE_MS,
  } = options;

  const sseIssueId = String(issueNumber ?? issueId ?? '');
  let lastTextChunkTime = 0;
  let lastEventTime = Date.now();
  let procExited = false;
  let agentText = '';
  let agentTextTruncated = false;
  let roundStartIndex = 0;
  let coderToolCallCounter = 0;
  const coderToolCallIds = new Map<string, string[]>();
  let coderSessionId: string | undefined;
  let sessionId = '';
  let closed = false;
  let initialized = false;
  const connectionStartTime = Date.now();

  log.info('Spawning opencode acp subprocess for multi-round connection', {
    cwd,
    timeout,
    issueId: issueId?.slice(0, 8),
  });
  writeSessionLog(workflowLogRepo, issueId, 'acp_session_start', { cwd, timeout, issueId: issueId?.slice(0, 8), mode: 'multi-round', timestamp: new Date().toISOString() });

  const resolvedBinPath = opencodeBinPath || resolveOpencodeBinPath() || 'opencode';

  const proc = spawn(resolvedBinPath, ['acp'], {
    cwd,
    stdio: ['pipe', 'pipe', 'inherit'],
    env: Object.fromEntries(
      Object.entries(process.env).filter(
        ([key]) =>
          key !== 'OPENCODE_SERVER_PASSWORD' &&
          key !== 'OPENCODE_SERVER_USERNAME'
      )
    ),
  });

  options.onProcessSpawned?.(proc);

  let rejectOnInit: ((err: Error) => void) | undefined;
  const spawnFailure = new Promise<never>((_, reject) => {
    rejectOnInit = reject;
  });

  proc.on('error', (err) => {
    log.error('opencode acp subprocess error', { error: err.message });
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_process_error', { error: err.message, mode: 'multi-round', timestamp: new Date().toISOString() });
    if (!initialized && rejectOnInit) {
      rejectOnInit(new Error(`[SPAWN_FAILED] ${err.message}`));
    }
  });

  proc.on('exit', () => {
    try { proc.stdin.destroy(); } catch {}
    try { proc.stdout.destroy(); } catch {}
  });

  proc.on('exit', (code) => {
    const phase = initialized ? 'running' : 'init';
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_process_exit', { exitCode: code, phase, mode: 'multi-round', timestamp: new Date().toISOString() });
    if (!initialized && code !== 0) {
      log.error('opencode acp subprocess exited before initialize', { exitCode: code, mode: 'multi-round' });
      if (rejectOnInit) {
        rejectOnInit(new Error(`[SPAWN_FAILED] opencode process exited before initialize (exit code: ${code ?? 'signal'})`));
      }
    }
  });

  // Close streams immediately on spawn failure to prevent EPIPE errors
  proc.stdin.on('error', () => {});
  proc.stdout.on('error', () => {});

  const ensureKill = () => {
    if (!procExited) {
      procExited = true;
      killProc(proc);
      setTimeout(() => {
        try {
          proc.kill('SIGKILL');
        } catch {
          // already exited
        }
      }, 5000);
    }
  };

  const input = Writable.toWeb(proc.stdin) as WritableStream<Uint8Array>;
  const output = Readable.toWeb(proc.stdout) as ReadableStream<Uint8Array>;
  const stream = ndJsonStream(input, output);

  const cleanup = async () => {
    const cleanupPromise = Promise.allSettled([
      stream.readable.cancel().catch(() => {}),
      stream.writable.abort().catch(() => {}),
    ]);
    let timeoutId: ReturnType<typeof setTimeout> | undefined;
    const timeoutPromise = new Promise<'timeout'>((resolve) => {
      timeoutId = setTimeout(() => {
        log.warn('Cleanup timed out after 5s, forcing kill');
        resolve('timeout');
      }, 5000);
    });
    const result = await Promise.race([cleanupPromise, timeoutPromise]);
    if (timeoutId !== undefined) clearTimeout(timeoutId);
    if (result !== 'timeout') {
      (result as PromiseSettledResult<void>[]).forEach((r, index) => {
        if (r.status === 'rejected') {
          log.error('Cleanup failed', { index, reason: String(r.reason) });
        }
      });
    }
    ensureKill();
  };

  const connection = new ClientSideConnection(
    (_agent) => ({
      sessionUpdate: async (notification: SessionNotification) => {
        try {
          lastEventTime = Date.now();
          const update = notification.update;
          const eventType = update.sessionUpdate;

          if (eventType === 'agent_thought_chunk') {
            // excluded from agentText — only agent_message_chunk contributes
          } else if (
            eventType === 'agent_message_chunk' &&
            update.content &&
            'text' in update.content
          ) {
            const textChunk = (update.content as { text: string }).text;
            if (!agentTextTruncated) {
              agentText += textChunk;
              if (agentText.length > MAX_AGENT_TEXT_LENGTH) {
                agentText = truncateAgentText(agentText);
                agentTextTruncated = true;
              }
            }
            if (onSessionUpdate) {
              onSessionUpdate(notification);
            } else if (eventBus && executionId) {
              const now = Date.now();
              if (throttleMs === 0 || now - lastTextChunkTime >= throttleMs) {
                const chunkPayload: Record<string, unknown> = {
                  issueId: sseIssueId,
                  projectId: projectId ?? '',
                  executionId,
                  acpSessionId: sessionId,
                  text: textChunk,
                };
                if (coderSessionId) chunkPayload.coderSessionId = coderSessionId;
                if (options.model) chunkPayload.model = options.model;
                eventBus.emit('coder_text_chunk', chunkPayload as any);
                lastTextChunkTime = now;
              }
            }
          } else if (onSessionUpdate) {
            onSessionUpdate(notification);
          }

          if (workflowLogRepo) {
            workflowLogRepo.insert(
              issueId ?? '',
              sessionId || null,
              eventType,
              update as unknown as Record<string, unknown>
            );
          }

          if (!onSessionUpdate && eventType === 'tool_call' && eventBus && executionId) {
            const toolData = update as Record<string, unknown>;
            const toolCallData = toolData.toolCall as
              | Record<string, unknown>
              | undefined;
            const toolStatus = (toolCallData?.status as string) ?? '';
            const state = toolStatus === 'completed' ? 'completed' : 'started';
            const toolName = (toolCallData?.toolName as string) ?? '';
            let toolCallId: string;
            if (state === 'started') {
              toolCallId = `${sessionId}-${toolName}-${coderToolCallCounter++}`;
              const key = `${sessionId}-${toolName}`;
              const list = coderToolCallIds.get(key) ?? [];
              list.push(toolCallId);
              coderToolCallIds.set(key, list);
            } else {
              const key = `${sessionId}-${toolName}`;
              const list = coderToolCallIds.get(key) ?? [];
              toolCallId =
                list.shift() ??
                `${sessionId}-${toolName}-${coderToolCallCounter++}`;
              if (list.length > 0) {
                coderToolCallIds.set(key, list);
              } else {
                coderToolCallIds.delete(key);
              }
            }
            const toolPayload: Record<string, unknown> = {
              issueId: sseIssueId,
              projectId: projectId ?? '',
              executionId,
              acpSessionId: sessionId,
              toolName,
              state,
              toolCallId,
              title: (toolCallData?.title as string) ?? undefined,
              rawInput: state === 'started' ? toolCallData?.input : undefined,
              rawOutput: state === 'completed' ? toolCallData?.output : undefined,
            };
            if (coderSessionId) toolPayload.coderSessionId = coderSessionId;
            if (options.model) toolPayload.model = options.model;
            eventBus.emit('coder_tool_call', toolPayload as any);
          }
        } catch (err) {
          log.error('sessionUpdate error', {
            error: err instanceof Error ? err.message : String(err),
          });
        }
      },
      requestPermission: async (
        params: RequestPermissionRequest
      ): Promise<RequestPermissionResponse> => {
        const allow = params.options.find(
          (o) => o.kind === 'allow_once' || o.kind === 'allow_always'
        );
        if (allow) {
          return {
            outcome: { outcome: 'selected', optionId: allow.optionId },
          };
        }
        return { outcome: { outcome: 'cancelled' } };
      },
    }),
    stream
  );

  const initResult = await Promise.race([
    connection.initialize({
      protocolVersion: PROTOCOL_VERSION,
      clientInfo: { name: 'mohist', version: '0.1.0' },
    }),
    createTimeout(timeout),
    spawnFailure,
  ]).catch(async (err: unknown) => {
    await cleanup();
    throw err;
  });

  initialized = true;
  rejectOnInit = undefined;

  if (initResult === 'timeout') {
    const duration = Date.now() - connectionStartTime;
    log.error('ACP initialize timed out', { timeout, duration });
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_timeout', { phase: 'initialize', timeout, duration, mode: 'multi-round', timestamp: new Date().toISOString() });
    await cleanup();
    throw new Error('Timed out during initialize');
  }

  log.info('ACP initialized, creating session');

  const sessionResult = await Promise.race([
    connection.newSession({ cwd, mcpServers: [] }),
    createTimeout(timeout),
  ]);

  if (sessionResult === 'timeout') {
    const duration = Date.now() - connectionStartTime;
    log.error('ACP newSession timed out', { timeout, duration });
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_timeout', { phase: 'newSession', timeout, duration, mode: 'multi-round', timestamp: new Date().toISOString() });
    await cleanup();
    throw new Error('Timed out during newSession');
  }

  sessionId = sessionResult.sessionId;
  log.info('ACP session created', { sessionId });

  if (coderSessionRepo && issueId) {
    try {
      const resolvedModel = options.model ?? load().model ?? undefined;
      const coderSession = coderSessionRepo.insert({
        issueId,
        acpSessionId: sessionId,
        executionId,
        taskDescription: 'multi-round acp connection',
        model: resolvedModel,
        coderType: 'opencode',
        stage: options.stage,
      });
      coderSessionId = coderSession.id;
      log.info('coder_session row created', {
        coderSessionId,
        acpSessionId: sessionId,
      });
      if (eventBus) {
        eventBus.emit('coder_session_started', {
          issueId: sseIssueId,
          projectId: projectId ?? '',
          coderSessionId,
          acpSessionId: sessionId,
          executionId,
          model: resolvedModel,
          coderType: 'opencode',
          stage: options.stage ?? undefined,
          taskDescription: undefined,
        });
      }
    } catch (err) {
      log.error('Failed to create coder_session row', {
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  const config = load();
  const configuredModel = stage && config.opencode?.stageModels?.[stage]
    ? config.opencode.stageModels[stage]
    : config.opencode?.model ?? null;

  if (configuredModel) {
    try {
      const availableModels = await getOpencodeDiscoveryService().getAvailableModels();
      if (!availableModels.includes(configuredModel)) {
        const errorMsg = `Configured model "${configuredModel}" is not available. Available models: ${availableModels.join(', ')}`;
        log.error('Model validation failed', { configuredModel, availableModels });
        writeSessionLog(workflowLogRepo, issueId, 'model_selection_failed', {
          configuredModel,
          availableModels,
          stage,
          error: errorMsg,
          timestamp: new Date().toISOString(),
        });
        await cleanup();
        throw new Error(errorMsg);
      }
    } catch (err) {
      if (err instanceof Error && err.message.startsWith('Configured model')) {
        throw err;
      }
      log.warn('Model discovery probe failed, skipping validation', { error: err instanceof Error ? err.message : String(err) });
    }

    try {
      await connection.setSessionConfigOption({
        configId: 'model',
        sessionId,
        value: configuredModel,
      });
      log.info('Model override applied', { model: configuredModel, stage });
      writeSessionLog(workflowLogRepo, issueId, 'model_selected', {
        model: configuredModel,
        stage: stage ?? null,
        source: stage && config.opencode?.stageModels?.[stage] ? 'stageModels' : 'opencode.model',
        sessionId,
        timestamp: new Date().toISOString(),
      });
    } catch (err) {
      log.warn('setSessionConfigOption failed, falling back to default model', {
        configuredModel,
        error: err instanceof Error ? err.message : String(err),
      });
      writeSessionLog(workflowLogRepo, issueId, 'model_fallback', {
        configuredModel,
        stage: stage ?? null,
        reason: err instanceof Error ? err.message : 'setSessionConfigOption failed',
        timestamp: new Date().toISOString(),
      });
    }
  }

  return {
    coderSessionId,
    acpSessionId: sessionId,
    async prompt(text: string): Promise<AcpSessionResult> {
      if (closed) {
        return { text: '', success: false, error: 'Connection is closed' };
      }

      roundStartIndex = agentText.length;

      const result = await runPromptWithHangRecovery({
        connection,
        sessionId,
        promptText: text,
        timeoutMs: timeout,
        hangIdleMs,
        cwd,
        issueId,
        projectId,
        executionId,
        sseIssueId,
        acpSessionId: sessionId,
        workflowLogRepo,
        eventBus,
        onBeforeKill,
        cleanup,
        getLastEventTime: () => lastEventTime,
        setLastEventTime: (t: number) => { lastEventTime = t; },
      });

      if (!result.success) {
        if (coderSessionRepo && coderSessionId) {
          try {
            coderSessionRepo.updateStatus(coderSessionId, 'failed');
            if (eventBus) {
              eventBus.emit('coder_session_completed', {
                issueId: sseIssueId,
                projectId: projectId ?? '',
                coderSessionId,
                status: 'failed',
                duration: Math.round(duration / 1000),
              });
            }
          } catch {
            // ignore
          }
        }
        return {
          text: agentText.slice(roundStartIndex),
          success: false,
          error: result.error,
          acpSessionId: sessionId,
          wipCommitted: result.wipCommitted,
        };
      }

      return {
        text: agentText.slice(roundStartIndex),
        success: true,
        acpSessionId: sessionId,
      };
    },

    async close(): Promise<void> {
      if (closed) return;
      closed = true;
      const duration = Date.now() - connectionStartTime;
      log.info('ACP connection closed', { sessionId, duration });
      writeSessionLog(workflowLogRepo, issueId, 'acp_session_completed', { sessionId, success: true, duration, mode: 'multi-round', timestamp: new Date().toISOString() });
      if (coderSessionRepo && coderSessionId) {
        try {
          const updatedSession = coderSessionRepo.updateStatus(coderSessionId, 'completed');
          if (eventBus) {
            const sessDuration = updatedSession.completedAt && updatedSession.createdAt
              ? Math.round((new Date(updatedSession.completedAt).getTime() - new Date(updatedSession.createdAt).getTime()) / 1000)
              : Math.round(duration / 1000);
            eventBus.emit('coder_session_completed', {
              issueId: sseIssueId,
              projectId: projectId ?? '',
              coderSessionId,
              status: 'completed',
              duration: sessDuration,
            });
          }
        } catch {
          // ignore
        }
      }
      await cleanup();
    },
  };
}