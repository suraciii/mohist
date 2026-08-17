import { describe, expect, it as vitestIt } from 'vitest'
import type { JsonObject, DispatchWorkItem } from '../src/core/types.js'
import type { ActionHost } from '../src/actions/host.js'
import { WorkExecutor } from '../src/runtime/executor.js'
import type { GitRunner } from '../src/runtime/git-probe.js'
import { verifyOnlyWorkspaceManager } from './support/workspace-mock.js'
import { defineTestAction, ActionRegistry } from './support/action-registry-test.js'
import { withTestRunnerResources } from './support/test-resources.js'

const nonGitRunner: GitRunner = async () => ({
  success: false,
  stdout: '',
  stderr: 'not a git repository',
  exitCode: 128,
  combinedOutput: 'not a git repository',
})

// Clean workspace on `main`: answers every probe the executor's
// branch-stability boundary and worktree-enforcement issue when an
// expected workspace branch is defined.
const cleanMainRunner: GitRunner = async (workDir, args) => {
  const command = args.join(' ')
  switch (command) {
    case 'rev-parse --git-path rebase-merge':
    case 'rev-parse --git-path rebase-apply':
    case 'rev-parse --git-path MERGE_HEAD':
    case 'rev-parse --git-path CHERRY_PICK_HEAD':
      return { success: true, stdout: `${workDir}/.git/${args[2]}\n`, stderr: '', exitCode: 0, combinedOutput: '' }
    case 'rev-parse HEAD':
      return { success: true, stdout: 'main-head-sha\n', stderr: '', exitCode: 0, combinedOutput: '' }
    case 'rev-parse --abbrev-ref HEAD':
      return { success: true, stdout: 'main\n', stderr: '', exitCode: 0, combinedOutput: '' }
    case 'status --porcelain':
      return { success: true, stdout: '', stderr: '', exitCode: 0, combinedOutput: '' }
    case 'rev-parse --is-inside-work-tree':
      return { success: true, stdout: 'true\n', stderr: '', exitCode: 0, combinedOutput: '' }
    case 'diff --cached --name-only':
    case 'diff --name-only':
    case 'ls-files --others --exclude-standard':
      return { success: true, stdout: '', stderr: '', exitCode: 0, combinedOutput: '' }
    default:
      throw new Error(`unexpected executor git call: ${command}`)
  }
}

const withExecutorResources = <T>(body: (workDir: string) => Promise<T>) =>
  withTestRunnerResources(async () => await body('/virtual/executor-raw-with'), { gitRunner: nonGitRunner })

const it = Object.assign(
  (name: string, body: (workDir: string) => unknown) =>
    vitestIt(name, () => withExecutorResources(async (workDir) => await body(workDir))),
  { each: vitestIt.each.bind(vitestIt) },
)

describe('WorkExecutor action input boundary', () => {
  it('exposes only recursively-rendered input to a custom Action', async (workDir) => {
    let capturedInputs: JsonObject | null = null
    let capturedHost: ActionHost | null = null

    const registry = new ActionRegistry([
      defineTestAction(
        'test/capture-inputs',
        async (inputs, host) => {
          capturedInputs = inputs
          capturedHost = host
          return { output: null }
        },
        {
          inputs: {
            task: { types: ['object'] },
          },
        },
      ),
    ])

    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
      {} as never,
      workDir,
    )

    const agentObject = { type: 'opencode', model: 'openai/gpt-5.4' }
    const placeholder = '${{ vars.agent }}'

    const workItem: DispatchWorkItem = {
      workflowRunId: 'wf-raw-with',
      workId: 'work-raw-with',
      workType: 'task',
      stage: 'build',
      title: 'Test action input boundary',
      uses: 'test/capture-inputs',
      with: { task: { with: { options: placeholder } } },
      variables: {
        workspace: { path: workDir, branch: null },
        vars: { agent: agentObject },
      },
    }

    const result = await executor.execute(workItem, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(capturedInputs).not.toBeNull()
    expect(capturedHost).not.toBeNull()

    const renderedTask = capturedInputs!.task as JsonObject
    expect((renderedTask as JsonObject).with).toEqual({ options: agentObject })
    expect(capturedInputs).not.toHaveProperty('variables')
    expect(capturedInputs).not.toHaveProperty('rawWith')
    expect(capturedInputs).not.toHaveProperty('rawTask')
    expect(capturedHost!.workDir).toBe(workDir)
  })

  it('receives inputs and host without exposing internal data', async (workDir) => {
    let capturedInputs: JsonObject | null = null
    let capturedHost: ActionHost | null = null
    const registry = new ActionRegistry([
      defineTestAction(
        'test/capture-inputs-boundary',
        async (inputs, host) => {
          capturedInputs = inputs
          capturedHost = host
          return { output: null }
        },
        {
          inputs: {
            prompt: { types: ['string'] },
          },
        },
      ),
    ])
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
      {} as never,
      workDir,
    )
    const workItem: DispatchWorkItem = {
      workflowRunId: 'wf-parent-context',
      workId: 'work-parent-context',
      workType: 'task',
      stage: 'plan',
      uses: 'test/capture-inputs-boundary',
      with: { prompt: 'child prompt' },
      variables: { workspace: { path: workDir, branch: null } },
      parentIssueContext: { title: 'Parent', body: 'Parent body' },
    }

    const result = await executor.execute(workItem, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(capturedInputs).toEqual({ prompt: 'child prompt' })
    expect(capturedHost!.workDir).toBe(workDir)
    expect(capturedInputs).not.toHaveProperty('variables')
    expect(capturedHost).not.toHaveProperty('variables')
  })

  // This test resolves `${{ workspace.branch }}` to a real non-null
  // branch, so the executor's branch-stability boundary probes the
  // workspace. Provide a clean `main` git state instead of the shared
  // non-git runner so the boundary invariant holds.
  vitestIt('assembles only namespaced dispatch roots and resolved workspace fields', async () => {
    const workDir = '/virtual/executor-raw-with'
    await withTestRunnerResources(async () => {
      let capturedInputs: JsonObject | null = null
      const registry = new ActionRegistry([
        defineTestAction(
          'test/context-roots',
          async (inputs) => {
            capturedInputs = inputs
            return { output: null }
          },
          {
            inputs: { context: { types: ['object'] } },
          },
        ),
      ])
      const executor = new WorkExecutor(
        registry,
        verifyOnlyWorkspaceManager({ path: workDir, branch: 'main' }),
        {} as never,
        workDir,
      )

      const result = await executor.execute(
        {
          workflowRunId: 'wf-context-roots',
          workId: 'work-context-roots',
          workType: 'task',
          uses: 'test/context-roots',
          with: {
            context: { value: '${{ vars.foo }}', path: '${{ workspace.path }}', branch: '${{ workspace.branch }}' },
          },
          variables: {
            foo: 'bare',
            runner: { os: 'fake' },
            failure: { output: 'not available' },
            workspace: { path: '/dispatch/path', branch: 'dispatch-branch' },
            vars: { foo: 'namespaced' },
          },
        },
        new AbortController().signal,
      )

      expect(result.status).toBe('completed')
      expect(capturedInputs).toEqual({ context: { value: 'namespaced', path: workDir, branch: 'main' } })

      const unavailable = await executor.execute(
        {
          workflowRunId: 'wf-context-roots',
          workId: 'work-context-roots-fail',
          workType: 'task',
          uses: 'test/context-roots',
          with: { context: { value: '${{ foo }}' } },
          variables: { foo: 'bare', vars: { foo: 'namespaced' }, workspace: { path: workDir } },
        },
        new AbortController().signal,
      )
      expect(unavailable.status).toBe('failed')
      expect(unavailable.message).toContain('${{ foo }}')
    }, { gitRunner: cleanMainRunner })
  })

  it('derives optional engine-sourced inputs from the dispatch snapshot', async (workDir) => {
    const capturedInputs: JsonObject[] = []
    const registry = new ActionRegistry([
      defineTestAction(
        'test/engine-input',
        async (inputs) => {
          capturedInputs.push(inputs)
          return { output: null }
        },
        {
          inputs: {
            buildPrompt: { types: ['string'], engineSource: 'prompts.build' },
            archiveHint: { types: ['string'], engineSource: 'vars.archive' },
          },
        },
      ),
    ])

    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
      {} as never,
      workDir,
    )

    const result = await executor.execute(
      {
        workflowRunId: 'wf-engine-input',
        workId: 'work-engine-input',
        workType: 'task',
        title: 'Engine input',
        uses: 'test/engine-input',
        with: { archiveHint: 'profile-provided value' },
        variables: {
          workspace: { path: workDir, branch: null },
          prompts: { build: 'build instructions' },
          vars: {},
        },
      },
      new AbortController().signal,
    )

    expect(result.status).toBe('completed')

    const replay = await executor.execute(
      {
        workflowRunId: 'wf-engine-input-replay',
        workId: 'work-engine-input-replay',
        workType: 'task',
        title: 'Engine input replay',
        uses: 'test/engine-input',
        with: { archiveHint: 'profile-provided value' },
        variables: {
          workspace: { path: workDir, branch: null },
          prompts: { build: 'build instructions' },
          vars: { archive: 'openspec/changes/archive/2026-08-14-issue-589' },
        },
      },
      new AbortController().signal,
    )

    expect(replay.status).toBe('completed')
    expect(capturedInputs).toEqual([
      { buildPrompt: 'build instructions' },
      {
        buildPrompt: 'build instructions',
        archiveHint: 'openspec/changes/archive/2026-08-14-issue-589',
      },
    ])
  })
})

describe('Dispatch rendering boundary', () => {
  it('renders immediate nested templates against the carried snapshot', async (workDir) => {
    let capturedInputs: JsonObject | null = null
    const registry = new ActionRegistry([
      defineTestAction(
        'test/render-snapshot',
        async (inputs) => {
          capturedInputs = inputs
          return { output: null }
        },
        {
          inputs: {
            prompt: { types: ['string'] },
            options: { types: ['object'] },
          },
        },
      ),
    ])
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
      {} as never,
      workDir,
    )
    const rawWith = {
      prompt: '${{ vars.message }}',
      options: { mode: '${{ vars.mode }}', retries: '${{ vars.retries }}' },
    }
    const workItem: DispatchWorkItem = {
      workflowRunId: 'wf-render',
      workId: 'work-render',
      workType: 'task',
      stage: 'plan',
      uses: 'test/render-snapshot',
      with: rawWith,
      variables: {
        workspace: { path: workDir, branch: null, changeDir: null },
        vars: { message: 'do work', mode: 'fast', retries: 2 },
      },
    }
    const result = await executor.execute(workItem, new AbortController().signal)
    expect(result.status).toBe('completed')
    expect(capturedInputs).toEqual({
      prompt: 'do work',
      options: { mode: 'fast', retries: 2 },
    })
    expect(workItem.with).toBe(rawWith)
    expect(workItem.with).toEqual({
      prompt: '${{ vars.message }}',
      options: { mode: '${{ vars.mode }}', retries: '${{ vars.retries }}' },
    })
  })

  it.each([
    ['object', { model: 'model-a', variant: 'high' }],
    ['array', [1, 2, 3]],
    ['number', 42],
    ['boolean', true],
  ])('preserves whole-value JSON type for a %s reference', async (_label, resolved) => {
    return await withExecutorResources(async (workDir) => {
      let capturedInputs: JsonObject | null = null
      const registry = new ActionRegistry([
        defineTestAction(
          'test/json-types',
          async (inputs) => {
            capturedInputs = inputs
            return { output: null }
          },
          {
            inputs: {
              agent: { types: ['string', 'number', 'boolean', 'object', 'array'] },
            },
          },
        ),
      ])
      const executor = new WorkExecutor(
        registry,
        verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
        {} as never,
        workDir,
      )
      const workItem: DispatchWorkItem = {
        workflowRunId: 'wf-json-types',
        workId: 'work-json-types',
        workType: 'task',
        stage: 'plan',
        uses: 'test/json-types',
        with: { agent: '${{ vars.value }}' },
        variables: {
          workspace: { path: workDir, branch: null, changeDir: null },
          vars: { value: resolved },
        },
      }
      const result = await executor.execute(workItem, new AbortController().signal)
      expect(result.status).toBe('completed')
      expect(capturedInputs).toEqual({ agent: resolved })
    })
  })

  it('fails an immediate whole-value reference without invoking the Action', async (workDir) => {
    let actionInvoked = false
    const registry = new ActionRegistry([
      defineTestAction(
        'test/missing-ref',
        async () => {
          actionInvoked = true
          return { output: null }
        },
        {
          inputs: { agent: { types: ['object'] } },
        },
      ),
    ])
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
      {} as never,
      workDir,
    )
    const workItem: DispatchWorkItem = {
      workflowRunId: 'wf-unresolved',
      workId: 'work-unresolved',
      workType: 'task',
      stage: 'plan',
      uses: 'test/missing-ref',
      with: { agent: '${{ vars.missing }}' },
      variables: {
        workspace: { path: workDir, branch: null, changeDir: null },
        vars: {},
      },
    }
    const result = await executor.execute(workItem, new AbortController().signal)
    expect(result.status).toBe('failed')
    expect(actionInvoked).toBe(false)
    expect(result.message).toContain('vars.missing')
  })

  it('keeps nested templates inside a deferred field unchanged for the Action', async (workDir) => {
    let capturedInputs: JsonObject | null = null
    const registry = new ActionRegistry([
      defineTestAction(
        'test/deferred-tasks',
        async (inputs) => {
          capturedInputs = inputs
          return { output: null }
        },
        {
          inputs: {
            tasks: { types: ['array'], render: 'deferred' },
          },
        },
      ),
    ])
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
      {} as never,
      workDir,
    )
    const deferredTasks: JsonObject = {
      items: [
        { id: 'child-1', uses: 'test/echo', with: { agent: '${{ vars.agent }}' } },
        { id: 'child-2', uses: 'test/echo', with: { message: 'literal' } },
      ],
    }
    const workItem: DispatchWorkItem = {
      workflowRunId: 'wf-deferred',
      workId: 'work-deferred',
      workType: 'task',
      stage: 'plan',
      uses: 'test/deferred-tasks',
      with: { tasks: deferredTasks.items as unknown as JsonObject },
      variables: {
        workspace: { path: workDir, branch: null, changeDir: null },
        vars: { agent: { model: 'model-a' } },
      },
    }
    const result = await executor.execute(workItem, new AbortController().signal)
    expect(result.status).toBe('completed')
    expect(capturedInputs).toEqual({ tasks: deferredTasks.items })
    expect((capturedInputs!.tasks as JsonObject[])[0]).toEqual({
      id: 'child-1',
      uses: 'test/echo',
      with: { agent: '${{ vars.agent }}' },
    })
  })

  it('Action mutation of a deferred reference cannot mutate DispatchWorkItem.with', async (workDir) => {
    const originalDeferred: JsonObject = { id: 'child', with: { agent: { name: '${{ vars.agent }}' } } }
    const observed: { mutated: JsonObject | null; sourceDeferred: unknown } = { mutated: null, sourceDeferred: null }

    const registry = new ActionRegistry([
      defineTestAction(
        'test/deferred-mutation',
        async (inputs) => {
          const tasks = inputs.tasks as JsonObject[]
          const first = tasks[0]! as JsonObject
          const innerWith = first.with as JsonObject
          ;(innerWith.agent as JsonObject)['name'] = 'MUTATED'
          observed.mutated = JSON.parse(JSON.stringify(inputs))
          return { output: null }
        },
        {
          inputs: { tasks: { types: ['array'], render: 'deferred' } },
        },
      ),
    ])
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
      {} as never,
      workDir,
    )
    const workItem: DispatchWorkItem = {
      workflowRunId: 'wf-mutation',
      workId: 'work-mutation',
      workType: 'task',
      stage: 'plan',
      uses: 'test/deferred-mutation',
      with: { tasks: [originalDeferred] },
      variables: {
        workspace: { path: workDir, branch: null, changeDir: null },
        vars: { agent: 'model-a' },
      },
    }
    const result = await executor.execute(workItem, new AbortController().signal)
    if (result.status !== 'completed') {
      throw new Error(`expected completed, got ${result.status}: ${result.message ?? ''}`)
    }

    const sourceTask = (workItem.with!.tasks as JsonObject[])[0]! as JsonObject
    expect((sourceTask.with as JsonObject).agent).toEqual({ name: '${{ vars.agent }}' })
    observed.sourceDeferred = JSON.parse(JSON.stringify(workItem.with!.tasks))

    const capturedTask = (observed.mutated!.tasks as JsonObject[])[0]! as JsonObject
    expect((capturedTask.with as JsonObject).agent).toEqual({ name: 'MUTATED' })
    expect(observed.sourceDeferred).toEqual([{ id: 'child', with: { agent: { name: '${{ vars.agent }}' } } }])
  })
})
