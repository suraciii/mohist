import { describe, expect, it } from 'vitest'
import { buildRegistrationState, WORKFLOW_TASK_COMPLETION_BOUNDARY_V1 } from './registration-state.js'
import type { RunnerOptions } from '../core/types.js'

const options = {
  runnerId: 'runner-1',
  runnerRoot: '/runner',
  serverUrl: 'https://runner.test',
  pollIntervalMs: 100,
  heartbeatIntervalMs: 100,
  dispatchLivenessProbeIntervalMs: 100,
} as RunnerOptions

describe('runner v1 admission registration', () => {
  it('advertises the Workflow completion boundary capability on every registration', () => {
    const registration = buildRegistrationState(options, null, { actions: [], tombstones: [] }, () => 'connection-1')

    expect(registration.capabilities).toEqual([WORKFLOW_TASK_COMPLETION_BOUNDARY_V1])
  })
})
