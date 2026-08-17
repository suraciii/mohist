import { createHash } from 'node:crypto'
import { describe, expect, vi } from 'vitest'
import type {
  OpenCodeRuntime,
  RuntimeFollowupResult,
  RuntimeResult,
  RuntimeTurnEvent,
  RuntimeTurnObserver,
} from '../src/runtime/opencode/index.js'
import type { PiFollowupResult, PiRuntime, PiRuntimeEvent, PiTurnObserver } from '../src/runtime/pi/index.js'
import {
  buildClient,
  defaultPiBinding,
  flush,
  invokeFollowup,
  lastBuilder,
  followupIt,
} from './support/followup-handler-fixture.js'

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

function opencodePayload(text = 'continue') {
  return {
    target: {
      kind: 'generic' as const,
      projectId: 'proj-1',
      sessionId: 'session-1',
      binding: {
        runtime: 'opencode' as const,
        runtimeSessionId: 'runtime-1',
        runnerId: 'runner-1',
        workDir: '/work/project',
      },
    },
    text,
    operationId: 'followup-guard-1',
    inputId: 'input-1',
    turnId: 'turn-1',
    slackExecutionContext: slackExecutionContext(),
  }
}

function piPayload(text = 'continue') {
  return {
    target: {
      kind: 'generic' as const,
      projectId: 'proj-1',
      sessionId: 'session-1',
      binding: defaultPiBinding(),
    },
    text,
    operationId: 'followup-guard-1',
    inputId: 'input-1',
    turnId: 'turn-1',
    slackExecutionContext: slackExecutionContext(),
  }
}

function openCodeSuccess(text = 'done'): RuntimeResult<RuntimeFollowupResult> {
  return {
    ok: true,
    value: {
      facts: { runtimeSessionId: 'runtime-1', workDir: '/work/project', finalAssistantText: text },
      diagnostics: [],
    },
    diagnostics: [],
  }
}

function piSuccess(text = 'done'): PiFollowupResult {
  return {
    ok: true,
    value: { runtimeSessionId: '/virtual/sessions/one.jsonl', workDir: '/workspace', finalAssistantText: text },
    diagnostics: [],
  }
}

function openCodeReplyEvent(): RuntimeTurnEvent {
  return {
    type: 'tool_call.started',
    runtimeSessionId: 'runtime-1',
    workDir: '/work/project',
    payload: {
      toolCallId: 'reply-after-advisory',
      toolName: 'bash',
      rawInput: { cmd: 'mo slack message send --conversation C1 --reply-to T1 --text done' },
      status: 'running',
    },
  }
}

function piReplyEvent(): PiRuntimeEvent {
  return {
    id: 'pi-reply-after-advisory',
    type: 'tool_call.started',
    runtimeSessionId: '/virtual/sessions/one.jsonl',
    workDir: '/workspace',
    payload: {
      toolCallId: 'reply-after-advisory',
      toolName: 'bash',
      rawInput: { command: 'mo slack message send --conversation C1 --reply-to T1 --text done' },
      status: 'running',
    },
  }
}

describe('Slack follow-up reply guard terminal boundaries', () => {
  followupIt(
    'OpenCode waits for runTurn completion, exhausts silent advisories, and closes activity once',
    async ({ recording }) => {
      let resolveOriginal!: (result: RuntimeResult<RuntimeFollowupResult>) => void
      const originalCompletion = new Promise<RuntimeResult<RuntimeFollowupResult>>((resolve) => {
        resolveOriginal = resolve
      })
      const calls: string[] = []
      const runtime: Partial<OpenCodeRuntime> = {
        ready: () => true,
        diagnostic: () => null,
        async followup(
          request: { prompt: string },
          _observer?: RuntimeTurnObserver,
          _signal?: AbortSignal,
        ): Promise<RuntimeResult<RuntimeFollowupResult>> {
          calls.push(request.prompt)
          if (calls.length === 1) return await originalCompletion
          return openCodeSuccess()
        },
      }
      const resolver = vi.fn(() => ({ runtimeSessionId: 'runtime-1', workDir: '/work/project', projectId: 'proj-1' }))
      buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime as OpenCodeRuntime })

      await expect(invokeFollowup(lastBuilder(), opencodePayload())).resolves.toEqual({ accepted: true })
      expect(calls).toHaveLength(1)
      expect(recording.producedFactCalls).toHaveLength(0)

      resolveOriginal(openCodeSuccess('original output'))
      await flush()
      await flush()

      expect(calls).toHaveLength(3)
      expect(calls[1]).toContain('deliberately remain silent')
      expect(calls[2]).toContain('deliberately remain silent')
      expect(recording.producedFactCalls.filter((record) => record.event.type === 'session.activity')).toHaveLength(1)
      expect(recording.producedFactCalls.at(-1)?.event.payload).toMatchObject({
        status: 'completed',
        output: 'original output',
        operationId: 'followup-guard-1',
      })
      expect(recording.beforeExecutionCalls).toHaveLength(1)
    },
  )

  followupIt(
    'Pi idle preflight is admission only and a reply during the advisory stops further reminders',
    async ({ recording }) => {
      let resolveOriginal!: (result: PiFollowupResult) => void
      const originalCompletion = new Promise<PiFollowupResult>((resolve) => {
        resolveOriginal = resolve
      })
      const calls: string[] = []
      const runtime: Partial<PiRuntime> = {
        ready: () => true,
        diagnostic: () => null,
        async followup(
          request: { prompt: string },
          observer?: PiTurnObserver,
          _signal?: AbortSignal,
        ): Promise<PiFollowupResult> {
          calls.push(request.prompt)
          if (calls.length === 1) {
            return {
              ok: true,
              value: {
                runtimeSessionId: '/virtual/sessions/one.jsonl',
                workDir: '/workspace',
                completion: originalCompletion,
              },
              diagnostics: [],
            }
          }
          observer?.onEvent?.(piReplyEvent())
          return piSuccess('advisory reply')
        },
      }
      const resolver = vi.fn(() => ({
        runtimeSessionId: '/virtual/sessions/one.jsonl',
        workDir: '/workspace',
        projectId: 'proj-1',
      }))
      buildClient({ resolver, outbox: recording.outbox, piRuntime: runtime as PiRuntime })

      await expect(invokeFollowup(lastBuilder(), piPayload())).resolves.toEqual({ accepted: true })
      expect(calls).toHaveLength(1)
      expect(recording.producedFactCalls).toHaveLength(0)

      resolveOriginal(piSuccess('original output'))
      await flush()
      await flush()

      expect(calls).toHaveLength(2)
      expect(recording.producedFactCalls.filter((record) => record.event.type === 'session.activity')).toHaveLength(1)
      expect(recording.producedFactCalls.at(-1)?.event.payload).toMatchObject({
        status: 'completed',
        output: 'original output',
        operationId: 'followup-guard-1',
      })
      expect(recording.beforeExecutionCalls).toHaveLength(1)
    },
  )

  followupIt('an advisory failure preserves the original follow-up activity payload', async ({ recording }) => {
    const calls: string[] = []
    const runtime: Partial<OpenCodeRuntime> = {
      ready: () => true,
      diagnostic: () => null,
      async followup(request: { prompt: string }): Promise<RuntimeResult<RuntimeFollowupResult>> {
        calls.push(request.prompt)
        if (calls.length > 1) {
          return {
            ok: false,
            error: { kind: 'turn-failed', message: 'advisory failed', diagnostics: [] },
            diagnostics: [],
          }
        }
        return openCodeSuccess('original output')
      },
    }
    const resolver = vi.fn(() => ({ runtimeSessionId: 'runtime-1', workDir: '/work/project', projectId: 'proj-1' }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime as OpenCodeRuntime })

    await expect(invokeFollowup(lastBuilder(), opencodePayload())).resolves.toEqual({ accepted: true })
    await flush()
    await flush()

    expect(calls).toHaveLength(2)
    expect(recording.producedFactCalls.filter((record) => record.event.type === 'session.activity')).toHaveLength(1)
    expect(recording.producedFactCalls.at(-1)?.event.payload).toMatchObject({
      status: 'completed',
      output: 'original output',
      operationId: 'followup-guard-1',
    })
  })

  followupIt(
    'a rejected reply action observed before terminal completion suppresses follow-up advisories',
    async ({ recording }) => {
      const runtime: Partial<OpenCodeRuntime> = {
        ready: () => true,
        diagnostic: () => null,
        async followup(
          _request: { prompt: string },
          observer?: RuntimeTurnObserver,
        ): Promise<RuntimeResult<RuntimeFollowupResult>> {
          observer?.onEvent?.({
            ...openCodeReplyEvent(),
            payload: { ...openCodeReplyEvent().payload, status: 'failed', rawOutput: 'rejected' },
          })
          return openCodeSuccess('attempted')
        },
      }
      const resolver = vi.fn(() => ({ runtimeSessionId: 'runtime-1', workDir: '/work/project', projectId: 'proj-1' }))
      buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime as OpenCodeRuntime })

      await expect(invokeFollowup(lastBuilder(), opencodePayload('publish'))).resolves.toEqual({ accepted: true })
      await flush()
      await flush()

      expect(recording.producedFactCalls.filter((record) => record.event.type === 'session.activity')).toHaveLength(1)
    },
  )
})
