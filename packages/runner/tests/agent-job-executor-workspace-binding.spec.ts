import { describe, expect, it, vi } from 'vitest'
import { AgentJobExecutor } from '../src/runtime/agent-job-executor.js'
import type { AgentJobRuntimeAccessors } from '../src/runtime/agent-job-executor.js'
import type { ServerConnection } from '../src/server/connection.js'
import { WorkspaceHomeClaimedError } from '../src/runtime/workspace-entity.js'
import type { DispatchWorkItem } from '../src/core/types.js'
import type {
  OpenCodeRuntime,
  RuntimeResult,
  RuntimeTurnRequest,
  RuntimeTurnResult,
} from '../src/runtime/opencode/index.js'

interface FakeRuntimeHandles {
  runtime: OpenCodeRuntime
  runTurnCalls: RuntimeTurnRequest[]
}

function makeFakeRuntime(): FakeRuntimeHandles {
  const runTurnCalls: RuntimeTurnRequest[] = []
  let nextResult: RuntimeResult<RuntimeTurnResult> = {
    ok: true,
    value: {
      facts: {
        finalAssistantText: 'agent finished',
        runtimeSessionId: 'ses_default',
        workDir: '/tmp/ws',
      },
      diagnostics: [],
    },
    diagnostics: [],
  }
  const runtime: Partial<OpenCodeRuntime> = {
    ready: () => true,
    diagnostic: () => null,
    async runTurn(
      request: RuntimeTurnRequest,
      _signal: AbortSignal,
      _observer?: unknown,
    ): Promise<RuntimeResult<RuntimeTurnResult>> {
      runTurnCalls.push(request)
      return nextResult
    },
  }
  return {
    runtime: runtime as OpenCodeRuntime,
    runTurnCalls,
  }
}

function makeAccessors(runtime: OpenCodeRuntime | null = makeFakeRuntime().runtime): AgentJobRuntimeAccessors {
  return {
    openCode: runtime,
    pi: null,
  }
}

function makeFakeConnection() {
  const connection = {
    async openAgentSession() {},
    async attachAgentSession() {},
    async getAgentSession() {
      return {
        runtimeSessionId: 'ses_default',
        workDir: '/tmp/ws',
      } as never
    },
    async agentSessionRuntimeEvents() {},
  } as unknown as ServerConnection
  return { connection }
}

function buildAgentJobWork(overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: '',
    workId: 'aj-1',
    workType: 'task',
    ownerKind: 'agent-job',
    agentJobId: 'aj-1',
    agentSessionId: 'session-1',
    projectId: 'proj-1',
    with: { prompt: 'do the agent thing', runtime: 'opencode', executionSource: 'non-slack' },
    variables: {
      workspace: { name: 'issue-9', branch: null, changeDir: null },
      repository: { name: 'master', gitUrl: 'https://example.test/repository.git', baseBranch: 'master' },
    },
    ...overrides,
  }
}

describe('AgentJobExecutor resolves a named workspace binding', () => {
  it('materializes the named workspace and anchors the prompt to its directory', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const provision = vi.fn(async () => ({ path: '/runner-root/workspaces/mohist-pay-abc123', created: true }))
    const manager = { provision } as never
    const executor = new AgentJobExecutor(
      connection.connection,
      makeAccessors(runtime.runtime),
      '/virtual/runner',
      undefined,
      manager,
    )

    const work = buildAgentJobWork({
      projectId: 'proj-1',
      variables: {
        workspace: {
          name: 'pay',
          repositories: [{ name: 'server', gitUrl: 'https://github.com/mohist/server.git' }],
        },
      },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(provision).toHaveBeenCalledWith(
      'proj-1',
      'pay',
      [{ name: 'server', gitUrl: 'https://github.com/mohist/server.git' }],
      expect.any(AbortSignal),
    )
    const request = runtime.runTurnCalls[0]
    expect(request.target.workDir).toBe('/runner-root/workspaces/mohist-pay-abc123')
    expect(request.prompt).toContain('[mohist-workspace-anchor]')
    expect(request.prompt).toContain('Working directory: /runner-root/workspaces/mohist-pay-abc123')
    expect(request.prompt).toContain('do not search $HOME')
    expect(request.prompt).toContain('repos/')
  })

  it('provisions the workflow repository into REPOS and anchors its branch', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const provision = vi.fn(async () => ({ path: '/runner-root/workspaces/mohist-pay-abc123', created: false }))
    const provisionForIssue = vi.fn(async () => ({
      path: '/runner-root/workspaces/mohist-pay-abc123',
      created: false,
    }))
    const executor = new AgentJobExecutor(
      connection.connection,
      makeAccessors(runtime.runtime),
      '/virtual/runner',
      undefined,
      { provision, provisionForIssue } as never,
    )

    const work = buildAgentJobWork({
      workflowRunId: 'wr-1',
      projectId: 'proj-1',
      variables: {
        workspace: { name: 'pay' },
        repository: {
          name: 'server',
          gitUrl: 'https://github.com/mohist/server.git',
          baseBranch: 'main',
        },
      },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(provisionForIssue).toHaveBeenCalledWith(
      'proj-1',
      'pay',
      'server',
      'https://github.com/mohist/server.git',
      'main',
      expect.any(AbortSignal),
      'wr-1',
      'aj-1',
    )
    const request = runtime.runTurnCalls[0]
    expect(request.target.workDir).toBe('/runner-root/workspaces/mohist-pay-abc123')
    expect(request.prompt).toContain('REPOS/server')
    expect(request.prompt).toContain('mohist/ws-pay')
  })

  it('rejects a dispatch that binds through the legacy workspace.path branch', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork({
      variables: {
        workspace: { path: '/legacy/path', branch: null, changeDir: null },
      },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('invalid-dispatch')
    expect(result.message).toContain("'workspace.name' to be a non-empty string")
    expect(result.message).toContain('workspace.path is not a Workspace binding')
    expect(runtime.runTurnCalls).toHaveLength(0)
  })

  it('fails with workspace-home-claimed when another runner owns the home', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const provision = vi.fn(async () => {
      throw new WorkspaceHomeClaimedError('already provisioned on runner-2')
    })
    const executor = new AgentJobExecutor(
      connection.connection,
      makeAccessors(runtime.runtime),
      '/virtual/runner',
      undefined,
      { provision } as never,
    )

    const work = buildAgentJobWork({
      projectId: 'proj-1',
      variables: { workspace: { name: 'pay' } },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('workspace-home-claimed')
    expect(runtime.runTurnCalls).toHaveLength(0)
  })

  it('fails with workspace-provisioning-failed when Workspace Home provisioning throws', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const provision = vi.fn(async () => {
      throw new Error('workspace Home provisioning failed: 500')
    })
    const executor = new AgentJobExecutor(
      connection.connection,
      makeAccessors(runtime.runtime),
      '/virtual/runner',
      undefined,
      { provision } as never,
    )

    const work = buildAgentJobWork({
      projectId: 'proj-1',
      variables: { workspace: { name: 'pay' } },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('workspace-provisioning-failed')
    expect(runtime.runTurnCalls).toHaveLength(0)
  })

  it('rejects a workspace object with neither name nor path', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork({ variables: { workspace: { repositories: [] } } })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('failed')
    expect(result.message).toMatch(/workspace\.name/)
    expect(runtime.runTurnCalls).toHaveLength(0)
  })
})
