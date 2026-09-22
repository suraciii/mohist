import { describe, expect, it, vi } from 'vitest'
import type { DispatchWorkItem } from '../src/core/types.js'
import { recoverInitialBindingIfNeeded, type BindingResolution } from '../src/runtime/agent-job-executor.js'
import { admitInitialProviderSubmission } from '../src/runtime/agent-job-turn.js'

const work: DispatchWorkItem = {
  workflowRunId: '',
  workId: 'work-1',
  workType: 'agent-job',
  ownerKind: 'agent-job',
  agentJobId: 'job-1',
  projectId: 'project-1',
  agentSessionId: 'session-1',
  initialInputId: 'input-1',
  initialTurnId: 'turn-1',
  with: { prompt: 'hello', runtime: 'opencode' },
}

const binding: BindingResolution = {
  agentSessionId: 'session-1',
  runnerId: 'runner-1',
  runtime: 'opencode',
  runtimeSessionId: 'runtime-old',
  processGeneration: 'process-1',
  initialOperationId: 'operation-1',
  submissionAttemptId: 'attempt-1',
}

function successfulRecoveryConnection(order: string[]) {
  return {
    runnerId: 'runner-1',
    prepareAgentJobInitialRecovery: vi.fn(async (_jobId: string, _body: unknown) => {
      order.push('prepare')
      return {
        phase: 'creating',
        candidateCreationAuthorized: true,
        runtime: null,
        runtimeSessionId: null,
      }
    }),
    completeAgentJobInitialRecovery: vi.fn(async (_jobId: string, _body: unknown) => {
      order.push('complete')
      return {
        phase: 'ready',
        candidateCreationAuthorized: false,
        runtime: 'opencode',
        runtimeSessionId: 'runtime-new',
      }
    }),
  }
}

function missingOpenCode(order: string[]) {
  return {
    ready: () => true,
    resolveSession: vi.fn(async () => {
      order.push('probe')
      return { ok: false, error: { kind: 'missing-session', message: 'missing' }, diagnostics: [] }
    }),
    createSession: vi.fn(async () => {
      order.push('create')
      return {
        ok: true,
        value: { runtimeSessionId: 'runtime-new', workDir: '/work' },
        diagnostics: [],
      }
    }),
  }
}

describe('initial AgentJob pre-submission recovery', () => {
  it('persists admission before one candidate creation and confirms both owners before execution', async () => {
    const order: string[] = []
    const connection = successfulRecoveryConnection(order)
    const runtime = missingOpenCode(order)

    const result = await recoverInitialBindingIfNeeded(
      work,
      binding,
      '/work',
      'opencode',
      connection as never,
      { openCode: runtime as never, pi: null },
      new AbortController().signal,
      null,
    )

    expect(result).toEqual({
      ok: true,
      binding: { ...binding, runtime: 'opencode', runtimeSessionId: 'runtime-new' },
    })
    expect(order).toEqual(['probe', 'prepare', 'create', 'complete'])
    expect(connection.prepareAgentJobInitialRecovery.mock.calls[0]?.[1]).toMatchObject({
      recoveryReason: 'same-runtime-missing',
    })
  })

  it.each(['unavailable-runtime', 'deadline-exceeded', 'turn-failed'] as const)(
    'does not treat %s as missing evidence',
    async (kind) => {
      const prepare = vi.fn()
      const createSession = vi.fn()
      const result = await recoverInitialBindingIfNeeded(
        work,
        binding,
        '/work',
        'opencode',
        { runnerId: 'runner-1', prepareAgentJobInitialRecovery: prepare } as never,
        {
          openCode: { ready: () => true, resolveSession: async () => ({
            ok: false,
            error: { kind, message: kind },
            diagnostics: [],
          }), createSession } as never,
          pi: null,
        },
        new AbortController().signal,
        null,
      )

      expect(result.ok).toBe(false)
      expect(prepare).not.toHaveBeenCalled()
      expect(createSession).not.toHaveBeenCalled()
    },
  )

  it('uses the approved OpenCode-to-Pi fallback only when OpenCode is not ready and Pi is ready', async () => {
    const order: string[] = []
    const connection = successfulRecoveryConnection(order)
    connection.completeAgentJobInitialRecovery.mockResolvedValueOnce({
      phase: 'ready',
      candidateCreationAuthorized: false,
      runtime: 'pi',
      runtimeSessionId: '/work/pi-new.jsonl',
    })
    const pi = {
      ready: () => true,
      createSession: vi.fn(async () => {
        order.push('create-pi')
        return {
          ok: true,
          value: { runtimeSessionId: '/work/pi-new.jsonl', workDir: '/work' },
          diagnostics: [],
        }
      }),
    }
    const openCodeProbe = vi.fn()

    const result = await recoverInitialBindingIfNeeded(
      work,
      binding,
      '/work',
      'opencode',
      connection as never,
      {
        openCode: { ready: () => false, resolveSession: openCodeProbe } as never,
        pi: pi as never,
      },
      new AbortController().signal,
      null,
    )

    expect(result).toEqual({
      ok: true,
      binding: { ...binding, runtime: 'pi', runtimeSessionId: '/work/pi-new.jsonl' },
    })
    expect(openCodeProbe).not.toHaveBeenCalled()
    expect(order).toEqual(['prepare', 'create-pi'])
    expect(connection.prepareAgentJobInitialRecovery.mock.calls[0]?.[1]).toMatchObject({
      expectedRuntime: 'opencode',
      recoveryReason: 'configured-fallback',
    })
    expect(connection.completeAgentJobInitialRecovery).toHaveBeenCalledOnce()
  })

  it('does not select configured fallback for a Manager execution', async () => {
    const prepare = vi.fn()
    const piCreate = vi.fn()

    const result = await recoverInitialBindingIfNeeded(
      work,
      binding,
      '/work',
      'opencode',
      { runnerId: 'runner-1', prepareAgentJobInitialRecovery: prepare } as never,
      {
        openCode: { ready: () => false } as never,
        pi: { ready: () => true, createSession: piCreate } as never,
      },
      new AbortController().signal,
      {} as never,
    )

    expect(result.ok).toBe(false)
    expect(prepare).not.toHaveBeenCalled()
    expect(piCreate).not.toHaveBeenCalled()
  })

  it('uses the same persisted operation after a lost prepare response without recreating twice', async () => {
    const order: string[] = []
    const prepare = vi.fn()
      .mockRejectedValueOnce(new Error('response lost'))
      .mockResolvedValueOnce({
        phase: 'creating',
        candidateCreationAuthorized: true,
        runtime: null,
        runtimeSessionId: null,
      })
    const connection = {
      ...successfulRecoveryConnection(order),
      prepareAgentJobInitialRecovery: prepare,
    }
    const runtime = missingOpenCode(order)

    const result = await recoverInitialBindingIfNeeded(
      work,
      binding,
      '/work',
      'opencode',
      connection as never,
      { openCode: runtime as never, pi: null },
      new AbortController().signal,
      null,
    )

    expect(result.ok).toBe(true)
    expect(prepare).toHaveBeenCalledTimes(2)
    expect(runtime.createSession).toHaveBeenCalledOnce()
  })

  it('reuses the same candidate when the Session replacement response is lost', async () => {
    const order: string[] = []
    const connection = successfulRecoveryConnection(order)
    connection.completeAgentJobInitialRecovery
      .mockRejectedValueOnce(new Error('replacement response lost'))
      .mockResolvedValueOnce({
        phase: 'ready',
        candidateCreationAuthorized: false,
        runtime: 'opencode',
        runtimeSessionId: 'runtime-new',
      })
    const runtime = missingOpenCode(order)

    const result = await recoverInitialBindingIfNeeded(
      work,
      binding,
      '/work',
      'opencode',
      connection as never,
      { openCode: runtime as never, pi: null },
      new AbortController().signal,
      null,
    )

    expect(result.ok).toBe(true)
    expect(runtime.createSession).toHaveBeenCalledOnce()
    expect(connection.completeAgentJobInitialRecovery).toHaveBeenCalledTimes(2)
    expect(connection.completeAgentJobInitialRecovery.mock.calls[0]?.[1])
      .toEqual(connection.completeAgentJobInitialRecovery.mock.calls[1]?.[1])
  })

  it('keeps uncertain candidate creation fenced and does not attempt binding completion', async () => {
    const order: string[] = []
    const connection = successfulRecoveryConnection(order)
    const runtime = missingOpenCode(order)
    runtime.createSession.mockResolvedValueOnce({
      ok: false,
      error: { kind: 'unknown', message: 'create response lost' },
      diagnostics: [],
    } as never)

    const result = await recoverInitialBindingIfNeeded(
      work,
      binding,
      '/work',
      'opencode',
      connection as never,
      { openCode: runtime as never, pi: null },
      new AbortController().signal,
      null,
    )

    expect(result.ok).toBe(false)
    expect(connection.completeAgentJobInitialRecovery).not.toHaveBeenCalled()
    expect(runtime.createSession).toHaveBeenCalledOnce()
  })

  it('retries a lost start receipt with the same executor attempt before provider submission', async () => {
    const starts: unknown[] = []
    const start = vi.fn(async (_jobId: string, body: unknown) => {
      starts.push(body)
      if (starts.length === 1) throw new Error('response lost')
      return {
        effectAdmitted: true,
        submissionAuthorized: true,
        runtime: 'opencode',
        runtimeSessionId: 'runtime-old',
      }
    })

    await admitInitialProviderSubmission(
      { startAgentJobInitialInput: start } as never,
      work,
      binding,
      'opencode',
      'runtime-old',
      new AbortController().signal,
    )

    expect(start).toHaveBeenCalledTimes(2)
    expect(starts[0]).toEqual(starts[1])
  })
})
