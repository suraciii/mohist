import { describe, expect, it, vi } from 'vitest'
import { buildActionHost } from '../src/runtime/executor-capabilities.js'
import type { ActionHost } from '../src/actions/host.js'
import type { AgentExecutionDefinition, DispatchWorkItem } from '../src/core/types.js'
import type { ServerConnection } from '../src/server/connection.js'
import type { SkillResolver } from '../src/runtime/skill-resolver.js'
import type { PiRuntime } from '../src/runtime/pi/index.js'
import type { OpenCodeRuntime } from '../src/runtime/opencode/index.js'

/**
 * Issue-557 T-002: the workflow-task launch path freezes the canonical
 * reasoning effort onto the dispatch — either via the `AgentDefinition`
 * snapshot (`AgentExecutionDefinition.reasoningEffort`) or the rendered
 * `vars.agent` options — and the runner's agent-turn capability host
 * forwards it into the runtime turn request beside model and variant.
 * These specs exercise the production `buildActionHost` turn path
 * directly (the `callAction` test support mirrors it for Action-level
 * tests only).
 */

function makeWork(overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: 'wf-effort-1',
    workId: 'work-effort-1',
    workType: 'task',
    stage: 'build',
    title: 'Turn with effort',
    uses: 'mohist/opencode',
    with: { prompt: 'do the work' },
    variables: {},
    workDir: '/tmp/work',
    projectId: null,
    ...overrides,
  } as DispatchWorkItem
}

function makeDeps(openCodeRuntime: unknown): Parameters<typeof buildActionHost>[0] {
  const skillResolver = {
    resolve: vi.fn(async () => ({ ok: true as const, skills: [] as { name: string; instructions: string }[] })),
  } as unknown as SkillResolver
  return {
    connection: { runnerId: 'runner-effort' } as unknown as ServerConnection,
    skillResolver,
    piRuntime: null as unknown as PiRuntime,
    openCodeRuntime: openCodeRuntime as OpenCodeRuntime,
    agentSessionRuntimeEventOutbox: null,
    runtimeEventRecordId: () => `rec-${Math.random()}`,
    bindingRecoveryCoordinator: null,
  }
}

function fakeOpenCodeRuntime() {
  const runTurn = vi.fn(async () => ({
    ok: true as const,
    value: {
      facts: { runtimeSessionId: 'ses_effort', finalAssistantText: 'done' },
      diagnostics: [],
    },
    diagnostics: [],
  }))
  return {
    runtime: {
      ready: () => true,
      diagnostic: () => null,
      createSession: vi.fn(async () => ({
        ok: true as const,
        value: { runtimeSessionId: 'ses_effort', workDir: '/tmp/work' },
        diagnostics: [],
      })),
      runTurn,
    } as unknown as OpenCodeRuntime,
    runTurn,
  }
}

function hostFor(work: DispatchWorkItem, runtime: unknown): ActionHost {
  return buildActionHost(
    makeDeps(runtime),
    work,
    '/tmp/work',
    new AbortController().signal,
    { debug: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn() } as never,
    new Set(['agent-turn'] as const),
  )
}

describe('buildActionHost agent-turn capability forwards the frozen effort', () => {
  it('forwards the Agent-definition effort into the runtime turn options', async () => {
    const { runtime, runTurn } = fakeOpenCodeRuntime()
    const definition: AgentExecutionDefinition = {
      instructions: 'be terse',
      runtime: 'opencode',
      model: 'openai/gpt-5.5',
      variant: 'balanced',
      reasoningEffort: 'high',
      skills: [],
    }
    const host = hostFor(makeWork({ agentDefinition: definition }), runtime)

    const result = await host.agent!.turn({ prompt: 'do', options: { reasoningEffort: 'low' } })

    expect(result.error).toBeUndefined()
    expect(runTurn).toHaveBeenCalledTimes(1)
    const request = (runTurn.mock.calls[0] as unknown as [unknown] | undefined)?.[0] as {
      options?: { model?: unknown; variant?: string | null; reasoningEffort?: string | null }
    }
    // The frozen definition wins over the caller option, exactly like variant.
    expect(request.options?.model).toEqual({ providerID: 'openai', modelID: 'gpt-5.5' })
    expect(request.options?.variant).toBe('balanced')
    expect(request.options?.reasoningEffort).toBe('high')
  })

  it('uses the rendered vars.agent effort option when no definition is frozen', async () => {
    const { runtime, runTurn } = fakeOpenCodeRuntime()
    const host = hostFor(makeWork(), runtime)

    const result = await host.agent!.turn({ prompt: 'do', options: { reasoningEffort: 'low' } })

    expect(result.error).toBeUndefined()
    const request = (runTurn.mock.calls[0] as unknown as [unknown] | undefined)?.[0] as {
      options?: { reasoningEffort?: string | null }
    }
    expect(request.options?.reasoningEffort).toBe('low')
  })

  it('passes a null effort when none was frozen or rendered', async () => {
    const { runtime, runTurn } = fakeOpenCodeRuntime()
    const host = hostFor(makeWork(), runtime)

    const result = await host.agent!.turn({ prompt: 'do' })

    expect(result.error).toBeUndefined()
    const request = (runTurn.mock.calls[0] as unknown as [unknown] | undefined)?.[0] as {
      options?: { reasoningEffort?: string | null }
    }
    // Absent effort is unset — never synthesized into a default.
    expect(request.options?.reasoningEffort).toBeNull()
  })
})
