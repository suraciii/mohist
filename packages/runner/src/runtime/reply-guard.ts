import { buildExecutionEnvelope } from './execution-envelope.js'
import {
  inlineSlackCollaborationSkill,
  readExecutionSourceContext,
  type SlackExecutionContext,
} from './slack-execution-context.js'
import type { ResolvedSkill } from './skill-resolver.js'
import { createTimeoutSignal } from '../system/timeout-signal.js'

export const DEFAULT_REPLY_GUARD_REMINDER_BUDGET = 1
export const DEFAULT_REPLY_GUARD_ADVISORY_TIMEOUT_MS = 30_000

export type ReplyGuardRuntimeKind = 'pi' | 'opencode'
export type ReplyGuardPhase = 'not-evaluated' | 'evaluating' | 'advisory-running' | 'closed'

export interface ReplyGuardState {
  readonly replyActionAttempted: boolean
  readonly remindersIssued: number
  readonly phase: ReplyGuardPhase
}

export interface NormalizedToolCallObservation {
  readonly id?: string
  readonly type: string
  readonly payload?: unknown
}

/**
 * A per-turn observation tracker. Runtime projectors call observe() while
 * projecting facts so the attempt marker is set synchronously with the
 * normalized tool-call start observation.
 */
export class ReplyActionObservationTracker {
  private attempted = false
  private readonly observedToolCalls = new Set<string>()
  private readonly attemptListeners = new Set<() => void>()

  observe(event: NormalizedToolCallObservation): boolean {
    if (!isReplyActionToolCallStarted(event)) return false

    const toolCallId = toolCallIdentity(event)
    if (toolCallId && this.observedToolCalls.has(toolCallId)) return false
    if (toolCallId) this.observedToolCalls.add(toolCallId)
    if (this.attempted) return false

    this.attempted = true
    for (const listener of [...this.attemptListeners]) listener()
    return true
  }

  get replyActionAttempted(): boolean {
    return this.attempted
  }

  onAttempt(listener: () => void): () => void {
    this.attemptListeners.add(listener)
    return () => this.attemptListeners.delete(listener)
  }
}

/**
 * Matches only the normalized start fact for the Agent-owned Slack reply
 * command. Final output, tool completion, and other runtime facts are not
 * evidence of an attempted reply.
 */
export function isReplyActionToolCallStarted(event: NormalizedToolCallObservation): boolean {
  if (event.type !== 'tool_call.started') return false
  const payload = recordValue(event.payload)
  if (!payload) return false

  const toolName =
    stringValue(payload['toolName']) ??
    stringValue(payload['normalizedName']) ??
    stringValue(payload['kind']) ??
    stringValue(payload['name'])
  if (!toolName) return false

  const normalizedToolName = normalizeToolName(toolName)
  if (normalizedToolName === 'mo_slack_message_send' || normalizedToolName === 'slack_message_send') return true
  if (!SHELL_TOOL_NAMES.has(normalizedToolName)) return false

  return isReplyActionCommand(readCommand(payload['rawInput'] ?? payload['input']))
}

export interface ReplyGuardRuntimeHandle {
  readonly kind: ReplyGuardRuntimeKind
  readonly isAvailable: () => boolean
}

export type ReplyGuardAdvisoryResult =
  | { readonly kind: 'completed' }
  | { readonly kind: 'silent' }
  | { readonly kind: 'failed' }
  | { readonly kind: 'unavailable' }
  | { readonly kind: 'interrupted' }

export interface ReplyGuardAdvisoryRequest {
  readonly runtime: ReplyGuardRuntimeKind
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly prompt: string
  readonly slackExecutionContext: SlackExecutionContext
  readonly replyAnchor: SlackExecutionContext['replyAnchor']
  readonly collaborationSkill: ResolvedSkill
  readonly observation: ReplyActionObservationTracker
  readonly signal: AbortSignal
}

export type ReplyGuardAdvisoryRunner = (request: ReplyGuardAdvisoryRequest) => Promise<ReplyGuardAdvisoryResult | void>

export type ReplyGuardDiagnosticKind = 'timeout' | 'failed' | 'unavailable' | 'interrupted'

export interface ReplyGuardDiagnostic {
  readonly kind: ReplyGuardDiagnosticKind
  readonly error?: unknown
}

export interface ReplyGuardCoordinatorOptions {
  readonly runtime: ReplyGuardRuntimeHandle | null
  readonly runtimeSessionId: string | null
  readonly workDir: string | null
  readonly slackExecutionContext: unknown
  readonly observation?: ReplyActionObservationTracker
  readonly runAdvisory?: ReplyGuardAdvisoryRunner
  readonly signal?: AbortSignal
  readonly reminderBudget?: number
  readonly advisoryTimeoutMs?: number
  readonly onDiagnostic?: (diagnostic: ReplyGuardDiagnostic) => void
}

export const REPLY_GUARD_ADVISORY_TEXT = [
  'This Slack-bound turn is reaching its end.',
  'Your reasoning and tool output are invisible to the Slack user.',
  'If this turn produced a useful result, publish a self-contained conclusion, evidence summary, and next step using the existing Slack reply action and supplied reply context.',
  'If there is no useful result or no appropriate message, deliberately remain silent.',
  'Do not ask the Runner or Server to author or publish a reply for you.',
].join(' ')

export function buildReplyGuardAdvisoryPrompt(context: SlackExecutionContext): string {
  const promptContext = {
    ...context,
    replyAnchor: Object.fromEntries(
      Object.entries(context.replyAnchor).filter(([, value]) => value !== null && value !== undefined),
    ) as SlackExecutionContext['replyAnchor'],
  }
  return buildExecutionEnvelope(
    REPLY_GUARD_ADVISORY_TEXT,
    null,
    [inlineSlackCollaborationSkill(context)],
    promptContext,
  )
}

/**
 * Coordinates the bounded, best-effort advisory for one eligible turn. The
 * original turn result is supplied to evaluate() and returned unchanged.
 */
export class ReplyGuardCoordinator {
  private readonly context: SlackExecutionContext | null
  private readonly runtime: ReplyGuardRuntimeHandle | null
  private readonly runtimeSessionId: string | null
  private readonly workDir: string | null
  private readonly observation: ReplyActionObservationTracker
  private readonly runAdvisory: ReplyGuardAdvisoryRunner | null
  private readonly signal: AbortSignal
  private readonly reminderBudget: number
  private readonly advisoryTimeoutMs: number
  private readonly onDiagnostic: ((diagnostic: ReplyGuardDiagnostic) => void) | null
  private readonly mutableState: { replyActionAttempted: boolean; remindersIssued: number; phase: ReplyGuardPhase }
  private evaluation: Promise<void> | null = null
  private activeAdvisoryAbort: (() => void) | null = null

  constructor(options: ReplyGuardCoordinatorOptions) {
    const contextRead = readExecutionSourceContext({ slackExecutionContext: options.slackExecutionContext })
    this.context =
      contextRead.kind === 'resolved' || contextRead.kind === 'legacy' ? contextRead.slackExecutionContext : null
    this.runtime = options.runtime
    this.runtimeSessionId = nonEmptyString(options.runtimeSessionId) ? options.runtimeSessionId : null
    this.workDir = nonEmptyString(options.workDir) ? options.workDir : null
    this.observation = options.observation ?? new ReplyActionObservationTracker()
    this.runAdvisory = options.runAdvisory ?? null
    this.signal = options.signal ?? neverAbortSignal()
    this.reminderBudget = normalizeBudget(options.reminderBudget)
    this.advisoryTimeoutMs = normalizeTimeout(options.advisoryTimeoutMs)
    this.onDiagnostic = options.onDiagnostic ?? null
    this.mutableState = {
      replyActionAttempted: this.observation.replyActionAttempted,
      remindersIssued: 0,
      phase: 'not-evaluated',
    }
    this.observation.onAttempt(() => {
      this.mutableState.replyActionAttempted = true
      this.activeAdvisoryAbort?.()
    })
  }

  get state(): ReplyGuardState {
    return { ...this.mutableState }
  }

  get eligible(): boolean {
    return this.context !== null && this.runtime !== null && this.runtimeSessionId !== null && this.workDir !== null
  }

  async evaluate<T>(originalResult: T): Promise<T> {
    if (this.evaluation) {
      await this.evaluation
      return originalResult
    }
    if (this.mutableState.phase !== 'not-evaluated') return originalResult

    this.mutableState.phase = 'evaluating'
    this.evaluation = this.evaluateGuard()
    await this.evaluation
    return originalResult
  }

  private async evaluateGuard(): Promise<void> {
    if (!this.eligible || this.signal.aborted) {
      this.close()
      return
    }
    if (!this.runAdvisory) {
      this.report({ kind: 'unavailable' })
      this.close()
      return
    }

    try {
      while (this.mutableState.remindersIssued < this.reminderBudget) {
        if (this.observation.replyActionAttempted || this.signal.aborted) {
          this.close()
          return
        }
        if (!this.runtime || !this.runtime.isAvailable()) {
          this.report({ kind: 'unavailable' })
          this.close()
          return
        }

        this.mutableState.remindersIssued += 1
        this.mutableState.phase = 'advisory-running'
        const outcome = await this.runBoundedAdvisory()
        this.mutableState.replyActionAttempted = this.observation.replyActionAttempted

        if (this.observation.replyActionAttempted) {
          this.close()
          return
        }
        if (outcome !== 'completed') {
          this.close()
          return
        }
        if (this.mutableState.remindersIssued >= this.reminderBudget) {
          this.close()
          return
        }
        this.mutableState.phase = 'evaluating'
      }
    } catch (error) {
      this.report({ kind: 'failed', error })
    }
    this.close()
  }

  private async runBoundedAdvisory(): Promise<'completed' | 'attempted' | 'aborted' | 'failed'> {
    if (!this.context || !this.runtime || !this.runtimeSessionId || !this.workDir || !this.runAdvisory) return 'aborted'

    const timeout = createTimeoutSignal(this.signal, this.advisoryTimeoutMs)
    const controller = new AbortController()
    let finishAbort!: (outcome: 'attempted' | 'aborted') => void
    const aborted = new Promise<'attempted' | 'aborted'>((resolve) => {
      finishAbort = resolve
    })
    const forwardAbort = () => {
      if (!controller.signal.aborted) controller.abort(timeout.signal.reason)
      finishAbort(this.observation.replyActionAttempted ? 'attempted' : 'aborted')
    }
    const onAttempt = () => {
      if (!controller.signal.aborted) controller.abort(new Error('Slack reply action was attempted'))
      finishAbort('attempted')
    }

    timeout.signal.addEventListener('abort', forwardAbort, { once: true })
    const removeAttemptListener = this.observation.onAttempt(onAttempt)
    this.activeAdvisoryAbort = onAttempt
    if (timeout.signal.aborted) forwardAbort()
    if (this.observation.replyActionAttempted) onAttempt()

    if (timeout.signal.aborted || this.observation.replyActionAttempted) {
      timeout.signal.removeEventListener('abort', forwardAbort)
      removeAttemptListener()
      timeout.dispose()
      this.activeAdvisoryAbort = null
      if (!this.observation.replyActionAttempted && this.signal.aborted) this.report({ kind: 'interrupted' })
      else if (!this.observation.replyActionAttempted && timeout.timedOut()) this.report({ kind: 'timeout' })
      return this.observation.replyActionAttempted ? 'attempted' : 'aborted'
    }

    const pending = Promise.resolve()
      .then(() =>
        this.runAdvisory!({
          runtime: this.runtime!.kind,
          runtimeSessionId: this.runtimeSessionId!,
          workDir: this.workDir!,
          prompt: buildReplyGuardAdvisoryPrompt(this.context!),
          slackExecutionContext: this.context!,
          replyAnchor: this.context!.replyAnchor,
          collaborationSkill: inlineSlackCollaborationSkill(this.context!),
          observation: this.observation,
          signal: controller.signal,
        }),
      )
      .then((result) => {
        if (isSuccessfulAdvisoryResult(result)) return 'completed' as const
        const kind = result?.kind
        if (kind === 'unavailable') this.report({ kind: 'unavailable' })
        else if (kind === 'interrupted') this.report({ kind: 'interrupted' })
        else this.report({ kind: 'failed' })
        return 'failed' as const
      })
      .catch((error) => {
        this.report({ kind: 'failed', error })
        return 'failed' as const
      })

    try {
      return await Promise.race([pending, aborted])
    } finally {
      pending.catch(() => undefined)
      timeout.signal.removeEventListener('abort', forwardAbort)
      removeAttemptListener()
      timeout.dispose()
      this.activeAdvisoryAbort = null
      if (!this.observation.replyActionAttempted && this.signal.aborted) this.report({ kind: 'interrupted' })
      else if (!this.observation.replyActionAttempted && timeout.timedOut()) this.report({ kind: 'timeout' })
    }
  }

  private close(): void {
    this.mutableState.replyActionAttempted = this.observation.replyActionAttempted
    this.mutableState.phase = 'closed'
  }

  private report(diagnostic: ReplyGuardDiagnostic): void {
    try {
      this.onDiagnostic?.(diagnostic)
    } catch {
      // Diagnostics must never affect the original turn.
    }
  }
}

const SHELL_TOOL_NAMES = new Set(['bash', 'sh', 'shell', 'terminal', 'command', 'exec', 'execute'])

function isSuccessfulAdvisoryResult(result: ReplyGuardAdvisoryResult | void): boolean {
  if (result === undefined) return true
  return result.kind === 'completed' || result.kind === 'silent'
}

function readCommand(value: unknown): string | null {
  if (typeof value === 'string') return value
  if (Array.isArray(value) && value.every((item) => typeof item === 'string')) return value.join(' ')
  const record = recordValue(value)
  if (!record) return null
  for (const key of ['command', 'cmd', 'script', 'args']) {
    const candidate = record[key]
    if (typeof candidate === 'string') return candidate
    if (Array.isArray(candidate) && candidate.every((item) => typeof item === 'string')) return candidate.join(' ')
  }
  return null
}

function isReplyActionCommand(command: string | null): boolean {
  if (!command) return false
  return /(?:^|[;&|]\s*|\b(?:sudo|env|command|exec)\s+)mo\s+slack\s+message\s+send(?:\s|$)/i.test(command.trim())
}

function toolCallIdentity(event: NormalizedToolCallObservation): string | null {
  const payload = recordValue(event.payload)
  return stringValue(payload?.['toolCallId']) ?? stringValue(event.id)
}

function normalizeToolName(value: string): string {
  return value
    .trim()
    .toLowerCase()
    .replace(/[ .-]+/g, '_')
}

function recordValue(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null
}

function stringValue(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value : null
}

function nonEmptyString(value: string | null): value is string {
  return typeof value === 'string' && value.trim().length > 0
}

function normalizeBudget(value: number | undefined): number {
  if (value === undefined) return DEFAULT_REPLY_GUARD_REMINDER_BUDGET
  if (!Number.isFinite(value) || value <= 0) return 0
  return Math.floor(value)
}

function normalizeTimeout(value: number | undefined): number {
  if (value === undefined) return DEFAULT_REPLY_GUARD_ADVISORY_TIMEOUT_MS
  if (!Number.isFinite(value) || value < 0) return DEFAULT_REPLY_GUARD_ADVISORY_TIMEOUT_MS
  return Math.floor(value)
}

function neverAbortSignal(): AbortSignal {
  return new AbortController().signal
}
