import { describe, expect, it, vi } from 'vitest'
import { RunnerControlDispatcher, type RunnerControlHandlers } from './runner-control-dispatcher.js'

function validQuery() {
  return {
    workflowRunId: 'run-1',
    projectId: 'project-1',
    issueNumber: 1,
    repositoryName: 'repo',
    gitUrl: 'https://example.test/repo.git',
    workspacePath: '/work/run-1',
    branch: 'run-1',
    baseBranch: 'main',
  }
}

function validTarget() {
  return {
    kind: 'generic',
    projectId: 'project-1',
    sessionId: 'session-1',
    binding: { runtime: 'opencode', runtimeSessionId: 'runtime-1', runnerId: 'runner-1', workDir: '/work/run-1' },
  }
}

function handlerSet(): RunnerControlHandlers {
  return {
    workspaceDiff: vi.fn(async () => 'diff'),
    workspaceCommits: vi.fn(async () => 'commits'),
    workspaceCommitDiff: vi.fn(async () => 'commit-diff'),
    workspaceStatus: vi.fn(async () => 'status'),
    workspaceFileContent: vi.fn(async () => 'content'),
    workspaceRemove: vi.fn(async () => 'removed'),
    sessionFollowup: vi.fn(async () => 'followup'),
    sessionStop: vi.fn(async () => 'stopped'),
    sessionCommand: vi.fn(async () => 'command'),
    workflowStatusChanged: vi.fn(),
  }
}

function harness(handlers = handlerSet(), holdOutput = false) {
  const sent: unknown[] = []
  const completions: Array<() => void> = []
  let errors = 0
  let current = true
  const dispatcher = new RunnerControlDispatcher(handlers, {
    enqueue: (value, complete) => {
      sent.push(value)
      if (complete) {
        if (holdOutput) completions.push(complete)
        else complete()
      }
      return true
    },
    protocolError: () => {
      errors++
    },
    isCurrent: () => current,
  })
  const receive = (value: unknown) => dispatcher.receive(typeof value === 'string' ? value : JSON.stringify(value))
  return {
    dispatcher,
    handlers,
    sent,
    receive,
    errors: () => errors,
    completeOutput: () => completions.shift()?.(),
    fence: () => {
      current = false
    },
  }
}

async function settle(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
}

describe('RunnerControlDispatcher', () => {
  it('routes all request methods with named params and tolerates extra properties', async () => {
    const h = harness()
    const target = validTarget()
    const requests = [
      ['workspace.diff', { query: validQuery(), extra: true }, 'workspaceDiff'],
      ['workspace.commits', { query: validQuery() }, 'workspaceCommits'],
      ['workspace.commit-diff', { query: validQuery(), hash: 'abc' }, 'workspaceCommitDiff'],
      ['workspace.status', { query: validQuery() }, 'workspaceStatus'],
      ['workspace.file-content', { query: validQuery(), path: 'src/a.ts' }, 'workspaceFileContent'],
      ['workspace.remove', { query: validQuery() }, 'workspaceRemove'],
      [
        'session.followup',
        { target, text: 'next', operationId: 'op-1', inputId: null, turnId: 'turn-1', attachments: [] },
        'sessionFollowup',
      ],
      ['session.stop', { target, sessionId: 'session-1', turnId: 'turn-1', operationId: 'op-2' }, 'sessionStop'],
      [
        'session.command',
        {
          sessionId: 'session-1',
          runtime: 'opencode',
          runtimeSessionId: 'runtime-1',
          runnerId: 'runner-1',
          workDir: '/work/run-1',
          command: 'reset',
          expectedRuntimeSessionId: 'runtime-1',
          operationId: 'op-3',
          projectId: null,
        },
        'sessionCommand',
      ],
    ] as const
    requests.forEach(([method, params], index) => h.receive({ jsonrpc: '2.0', id: `id-${index}`, method, params }))
    await settle()
    for (const [, , name] of requests) expect(h.handlers[name]).toHaveBeenCalledOnce()
    expect(h.sent).toHaveLength(9)
  })

  it('preserves nullable results and typed domain results', async () => {
    const handlers = handlerSet()
    handlers.workspaceDiff = vi.fn(async () => null)
    handlers.sessionStop = vi.fn(async () => ({ state: 'unavailable' }))
    const h = harness(handlers)
    h.receive({ jsonrpc: '2.0', id: 'read', method: 'workspace.diff', params: { query: validQuery() } })
    h.receive({
      jsonrpc: '2.0',
      id: 'stop',
      method: 'session.stop',
      params: { target: validTarget(), sessionId: 'session-1', turnId: 'turn-1', operationId: 'op' },
    })
    await settle()
    expect(h.sent).toContainEqual({ jsonrpc: '2.0', id: 'read', result: null })
    expect(h.sent).toContainEqual({ jsonrpc: '2.0', id: 'stop', result: { state: 'unavailable' } })
  })

  it('invokes status reconciliation without a response', async () => {
    const h = harness()
    h.receive({
      jsonrpc: '2.0',
      method: 'workflow.status-changed',
      params: { workflowRunId: 'run-1', status: 'Completed' },
    })
    await settle()
    expect(h.handlers.workflowStatusChanged).toHaveBeenCalledWith({ workflowRunId: 'run-1', status: 'Completed' })
    expect(h.sent).toEqual([])
  })

  it('swallows synchronous throws and asynchronous rejections from notifications', async () => {
    const handlers = handlerSet()
    handlers.workflowStatusChanged = vi
      .fn()
      .mockImplementationOnce(() => {
        throw new Error('sync')
      })
      .mockRejectedValueOnce(new Error('async'))
    const h = harness(handlers)
    const notification = {
      jsonrpc: '2.0',
      method: 'workflow.status-changed',
      params: { workflowRunId: 'run-1', status: 'Completed' },
    }
    expect(() => h.receive(notification)).not.toThrow()
    expect(() => h.receive(notification)).not.toThrow()
    await settle()
    expect(h.sent).toEqual([])
  })

  it('normalizes omitted nullable command and binding members before dispatch', async () => {
    const h = harness()
    const target = validTarget()
    delete (target.binding as Partial<typeof target.binding>).workDir
    h.receive({
      jsonrpc: '2.0',
      id: 'followup',
      method: 'session.followup',
      params: { target, text: 'next', operationId: 'op-1', turnId: 'turn-1' },
    })
    h.receive({
      jsonrpc: '2.0',
      id: 'stop',
      method: 'session.stop',
      params: { target, sessionId: 'session-1', operationId: 'op-2', turnId: 'turn-2' },
    })
    h.receive({
      jsonrpc: '2.0',
      id: 'reset',
      method: 'session.command',
      params: {
        sessionId: 'session-1',
        runtime: 'opencode',
        runnerId: 'runner-1',
        command: 'reset',
        operationId: 'op-3',
      },
    })
    h.receive({
      jsonrpc: '2.0',
      id: 'reset-explicit-null',
      method: 'session.command',
      params: {
        sessionId: 'session-1',
        runtime: 'opencode',
        runnerId: 'runner-1',
        command: 'reset',
        expectedRuntimeSessionId: null,
        operationId: 'op-4',
      },
    })
    await settle()
    expect(h.handlers.sessionFollowup).toHaveBeenCalledWith(
      expect.objectContaining({
        target: expect.objectContaining({ binding: expect.objectContaining({ workDir: null }) }),
      }),
    )
    expect(h.handlers.sessionStop).toHaveBeenCalledWith(
      expect.objectContaining({
        target: expect.objectContaining({ binding: expect.objectContaining({ workDir: null }) }),
      }),
    )
    expect(h.handlers.sessionCommand).toHaveBeenCalledWith(
      expect.objectContaining({
        runtimeSessionId: null,
        workDir: null,
        expectedRuntimeSessionId: null,
        projectId: null,
      }),
    )
    expect(h.handlers.sessionCommand).toHaveBeenCalledTimes(2)
  })

  it('returns standard errors and counts response-producing protocol failures', async () => {
    const h = harness()
    h.receive('{')
    h.receive([])
    h.receive({ jsonrpc: '2.0', id: 'unknown', method: 'other', params: {} })
    h.receive({ jsonrpc: '2.0', id: 'params', method: 'workspace.diff', params: {} })
    await settle()
    expect(h.sent).toEqual([
      { jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error' } },
      { jsonrpc: '2.0', id: null, error: { code: -32600, message: 'Invalid Request' } },
      { jsonrpc: '2.0', id: 'unknown', error: { code: -32601, message: 'Method not found' } },
      { jsonrpc: '2.0', id: 'params', error: { code: -32602, message: 'Invalid params' } },
    ])
    expect(h.errors()).toBe(4)
  })

  it('rejects malformed nested values before effects and ignores unknown notifications', () => {
    const h = harness()
    h.receive({
      jsonrpc: '2.0',
      id: 'bad',
      method: 'session.followup',
      params: {
        target: validTarget(),
        text: 'x',
        operationId: 'op',
        turnId: 'turn',
        attachments: [{ id: 'a', name: 'a', contentType: null, size: -1 }],
      },
    })
    h.receive({ jsonrpc: '2.0', method: 'unknown.notification', params: {} })
    expect(h.handlers.sessionFollowup).not.toHaveBeenCalled()
    expect(h.sent[0]).toEqual({ jsonrpc: '2.0', id: 'bad', error: { code: -32602, message: 'Invalid params' } })
    expect(h.errors()).toBe(1)
  })

  it('does not invoke a duplicate live ID and preserves the original operation', async () => {
    let complete!: (value: string) => void
    const handlers = handlerSet()
    handlers.workspaceDiff = vi.fn(
      () =>
        new Promise((resolve) => {
          complete = resolve
        }),
    )
    const h = harness(handlers, true)
    const request = { jsonrpc: '2.0', id: 'same', method: 'workspace.diff', params: { query: validQuery() } }
    h.receive(request)
    h.receive(request)
    expect(handlers.workspaceDiff).toHaveBeenCalledOnce()
    expect(h.sent[0]).toEqual({ jsonrpc: '2.0', id: 'same', error: { code: -32600, message: 'Invalid Request' } })
    complete('original')
    await settle()
    expect(h.sent[1]).toEqual({ jsonrpc: '2.0', id: 'same', result: 'original' })
    h.completeOutput()
  })

  it('allows concurrent handlers, out-of-order completion, internal errors, and drops late output after fencing', async () => {
    const completions = new Map<string, (value: string) => void>()
    const handlers = handlerSet()
    handlers.workspaceCommitDiff = vi.fn((_query, hash) =>
      hash === 'throw' ? Promise.reject(new Error('boom')) : new Promise((resolve) => completions.set(hash, resolve)),
    )
    const h = harness(handlers)
    for (const hash of ['one', 'two', 'throw'])
      h.receive({ jsonrpc: '2.0', id: hash, method: 'workspace.commit-diff', params: { query: validQuery(), hash } })
    completions.get('two')!('second')
    await settle()
    expect(h.sent).toContainEqual({ jsonrpc: '2.0', id: 'two', result: 'second' })
    expect(h.sent).toContainEqual({ jsonrpc: '2.0', id: 'throw', error: { code: -32603, message: 'Internal error' } })
    h.fence()
    completions.get('one')!('late')
    await settle()
    expect(h.sent).not.toContainEqual({ jsonrpc: '2.0', id: 'one', result: 'late' })
  })
})
