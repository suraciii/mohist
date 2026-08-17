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

  it('maps scope drift to an explicit renewed review', () => {
    expect(
      getAgentLaunchErrorFeedback({ code: 'launch_scope_changed' }),
    ).toMatchObject({
      kind: 'launch-scope-changed',
      nextAction: expect.stringMatching(/Review the updated scope/i),
    })
  })

  it('identifies an idempotency conflict as a new-key decision', () => {
    expect(getAgentLaunchErrorFeedback({ code: 'launch_idempotency_conflict' })).toMatchObject({
      kind: 'launch-conflict',
      nextAction: expect.stringMatching(/new launch with a new key/i),
    })
  })
})
