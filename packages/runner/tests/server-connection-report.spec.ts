import { describe, expect, it as vitestIt } from 'vitest'
import { RunnerTransportError, ServerConnection } from '../src/server/connection.js'
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
  it('preserves accepted, refused, and outstanding acknowledgements', async () => {
    const connection = new ServerConnection(options())
    const work = { workflowRunId: 'wf-1', workId: 'work-1', workType: 'task' }
    const signal = new AbortController().signal

    for (const verdict of ['accepted', 'refused', 'outstanding'] as const) {
      fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({ verdict }) }))
      await expect(connection.report(work, { status: 'completed' }, signal)).resolves.toEqual({ verdict })
    }

    expect(fetchMock).toHaveBeenCalledTimes(3)
    expect(fetchMock.mock.calls.every(([url]) => url === 'https://runner.test/api/runner/runner-1/report')).toBe(true)
  })

  it('preserves a nullable acknowledgement as an outstanding report', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: 'null' }))
    const connection = new ServerConnection(options())

    await expect(
      connection.report(
        { workflowRunId: 'wf-1', workId: 'work-1', workType: 'task' },
        { status: 'completed' },
        new AbortController().signal,
      ),
    ).resolves.toEqual({ verdict: null })
  })

  it('classifies malformed acknowledgement JSON as a protocol transport failure', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: 'not-json' }))
    const connection = new ServerConnection(options())

    const error = await connection
      .report(
        { workflowRunId: 'wf-1', workId: 'work-1', workType: 'task' },
        { status: 'completed' },
        new AbortController().signal,
      )
      .catch((value: unknown) => value)

    expect(error).toBeInstanceOf(RunnerTransportError)
    expect(error).toMatchObject({ operation: 'report', kind: 'protocol' })
  })

  it('classifies report HTTP failures with stable transport fields', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 409,
        body: JSON.stringify({ code: 'report_outstanding', data: { message: 'report is still being processed' } }),
      }),
    )
    const connection = new ServerConnection(options())

    const error = await connection
      .report(
        { workflowRunId: 'wf-1', workId: 'work-1', workType: 'task' },
        { status: 'completed' },
        new AbortController().signal,
      )
      .catch((value: unknown) => value)

    expect(error).toBeInstanceOf(RunnerTransportError)
    expect(error).toMatchObject({
      operation: 'report',
      kind: 'http',
      httpStatus: 409,
      serverCode: 'report_outstanding',
    })
  })

  it('forwardsCleanupAttemptsToServerWhenResultIncludesThem', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: '{}' }))
    const connection = new ServerConnection(options())
    const work = {
      workflowRunId: 'wf-1',
      workId: 'work-1',
      actionAttemptId: 'task-1.1',
      workType: 'task',
    }
    await connection.report(
      work,
      { status: 'failed', message: 'dirty', output: '{}', cleanupAttempts: 3 },
      new AbortController().signal,
    )
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const init = fetchMock.mock.calls[0][1] as RequestInit
    const body = JSON.parse(init.body as string)
    expect(body.actionAttemptId).toBe('task-1.1')
    expect(body.cleanupAttempts).toBe(3)
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
            {
              workflowRunId: 'wf-1',
              workId: 'work-1',
              workType: 'task',
              recoveryRemaining: null,
            },
            {
              workflowRunId: 'wf-1',
              workId: 'work-2',
              workType: 'task',
              recoveryRemaining: 1,
            },
            { workflowRunId: 'wf-1', workId: 'work-3', workType: 'task' },
          ],
        }),
      }),
    )

    const connection = new ServerConnection(options())
    const polled = await connection.poll(new AbortController().signal, {
      processGeneration: 'test-generation',
      inFlight: [],
      awaitingAck: [],
      admissionReady: false,
    })
    const works = polled.map((dispatch) => dispatch.work)

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
            {
              workflowRunId: 'wf-1',
              workId: 'plan-2',
              workType: 'task',
              parentIssueContext: null,
            },
            { workflowRunId: 'wf-1', workId: 'plan-3', workType: 'task' },
          ],
        }),
      }),
    )

    const connection = new ServerConnection(options())
    const polled = await connection.poll(new AbortController().signal, {
      processGeneration: 'test-generation',
      inFlight: [],
      awaitingAck: [],
      admissionReady: false,
    })
    const works = polled.map((dispatch) => dispatch.work)

    expect(works[0]?.parentIssueContext).toEqual({
      title: 'Parent',
      body: 'Parent body',
    })
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
                markers: [
                  {
                    path: '_output',
                    oneOf: ['<promise>PASS</promise>', '<promise>FAIL</promise>'],
                  },
                ],
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
    const polled = await connection.poll(new AbortController().signal, {
      processGeneration: 'test-generation',
      inFlight: [],
      awaitingAck: [],
      admissionReady: false,
    })
    const works = polled.map((dispatch) => dispatch.work)

    // Expect is decoded as a structured object (NOT stringified) so
    // the executor's completion evaluator can read it.
    expect(works[0]?.expect).toEqual({
      markers: [
        {
          path: '_output',
          oneOf: ['<promise>PASS</promise>', '<promise>FAIL</promise>'],
        },
      ],
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
              with: JSON.stringify({
                prompt: 'child prompt: ${{ vars.agent }}',
                mode: '${{ vars.mode }}',
              }),
              expect: JSON.stringify({
                markers: [{ path: '_output', contains: '${{ vars.marker }}' }],
              }),
              variables: JSON.stringify({
                vars: {
                  agent: { model: 'model-a' },
                  mode: 'fast',
                  marker: 'PASS',
                },
              }),
            },
          ],
        }),
      }),
    )

    const connection = new ServerConnection(options())
    const polled = await connection.poll(new AbortController().signal, {
      processGeneration: 'test-generation',
      inFlight: [],
      awaitingAck: [],
      admissionReady: false,
    })
    const work = polled[0]!.work

    expect(work.with).toEqual({
      prompt: 'child prompt: ${{ vars.agent }}',
      mode: '${{ vars.mode }}',
    })
    expect(work.expect).toEqual({
      markers: [{ path: '_output', contains: '${{ vars.marker }}' }],
    })
    expect(work.variables).toEqual({
      vars: { agent: { model: 'model-a' }, mode: 'fast', marker: 'PASS' },
    })
    expect(JSON.stringify(work.with)).toContain('${{ vars.agent }}')
    expect(JSON.stringify(work.expect)).toContain('${{ vars.marker }}')
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
