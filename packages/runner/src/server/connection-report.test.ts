import { expect, it } from 'vitest'
import { ServerConnection } from './connection.js'
import type { DispatchWorkItem } from '../core/types.js'
import { transportFetch, withFakeTransport } from '../../tests/support/fake-transport.js'

const options = {
  serverUrl: 'https://runner.test',
  runnerId: 'runner-1',
  runnerRoot: '/virtual/runner',
  pollIntervalMs: 1,
  heartbeatIntervalMs: 1,
  dispatchLivenessProbeIntervalMs: 1,
}

const signal = new AbortController().signal
const fetchSpy = transportFetch

it('sends the complete runtime binding in a workflow Agent report', async () => {
  await withFakeTransport(async () => {
    fetchSpy.mockResolvedValue(
      new Response(JSON.stringify({ tracked: true }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )

    const work: DispatchWorkItem = {
      workflowRunId: 'workflow-1',
      workId: 'work-1',
      taskRunId: 'task-1',
      workType: 'task',
      ownerKind: 'workflow',
    }
    const binding = {
      agentSessionId: 'session-1',
      agentTurnId: 'turn-1',
      runtime: 'opencode',
      runtimeSessionId: 'runtime-session-1',
    } as const

    await new ServerConnection(options).report(work, { status: 'completed' }, signal, binding)

    const [url, init] = fetchSpy.mock.calls[0]!
    expect(url).toBe('https://runner.test/api/runner/runner-1/report')
    expect(JSON.parse(String((init as RequestInit).body))).toMatchObject({
      workId: 'work-1',
      taskRunId: 'task-1',
      agentSessionId: 'session-1',
      agentTurnId: 'turn-1',
      runtime: 'opencode',
      runtimeSessionId: 'runtime-session-1',
    })
  })
})
