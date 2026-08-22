import { createHash } from 'node:crypto'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createRuntimeTurnEventProjector } from '../src/runtime/opencode/event-projection.js'
import {
  buildReplyGuardAdvisoryPrompt,
  DEFAULT_REPLY_GUARD_ADVISORY_TIMEOUT_MS,
  DEFAULT_REPLY_GUARD_REMINDER_BUDGET,
  isReplyActionToolCallStarted,
  ReplyActionObservationTracker,
  ReplyGuardCoordinator,
  type ReplyGuardAdvisoryRequest,
  type ReplyGuardRuntimeHandle,
} from '../src/runtime/reply-guard.js'
import { createPiProjector } from '../src/runtime/pi/projector.js'
import type { SlackExecutionContext } from '../src/runtime/slack-execution-context.js'

const runtime: ReplyGuardRuntimeHandle = { kind: 'pi', isAvailable: () => true }

function slackContext(): SlackExecutionContext {
  const instructions = 'You are the speaker in this Slack conversation. Silence is valid.'
  return {
    version: 1,
    replyAnchor: {
      workspaceId: 'workspace-1',
      conversationId: 'conversation-1',
      threadRootMessageId: 'thread-1',
      triggeringMessageId: 'message-1',
      initiatingMemberId: 'member-1',
      connectionId: 'connection-1',
      sessionId: 'session-1',
      dispatchRef: 'dispatch-1',
    },
    collaborationSkill: {
      name: 'mohist-slack-collaboration',
      version: '1',
      instructions,
      contentHash: createHash('sha256').update(instructions, 'utf8').digest('hex'),
    },
  }
}

function startedEvent(command: string, field: 'command' | 'cmd' = 'command') {
  return {
    type: 'tool_call.started',
    payload: {
      toolCallId: 'reply-call-1',
      toolName: 'bash',
      rawInput: { [field]: command },
    },
  }
}

function coordinator(overrides: Partial<ConstructorParameters<typeof ReplyGuardCoordinator>[0]> = {}) {
  return new ReplyGuardCoordinator({
    runtime,
    runtimeSessionId: 'session-1',
    workDir: '/workspace/project',
    slackExecutionContext: slackContext(),
    ...overrides,
  })
}

afterEach(() => {
  vi.useRealTimers()
})

describe('reply action observation', () => {
  it('recognizes Pi and OpenCode normalized tool-call starts', () => {
    const piProjector = createPiProjector('session-1', '/workspace/project')
    const piStarted = piProjector.project({
      type: 'tool_execution_start',
      toolCallId: 'pi-call',
      toolName: 'bash',
      args: { command: 'mo slack message send --conversation C1 --reply-to T1 --text done' },
    })[0]
    const piTracker = new ReplyActionObservationTracker()

    expect(piStarted).toBeDefined()
    expect(isReplyActionToolCallStarted(piStarted!)).toBe(true)
    expect(piTracker.observe(piStarted!)).toBe(true)
    expect(piTracker.replyActionAttempted).toBe(true)

    const openCodeProjector = createRuntimeTurnEventProjector('session-1', '/workspace/project')
    const openCodeStarted = openCodeProjector.project({
      type: 'session.next.tool.called',
      sessionID: 'session-1',
      directory: '/workspace/project',
      payload: {
        callID: 'opencode-call',
        tool: 'bash',
        input: { cmd: 'mo slack message send --conversation C1 --reply-to T1 --text done' },
      },
    })[0]
    const openCodeTracker = new ReplyActionObservationTracker()

    expect(openCodeStarted).toBeDefined()
    expect(isReplyActionToolCallStarted(openCodeStarted!)).toBe(true)
    expect(openCodeTracker.observe(openCodeStarted!)).toBe(true)
    expect(openCodeTracker.replyActionAttempted).toBe(true)
  })

  it('keeps a rejected or interrupted reply action marked after completion facts', () => {
    const tracker = new ReplyActionObservationTracker()
    const start = startedEvent('mo slack message send --conversation C1 --reply-to T1 --text done')
    const completed = {
      ...start,
      type: 'tool_call.completed',
      payload: { ...start.payload, status: 'failed', rawOutput: 'rejected' },
    }

    expect(tracker.observe(start)).toBe(true)
    expect(tracker.observe(completed)).toBe(false)
    expect(tracker.replyActionAttempted).toBe(true)
  })

  it('does not infer an attempt from final text, unrelated tools, or terminal facts', () => {
    const tracker = new ReplyActionObservationTracker()

    expect(tracker.observe({ type: 'message.delta', payload: { text: 'Done' } })).toBe(false)
    expect(
      tracker.observe({
        type: 'tool_call.started',
        payload: { toolCallId: 'other', toolName: 'bash', rawInput: { command: 'echo mo slack message send' } },
      }),
    ).toBe(false)
    expect(tracker.observe({ type: 'turn.completed', payload: {} })).toBe(false)
    expect(tracker.replyActionAttempted).toBe(false)
  })

  it('deduplicates the same tool-call start and never clears the marker', () => {
    const tracker = new ReplyActionObservationTracker()
    const event = startedEvent('mo slack message send --conversation C1 --reply-to T1 --text done')

    expect(tracker.observe(event)).toBe(true)
    expect(tracker.observe(event)).toBe(false)
    expect(tracker.replyActionAttempted).toBe(true)
  })
})

describe('ReplyGuardCoordinator', () => {
  it('uses the default one-reminder budget and closes after one silent opportunity', async () => {
    const requests: ReplyGuardAdvisoryRequest[] = []
    const guard = coordinator({
      runAdvisory: async (request) => {
        requests.push(request)
        return { kind: 'silent' }
      },
    })

    const result = await guard.evaluate({ status: 'success' })
    await guard.evaluate({ status: 'duplicate' })

    expect(result).toEqual({ status: 'success' })
    expect(DEFAULT_REPLY_GUARD_REMINDER_BUDGET).toBe(1)
    expect(DEFAULT_REPLY_GUARD_ADVISORY_TIMEOUT_MS).toBe(30_000)
    expect(requests).toHaveLength(1)
    expect(guard.state).toEqual({
      replyActionAttempted: false,
      remindersIssued: 1,
      phase: 'closed',
    })
  })

  it('passes the existing session, work directory, reply context, and collaboration skill', async () => {
    const requests: ReplyGuardAdvisoryRequest[] = []
    const context = slackContext()
    const guard = coordinator({
      slackExecutionContext: context,
      runtime: { kind: 'opencode', isAvailable: () => true },
      runAdvisory: async (request) => {
        requests.push(request)
        request.observation.observe(
          startedEvent('mo slack message send --conversation C1 --reply-to T1 --text done', 'cmd'),
        )
        return { kind: 'completed' }
      },
    })

    await guard.evaluate('original')

    const request = requests[0]
    expect(request).toMatchObject({
      runtime: 'opencode',
      runtimeSessionId: 'session-1',
      workDir: '/workspace/project',
      slackExecutionContext: context,
      replyAnchor: context.replyAnchor,
      collaborationSkill: {
        name: context.collaborationSkill.name,
        instructions: context.collaborationSkill.instructions,
      },
    })
    expect(request.prompt).toBe(buildReplyGuardAdvisoryPrompt(context))
    expect(request.prompt).toContain('publish a self-contained conclusion')
    expect(request.prompt).toContain('deliberately remain silent')
    expect(request.prompt).toContain(context.replyAnchor.conversationId)
    expect(request.signal).toBeInstanceOf(AbortSignal)
    expect(guard.state).toMatchObject({ replyActionAttempted: true, remindersIssued: 1, phase: 'closed' })
  })

  it('stops after a reply action attempt even when the later action result fails', async () => {
    const tracker = new ReplyActionObservationTracker()
    let completionKnown = false
    const guard = coordinator({
      observation: tracker,
      runAdvisory: async (request) => {
        expect(request.observation.replyActionAttempted).toBe(false)
        request.observation.observe(startedEvent('mo slack message send --conversation C1 --reply-to T1 --text done'))
        completionKnown = true
        return { kind: 'failed' }
      },
    })

    const original = { status: 'failed', exitCode: 1 }
    expect(await guard.evaluate(original)).toBe(original)
    expect(completionKnown).toBe(true)
    expect(guard.state).toEqual({ replyActionAttempted: true, remindersIssued: 1, phase: 'closed' })
  })

  it('bypasses malformed or non-Slack context without invoking the advisory', async () => {
    const runAdvisory = vi.fn(async () => ({ kind: 'completed' as const }))
    const malformed = coordinator({ slackExecutionContext: { version: 1, replyAnchor: {} }, runAdvisory })
    const absent = coordinator({ slackExecutionContext: null, runAdvisory })

    await malformed.evaluate('malformed')
    await absent.evaluate('absent')

    expect(runAdvisory).not.toHaveBeenCalled()
    expect(malformed.state.phase).toBe('closed')
    expect(absent.state.phase).toBe('closed')
  })

  it('closes without a reminder when the runtime is unavailable', async () => {
    const diagnostics: string[] = []
    const runAdvisory = vi.fn(async () => ({ kind: 'completed' as const }))
    const guard = coordinator({
      runtime: { kind: 'pi', isAvailable: () => false },
      runAdvisory,
      onDiagnostic: (diagnostic) => diagnostics.push(diagnostic.kind),
    })

    await guard.evaluate('original')

    expect(runAdvisory).not.toHaveBeenCalled()
    expect(diagnostics).toEqual(['unavailable'])
    expect(guard.state).toMatchObject({ remindersIssued: 0, phase: 'closed' })
  })

  it('contains advisory failure and preserves the original result without retrying', async () => {
    const runAdvisory = vi.fn(async () => {
      throw new Error('runtime rejected advisory')
    })
    const guard = coordinator({ runAdvisory })
    const original = { status: 'success', output: 'unchanged' }

    expect(await guard.evaluate(original)).toBe(original)
    await guard.evaluate(original)

    expect(runAdvisory).toHaveBeenCalledTimes(1)
    expect(guard.state).toMatchObject({ remindersIssued: 1, phase: 'closed' })
  })

  it('bounds an advisory, aborts it, and ignores its late completion', async () => {
    vi.useFakeTimers()
    let resolveAdvisory!: () => void
    const diagnostics: string[] = []
    const runAdvisory = vi.fn(
      async () =>
        await new Promise<void>((resolve) => {
          resolveAdvisory = resolve
        }),
    )
    const guard = coordinator({
      runAdvisory,
      advisoryTimeoutMs: 10,
      onDiagnostic: (diagnostic) => diagnostics.push(diagnostic.kind),
    })

    const evaluation = guard.evaluate('original')
    await vi.advanceTimersByTimeAsync(10)
    await evaluation
    resolveAdvisory()
    await Promise.resolve()
    await guard.evaluate('duplicate')

    expect(runAdvisory).toHaveBeenCalledTimes(1)
    expect(diagnostics).toContain('timeout')
    expect(guard.state).toMatchObject({ remindersIssued: 1, phase: 'closed' })
  })

  it('preserves the original result when interrupted during an advisory', async () => {
    const controller = new AbortController()
    let resolveAdvisory!: () => void
    const runAdvisory = vi.fn(
      async () =>
        await new Promise<void>((resolve) => {
          resolveAdvisory = resolve
        }),
    )
    const diagnostics: string[] = []
    const guard = coordinator({
      signal: controller.signal,
      runAdvisory,
      onDiagnostic: (diagnostic) => diagnostics.push(diagnostic.kind),
    })

    const evaluation = guard.evaluate('original')
    await Promise.resolve()
    controller.abort()
    const result = await evaluation
    resolveAdvisory()

    expect(result).toBe('original')
    expect(runAdvisory).toHaveBeenCalledTimes(1)
    expect(diagnostics).toContain('interrupted')
    expect(guard.state.phase).toBe('closed')
  })

  it('does not start an advisory after the original signal is already interrupted', async () => {
    const controller = new AbortController()
    controller.abort()
    const runAdvisory = vi.fn(async () => ({ kind: 'completed' as const }))
    const guard = coordinator({ signal: controller.signal, runAdvisory })

    await guard.evaluate('original')

    expect(runAdvisory).not.toHaveBeenCalled()
    expect(guard.state).toMatchObject({ remindersIssued: 0, phase: 'closed' })
  })
})
