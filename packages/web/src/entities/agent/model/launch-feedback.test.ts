import { describe, expect, it } from 'vitest'
import { getAgentLaunchErrorFeedback } from './launch-feedback'

describe('getAgentLaunchErrorFeedback task-first outcomes', () => {
  it('names both repairs for an unresolved execution configuration', () => {
    const feedback = getAgentLaunchErrorFeedback({ code: 'execution_config_unresolvable' })

    expect(feedback).toMatchObject({
      kind: 'execution-config-unresolvable',
      nextAction: expect.stringMatching(/Runtime and Model.*Project default/i),
    })
  })

  it('makes pending convergence retryable with the original key', () => {
    expect(getAgentLaunchErrorFeedback({ code: 'launch_setup_pending' })).toMatchObject({
      kind: 'launch-pending',
      nextAction: expect.stringMatching(/same Idempotency-Key/i),
    })
  })

  it('identifies an idempotency conflict as a new-key decision', () => {
    expect(getAgentLaunchErrorFeedback({ code: 'launch_idempotency_conflict' })).toMatchObject({
      kind: 'launch-conflict',
      nextAction: expect.stringMatching(/new launch with a new key/i),
    })
  })

  it('maps launch_scope_changed to a re-run repair path that keeps the task', () => {
    const feedback = getAgentLaunchErrorFeedback({ code: 'launch_scope_changed' })

    expect(feedback).toMatchObject({
      kind: 'scope-changed',
      title: expect.stringMatching(/scope changed/i),
      nextAction: expect.stringMatching(/re-run the launch/i),
    })
    expect(feedback?.message).toMatch(/task is unchanged/i)
  })

  it('also maps a 409 scope-changed response without a code field', () => {
    expect(
      getAgentLaunchErrorFeedback({ status: 409, message: 'The confirmed execution scope changed before launch.' }),
    ).toMatchObject({
      kind: 'scope-changed',
    })
  })
})
