import { createCredentialMaskerFromEnvironment, type CredentialMasker } from '../task-log.js'
import { diagnostic } from './errors.js'
import { createPiProjector } from './projector.js'
import type {
  PiDiagnostic,
  PiErrorKind,
  PiFollowupRequest,
  PiFollowupResult,
  PiResult,
  PiRuntimeEvent,
  PiTurnObserver,
} from './types.js'
import type { PiSdkSession } from './sdk.js'

export type PiFollowupFailureKind = Extract<PiErrorKind, 'turn-failed' | 'interrupted'>

export interface PiFollowupSupport {
  readonly masker?: CredentialMasker
  readonly withSessionLock: <T>(path: string, operation: () => Promise<T>) => Promise<T>
  readonly failure: (
    kind: PiFollowupFailureKind,
    messageText: string,
    diagnostics?: readonly PiDiagnostic[],
  ) => PiResult<never>
  readonly mask: (value: string) => string
}

interface FollowupInput {
  readonly support: PiFollowupSupport
  readonly path: string
  readonly session: PiSdkSession
  readonly request: PiFollowupRequest
  readonly observer: PiTurnObserver | undefined
  readonly signal: AbortSignal | undefined
}

export function startIdleFollowup(input: FollowupInput): {
  readonly admission: Promise<PiFollowupResult>
  readonly terminal: Promise<PiFollowupResult>
} {
  const { support, path, session, request, observer, signal } = input
  const projector = createPiProjector(
    path,
    request.target.workDir,
    support.masker ?? createCredentialMaskerFromEnvironment(),
  )
  const report = (events: readonly PiRuntimeEvent[]) => events.forEach((event) => observer?.onEvent?.(event))
  let settleAdmission!: (result: PiFollowupResult) => void
  let settleTerminal!: (result: PiFollowupResult) => void
  let admissionSettled = false
  const admission = new Promise<PiFollowupResult>((resolve) => {
    settleAdmission = resolve
  })
  const terminal = new Promise<PiFollowupResult>((resolve) => {
    settleTerminal = resolve
  })
  const settleAdmissionOnce = (result: PiFollowupResult) => {
    if (admissionSettled) return
    admissionSettled = true
    settleAdmission(result)
  }

  const operation = support.withSessionLock(path, async () => {
    let unsubscribe = () => {}
    let aborting = false
    const onAbort = () => {
      if (aborting) return
      aborting = true
      void session.abort().catch(() => undefined)
      const interrupted = support.failure('interrupted', 'Pi follow-up was interrupted')
      settleAdmissionOnce(interrupted)
      settleTerminal(interrupted)
    }
    if (signal) {
      signal.addEventListener('abort', onAbort, { once: true })
      if (signal.aborted) onAbort()
    }
    try {
      if (aborting) return support.failure('interrupted', 'Pi follow-up was interrupted')
      unsubscribe = session.subscribe((event) => report(projector.project(event)))
      let preflightAccepted = false
      await session.prompt(request.prompt, {
        expandPromptTemplates: false,
        preflight: (success) => {
          if (success) {
            preflightAccepted = true
            settleAdmissionOnce({
              ok: true,
              value: { runtimeSessionId: path, workDir: request.target.workDir },
              diagnostics: [],
            })
            return
          }
          settleAdmissionOnce(
            support.failure('turn-failed', 'Pi rejected follow-up reception (preflight rejected the prompt)', [
              diagnostic(
                'preflight-rejected',
                'Pi preflight rejected the follow-up prompt — model or credentials missing',
              ),
            ]),
          )
        },
      })
      report(projector.reconcile(session.messages))
      const terminalResult: PiFollowupResult = {
        ok: true,
        value: {
          runtimeSessionId: path,
          workDir: request.target.workDir,
          finalAssistantText: finalText(session.messages),
        },
        diagnostics: [],
      }
      if (!preflightAccepted) settleAdmissionOnce(terminalResult)
      return terminalResult
    } catch (cause) {
      const failure = support.failure('turn-failed', 'Pi follow-up prompt failed', [
        diagnostic('prompt-failed', support.mask(message(cause))),
      ])
      settleAdmissionOnce(failure)
      return failure
    } finally {
      unsubscribe()
      if (signal) signal.removeEventListener('abort', onAbort)
    }
  })
  void operation.then(settleTerminal, (cause) => {
    const failure = support.failure('turn-failed', 'Pi follow-up prompt failed', [
      diagnostic('prompt-failed', support.mask(message(cause))),
    ])
    settleAdmissionOnce(failure)
    settleTerminal(failure)
  })

  return { admission, terminal }
}

export function waitForPiTerminal(
  input: FollowupInput,
  checkImmediately = true,
): {
  readonly completion: Promise<PiFollowupResult>
  readonly cancel: () => void
} {
  const { support, path, session, request, observer, signal } = input
  const projector = createPiProjector(
    path,
    request.target.workDir,
    support.masker ?? createCredentialMaskerFromEnvironment(),
  )
  const report = (events: readonly PiRuntimeEvent[]) => events.forEach((event) => observer?.onEvent?.(event))
  let cancel!: () => void
  const completion = new Promise<PiFollowupResult>((resolve) => {
    let settled = false
    let timer: ReturnType<typeof setInterval> | null = null
    let unsubscribe = () => {}
    const finish = (result: PiFollowupResult) => {
      if (settled) return
      settled = true
      if (timer) clearInterval(timer)
      unsubscribe()
      signal?.removeEventListener('abort', onAbort)
      resolve(result)
    }
    const check = () => {
      if (settled || session.isStreaming) return
      report(projector.reconcile(session.messages))
      finish({
        ok: true,
        value: {
          runtimeSessionId: path,
          workDir: request.target.workDir,
          finalAssistantText: finalText(session.messages),
        },
        diagnostics: [],
      })
    }
    const onAbort = () => {
      void session.abort().catch(() => undefined)
      finish(support.failure('interrupted', 'Pi follow-up was interrupted'))
    }
    cancel = () => finish(support.failure('interrupted', 'Pi follow-up was interrupted'))
    unsubscribe = session.subscribe((event) => {
      report(projector.project(event))
      check()
    })
    if (signal) {
      signal.addEventListener('abort', onAbort, { once: true })
      if (signal.aborted) onAbort()
    }
    timer = setInterval(check, 100)
    timer.unref?.()
    if (checkImmediately) check()
  })
  return { completion, cancel }
}

function finalText(messages: readonly { role?: string; content?: unknown }[]): string | null {
  const assistant = [...messages].reverse().find((item) => item.role === 'assistant')
  return contentText(assistant?.content)
}

function contentText(content: unknown): string | null {
  if (typeof content === 'string') return content
  if (!Array.isArray(content)) return null
  const text = content
    .map((part) =>
      typeof part === 'string'
        ? part
        : part && typeof part === 'object' && 'text' in part && typeof part.text === 'string'
          ? part.text
          : '',
    )
    .join('')
  return text || null
}

function message(cause: unknown): string {
  return cause instanceof Error ? cause.message || 'Pi operation failed' : String(cause)
}
