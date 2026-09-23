import { describe, expect, it, vi } from 'vitest'
import type { DispatchWorkItem } from '../src/core/types.js'
import type { BindingResolution } from '../src/runtime/agent-job-executor.js'
import { executeCodexTurn } from '../src/runtime/agent-job-codex-turn.js'
import { executeOpenCodeTurn, executePiTurn } from '../src/runtime/agent-job-turn.js'
import { CodexRuntime } from '../src/runtime/codex/index.js'
import type { CodexReadinessProbe } from '../src/runtime/codex/readiness.js'
import type { CodexServerHandle } from '../src/runtime/codex/server-process.js'
import { OpenCodeRuntime } from '../src/runtime/opencode/index.js'
import { PiRuntime } from '../src/runtime/pi/index.js'
import { buildRuntime, DEFAULT_SESSION_ID } from './support/opencode-turn-test-support.js'
import { ControlledPiSession, controlledPiSdk } from './support/pi-turn-session.js'
import { deferred } from './support/deferred.js'

const runtimeWork = (runtime: 'opencode' | 'pi' | 'codex'): DispatchWorkItem => ({
  workflowRunId: '',
  workId: `work-${runtime}`,
  workType: 'agent-job',
  ownerKind: 'agent-job',
  agentJobId: `job-${runtime}`,
  projectId: 'project-1',
  agentSessionId: 'session-1',
  initialInputId: 'input-1',
  initialTurnId: 'turn-1',
  with: { prompt: 'hello', runtime },
})

const runtimeBinding = (runtime: 'opencode' | 'pi' | 'codex', runtimeSessionId: string | null): BindingResolution => ({
  agentSessionId: 'session-1',
  runnerId: 'runner-1',
  runtime,
  runtimeSessionId,
  processGeneration: 'process-1',
  initialOperationId: 'operation-1',
  submissionAttemptId: 'attempt-1',
})

function connection(startAgentJobInitialInput: ReturnType<typeof vi.fn>) {
  return {
    runnerId: 'runner-1',
    openAgentSession: vi.fn(async () => undefined),
    attachAgentSession: vi.fn(async () => undefined),
    agentSessionRuntimeEvents: vi.fn(async () => undefined),
    startAgentJobInitialInput,
  }
}

function heldAdmission(runtime: 'opencode' | 'pi' | 'codex', runtimeSessionId: string) {
  const entered = deferred()
  const release = deferred()
  const start = vi.fn(async () => {
    entered.resolve()
    await release.promise
    return {
      effectAdmitted: true,
      submissionAuthorized: true,
      runtime,
      runtimeSessionId,
    }
  })
  return { start, entered, release }
}

function rejectedAdmission(runtime: 'opencode' | 'pi' | 'codex', runtimeSessionId: string) {
  return vi.fn(async () => ({
    effectAdmitted: true,
    submissionAuthorized: false,
    runtime,
    runtimeSessionId,
  }))
}

function uncertainAdmission() {
  return vi.fn(async () => {
    throw new Error('admission response unavailable')
  })
}

function runOpenCode(runtime: OpenCodeRuntime, start: ReturnType<typeof vi.fn>) {
  const work = runtimeWork('opencode')
  return executeOpenCodeTurn(
    {
      connection: connection(start) as never,
      runtimes: { openCode: runtime, pi: null, codex: null },
      options: {},
    },
    work,
    new AbortController().signal,
    work.with ?? null,
    'hello',
    { kind: 'absent' },
    null,
    null,
    null,
    '/tmp/projA',
    runtimeBinding('opencode', null),
    [],
    [],
  )
}

function runPi(runtime: PiRuntime, runtimeSessionId: string, start: ReturnType<typeof vi.fn>) {
  const work = runtimeWork('pi')
  return executePiTurn(
    {
      connection: connection(start) as never,
      runtimes: { openCode: null, pi: runtime, codex: null },
      options: {},
    },
    work,
    new AbortController().signal,
    work.with ?? null,
    'hello',
    { kind: 'absent' },
    null,
    null,
    null,
    '/work',
    runtimeBinding('pi', runtimeSessionId),
    [],
  )
}

function runCodex(runtime: CodexRuntime, start: ReturnType<typeof vi.fn>) {
  const work = runtimeWork('codex')
  return executeCodexTurn(
    {
      connection: connection(start) as never,
      runtimes: { openCode: null, pi: null, codex: runtime },
      options: {},
    },
    work,
    new AbortController().signal,
    work.with ?? null,
    'hello',
    'gpt-5',
    null,
    null,
    '/work',
    runtimeBinding('codex', 'thread-existing'),
    [],
    [],
  )
}

function buildCodexRuntime() {
  const listeners = new Set<(message: unknown) => void>()
  const providerRequestEntered = deferred()
  const providerResponse = deferred<unknown>()
  const completionObserverReady = deferred()
  const turnStart = vi.fn(async (request: { readonly id: number }) => {
    providerRequestEntered.resolve()
    return await providerResponse.promise.then((result) => ({ jsonrpc: '2.0', id: request.id, result }))
  })
  const handle: CodexServerHandle = {
    codexHome: '/runner/.mohist/codex',
    async send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R> {
      if (request.method === 'initialize') {
        return {
          jsonrpc: '2.0',
          id: request.id,
          result: {
            protocolVersion: 'v2',
            codexHome: '/runner/.mohist/codex',
            userAgent: 'codex/0.153.0',
          },
        } as R
      }
      if (request.method === 'model/list') {
        return {
          jsonrpc: '2.0',
          id: request.id,
          result: { models: [{ id: 'gpt-5' }], complete: true },
        } as R
      }
      if (request.method === 'thread/resume') {
        return {
          jsonrpc: '2.0',
          id: request.id,
          result: { thread: { id: 'thread-existing', cwd: '/work' } },
        } as R
      }
      if (request.method === 'turn/start') return (await turnStart(request)) as R
      throw new Error(`Unexpected Codex method ${request.method}`)
    },
    notify: () => true,
    denyServerRequest: () => undefined,
    subscribe(listener) {
      listeners.add(listener)
      if (listeners.size === 2) completionObserverReady.resolve()
      return () => listeners.delete(listener)
    },
    async close() {
      listeners.clear()
    },
  }
  const readinessProbe: CodexReadinessProbe = {
    cli: {
      resolveCodexBinary: async () => '/usr/local/bin/codex',
      resolveCodexVersion: async () => '0.153.0',
    },
    authentication: { hasManagedAuthentication: async () => true },
    catalog: { loadCatalog: async () => null },
  }
  const runtime = new CodexRuntime({
    codexHome: handle.codexHome,
    cwd: '/work',
    serverFactory: async () => handle,
    readinessProbe,
  })
  return {
    runtime,
    turnStart,
    providerRequestEntered,
    providerResponse,
    completionObserverReady,
    completeTurn() {
      for (const listener of listeners) {
        listener({
          type: 'turn/completed',
          threadId: 'thread-existing',
          turnId: 'turn-provider-1',
          status: 'completed',
        })
      }
    },
  }
}

describe('OpenCode initial provider admission through production runtime', () => {
  it('holds fake SDK session.prompt until affirmative durable admission', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    expect(await runtime.start()).toMatchObject({ ok: true })
    const admission = heldAdmission('opencode', DEFAULT_SESSION_ID)

    const execution = runOpenCode(runtime, admission.start)
    await admission.entered.promise
    expect(client.sessionPrompt).not.toHaveBeenCalled()
    admission.release.resolve()
    await expect(execution).resolves.toMatchObject({ status: 'completed' })

    expect(admission.start).toHaveBeenCalledOnce()
    expect(client.sessionPrompt).toHaveBeenCalledOnce()
    await runtime.shutdown()
  })

  it('sends no fake SDK session.prompt when admission is rejected', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    expect(await runtime.start()).toMatchObject({ ok: true })
    const start = rejectedAdmission('opencode', DEFAULT_SESSION_ID)

    await expect(runOpenCode(runtime, start)).resolves.toMatchObject({ status: 'failed' })

    expect(start).toHaveBeenCalledOnce()
    expect(client.sessionPrompt).not.toHaveBeenCalled()
    await runtime.shutdown()
  })

  it('sends no fake SDK session.prompt after two uncertain admission attempts', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    expect(await runtime.start()).toMatchObject({ ok: true })
    const start = uncertainAdmission()

    await expect(runOpenCode(runtime, start)).resolves.toMatchObject({ status: 'failed' })

    expect(start).toHaveBeenCalledTimes(2)
    expect(client.sessionPrompt).not.toHaveBeenCalled()
    await runtime.shutdown()
  })
})

describe('Pi initial provider admission through production runtime', () => {
  it('holds fake SDK session.prompt until affirmative durable admission', async () => {
    const session = new ControlledPiSession()
    const runtime = new PiRuntime({ agentDir: '/agent', sdkFactory: controlledPiSdk(session) })
    expect(await runtime.start()).toMatchObject({ ok: true })
    const admission = heldAdmission('pi', session.sessionFile)

    const execution = runPi(runtime, session.sessionFile, admission.start)
    await admission.entered.promise
    expect(session.prompt).not.toHaveBeenCalled()
    admission.release.resolve()
    await session.promptEntered.promise
    expect(session.prompt).toHaveBeenCalledOnce()
    session.messages.push({ role: 'assistant', content: [{ type: 'text', text: 'done' }], stopReason: 'stop' })
    session.isStreaming = false
    session.emit({ type: 'agent_settled' })
    session.promptCompletion.resolve()
    await expect(execution).resolves.toMatchObject({ status: 'completed' })

    expect(admission.start).toHaveBeenCalledOnce()
    expect(session.prompt).toHaveBeenCalledOnce()
    await runtime.shutdown()
  })

  it('sends no fake SDK session.prompt when admission is rejected', async () => {
    const session = new ControlledPiSession()
    const runtime = new PiRuntime({ agentDir: '/agent', sdkFactory: controlledPiSdk(session) })
    expect(await runtime.start()).toMatchObject({ ok: true })
    const start = rejectedAdmission('pi', session.sessionFile)

    await expect(runPi(runtime, session.sessionFile, start)).resolves.toMatchObject({ status: 'failed' })

    expect(start).toHaveBeenCalledOnce()
    expect(session.prompt).not.toHaveBeenCalled()
    await runtime.shutdown()
  })

  it('sends no fake SDK session.prompt after two uncertain admission attempts', async () => {
    const session = new ControlledPiSession()
    const runtime = new PiRuntime({ agentDir: '/agent', sdkFactory: controlledPiSdk(session) })
    expect(await runtime.start()).toMatchObject({ ok: true })
    const start = uncertainAdmission()

    await expect(runPi(runtime, session.sessionFile, start)).resolves.toMatchObject({ status: 'failed' })

    expect(start).toHaveBeenCalledTimes(2)
    expect(session.prompt).not.toHaveBeenCalled()
    await runtime.shutdown()
  })
})

describe('Codex initial provider admission through production runtime', () => {
  it('holds fake app-server turn/start until affirmative durable admission', async () => {
    const fixture = buildCodexRuntime()
    expect(await fixture.runtime.start()).toMatchObject({ ok: true })
    const admission = heldAdmission('codex', 'thread-existing')

    const execution = runCodex(fixture.runtime, admission.start)
    await admission.entered.promise
    expect(fixture.turnStart).not.toHaveBeenCalled()
    admission.release.resolve()
    await fixture.providerRequestEntered.promise
    expect(fixture.turnStart).toHaveBeenCalledOnce()
    fixture.providerResponse.resolve({
      threadId: 'thread-existing',
      turnId: 'turn-provider-1',
      status: 'inProgress',
    })
    await fixture.completionObserverReady.promise
    fixture.completeTurn()
    await expect(execution).resolves.toMatchObject({ status: 'completed' })

    expect(admission.start).toHaveBeenCalledOnce()
    expect(fixture.turnStart).toHaveBeenCalledOnce()
    await fixture.runtime.shutdown()
  })

  it('sends no fake app-server turn/start when admission is rejected', async () => {
    const fixture = buildCodexRuntime()
    expect(await fixture.runtime.start()).toMatchObject({ ok: true })
    const start = rejectedAdmission('codex', 'thread-existing')

    await expect(runCodex(fixture.runtime, start)).resolves.toMatchObject({ status: 'failed' })

    expect(start).toHaveBeenCalledOnce()
    expect(fixture.turnStart).not.toHaveBeenCalled()
    await fixture.runtime.shutdown()
  })

  it('sends no fake app-server turn/start after two uncertain admission attempts', async () => {
    const fixture = buildCodexRuntime()
    expect(await fixture.runtime.start()).toMatchObject({ ok: true })
    const start = uncertainAdmission()

    await expect(runCodex(fixture.runtime, start)).resolves.toMatchObject({ status: 'failed' })

    expect(start).toHaveBeenCalledTimes(2)
    expect(fixture.turnStart).not.toHaveBeenCalled()
    await fixture.runtime.shutdown()
  })
})
