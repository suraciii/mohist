import { describe, expect, it as vitestIt, vi } from 'vitest'
import type { ActionHost } from '../src/actions/host.js'
import { normalizeWorkflowActionInput } from '../src/actions/input-compatibility.js'
import { createDefaultRegistry } from '../src/actions/registry.js'
import type { ActionResult, DispatchWorkItem, JsonObject } from '../src/core/types.js'
import { WorkExecutor } from '../src/runtime/executor.js'
import type { GitRunner } from '../src/runtime/git-probe.js'
import { verifyOnlyWorkspaceManager } from './support/workspace-mock.js'
import { defineTestAction, ActionRegistry } from './support/action-registry-test.js'
import { withTestRunnerResources } from './support/test-resources.js'

const nonGitRunner: GitRunner = async () => ({
  success: false,
  exitCode: 128,
  stdout: '',
  stderr: 'not a git repository',
  combinedOutput: 'not a git repository',
})

const it = (name: string, body: (workDir: string) => Promise<void> | void) =>
  vitestIt(name, () =>
    withTestRunnerResources(
      async () => {
        await body('/virtual/executor-replay-compatibility')
      },
      {
        gitRunner: nonGitRunner,
      },
    ),
  )

const scriptInputs = {
  run: { types: ['string'] as const, required: true as const },
  shell: { types: ['string'] as const },
  timeout: { types: ['number'] as const },
}

function createScriptRegistry(
  handler: (inputs: JsonObject, host: ActionHost) => Promise<ActionResult>,
): ActionRegistry {
  return new ActionRegistry([
    defineTestAction('core/script', handler, {
      inputs: scriptInputs,
      errors: [{ code: 'script-failed' }],
    }),
  ])
}

function createExecutor(
  workDir: string,
  handler: (inputs: JsonObject, host: ActionHost) => Promise<ActionResult>,
): WorkExecutor {
  return new WorkExecutor(
    createScriptRegistry(handler),
    verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
    { async patchRunVars() {} } as never,
    workDir,
  )
}

function historicalWork(workDir: string, overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: 'wf-replay-compatibility',
    workId: 'script.1',
    workType: 'task',
    stage: 'build',
    title: 'Run historical script',
    uses: 'core/script',
    with: {
      run: '${{ vars.run }}',
      shell: '${{ vars.shell }}',
      timeout: '${{ vars.timeout }}',
      resourceProfile: {
        limits: '${{ vars.retiredProfile }}',
        workingDirectory: '${{ vars.retiredWorkingDirectory }}',
      },
    },
    variables: {
      workspace: { path: workDir, branch: null },
      vars: { run: 'echo replayed', shell: 'bash', timeout: 125, marker: 'present' },
    },
    ...overrides,
  }
}

describe('historical core/script execution compatibility', () => {
  it('executes direct legacy and current Workflow dispatches without the retired field', async (workDir) => {
    for (const ownerKind of [undefined, 'workflow'] as const) {
      const captured: JsonObject[] = []
      const executor = createExecutor(workDir, async (inputs) => {
        captured.push(inputs)
        return { output: null }
      })
      const work = historicalWork(workDir, ownerKind === undefined ? {} : { ownerKind })
      const rawWith = structuredClone(work.with)

      const result = await executor.execute(work, new AbortController().signal)

      expect(result.status).toBe('completed')
      expect(captured).toEqual([{ run: 'echo replayed', shell: 'bash', timeout: 125 }])
      expect(work.with).toEqual(rawWith)
      expect(work.with).toHaveProperty('resourceProfile')
    }
  })

  it('ignores unresolved and invalid retired data before rendering and validation', async (workDir) => {
    const captured: JsonObject[] = []
    const executor = createExecutor(workDir, async (inputs) => {
      captured.push(inputs)
      return { output: null }
    })
    const work = historicalWork(workDir, {
      with: {
        run: '${{ vars.run }}',
        shell: '${{ vars.shell }}',
        timeout: '${{ vars.timeout }}',
        resourceProfile: {
          timeout: 'not-a-number',
          limit: '${{ vars.missingRetiredValue }}',
          nested: { shell: false },
        },
      },
    })

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(captured).toEqual([{ run: 'echo replayed', shell: 'bash', timeout: 125 }])
    expect(work.with).toHaveProperty('resourceProfile.limit', '${{ vars.missingRetiredValue }}')
  })

  it('keeps the compatibility gate scoped to Workflow core/script tasks', () => {
    const input: JsonObject = {
      resourceProfile: 'legacy',
      nested: { resourceProfile: 'preserve' },
    }
    const cases: Array<[string, Pick<DispatchWorkItem, 'workType' | 'uses' | 'ownerKind'>]> = [
      ['agent-job', { workType: 'task', uses: 'core/script', ownerKind: 'agent-job' }],
      ['checks', { workType: 'checks', uses: 'core/script', ownerKind: 'workflow' }],
      ['other Action', { workType: 'task', uses: 'test/other', ownerKind: 'workflow' }],
    ]

    for (const [label, work] of cases) {
      const normalized = normalizeWorkflowActionInput(work, input)
      expect(normalized, label).toEqual(input)
      expect(normalized, label).not.toBe(input)
    }

    expect(normalizeWorkflowActionInput({ workType: 'task', uses: 'core/script' }, input)).toEqual({
      nested: { resourceProfile: 'preserve' },
    })
    expect(input).toEqual({
      resourceProfile: 'legacy',
      nested: { resourceProfile: 'preserve' },
    })
  })

  it('rejects unrelated unknown inputs without invoking the Action', async (workDir) => {
    const invoked = vi.fn(async () => ({ output: null }))
    const executor = createExecutor(workDir, invoked)
    const result = await executor.execute(
      historicalWork(workDir, {
        with: {
          run: 'echo replayed',
          resourceProfile: '${{ vars.missingRetiredValue }}',
          otherUnknown: 'reject me',
        },
      }),
      new AbortController().signal,
    )

    expect(result).toMatchObject({
      status: 'failed',
      error: { code: 'invalid-input', message: "Action 'core/script' received unknown input 'otherUnknown'" },
    })
    expect(invoked).not.toHaveBeenCalled()
  })

  for (const [field, value] of [
    ['run', 123],
    ['shell', false],
    ['timeout', 'too slow'],
  ] as const) {
    it(`continues rejecting invalid ${field} values`, async (workDir) => {
      const invoked = vi.fn(async () => ({ output: null }))
      const executor = createExecutor(workDir, invoked)
      const withInput: JsonObject = {
        run: 'echo replayed',
        resourceProfile: '${{ vars.missingRetiredValue }}',
        [field]: value,
      }
      const result = await executor.execute(historicalWork(workDir, { with: withInput }), new AbortController().signal)

      expect(result.status).toBe('failed')
      expect(result.error?.code).toBe('invalid-input')
      expect(result.message).toContain(`input '${field}'`)
      expect(invoked).not.toHaveBeenCalled()
    })
  }

  it('keeps the current core/script manifest and catalog strict', () => {
    const registry = createDefaultRegistry()
    const resolved = registry.resolve('core/script')
    if (resolved.kind !== 'definition') throw new Error('Missing core/script')

    expect(resolved.definition.manifest.inputs.run).toMatchObject({ types: ['string'], required: true })
    expect(resolved.definition.manifest.inputs.shell).toMatchObject({ types: ['string'] })
    expect(resolved.definition.manifest.inputs.timeout).toMatchObject({ types: ['number'] })
    expect(resolved.definition.manifest.inputs.resourceProfile).toBeUndefined()

    const catalogEntry = registry.catalog().actions.find((action) => action.name === 'core/script')
    expect(catalogEntry?.inputs.map((input) => input.name)).toEqual(['run', 'shell', 'timeout'])
  })

  it('replays a retrySelf continuation with raw declarations and an exact budget decrement', async (workDir) => {
    const captured: JsonObject[] = []
    const handler = vi.fn(async (inputs: JsonObject) => {
      captured.push(structuredClone(inputs))
      return captured.length === 1
        ? { error: { code: 'script-failed', message: 'historical script failed' } }
        : { output: { value: 'replayed' } }
    })
    const executor = createExecutor(workDir, handler)
    const recovery = {
      budget: 2,
      handlers: [{ when: 'error.code=script-failed', tasks: [], retrySelf: true }],
    }
    const rawWith = {
      run: '${{ vars.run }}',
      shell: '${{ vars.shell }}',
      timeout: '${{ vars.timeout }}',
      resourceProfile: { limit: '${{ vars.missingRetiredValue }}' },
    }
    const original = historicalWork(workDir, {
      with: rawWith,
      workId: 'script.2',
      taskRunId: 'task-run-2',
      artifacts: { files: [{ path: 'report.txt' }] },
      setVars: { result: 'value' },
      expect: { completion: '${{ vars.marker }}' },
      recovery,
      recoveryRemaining: 1,
    })

    const firstResult = await executor.execute(original, new AbortController().signal)
    const continuation = firstResult.addTasks?.find((task) => task.id === 'script')

    expect(firstResult.status).toBe('completed')
    expect(continuation).toEqual({
      id: 'script',
      title: 'Run historical script',
      uses: 'core/script',
      with: rawWith,
      artifacts: { files: [{ path: 'report.txt' }] },
      setVars: { result: 'value' },
      recovery,
      recoveryRemaining: 0,
      expect: { completion: '${{ vars.marker }}' },
    })
    expect(continuation?.with).not.toBe(original.with)
    expect(continuation?.recovery).not.toBe(original.recovery)
    expect(original.with).toEqual(rawWith)

    const retryWork: DispatchWorkItem = {
      ...original,
      workId: continuation!.id,
      title: continuation!.title,
      uses: continuation!.uses,
      with: continuation!.with,
      artifacts: continuation!.artifacts,
      setVars: continuation!.setVars,
      recovery: continuation!.recovery,
      recoveryRemaining: continuation!.recoveryRemaining,
      expect: continuation!.expect,
    }
    const retryResult = await executor.execute(retryWork, new AbortController().signal)

    expect(retryResult.status).toBe('completed')
    expect(captured).toEqual([
      { run: 'echo replayed', shell: 'bash', timeout: 125 },
      { run: 'echo replayed', shell: 'bash', timeout: 125 },
    ])
    expect(original.with).toEqual(rawWith)
    expect(retryWork.with).toEqual(rawWith)
  })
})
