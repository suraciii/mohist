import { describe, expect, it, vi } from 'vitest'
import type { SessionCommandJournalStore } from '../runtime/session-command-journal.js'
import { createRunnerControlHandlers } from './runner-control-handlers.js'

describe('createRunnerControlHandlers', () => {
  it('binds all nine methods to the existing transport-neutral domain handlers', async () => {
    const command = vi.fn(async () => ({ ok: true }))
    const journal = {
      load: vi.fn(async () => {}),
      get: vi.fn(async () => null),
      start: vi.fn(async () => {}),
      complete: vi.fn(async () => {}),
    } as unknown as SessionCommandJournalStore
    const statusChanged = vi.fn()
    const handlers = createRunnerControlHandlers({
      workspaceGit: {
        resolveQuery: () => null,
        allowUnverifiedWorkspaceQueriesForTest: true,
      },
      workspaceRemoval: { runnerRoot: '/runner' },
      followup: {},
      cancel: {},
      sessionCommand: { handler: command, journal },
      onWorkflowStatusChanged: statusChanged,
    })
    const query = {}

    await expect(handlers.workspaceDiff(query)).resolves.toBeNull()
    await expect(handlers.workspaceCommits(query)).resolves.toBeNull()
    await expect(handlers.workspaceCommitDiff(query, 'abc')).resolves.toBeNull()
    await expect(handlers.workspaceStatus(query)).resolves.toEqual({ exists: false })
    await expect(handlers.workspaceFileContent(query, 'a.ts')).resolves.toEqual({ base: null, head: null })
    await expect(handlers.workspaceRemove(query)).resolves.toMatchObject({ status: 'missing' })
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
      }),
    ).resolves.toEqual({ ok: true })
    await handlers.workflowStatusChanged({ workflowRunId: 'run', status: 'Completed' })
    expect(command).toHaveBeenCalledOnce()
    expect(statusChanged).toHaveBeenCalledOnce()
  })
})
