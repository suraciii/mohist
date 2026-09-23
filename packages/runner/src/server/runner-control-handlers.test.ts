import { describe, expect, it, vi } from 'vitest'
import { createRunnerControlHandlers } from './runner-control-handlers.js'

const probeParams = {
  sessionId: 'session',
  observationId: 'observation',
  runnerId: 'runner',
  runtime: 'opencode',
  runtimeSessionId: 'runtime',
  workDir: '/work',
  bindingEpoch: 0,
  contextGeneration: 1,
} as const

describe('createRunnerControlHandlers', () => {
  it('binds all ten methods to the existing transport-neutral domain handlers', async () => {
    const command = vi.fn(async () => ({ ok: true }))
    const admittedWorkDirs: string[] = []
    const withWorkspaceUse = async <T>(workDir: string, work: () => Promise<T>): Promise<T> => {
      admittedWorkDirs.push(workDir)
      return await work()
    }
    const resolveSession = vi.fn(async () => ({
      ok: true,
      value: { runtimeSessionId: 'runtime', workDir: '/work', activeTurn: false },
      diagnostics: [],
    }))
    const handlers = createRunnerControlHandlers({
      workspaceGit: {
        resolveQuery: () => null,
        allowUnverifiedWorkspaceQueriesForTest: true,
      },
      workspaceRemoval: { runnerRoot: '/runner' },
      followup: {},
      cancel: {},
      sessionCommand: { handler: command },
      withWorkspaceUse,
      sessionProbe: {
        runnerId: 'runner',
        enabledRuntimes: new Set(['opencode'] as const),
        openCode: { ready: () => true, resolveSession } as never,
      },
    })
    const query = {}

    await expect(handlers.workspaceDiff(query)).resolves.toBeNull()
    await expect(handlers.workspaceCommits(query)).resolves.toBeNull()
    await expect(handlers.workspaceCommitDiff(query, 'abc')).resolves.toBeNull()
    await expect(handlers.workspaceStatus(query)).resolves.toEqual({ exists: false })
    await expect(handlers.workspaceFileContent(query, 'a.ts')).resolves.toEqual({ base: null, head: null })
    await expect(handlers.workspaceRemove(query)).resolves.toMatchObject({
      status: 'unsafe',
      reason: 'workspace_identity_mismatch',
    })
    await expect(handlers.sessionFollowup({ text: 'next', operationId: 'followup', turnId: 'turn' })).resolves.toEqual({
      accepted: false,
      error: 'unavailable',
    })
    await expect(
      handlers.sessionStop({
        target: {
          kind: 'generic',
          projectId: 'project',
          sessionId: 'session',
          binding: { runtime: 'opencode', runtimeSessionId: 'runtime', runnerId: 'runner', workDir: '/work' },
        },
        sessionId: 'session',
        turnId: 'turn',
        operationId: 'stop',
      }),
    ).resolves.toEqual({ state: 'unavailable' })
    await expect(
      handlers.sessionCommand({
        sessionId: 'session',
        runtime: 'opencode',
        runtimeSessionId: 'runtime',
        runnerId: 'runner',
        workDir: '/work',
        command: 'compact',
        operationId: 'command',
        processGeneration: 'generation',
      }),
    ).resolves.toEqual({ ok: true })
    expect(command).toHaveBeenCalledOnce()
    await expect(handlers.sessionProbe(probeParams)).resolves.toEqual({
      probe: probeParams,
      observation: 'idle',
    })
    expect(resolveSession).toHaveBeenCalledWith({
      target: { runtime: 'opencode', runtimeSessionId: 'runtime', workDir: '/work' },
    })
    expect(admittedWorkDirs).toContain('/work')
  })
})
