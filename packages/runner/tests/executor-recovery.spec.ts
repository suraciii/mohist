import { describe, expect, it } from 'vitest'
import type { DispatchWorkItem } from '../src/core/types.js'
import { tryRecovery } from '../src/runtime/recovery.js'

function work(recovery: DispatchWorkItem['recovery']): DispatchWorkItem {
  return {
    workflowRunId: 'wf-recovery',
    workId: 'integrate:rebase.2',
    workType: 'task',
    stage: 'integrate',
    title: 'Rebase branch',
    uses: 'mohist/rebase',
    with: { baseBranch: 'master' },
    recovery,
    recoveryRemaining: 1,
  }
}

describe('recovery action error protocol', () => {
  it('recovers ordinary script failures through the configured handler', () => {
    const result = tryRecovery(
      work({
        budget: 2,
        handlers: [{ tasks: [{ id: 'fix-ci' }], retrySelf: true }],
      }),
      {
        status: 'failed',
        error: { code: 'script-failed', message: 'Script exited with code 1' },
      },
    )

    expect(result).toMatchObject({ status: 'completed' })
  })

  it('matches an Action error by error.code and preserves the error', () => {
    const result = tryRecovery(
      work({
        budget: 1,
        handlers: [
          {
            when: 'error.code=conflict',
            tasks: [{ id: 'resolve', title: 'Resolve', with: { message: '${{ failure.error.message }}' } }],
            retrySelf: true,
          },
        ],
      }),
      {
        status: 'failed',
        artifactUploadIds: ['artup_1'],
        error: { code: 'conflict', message: 'Rebase stopped on a conflict.' },
      },
    )

    expect(result).toMatchObject({
      status: 'completed',
      artifactUploadIds: ['artup_1'],
      error: { code: 'conflict', message: 'Rebase stopped on a conflict.' },
      addTasks: [
        { id: 'resolve', with: { message: 'Rebase stopped on a conflict.' } },
        { id: 'integrate:rebase', recoveryRemaining: 0 },
      ],
    })
  })

  it('copies the raw self-retry declaration and continuation metadata', () => {
    const recovery = {
      budget: 2,
      handlers: [{ when: 'error.code=conflict', tasks: [], retrySelf: true }],
    }
    const original = work(recovery)
    original.with = { options: '${{ vars.agent }}', nested: { value: '${{ vars.mode }}' } }
    original.expect = { markers: [{ path: 'result', failIf: '${{ vars.marker }}' }] }
    original.artifacts = { report: { path: 'report.json' } }
    original.setVars = { result: '${{ failure.error.code }}' }
    original.recovery = recovery

    const result = tryRecovery(original, {
      status: 'failed',
      error: { code: 'conflict', message: 'conflict' },
    })
    const retry = result?.addTasks?.[0]

    expect(retry).toEqual({
      id: 'integrate:rebase',
      title: 'Rebase branch',
      uses: 'mohist/rebase',
      with: { options: '${{ vars.agent }}', nested: { value: '${{ vars.mode }}' } },
      expect: { markers: [{ path: 'result', failIf: '${{ vars.marker }}' }] },
      artifacts: { report: { path: 'report.json' } },
      setVars: { result: '${{ failure.error.code }}' },
      recovery,
      recoveryRemaining: 0,
    })
    expect(retry?.with).not.toBe(original.with)
    expect(retry?.expect).not.toBe(original.expect)
    expect(retry?.artifacts).not.toBe(original.artifacts)
    expect(retry?.setVars).not.toBe(original.setVars)
    expect(retry?.recovery).not.toBe(original.recovery)
  })

  it('expands only triggering failure references in handler declarations', () => {
    const result = tryRecovery(
      work({
        budget: 1,
        handlers: [
          {
            when: 'error.code=conflict',
            tasks: [
              {
                id: 'recover:fix',
                with: {
                  changeId: '${{ failure.output.changeId }}',
                  options: '${{ vars.agent }}',
                },
                expect: { marker: '${{ vars.marker }}' },
              },
            ],
            retrySelf: false,
          },
        ],
      }),
      {
        status: 'failed',
        output: { changeId: 'change-42' },
        error: { code: 'conflict', message: 'conflict' },
      },
    )

    expect(result?.addTasks?.[0]).toEqual({
      id: 'recover:fix',
      title: 'recover:fix',
      uses: null,
      with: { changeId: 'change-42', options: '${{ vars.agent }}' },
      expect: { marker: '${{ vars.marker }}' },
      artifacts: null,
      setVars: null,
      recovery: null,
    })
  })

  it('uses a default handler only for failures', () => {
    const recovery = { budget: 1, handlers: [{ tasks: [{ id: 'fix', title: 'Fix' }], retrySelf: false }] }
    expect(
      tryRecovery(work(recovery), { status: 'failed', error: { code: 'timeout', message: 'Timed out' } }),
    ).toMatchObject({ status: 'completed', addTasks: [{ id: 'fix' }] })
    expect(tryRecovery(work(recovery), { status: 'completed', output: { promise: 'PASS' } })).toBeNull()
  })

  it('matches successful completion output with output.promise', () => {
    const result = tryRecovery(
      work({
        budget: 1,
        handlers: [{ when: 'output.promise=FAIL', tasks: [{ id: 'fix', title: 'Fix' }], retrySelf: false }],
      }),
      { status: 'completed', output: { promise: 'FAIL' } },
    )
    expect(result).toMatchObject({ status: 'completed', addTasks: [{ id: 'fix' }] })
  })

  it('resolves the fix-pr-checks Prompt while preserving vars and expanding failure.error.message', () => {
    const result = tryRecovery(
      work({
        budget: 1,
        handlers: [
          {
            when: 'error.code=pr-checks-failed',
            tasks: [
              {
                id: 'recover:fix-pr-checks',
                uses: 'mohist/opencode',
                with: { prompt: '${{ prompts.fix-pr-checks }}' },
              },
            ],
            retrySelf: false,
          },
        ],
      }),
      {
        status: 'failed',
        error: { code: 'pr-checks-failed', message: 'PR checks failed' },
      },
      {
        prompts: {
          'fix-pr-checks':
            'Repair PR #${{ vars.github.pr.number }} (${{ vars.github.pr.url }}): ${{ failure.error.message }}',
        },
        github: { pr: { number: 42, url: 'https://github.example/pr/42' } },
      },
    )

    expect(result).toMatchObject({
      status: 'completed',
      addTasks: [
        {
          id: 'recover:fix-pr-checks',
          with: {
            prompt: 'Repair PR #${{ vars.github.pr.number }} (${{ vars.github.pr.url }}): PR checks failed',
          },
        },
      ],
    })
  })

  it('reports the Prompt, expression, and available failure context when recovery rendering fails', () => {
    const result = tryRecovery(
      work({
        budget: 1,
        handlers: [
          {
            when: 'error.code=pr-checks-failed',
            tasks: [
              {
                id: 'recover:fix-pr-checks',
                with: { prompt: '${{ prompts.fix-pr-checks }}' },
              },
            ],
            retrySelf: false,
          },
        ],
      }),
      {
        status: 'failed',
        error: { code: 'pr-checks-failed', message: 'PR checks failed' },
      },
      {
        prompts: { 'fix-pr-checks': 'PR #${{ failure.output.prNumber }}' },
      },
    )

    expect(result).toMatchObject({ status: 'failed', error: { code: 'recovery-reference-unresolved' } })
    expect(result?.message).toContain("Prompt 'fix-pr-checks'")
    expect(result?.message).toContain('${{ failure.output.prNumber }}')
    expect(result?.message).toContain('failure.output is unavailable')
    expect(result?.message).toContain('failure.error fields [code, message]')
  })
})

describe('recovery budget clamp', () => {
  const recovery = {
    budget: 2,
    handlers: [{ when: 'error.code=conflict', tasks: [], retrySelf: true }],
  }
  const failedResult = {
    status: 'failed',
    error: { code: 'conflict', message: 'conflict' },
  } as const

  function dispatchWithRemaining(remaining: number | null): DispatchWorkItem {
    return {
      ...work(recovery),
      recoveryRemaining: remaining,
    }
  }

  it('clamps a negative continuation allowance to zero and skips automatic recovery', () => {
    const result = tryRecovery(dispatchWithRemaining(-3), failedResult)

    expect(result).toBeNull()
  })

  it('clamps an above-budget continuation allowance to the declared budget', () => {
    const result = tryRecovery(dispatchWithRemaining(99), failedResult)

    expect(result).toMatchObject({
      status: 'completed',
      addTasks: [{ id: 'integrate:rebase', recoveryRemaining: 1 }],
    })
  })

  it('leaves an in-range continuation allowance untouched', () => {
    const result = tryRecovery(dispatchWithRemaining(1), failedResult)

    expect(result).toMatchObject({
      status: 'completed',
      addTasks: [{ id: 'integrate:rebase', recoveryRemaining: 0 }],
    })
  })
})

describe('recovery materialization preserves workspace and run-branch identity', () => {
  function makeRebaseWork(recovery: DispatchWorkItem['recovery']): DispatchWorkItem {
    return {
      workflowRunId: 'wf-identity-rebase',
      workId: 'integrate:rebase.2',
      workType: 'task',
      stage: 'integrate',
      title: 'Rebase branch',
      uses: 'mohist/rebase',
      with: { baseBranch: 'master', remote: 'origin' },
      variables: {
        workflow: { runId: 'wf-identity-rebase' },
        workspace: { path: '/virtual/run-identity/workspace', branch: 'mohist/run-wf-identity-rebase' },
        repository: { baseBranch: 'master', gitUrl: 'https://example.test/repository.git' },
        issue: { number: 7 },
      },
      recovery,
      recoveryRemaining: 1,
    }
  }

  it('self-retry keeps the original with verbatim — no second expectedBranch declaration is added', () => {
    const recovery = {
      budget: 2,
      handlers: [{ when: 'error.code=conflict', tasks: [], retrySelf: true }],
    }
    const original = makeRebaseWork(recovery)
    const result = tryRecovery(original, {
      status: 'failed',
      error: { code: 'conflict', message: 'conflict' },
    })
    const retry = result?.addTasks?.find((task) => task.id === 'integrate:rebase')

    // The retry task inherits `uses`, `with`, `expect`, `artifacts`,
    // `setVars`, `recovery`, and `recoveryRemaining` from the original
    // task. `expectedBranch` is engine-injected from
    // `variables.workspace.branch` so the workflow profile does not
    // need to declare it again, and `tryRecovery` does not synthesise
    // one either — the retry's `with` keys are exactly the original's.
    expect(retry).toBeDefined()
    expect(retry?.uses).toBe('mohist/rebase')
    expect(retry?.with).toEqual({ baseBranch: 'master', remote: 'origin' })
    expect(retry?.with).not.toHaveProperty('expectedBranch')
    expect(retry?.recovery).toEqual(recovery)
    expect(retry?.recoveryRemaining).toBe(0)
  })

  it('handler-materialized tasks inherit the original with and never substitute baseBranch for expectedBranch', () => {
    const recovery = {
      budget: 1,
      handlers: [
        {
          when: 'error.code=conflict',
          retrySelf: false,
          tasks: [
            {
              id: 'resolve-rebase-conflicts',
              title: 'Resolve rebase conflicts',
              uses: 'mohist/opencode',
              with: { prompt: 'Resolve the rebase conflicts' },
            },
          ],
        },
      ],
    }
    const result = tryRecovery(makeRebaseWork(recovery), {
      status: 'failed',
      error: { code: 'conflict', message: 'conflict' },
    })

    expect(result).toMatchObject({
      status: 'completed',
      addTasks: [{ id: 'resolve-rebase-conflicts', uses: 'mohist/opencode' }],
    })
    // The handler task's `with` is exactly what the workflow author
    // declared — `expectedBranch` is never injected by `tryRecovery` and
    // never substituted by the rebase target. The workspace identity
    // travels with the workflow-run variables, not via `with`.
    const resolve = result?.addTasks?.find((task) => task.id === 'resolve-rebase-conflicts')
    expect(resolve?.with).toEqual({ prompt: 'Resolve the rebase conflicts' })
    expect(resolve?.with).not.toHaveProperty('expectedBranch')
    expect(resolve?.with).not.toHaveProperty('baseBranch')
  })

  it('self-retry preserves variables.workspace.path and workflowRunId via the immutable with clone', () => {
    const recovery = {
      budget: 2,
      handlers: [{ when: 'error.code=conflict', tasks: [], retrySelf: true }],
    }
    const original = makeRebaseWork(recovery)
    const result = tryRecovery(original, {
      status: 'failed',
      error: { code: 'conflict', message: 'conflict' },
    })
    const retry = result?.addTasks?.find((task) => task.id === 'integrate:rebase')

    // The retry's `with` is a fresh clone (immutable against the
    // original), and contains exactly the original input keys — the
    // workspace path and run branch travel through `variables`
    // (preserved by the server when it materialises the retry into a
    // new dispatch), not via `with`.
    expect(retry).toBeDefined()
    expect(retry?.with).toEqual({ baseBranch: 'master', remote: 'origin' })
    // Cloning must not regress the original.
    expect(retry?.with).not.toBe(original.with)
    // The retry doesn't bring along variables — those are rebuilt by
    // the server-side dispatch materialization, which is verified in
    // `recovery-round-cross-boundary.spec.ts`.
    expect((retry as unknown as { variables?: unknown }).variables).toBeUndefined()
  })
})
