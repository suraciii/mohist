import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import type {
  FollowupParams,
  JsonRpcErrorResponse,
  JsonRpcNotification,
  JsonRpcRequest,
  JsonRpcSuccessResponse,
  SessionCommandRequest,
  SessionStopParams,
  WorkflowRunStatusNotification,
  WorkspaceCommitDiffParams,
  WorkspaceFileContentParams,
  WorkspaceQueryParams,
} from '../src/contracts/runner-control.js'

const requestMethods = [
  'workspace.diff',
  'workspace.commits',
  'workspace.commit-diff',
  'workspace.status',
  'workspace.file-content',
  'workspace.remove',
  'session.followup',
  'session.stop',
  'session.command',
] as const

const standardErrors = new Map([
  [-32700, 'Parse error'],
  [-32600, 'Invalid Request'],
  [-32601, 'Method not found'],
  [-32602, 'Invalid params'],
  [-32603, 'Internal error'],
  [-32001, 'Response too large'],
])

interface FixtureEntry {
  method: (typeof requestMethods)[number]
  request: unknown
  success: unknown
  nullableSuccess?: unknown
  error: unknown
}

interface FixtureCatalog {
  requests: FixtureEntry[]
  notifications: Array<{ method: string; notification: unknown }>
}

describe('runner control JSON contract', () => {
  it('covers every request, result, nullable result, and specified error', () => {
    const catalog = readCatalog()
    expect(catalog.requests.map((entry) => entry.method)).toEqual(requestMethods)
    expect(catalog.requests.filter((entry) => entry.nullableSuccess).map((entry) => entry.method)).toEqual([
      'workspace.diff',
      'workspace.commits',
      'workspace.commit-diff',
    ])

    for (const entry of catalog.requests) assertEntry(entry)
    expect(new Set(catalog.requests.map((entry) => error(entry.error).error.code))).toEqual(
      new Set(standardErrors.keys()),
    )
  })

  it('uses the named params shapes consumed by the Runner', () => {
    const entries = new Map(readCatalog().requests.map((entry) => [entry.method, entry]))
    const query = request<WorkspaceQueryParams>(entries.get('workspace.diff')!).params.query
    const commit = request<WorkspaceCommitDiffParams>(entries.get('workspace.commit-diff')!).params
    const file = request<WorkspaceFileContentParams>(entries.get('workspace.file-content')!).params
    const followup = request<FollowupParams>(entries.get('session.followup')!).params
    const stop = request<SessionStopParams>(entries.get('session.stop')!).params
    const command = request<SessionCommandRequest>(entries.get('session.command')!).params

    expect(query).toMatchObject({ workflowRunId: 'run_101', issueNumber: 657, baseBranch: 'main' })
    expect(commit).toMatchObject({ hash: 'def4567890', query })
    expect(file).toMatchObject({ path: 'src/control.ts', query })
    expect(followup).toMatchObject({ operationId: 'operation_followup_1', turnId: 'turn_followup_1' })
    expect(stop).toMatchObject({ sessionId: 'session_1', turnId: 'turn_stop_1', operationId: 'operation_stop_1' })
    expect(command).toMatchObject({ command: 'reset', operationId: 'operation_command_1' })
  })

  it('covers the workflow status notification', () => {
    const entry = readCatalog().notifications[0]!
    const notification = entry.notification as JsonRpcNotification<WorkflowRunStatusNotification>

    expect(entry.method).toBe('workflow.status-changed')
    expect(notification).toEqual({
      jsonrpc: '2.0',
      method: 'workflow.status-changed',
      params: { workflowRunId: 'run_101', status: 'Completed' },
    })
    expect(notification).not.toHaveProperty('id')
  })
})

function assertEntry(entry: FixtureEntry): void {
  const rpcRequest = request<Record<string, unknown>>(entry)
  const rpcSuccess = entry.success as JsonRpcSuccessResponse<unknown>
  const rpcError = error(entry.error)

  expect(rpcRequest).toMatchObject({ jsonrpc: '2.0', method: entry.method })
  expect(rpcRequest.id).not.toBe('')
  expect(Array.isArray(rpcRequest.params)).toBe(false)
  expect(rpcSuccess).toMatchObject({ jsonrpc: '2.0', id: rpcRequest.id })
  expect(rpcSuccess).toHaveProperty('result')
  expect(rpcError.error.message).toBe(standardErrors.get(rpcError.error.code))
  expect(rpcError.id === null || rpcError.id === rpcRequest.id).toBe(true)

  if (entry.nullableSuccess) {
    expect(entry.nullableSuccess).toEqual({ jsonrpc: '2.0', id: rpcRequest.id, result: null })
  }
}

function request<TParams>(entry: FixtureEntry): JsonRpcRequest<TParams> {
  return entry.request as JsonRpcRequest<TParams>
}

function error(value: unknown): JsonRpcErrorResponse {
  return value as JsonRpcErrorResponse
}

function readCatalog(): FixtureCatalog {
  const url = new URL('../../../fixtures/runner-control.json', import.meta.url)
  return JSON.parse(readFileSync(url, 'utf8')) as FixtureCatalog
}
