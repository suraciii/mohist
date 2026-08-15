import { describe, expect, it as vitestIt, vi } from 'vitest'
import { WorkExecutor } from '../src/runtime/executor.js'
import { piAction } from '../src/actions/pi.js'
import type { DispatchWorkItem } from '../src/core/types.js'
import { makeRecordingOutbox } from './support/outbox-test-helpers.js'
import { defineTestActions } from './support/action-registry-test.js'
import { verifyOnlyWorkspaceManager } from './support/workspace-mock.js'
import { withTestRunnerResources } from './support/test-resources.js'

const workDir = '/virtual/mohist-pi-action-recovery'
const runtimeSessionId = '/virtual/mohist-pi-action-recovery/sessions/pi-1'

const recoveryErrors = [
  'runtime-unavailable',
  'turn-failed',
  'turn-unresolved',
  'session-binding-failed',
  'execution-unavailable',
  'session-workspace-mismatch',
  'runtime-session-missing',
].map((code) => ({ code, description: code }))

function recoveryWork(): DispatchWorkItem {
  return {
    workflowRunId: 'wf-pi-recovery',
    workId: 'plan.1',
    taskRunId: 'task-pi-recovery',
    workType: 'task',
    stage: 'plan',
    title: 'Run agent',
    uses: 'mohist/pi',
    with: { prompt: 'invoke the agent' },
    projectId: 'project-1',
    variables: { workspace: { path: workDir } },
    ownerKind: 'workflow',
    agentRecovery: { runtime: 'pi', runtimeSessionId },
  }
}

function actionRegistry() {
  return defineTestActions({
    'mohist/pi': {
      inputs: {
        prompt: { types: ['string'], required: true },
        session: { types: ['string'] },
        options: { types: ['object'] },
      },
      capabilities: ['agent-turn'],
      errors: recoveryErrors,
      run: (inputs, host) => piAction(inputs, host),
    },
  })
}

function fakeConnection() {
  return {
    runnerId: 'runner-1',
    openWorkflowAgentSession: vi.fn(async () => {
      return { sessionId: 'agent-session-1', runtimeSessionId, workDir }
    }),
    attachWorkflowAgentSession: vi.fn(async () => undefined),
    recoverMissingWorkflowAgentSession: vi.fn(async () => undefined),
  } as never
}

function fakePiRuntime(inspectTurn: unknown) {
  return {
    ready: () => true,
    diagnostic: () => null,
    inspectTurn,
    createSession: vi.fn(async () => {
      throw new Error('recovery must not create a session')
    }),
    runTurn: vi.fn(async () => {
      throw new Error('recovery must not run a turn')
    }),
  } as never
}

function createExecutor(piRuntime: unknown, connection: unknown) {
  const outbox = makeRecordingOutbox()
  const executor = new WorkExecutor(
    actionRegistry(),
    verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
    connection as never,
    workDir,
    undefined,
    null,
    null,
    outbox.outbox,
    (() => {
      let n = 0
      return () => `pi-recovery-${++n}`
    })(),
    piRuntime as never,
  )
  return { executor, outbox }
}

function it(name: string, body: () => Promise<void>): void {
  vitestIt(
    name,
    async () =>
      await withTestRunnerResources(body, {
        gitRunner: async () => ({
          success: false,
          exitCode: 128,
          stdout: '',
          stderr: 'not a git repository',
          combinedOutput: 'not a git repository',
        }),
      }),
  )
}

describe('mohist/pi recovery dispatch reconciliation', () => {
  it('adopts the recorded terminal turn without binding, input, or execution', async () => {
    const inspectTurn = vi.fn(async () => ({
      ok: true,
      value: {
        runtimeSessionId,
        workDir,
        activeTurn: false,
        finalAssistantText: 'adopted final text',
        failed: false,
        errorMessage: null,
      },
      diagnostics: [],
    }))
    const connection = fakeConnection()
    const { executor, outbox } = createExecutor(fakePiRuntime(inspectTurn), connection)

    const result = await executor.execute(recoveryWork(), new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(inspectTurn).toHaveBeenCalledWith({
      target: { runtime: 'pi', runtimeSessionId, workDir },
    })
    expect(outbox.eventTypeList()).toEqual([])
    expect(
      (connection as { openWorkflowAgentSession: ReturnType<typeof vi.fn> }).openWorkflowAgentSession,
    ).not.toHaveBeenCalled()
  })

  it('adopts the recorded failed turn as a terminal failure', async () => {
    const inspectTurn = vi.fn(async () => ({
      ok: true,
      value: {
        runtimeSessionId,
        workDir,
        activeTurn: false,
        finalAssistantText: null,
        failed: true,
        errorMessage: 'provider exploded',
      },
      diagnostics: [],
    }))
    const { executor } = createExecutor(fakePiRuntime(inspectTurn), fakeConnection())

    const result = await executor.execute(recoveryWork(), new AbortController().signal)

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('turn-failed')
    expect(result.error?.message).toBe('provider exploded')
  })

  it('reports unknown for a missing session, a foreign active turn, and non-pi runtimes', async () => {
    const missing = vi.fn(async () => ({
      ok: false,
      error: { kind: 'missing-session', message: 'The bound Pi Session is missing', diagnostics: [] },
      diagnostics: [],
    }))
    const missingResult = await createExecutor(fakePiRuntime(missing), fakeConnection()).executor.execute(
      recoveryWork(),
      new AbortController().signal,
    )
    expect(missingResult.status).toBe('unknown')
    expect(missingResult.error?.code).toBe('runtime-session-missing')

    const streaming = vi.fn(async () => ({
      ok: true,
      value: {
        runtimeSessionId,
        workDir,
        activeTurn: true,
        finalAssistantText: null,
        failed: false,
        errorMessage: null,
      },
      diagnostics: [],
    }))
    const streamingResult = await createExecutor(fakePiRuntime(streaming), fakeConnection()).executor.execute(
      recoveryWork(),
      new AbortController().signal,
    )
    expect(streamingResult.status).toBe('unknown')
    expect(streamingResult.error?.code).toBe('turn-unresolved')

    const foreignRuntime = await createExecutor(fakePiRuntime(vi.fn()), fakeConnection()).executor.execute(
      { ...recoveryWork(), agentRecovery: { runtime: 'opencode', runtimeSessionId } },
      new AbortController().signal,
    )
    expect(foreignRuntime.status).toBe('unknown')
  })

  it('reports unknown when the Pi runtime is not ready to inspect the binding', async () => {
    const piRuntime = {
      ready: () => false,
      diagnostic: () => ({ severity: 'error', code: 'pi-catalog-failed', message: 'catalog unavailable' }),
    }
    const { executor } = createExecutor(piRuntime as never, fakeConnection())

    const result = await executor.execute(recoveryWork(), new AbortController().signal)

    expect(result.status).toBe('unknown')
    expect(result.error?.code).toBe('runtime-unavailable')
  })
})
