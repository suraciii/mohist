import { describe, expect, it as vitestIt } from 'vitest'
import { ServerConnection } from '../src/server/connection.js'
import { transportFetch, withFakeTransport } from './support/fake-transport.js'

interface MockResponseInit {
  status: number
  contentType?: string
  body?: string | Buffer
}

const fetchMock = transportFetch
const it = (name: string, body: () => unknown) => vitestIt(name, () => withFakeTransport(async () => await body()))

function mockResponse({ status, contentType = 'application/json', body = '{}' }: MockResponseInit): Response {
  return new Response(typeof body === 'string' ? body : new Uint8Array(body), {
    status,
    headers: { 'content-type': contentType },
  })
}

function options() {
  return {
    serverUrl: 'https://runner.test',
    runnerId: 'runner-1',
    runnerRoot: '/virtual/runner',
    pollIntervalMs: 100,
    heartbeatIntervalMs: 60_000,
    dispatchLivenessProbeIntervalMs: 60_000,
  }
}

describe('ServerConnection.report', () => {
  it('forwardsCleanupAttemptsToServerWhenResultIncludesThem', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: '{}' }))
    const connection = new ServerConnection(options())
    const work = { workflowRunId: 'wf-1', workId: 'work-1', taskRunId: 'task-1.1', workType: 'task' }
    await connection.report(
      work,
      { status: 'failed', message: 'dirty', output: '{}', cleanupAttempts: 3 },
      new AbortController().signal,
    )
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const init = fetchMock.mock.calls[0][1] as RequestInit
    const body = JSON.parse(init.body as string)
    expect(body.taskRunId).toBe('task-1.1')
    expect(body.cleanupAttempts).toBe(3)
  })

  it('preservesRunnerRestartedUnknownStatusAndOriginalWorkflowIdentity', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({ tracked: true }) }))
    const connection = new ServerConnection(options())
    await connection.report(
      {
        workflowRunId: 'wf-restart',
        workId: 'work-restart',
        taskRunId: 'task-restart',
        workType: 'task',
        uses: 'mohist/opencode',
        ownerKind: 'workflow',
        projectId: 'project-1',
      },
      {
        status: 'unknown',
        message: 'runner-restarted',
        error: { code: 'runner-restarted', message: 'runner-restarted' },
      },
      new AbortController().signal,
    )
    const body = JSON.parse((fetchMock.mock.calls[0][1] as RequestInit).body as string)
    expect(body).toMatchObject({
      workflowRunId: 'wf-restart',
      workId: 'work-restart',
      taskRunId: 'task-restart',
      ownerKind: 'workflow',
      status: 'unknown',
      error: { code: 'runner-restarted' },
    })
  })

  it('sendsNullCleanupAttemptsWhenResultOmitsThem', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: '{}' }))
    const connection = new ServerConnection(options())
    const work = { workflowRunId: 'wf-1', workId: 'work-1', workType: 'task' }
    await connection.report(work, { status: 'completed', message: 'ok', output: '{}' }, new AbortController().signal)
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const init = fetchMock.mock.calls[0][1] as RequestInit
    const body = JSON.parse(init.body as string)
    expect(body.cleanupAttempts).toBeNull()
  })

  it('forwardsRecoveryRemainingOnReportedFollowUps', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: '{}' }))
    const connection = new ServerConnection(options())
    const work = { workflowRunId: 'wf-1', workId: 'work-1', workType: 'task' }
    await connection.report(
      work,
      {
        status: 'completed',
        output: '{}',
        addTasks: [
          {
            id: 'work-1',
            title: 'Work',
            recovery: { budget: 2, handlers: [] },
            recoveryRemaining: 1,
          },
        ],
      },
      new AbortController().signal,
    )

    const init = fetchMock.mock.calls[0][1] as RequestInit
    const body = JSON.parse(init.body as string)
    expect(body.addTasks[0].recoveryRemaining).toBe(1)
  })
})

describe('ServerConnection.poll recovery state', () => {
  it('preserves explicit null and numeric state while keeping an absent state absent', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({
          dispatches: [
            { workflowRunId: 'wf-1', workId: 'work-1', workType: 'task', recoveryRemaining: null },
            { workflowRunId: 'wf-1', workId: 'work-2', workType: 'task', recoveryRemaining: 1 },
            { workflowRunId: 'wf-1', workId: 'work-3', workType: 'task' },
          ],
        }),
      }),
    )

    const connection = new ServerConnection(options())
    const works = await connection.poll(new AbortController().signal)

    expect(works[0]?.recoveryRemaining).toBeNull()
    expect(Object.prototype.hasOwnProperty.call(works[0], 'recoveryRemaining')).toBe(true)
    expect(works[1]?.recoveryRemaining).toBe(1)
    expect(Object.prototype.hasOwnProperty.call(works[1], 'recoveryRemaining')).toBe(true)
    expect(Object.prototype.hasOwnProperty.call(works[2], 'recoveryRemaining')).toBe(false)
  })

  it('decodes parent issue context while preserving null and absence', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({
          dispatches: [
            {
              workflowRunId: 'wf-1',
              workId: 'plan-1',
              workType: 'task',
              parentIssueContext: { title: 'Parent', body: 'Parent body' },
            },
            { workflowRunId: 'wf-1', workId: 'plan-2', workType: 'task', parentIssueContext: null },
            { workflowRunId: 'wf-1', workId: 'plan-3', workType: 'task' },
          ],
        }),
      }),
    )

    const connection = new ServerConnection(options())
    const works = await connection.poll(new AbortController().signal)

    expect(works[0]?.parentIssueContext).toEqual({ title: 'Parent', body: 'Parent body' })
    expect(works[1]?.parentIssueContext).toBeNull()
    expect(Object.prototype.hasOwnProperty.call(works[1], 'parentIssueContext')).toBe(true)
    expect(Object.prototype.hasOwnProperty.call(works[2], 'parentIssueContext')).toBe(false)
  })

  it('parses expect from the dispatch response into DispatchWorkItem.expect', async () => {
    // T-003 acceptance: "DispatchWorkItem and AddTaskInput carry
    // expect; connection.ts parseDispatchWorkItem parses expect from the dispatch
    // response".
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({
          dispatches: [
            {
              workflowRunId: 'wf-1',
              workId: 'opencode-1',
              workType: 'task',
              uses: 'mohist/opencode',
              with: JSON.stringify({ prompt: 'do work' }),
              expect: JSON.stringify({
                markers: [{ path: '_output', oneOf: ['<promise>PASS</promise>', '<promise>FAIL</promise>'] }],
              }),
            },
            {
              workflowRunId: 'wf-1',
              workId: 'no-expect-1',
              workType: 'task',
              uses: 'mohist/rebase',
              with: JSON.stringify({ baseBranch: 'main' }),
            },
          ],
        }),
      }),
    )

    const connection = new ServerConnection(options())
    const works = await connection.poll(new AbortController().signal)

    // Expect is decoded as a structured object (NOT stringified) so
    // the executor's completion evaluator can read it.
    expect(works[0]?.expect).toEqual({
      markers: [{ path: '_output', oneOf: ['<promise>PASS</promise>', '<promise>FAIL</promise>'] }],
    })
    // Action Input (`with`) is decoded independently and DOES NOT
    // contain the completion contract.
    expect(works[0]?.with).toEqual({ prompt: 'do work' })

    // An absent `expect` field surfaces as `null` so the executor can
    // tell "no completion contract" apart from "completion contract
    // was empty".
    expect(works[1]?.expect).toBeNull()
    expect(Object.prototype.hasOwnProperty.call(works[1] ?? {}, 'expect')).toBe(true)
  })

  it('parseDispatchWorkItem keeps raw with/expect declarations intact', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({
          dispatches: [
            {
              workflowRunId: 'wf-raw-decl',
              workId: 'opencode-raw',
              workType: 'task',
              uses: 'mohist/opencode',
              with: JSON.stringify({ prompt: 'child prompt: ${{ vars.agent }}', mode: '${{ vars.mode }}' }),
              expect: JSON.stringify({ markers: [{ path: '_output', contains: '${{ vars.marker }}' }] }),
              variables: JSON.stringify({
                vars: { agent: { model: 'model-a' }, mode: 'fast', marker: 'PASS' },
              }),
            },
          ],
        }),
      }),
    )

    const connection = new ServerConnection(options())
    const works = await connection.poll(new AbortController().signal)
    const work = works[0]!

    expect(work.with).toEqual({ prompt: 'child prompt: ${{ vars.agent }}', mode: '${{ vars.mode }}' })
    expect(work.expect).toEqual({ markers: [{ path: '_output', contains: '${{ vars.marker }}' }] })
    expect(work.variables).toEqual({ vars: { agent: { model: 'model-a' }, mode: 'fast', marker: 'PASS' } })
    expect(JSON.stringify(work.with)).toContain('${{ vars.agent }}')
    expect(JSON.stringify(work.expect)).toContain('${{ vars.marker }}')
  })
})

describe('ServerConnection update interruption handoff', () => {
  it('readsTheWrappedPendingOperationAndAffectedWorkInventory', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({
          data: {
            operation: {
              operationId: 'runner-update:1',
              runnerId: 'runner-1',
              createdAt: '2026-08-15T00:00:00Z',
              affectedWorks: [
                { ownerKind: 'workflow', ownerId: 'wf-1', workId: 'work-1', taskRunId: 'task-1', workType: 'task' },
              ],
            },
          },
        }),
      }),
    )
    const connection = new ServerConnection(options())
    await expect(connection.fetchPendingUpdateOperation(new AbortController().signal)).resolves.toEqual({
      operationId: 'runner-update:1',
      runnerId: 'runner-1',
      createdAt: '2026-08-15T00:00:00Z',
      affectedWorks: [
        { ownerKind: 'workflow', ownerId: 'wf-1', workId: 'work-1', taskRunId: 'task-1', workType: 'task' },
      ],
    })
  })

  it('sendsTheExactReceiptAndTreatsAcknowledgeAsTerminal', async () => {
    const receipt = {
      workflowRunId: 'wf-1',
      taskRunId: 'task-1',
      workId: 'work-1',
      runnerId: 'runner-1',
      agentSessionId: 'session-1',
      agentTurnId: 'turn-1',
      runtime: 'pi',
      runtimeSessionId: '/workspace/session.jsonl',
      recoveryGeneration: 0,
      receiptId: 'receipt-1',
      payload: { type: 'update-interrupted', updateOperationId: 'runner-update:1', stopConfirmed: true },
    } as const
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({ appliedReceiptId: 'receipt-1', status: 'accepted' }),
      }),
    )
    const connection = new ServerConnection(options())
    await expect(connection.sendRecoveryReceipt(receipt, new AbortController().signal)).resolves.toEqual({
      appliedReceiptId: 'receipt-1',
      status: 'accepted',
    })
    const init = fetchMock.mock.calls[0][1] as RequestInit
    expect(JSON.parse(init.body as string)).toEqual(receipt)
  })

  it('keepsRetryableReceiptResponsesAsDeliveryFailures', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 409,
        body: JSON.stringify({ appliedReceiptId: 'receipt-1', status: 'retryable', reason: 'replacement-pending' }),
      }),
    )
    const connection = new ServerConnection(options())
    await expect(
      connection.sendRecoveryReceipt(
        {
          workflowRunId: 'wf-1',
          taskRunId: 'task-1',
          workId: 'work-1',
          runnerId: 'runner-1',
          agentSessionId: 'session-1',
          agentTurnId: 'turn-1',
          runtime: 'pi',
          runtimeSessionId: '/workspace/session.jsonl',
          recoveryGeneration: 0,
          receiptId: 'receipt-1',
          payload: { type: 'update-interrupted', updateOperationId: 'runner-update:1', stopConfirmed: true },
        },
        new AbortController().signal,
      ),
    ).rejects.toMatchObject({ retryable: true })
  })
})

describe('ServerConnection.patchRunVars', () => {
  it('patchesWorkflowRunProfileVariablesWithVariableBundleShape', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: '{}' }))
    const connection = new ServerConnection(options())

    await connection.patchRunVars('wf-1', { github: { pr: { number: 249 } } }, new AbortController().signal)

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('https://runner.test/api/workflow-runs/wf-1/variables')
    expect(init.method).toBe('PATCH')
    expect(JSON.parse(init.body as string)).toEqual({
      vars: {
        github: {
          pr: {
            number: 249,
          },
        },
      },
    })
  })
})
