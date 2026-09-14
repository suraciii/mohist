import { describe, expect, it as vitestIt } from 'vitest'
import { projectTurnToWorkItemResult } from '../src/runtime/agent-job-executor.js'
import type { RuntimeResult, RuntimeTurnFacts, RuntimeTurnResult } from '../src/runtime/opencode/index.js'
import { withDefaultRunnerTestResources } from './support/test-resources.js'

function it(name: string, body: () => Promise<void> | void): void {
  vitestIt(name, async () => {
    await withDefaultRunnerTestResources(async () => await body())
  })
}

describe('AgentJobExecutor work-result projection', () => {
  it('maps a successful RuntimeResult to a completed work result', () => {
    const result: RuntimeResult<RuntimeTurnResult> = {
      ok: true,
      value: {
        facts: {
          finalAssistantText: 'yes',
          runtimeSessionId: 'ses_a',
          workDir: '/tmp/w',
        } satisfies RuntimeTurnFacts,
        diagnostics: [],
      },
      diagnostics: [],
    }
    const workResult = projectTurnToWorkItemResult(result, 'opencode', 'openai/gpt-5.5', 'high')
    expect(workResult.status).toBe('completed')
    expect(workResult.exitCode).toBe(0)
    expect(workResult.error).toBeUndefined()
    const parsed = workResult.output as Record<string, unknown>
    expect(parsed.status).toBe('success')
    expect(parsed.runtimeSessionId).toBe('ses_a')
    expect(parsed.text).toBe('yes')
  })

  it('maps a failed RuntimeResult to a failed work result with the runtime error', () => {
    const result: RuntimeResult<RuntimeTurnResult> = {
      ok: false,
      error: { kind: 'turn-failed', message: 'boom', diagnostics: [] },
      diagnostics: [],
    }
    const workResult = projectTurnToWorkItemResult(result, 'opencode', null, null)
    expect(workResult.status).toBe('failed')
    expect(workResult.error).toEqual({ code: 'turn-failed', message: 'boom' })
    expect(workResult.exitCode).toBe(1)
    const parsed = workResult.output as Record<string, unknown>
    expect(parsed.status).toBe('failure')
    expect(parsed.error).toBe('boom')
  })

  it("preserves OpenCode's explicit unsupported-effort category", () => {
    const result: RuntimeResult<RuntimeTurnResult> = {
      ok: false,
      error: {
        kind: 'unsupported-execution-configuration',
        message: 'OpenCode does not support a reasoning effort',
        diagnostics: [{ severity: 'error', code: 'unsupported_execution_configuration', message: 'unsupported' }],
      },
      diagnostics: [{ severity: 'error', code: 'unsupported_execution_configuration', message: 'unsupported' }],
    }
    const workResult = projectTurnToWorkItemResult(result, 'opencode', 'openai/gpt-5', 'high')

    expect(workResult.status).toBe('failed')
    expect(workResult.error).toEqual({
      code: 'unsupported-execution-configuration',
      message: 'OpenCode does not support a reasoning effort',
    })
    expect((workResult.output as Record<string, unknown>).error).toBe('OpenCode does not support a reasoning effort')
  })

  it('promotes provider quota diagnostics to the non-recoverable result code', () => {
    const result: RuntimeResult<RuntimeTurnResult> = {
      ok: false,
      error: {
        kind: 'turn-failed',
        message: 'quota exhausted',
        diagnostics: [{ severity: 'error', code: 'provider-quota-exhausted', message: 'quota exhausted' }],
      },
      diagnostics: [],
    }

    const workResult = projectTurnToWorkItemResult(result, 'opencode', null, null)

    expect(workResult.error).toEqual({ code: 'provider-quota-exhausted', message: 'quota exhausted' })
  })
})
