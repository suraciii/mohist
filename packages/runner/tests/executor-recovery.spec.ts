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
  it('leaves resource containment as a bounded diagnostic failure', () => {
    const result = tryRecovery(
      work({
        budget: 2,
        handlers: [{ tasks: [{ id: 'fix-ci' }], retrySelf: true }],
      }),
      {
        status: 'failed',
        error: { code: 'resource-containment', message: 'Script exceeded its per-work resource bound' },
      },
    )

    expect(result).toBeNull()
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
