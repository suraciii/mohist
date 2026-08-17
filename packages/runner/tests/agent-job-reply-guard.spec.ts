import { createHash } from 'node:crypto'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AgentJobExecutor } from '../src/runtime/agent-job-executor.js'
import type { DispatchWorkItem } from '../src/core/types.js'
import type { ServerConnection } from '../src/server/connection.js'
import type {
  OpenCodeRuntime,
  RuntimeResult,
  RuntimeTurnEvent,
  RuntimeTurnObserver,
  RuntimeTurnRequest,
  RuntimeTurnResult,
} from '../src/runtime/opencode/index.js'
import type {
  PiResult,
  PiRuntime,
  PiRuntimeEvent,
  PiTurnObserver,
  PiTurnRequest,
  PiTurnResult,
} from '../src/runtime/pi/index.js'
import { withDefaultRunnerTestResources } from './support/test-resources.js'

const WORK_DIR = '/workspace/agent-job'
const SESSION_ID = 'session-runtime-1'

function test(name: string, body: () => Promise<void>): void {
  it(name, async () => await withDefaultRunnerTestResources(body))
}

type FollowupMode = 'silent' | 'reply' | 'failure' | 'hang'

function slackExecutionContext() {
  const instructions = 'Speak for the Agent in Slack. Silence is valid when there is no useful conclusion.'
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

function buildWork(overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: '',
    workId: 'agent-job-1',
    workType: 'task',
    ownerKind: 'agent-job',
    agentJobId: 'agent-job-1',
    agentSessionId: 'agent-session-1',
    projectId: 'project-1',
    initialInputId: 'input-1',
    initialTurnId: 'turn-1',
    variables: { workspace: { path: WORK_DIR } },
    with: {
      prompt: 'Inspect the change and report the result.',
      slackExecutionContext: slackExecutionContext(),
    },
    ...overrides,
  }
}

function connection(): ServerConnection {
  return {
    runnerId: 'runner-1',
    async getAgentSession() {
      return null
    },
    async openAgentSession() {},
    async attachAgentSession() {},
    async agentSessionRuntimeEvents() {},
  } as unknown as ServerConnection
}

function successOpenCodeResult(text = 'finished'): RuntimeResult<RuntimeTurnResult> {
  return {
    ok: true,
    value: {
      facts: { finalAssistantText: text, runtimeSessionId: SESSION_ID, workDir: WORK_DIR },
      diagnostics: [],
    },
    diagnostics: [],
  }
}

function failedOpenCodeResult(message = 'original turn failed'): RuntimeResult<RuntimeTurnResult> {
  return {
    ok: false,
    error: { kind: 'turn-failed', message, diagnostics: [] },
    diagnostics: [],
  }
}

function successPiResult(text = 'finished'): PiResult<PiTurnResult> {
  return {
    ok: true,
    value: {
      facts: { finalAssistantText: text, runtimeSessionId: '/workspace/session.jsonl', workDir: WORK_DIR },
      diagnostics: [],
    },
    diagnostics: [],
  }
}

function replyOpenCodeEvent(type: 'tool_call.started' | 'tool_call.completed' = 'tool_call.started'): RuntimeTurnEvent {
  return {
    type,
    runtimeSessionId: SESSION_ID,
    workDir: WORK_DIR,
    payload: {
      toolCallId: 'reply-call-1',
      toolName: 'bash',
      status: type === 'tool_call.completed' ? 'failed' : 'running',
      rawInput: { cmd: 'mo slack message send --conversation C1 --reply-to T1 --text done' },
      ...(type === 'tool_call.completed' ? { rawOutput: 'rejected' } : {}),
    },
  }
}

function replyPiEvent(): PiRuntimeEvent {
  return {
    id: 'pi-reply-call-1',
    type: 'tool_call.started',
    runtimeSessionId: '/workspace/session.jsonl',
    workDir: WORK_DIR,
    payload: {
      toolCallId: 'reply-call-1',
      toolName: 'bash',
      status: 'running',
      rawInput: { command: 'mo slack message send --conversation C1 --reply-to T1 --text done' },
    },
  }
}

function makeOpenCodeRuntime(options: {
  turnResult?: RuntimeResult<RuntimeTurnResult>
  turnEvents?: RuntimeTurnEvent[]
  followupMode?: FollowupMode
  followupEvents?: RuntimeTurnEvent[]
} = {}) {
  const runTurnCalls: RuntimeTurnRequest[] = []
  const followupCalls: RuntimeTurnRequest[] = []
  const turnResult = options.turnResult ?? successOpenCodeResult()
  const turnEvents = options.turnEvents ?? []
  const followupMode = options.followupMode ?? 'silent'
  const followupEvents = options.followupEvents ?? []
  const runtime: Partial<OpenCodeRuntime> = {
    ready: () => true,
    diagnostic: () => null,
    async runTurn(request: RuntimeTurnRequest, _signal: AbortSignal, observer?: RuntimeTurnObserver) {
      runTurnCalls.push(request)
      await observer?.onSessionReady?.({ runtimeSessionId: SESSION_ID, workDir: WORK_DIR })
      for (const event of turnEvents) observer?.onEvent?.(event)
      return turnResult
    },
    async followup(request, observer, signal) {
      followupCalls.push(request as unknown as RuntimeTurnRequest)
      for (const event of followupEvents) observer?.onEvent?.(event)
      if (followupMode === 'failure') return failedOpenCodeResult('advisory failed')
      if (followupMode === 'hang') {
        return await new Promise<RuntimeResult<RuntimeTurnResult>>((resolve) => {
          signal?.addEventListener('abort', () => resolve(failedOpenCodeResult('advisory interrupted')), { once: true })
        })
      }
      return {
        ok: true,
        value: { facts: { runtimeSessionId: SESSION_ID, workDir: WORK_DIR }, diagnostics: [] },
        diagnostics: [],
      }
    },
  }
  return { runtime: runtime as OpenCodeRuntime, runTurnCalls, followupCalls }
}

function makePiRuntime(options: {
  turnResult?: PiResult<PiTurnResult>
  turnEvents?: PiRuntimeEvent[]
  followupMode?: FollowupMode
  followupEvents?: PiRuntimeEvent[]
} = {}) {
  const runTurnCalls: PiTurnRequest[] = []
  const followupCalls: PiTurnRequest[] = []
  const turnResult = options.turnResult ?? successPiResult()
  const turnEvents = options.turnEvents ?? []
  const followupMode = options.followupMode ?? 'silent'
  const followupEvents = options.followupEvents ?? []
  const runtime: Partial<PiRuntime> = {
    ready: () => true,
    diagnostic: () => null,
    catalog: () => null,
    async createSession() {
      return { ok: true, value: { runtimeSessionId: '/workspace/session.jsonl', workDir: WORK_DIR }, diagnostics: [] }
    },
    async runTurn(request: PiTurnRequest, _signal: AbortSignal, observer?: PiTurnObserver) {
      runTurnCalls.push(request)
      for (const event of turnEvents) await observer?.onEvent?.(event)
      return turnResult
    },
    async followup(request, observer, signal) {
      followupCalls.push(request as unknown as PiTurnRequest)
      for (const event of followupEvents) await observer?.onEvent?.(event)
      if (followupMode === 'failure') {
        return {
          ok: false,
          error: { kind: 'turn-failed', message: 'advisory failed', diagnostics: [] },
          diagnostics: [],
        }
      }
      if (followupMode === 'hang') {
        return await new Promise<PiResult<never>>((resolve) => {
          signal?.addEventListener(
            'abort',
            () => resolve({ ok: false, error: { kind: 'interrupted', message: 'advisory interrupted', diagnostics: [] }, diagnostics: [] }),
            { once: true },
          )
        })
      }
      return {
        ok: true,
        value: { runtimeSessionId: '/workspace/session.jsonl', workDir: WORK_DIR },
        diagnostics: [],
      }
    },
  }
  return { runtime: runtime as PiRuntime, runTurnCalls, followupCalls }
}

test('guards an unpublished initial OpenCode turn with two bounded reminders and preserves its result', async () => {
  const runtime = makeOpenCodeRuntime({ followupMode: 'silent' })
  const result = await new AgentJobExecutor(connection(), { openCode: runtime.runtime, pi: null }).execute(
    buildWork({ with: { prompt: 'Inspect the change.', runtime: 'opencode', slackExecutionContext: slackExecutionContext() } }),
    new AbortController().signal,
  )

  expect(result).toMatchObject({
    status: 'completed',
    message: 'AgentJob completed',
    output: expect.objectContaining({ runtimeSessionId: SESSION_ID, text: 'finished' }),
  })
  expect(runtime.runTurnCalls).toHaveLength(1)
  expect(runtime.followupCalls).toHaveLength(2)
  expect(runtime.followupCalls.every((request) => request.target.runtimeSessionId === SESSION_ID)).toBe(true)
  expect(runtime.followupCalls.every((request) => request.target.workDir === WORK_DIR)).toBe(true)
  expect(runtime.followupCalls.every((request) => request.prompt.includes('deliberately remain silent'))).toBe(true)
  expect(runtime.followupCalls.every((request) => request.options?.skills?.[0]?.name === 'mohist-slack-collaboration')).toBe(true)
})

test('guards an unpublished initial Pi turn through the same follow-up path', async () => {
  const runtime = makePiRuntime({ followupMode: 'silent' })
  const result = await new AgentJobExecutor(connection(), { openCode: null, pi: runtime.runtime }).execute(
    buildWork({ with: { prompt: 'Inspect the change.', runtime: 'pi', slackExecutionContext: slackExecutionContext() } }),
    new AbortController().signal,
  )

  expect(result.status).toBe('completed')
  expect(runtime.runTurnCalls).toHaveLength(1)
  expect(runtime.followupCalls).toHaveLength(2)
  expect(runtime.followupCalls[0]?.target.runtimeSessionId).toBe('/workspace/session.jsonl')
  expect(runtime.followupCalls[0]?.target.workDir).toBe(WORK_DIR)
})

test('does not advise after an accepted or rejected reply action attempt', async () => {
  const runtime = makeOpenCodeRuntime({
    turnEvents: [replyOpenCodeEvent(), replyOpenCodeEvent('tool_call.completed')],
  })
  const result = await new AgentJobExecutor(connection(), { openCode: runtime.runtime, pi: null }).execute(
    buildWork({ with: { prompt: 'Publish the result.', slackExecutionContext: slackExecutionContext() } }),
    new AbortController().signal,
  )

  expect(result.status).toBe('completed')
  expect(runtime.followupCalls).toHaveLength(0)
})

test('does not advise after a Pi reply action attempt even when the action later fails', async () => {
  const runtime = makePiRuntime({ turnEvents: [replyPiEvent()] })
  const result = await new AgentJobExecutor(connection(), { openCode: null, pi: runtime.runtime }).execute(
    buildWork({ with: { prompt: 'Publish the Pi result.', runtime: 'pi', slackExecutionContext: slackExecutionContext() } }),
    new AbortController().signal,
  )

  expect(result.status).toBe('completed')
  expect(runtime.followupCalls).toHaveLength(0)
})

test('stops after an Agent-authored reply during the first advisory', async () => {
  const runtime = makeOpenCodeRuntime({
    followupMode: 'reply',
    followupEvents: [replyOpenCodeEvent()],
  })
  const result = await new AgentJobExecutor(connection(), { openCode: runtime.runtime, pi: null }).execute(
    buildWork(),
    new AbortController().signal,
  )

  expect(result.status).toBe('completed')
  expect(runtime.followupCalls).toHaveLength(1)
})

test('preserves a failed initial WorkItemResult when an advisory invocation fails', async () => {
  const runtime = makeOpenCodeRuntime({
    turnResult: failedOpenCodeResult(),
    followupMode: 'failure',
  })
  const result = await new AgentJobExecutor(connection(), { openCode: runtime.runtime, pi: null }).execute(
    buildWork(),
    new AbortController().signal,
  )

  expect(result).toMatchObject({
    status: 'failed',
    message: 'original turn failed',
    error: { code: 'turn-failed', message: 'original turn failed' },
  })
  expect(runtime.followupCalls).toHaveLength(1)
  expect(runtime.runTurnCalls).toHaveLength(1)
})

test('preserves the original result and does not retry after an advisory timeout', async () => {
  vi.useFakeTimers()
  const runtime = makeOpenCodeRuntime({ followupMode: 'hang' })
  const controller = new AbortController()
  const execution = new AgentJobExecutor(connection(), { openCode: runtime.runtime, pi: null }).execute(
    buildWork(),
    controller.signal,
  )
  await vi.waitFor(() => expect(runtime.followupCalls).toHaveLength(1))
  await vi.advanceTimersByTimeAsync(30_000)

  const result = await execution
  expect(result.status).toBe('completed')
  expect(runtime.runTurnCalls).toHaveLength(1)
  expect(runtime.followupCalls).toHaveLength(1)
})

test('bypasses the guard for absent and malformed Slack contexts', async () => {
  const workItems = [
    { work: buildWork({ with: { prompt: 'No Slack guard.' } }), status: 'completed' },
    {
      work: buildWork({ with: { prompt: 'Malformed Slack guard.', slackExecutionContext: { version: 1, replyAnchor: {} } } }),
      status: 'failed',
    },
  ] as const
  for (const item of workItems) {
    const runtime = makeOpenCodeRuntime()
    const result = await new AgentJobExecutor(connection(), { openCode: runtime.runtime, pi: null }).execute(
      item.work,
      new AbortController().signal,
    )

    expect(result.status).toBe(item.status)
    expect(runtime.followupCalls).toHaveLength(0)
  }
})

test('does not treat final assistant output alone as a reply attempt on Pi', async () => {
  const runtime = makePiRuntime({ followupMode: 'silent' })
  const result = await new AgentJobExecutor(connection(), { openCode: null, pi: runtime.runtime }).execute(
    buildWork({ with: { prompt: 'Text only.', runtime: 'pi', slackExecutionContext: slackExecutionContext() } }),
    new AbortController().signal,
  )

  expect(result.output).toEqual(expect.objectContaining({ text: 'finished' }))
  expect(runtime.followupCalls).toHaveLength(2)
})

afterEach(() => {
  vi.useRealTimers()
})
