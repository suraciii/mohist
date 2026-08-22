import { createHash } from 'node:crypto'
import { join } from 'node:path'
import { describe, expect, it as vitestIt, vi } from 'vitest'
import { createFollowupHandler } from './followup-handler.js'
import { MemoryFileSystem } from '../../tests/support/memory-filesystem.js'
import { withTestRunnerResources } from '../../tests/support/test-resources.js'

function it(name: string, body: (fileSystem: MemoryFileSystem) => Promise<void>): void {
  vitestIt(name, async () => {
    const fileSystem = new MemoryFileSystem()
    try {
      await withTestRunnerResources(async () => await body(fileSystem), { fileSystem })
    } finally {
      await fileSystem.deleteDirectory('/')
      if (fileSystem.exists('/')) throw new Error('follow-up test filesystem was not cleaned up')
    }
  })
}

describe('follow-up terminal failure categories', () => {
  it('records an OpenCode runtime error kind as failureCategory without changing the terminal shape', async () => {
    const records: any[] = []
    const outbox = {
      ready: () => true,
      enqueueBeforeExecution: vi.fn(async (record: unknown) => records.push(record)),
      enqueueProducedFact: vi.fn(async (record: unknown) => records.push(record)),
    }
    const receive = createFollowupHandler({
      followupTargetResolver: () => ({ runtimeSessionId: 'runtime-1', workDir: '/work', projectId: 'project-1' }),
      agentSessionRuntimeEventOutbox: outbox as never,
      openCodeRuntime: {
        ready: () => true,
        followup: vi.fn(async () => ({
          ok: false as const,
          error: { kind: 'unavailable-runtime' as const, message: 'runtime unavailable', diagnostics: [] },
          diagnostics: [],
        })),
      } as never,
    })

    expect(await receive(genericFollowupPayload('opencode'))).toEqual({ accepted: true })
    await flushMicrotasks()

    const terminal = records.find((record) => record.event?.type === 'session.activity')
    expect(terminal).toMatchObject({
      id: expect.stringContaining('followup-activity:operation-1:'),
      event: {
        type: 'session.activity',
        payload: {
          activity: 'unknown',
          status: 'failed',
          failureCategory: 'unavailable-runtime',
          failureReason: 'runtime unavailable',
          source: 'followup',
          operationId: 'operation-1',
          turnId: 'turn-1',
          runtimeSessionId: 'runtime-1',
        },
      },
    })
    expect(Object.keys(terminal.event.payload).sort()).toEqual([
      'activity',
      'completedAt',
      'failureCategory',
      'failureReason',
      'operationId',
      'runtimeSessionId',
      'source',
      'status',
      'turnId',
    ])
  })

  it('uses the shared Pi mapping for deadline-exceeded and missing-session', async () => {
    for (const [kind, category] of [
      ['deadline-exceeded', 'timeout'],
      ['missing-session', 'runtime-session-missing'],
    ] as const) {
      const records: any[] = []
      const outbox = {
        ready: () => true,
        enqueueBeforeExecution: vi.fn(async (record: unknown) => records.push(record)),
        enqueueProducedFact: vi.fn(async (record: unknown) => records.push(record)),
      }
      const receive = createFollowupHandler({
        followupTargetResolver: () => ({ runtimeSessionId: 'runtime-1', workDir: '/work', projectId: 'project-1' }),
        agentSessionRuntimeEventOutbox: outbox as never,
        piRuntime: {
          ready: () => true,
          followup: vi.fn(async () => ({
            ok: false as const,
            error: { kind, message: `${kind} message`, diagnostics: [] },
            diagnostics: [],
          })),
        } as never,
      })

      expect(await receive(genericFollowupPayload('pi'))).toEqual({ accepted: true })
      await flushMicrotasks()

      const terminal = records.find((record) => record.event?.type === 'session.activity')
      expect(terminal.event.payload).toMatchObject({
        failureCategory: category,
        failureReason: `${kind} message`,
      })
    }
  })

  it('keeps failureCategory absent when observer event flushing fails', async () => {
    const records: any[] = []
    const outbox = {
      ready: () => true,
      enqueueBeforeExecution: vi.fn(async (record: unknown) => records.push(record)),
      enqueueProducedFact: vi
        .fn()
        .mockRejectedValueOnce(new Error('observer flush failed'))
        .mockImplementation(async (record: unknown) => records.push(record)),
    }
    const receive = createFollowupHandler({
      followupTargetResolver: () => ({ runtimeSessionId: 'runtime-1', workDir: '/work', projectId: 'project-1' }),
      agentSessionRuntimeEventOutbox: outbox as never,
      openCodeRuntime: {
        ready: () => true,
        followup: vi.fn(async (_request: unknown, observer: { onEvent?: (event: unknown) => void } | undefined) => {
          observer?.onEvent?.({ type: 'session.status', runtimeSessionId: 'runtime-1', workDir: '/work', payload: {} })
          return { ok: true as const, value: { facts: {} }, diagnostics: [] }
        }),
      } as never,
    })

    expect(await receive(genericFollowupPayload('opencode'))).toEqual({ accepted: true })
    await flushMicrotasks()

    const terminal = records.find((record) => record.event?.type === 'session.activity')
    expect(terminal.event.payload).toMatchObject({ status: 'failed', failureReason: 'observer flush failed' })
    expect(terminal.event.payload).not.toHaveProperty('failureCategory')
  })

  it('keeps failureCategory absent when follow-up execution rejects or throws', async () => {
    for (const followup of [
      vi.fn(async () => {
        throw new Error('rejected followup')
      }),
      vi.fn(() => {
        throw new Error('thrown followup')
      }),
    ]) {
      const records: any[] = []
      const outbox = {
        ready: () => true,
        enqueueBeforeExecution: vi.fn(async (record: unknown) => records.push(record)),
        enqueueProducedFact: vi.fn(async (record: unknown) => records.push(record)),
      }
      const receive = createFollowupHandler({
        followupTargetResolver: () => ({ runtimeSessionId: 'runtime-1', workDir: '/work', projectId: 'project-1' }),
        agentSessionRuntimeEventOutbox: outbox as never,
        openCodeRuntime: { ready: () => true, followup } as never,
      })

      const result = await receive(genericFollowupPayload('opencode'))
      await flushMicrotasks()
      const terminal = records.find((record) => record.event?.type === 'session.activity')
      expect(result).toEqual({ accepted: true })
      expect(terminal.event.payload).toMatchObject({ status: 'failed', failureReason: expect.any(String) })
      expect(terminal.event.payload).not.toHaveProperty('failureCategory')
    }
  })

  it('keeps unknown for an expired manager credential even when the runtime has an error kind', async () => {
    const records: any[] = []
    const outbox = {
      ready: () => true,
      enqueueBeforeExecution: vi.fn(async (record: unknown) => records.push(record)),
      enqueueProducedFact: vi.fn(async (record: unknown) => records.push(record)),
    }
    const receive = createFollowupHandler({
      followupTargetResolver: () => ({
        runtimeSessionId: 'runtime-1',
        workDir: '/work',
        projectId: '__mohist_slack_manager__',
      }),
      agentSessionRuntimeEventOutbox: outbox as never,
      piRuntime: {
        ready: () => true,
        followup: vi.fn(async () => ({
          ok: false as const,
          error: { kind: 'deadline-exceeded' as const, message: 'deadline', diagnostics: [] },
          diagnostics: [],
        })),
      } as never,
      runnerRoot: '/tmp/runner',
      createManagerExecutionBoundary: vi.fn(async () => ({
        hasExpired: () => true,
        mask: (value: string) => value,
        redact: (value: unknown) => value,
        dispose: vi.fn(async () => undefined),
      })) as never,
    })

    expect(await receive(managerFollowupPayload())).toEqual({ accepted: true })
    await flushMicrotasks()

    const terminal = records.find((record) => record.event?.type === 'session.activity')
    expect(terminal.event.payload).toMatchObject({
      status: 'unknown',
      reason: 'manager-credential-expired',
      failureCategory: 'unknown',
    })
  })
})

describe('follow-up attachment delivery', () => {
  it('rejects an explicit Slack follow-up without context before resolver or enqueue', async () => {
    const resolver = vi.fn()
    const enqueue = vi.fn()
    const receive = createFollowupHandler({
      followupTargetResolver: resolver,
      agentSessionRuntimeEventOutbox: { ready: () => true, enqueueBeforeExecution: enqueue } as never,
      openCodeRuntime: (() => ({ ready: () => true })) as never,
      strictExecutionSourceValidation: true,
    })

    const result = await receive({
      executionSource: 'slack',
      slackExecutionContext: null,
      text: 'continue',
      operationId: 'operation-1',
      turnId: 'turn-1',
      target: { kind: 'generic', projectId: 'project-1', sessionId: 'session-1' },
    } as never)

    expect(result).toEqual({ accepted: false, error: 'unavailable' })
    expect(resolver).not.toHaveBeenCalled()
    expect(enqueue).not.toHaveBeenCalled()
  })

  it('revokes a fresh Manager grant when a submitted follow-up is redelivered after terminal completion', async () => {
    const boundary = {
      hasExpired: () => false,
      mask: (value: string) => value,
      redact: (value: unknown) => value,
      dispose: vi.fn(async () => undefined),
    }
    const runtime = {
      ready: () => true,
      followup: vi.fn(),
    }
    const outbox = {
      ready: () => true,
      enqueueBeforeExecution: vi.fn(),
      enqueueProducedFact: vi.fn(),
    }
    const journal = {
      claim: vi.fn(async () => 'submitted' as const),
      load: vi.fn(),
      markSubmitted: vi.fn(),
      release: vi.fn(),
    }
    const revoke = vi.fn(async () => undefined)
    const receive = createFollowupHandler({
      followupTargetResolver: () => ({
        runtimeSessionId: 'runtime-1',
        workDir: '/work',
        projectId: '__mohist_slack_manager__',
      }),
      agentSessionRuntimeEventOutbox: outbox as never,
      piRuntime: runtime as never,
      runnerRoot: '/tmp/runner',
      createManagerExecutionBoundary: vi.fn(async () => boundary as never) as never,
      followupOperationJournal: journal,
      onManagerExecutionFinished: revoke,
    })

    expect(await receive(managerFollowupPayload())).toEqual({ accepted: true })
    expect(boundary.dispose).toHaveBeenCalledOnce()
    expect(revoke).toHaveBeenCalledWith('manager:session-1:operation-1')
    expect(runtime.followup).not.toHaveBeenCalled()
  })

  it('marks a successful Manager result as expiry recovery when the grant expired during execution', async () => {
    const records: any[] = []
    const runtime = {
      ready: () => true,
      followup: vi.fn(async () => ({
        ok: true as const,
        value: { facts: {} },
        diagnostics: [],
      })),
    }
    const boundary = {
      hasExpired: () => true,
      mask: (value: string) => value,
      redact: (value: unknown) => value,
      dispose: vi.fn(async () => undefined),
    }
    const outbox = {
      ready: () => true,
      enqueueBeforeExecution: vi.fn(async (record: unknown) => records.push(record)),
      enqueueProducedFact: vi.fn(async (record: unknown) => records.push(record)),
    }
    const receive = createFollowupHandler({
      followupTargetResolver: () => ({
        runtimeSessionId: 'runtime-1',
        workDir: '/work',
        projectId: '__mohist_slack_manager__',
      }),
      agentSessionRuntimeEventOutbox: outbox as never,
      piRuntime: runtime as never,
      runnerRoot: '/tmp/runner',
      createManagerExecutionBoundary: vi.fn(async () => boundary as never) as never,
    })

    expect(await receive(managerFollowupPayload())).toEqual({ accepted: true })
    await flushMicrotasks()

    const terminal = records.find((record) => record.event?.type === 'session.activity')
    expect(terminal.event.payload).toMatchObject({
      activity: 'unknown',
      status: 'unknown',
      failureCategory: 'unknown',
      reason: 'manager-credential-expired',
    })
  })

  it('marks a CLI expiry error as expiry recovery instead of ordinary failure', async () => {
    const records: any[] = []
    const runtime = {
      ready: () => true,
      followup: vi.fn(async () => ({
        ok: false as const,
        error: { kind: 'command-failed', message: 'manager_credential_expired' },
        diagnostics: [],
      })),
    }
    const boundary = {
      hasExpired: () => true,
      mask: (value: string) => value,
      redact: (value: unknown) => value,
      dispose: vi.fn(async () => undefined),
    }
    const outbox = {
      ready: () => true,
      enqueueBeforeExecution: vi.fn(async (record: unknown) => records.push(record)),
      enqueueProducedFact: vi.fn(async (record: unknown) => records.push(record)),
    }
    const receive = createFollowupHandler({
      followupTargetResolver: () => ({
        runtimeSessionId: 'runtime-1',
        workDir: '/work',
        projectId: '__mohist_slack_manager__',
      }),
      agentSessionRuntimeEventOutbox: outbox as never,
      piRuntime: runtime as never,
      runnerRoot: '/tmp/runner',
      createManagerExecutionBoundary: vi.fn(async () => boundary as never) as never,
    })

    expect(await receive(managerFollowupPayload())).toEqual({ accepted: true })
    await flushMicrotasks()

    const terminal = records.find((record) => record.event?.type === 'session.activity')
    expect(terminal.event.payload).toMatchObject({
      activity: 'unknown',
      status: 'unknown',
      failureCategory: 'unknown',
      reason: 'manager-credential-expired',
      failureReason: 'manager_credential_expired',
    })
  })

  it('executes an attachment-only turn through the owning input scope', async (fileSystem) => {
    const workDir = '/virtual/mohist-followup-attachment'
    const runtimeFollowup = vi.fn(async (request: { prompt: string; fileParts?: readonly unknown[] }) => ({
      ok: true as const,
      value: { facts: { runtimeSessionId: 'runtime-1', workDir }, diagnostics: [] },
      diagnostics: [],
    }))
    const runtime = {
      ready: () => true,
      resolveSession: vi.fn(async () => ({ ok: true as const, value: { activeTurn: false } })),
      followup: runtimeFollowup,
    }
    const records: unknown[] = []
    const outbox = {
      ready: () => true,
      enqueueBeforeExecution: vi.fn(async (record: unknown) => {
        records.push(record)
      }),
      enqueueProducedFact: vi.fn(async (record: unknown) => {
        records.push(record)
      }),
    }
    const serverConnection = {
      runnerId: 'runner-1',
      openAgentInputAttachment: vi.fn(
        async (projectId: string, sessionId: string, inputId: string, attachmentId: string) => {
          expect([projectId, sessionId, inputId, attachmentId]).toEqual([
            'project-1',
            'session-1',
            'input-1',
            'attachment-1',
          ])
          return {
            bytes: new TextEncoder().encode('follow-up attachment'),
            contentType: 'text/plain',
            contentDisposition: null,
          }
        },
      ),
    }

    const receive = createFollowupHandler({
      followupTargetResolver: () => ({
        runtimeSessionId: 'runtime-1',
        workDir,
        projectId: 'project-1',
      }),
      agentSessionRuntimeEventOutbox: outbox as never,
      openCodeRuntime: runtime as never,
      connection: serverConnection as never,
      runnerId: 'runner-1',
    })

    const result = await receive({
      target: {
        kind: 'generic',
        projectId: 'project-1',
        sessionId: 'session-1',
        binding: {
          runtime: 'opencode',
          runtimeSessionId: 'runtime-1',
          runnerId: 'runner-1',
          workDir,
        },
      },
      text: '',
      inputId: 'input-1',
      turnId: 'turn-1',
      attachments: [{ id: 'attachment-1', name: 'notes.txt', contentType: 'text/plain', size: 20 }],
      callerTempUrl: 'https://provider.invalid/temp-token',
      providerToken: 'secret-token',
      rawPlatformEvent: { token: 'secret-token' },
    } as never)

    expect(result).toEqual({ accepted: true })
    expect(runtimeFollowup).toHaveBeenCalledOnce()
    const request = runtimeFollowup.mock.calls[0]?.[0]
    expect(request.prompt).toContain('[mohist-attachments]')
    expect(request.prompt).toContain('notes.txt')
    expect(request.prompt).not.toContain('provider.invalid')
    expect(request.prompt).not.toContain('secret-token')
    expect(request.fileParts).toBeUndefined()
    expect(await fileSystem.readText(join(workDir, '.mohist/attachments/input-1/attachment-1/notes.txt'))).toBe(
      'follow-up attachment',
    )
    expect(JSON.stringify(records)).not.toContain('secret-token')
    expect(JSON.stringify(records)).not.toContain('rawPlatformEvent')
  })
})

async function flushMicrotasks(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

function genericFollowupPayload(runtime: 'opencode' | 'pi') {
  return {
    target: {
      kind: 'generic',
      projectId: 'project-1',
      sessionId: 'session-1',
      binding: {
        runtime,
        runtimeSessionId: 'runtime-1',
        runnerId: 'runner-1',
        workDir: '/work',
      },
    },
    text: 'continue',
    operationId: 'operation-1',
    turnId: 'turn-1',
  } as const
}

function managerFollowupPayload() {
  const instructions = 'Manager collaboration instructions'
  return {
    target: {
      kind: 'generic',
      projectId: '__mohist_slack_manager__',
      sessionId: 'session-1',
      binding: {
        runtime: 'pi',
        runtimeSessionId: 'runtime-1',
        runnerId: 'runner-1',
        workDir: '/work',
      },
    },
    text: 'continue',
    operationId: 'operation-1',
    turnId: 'turn-1',
    slackExecutionContext: {
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
        projectId: '__mohist_slack_manager__',
        ownerKind: 'manager',
      },
      collaborationSkill: {
        name: 'test-skill',
        version: '1',
        instructions,
        contentHash: createHash('sha256').update(instructions, 'utf8').digest('hex'),
      },
    },
    managerExecutionGrant: {
      managementCredential: 'management-secret',
      replyCredential: 'reply-secret',
      executionId: 'manager:session-1:operation-1',
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
      deploymentEpoch: 'epoch-1',
    },
  } as const
}
