import { join } from 'node:path'
import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { describe, expect, it as vitestIt, vi } from 'vitest'
import { AgentJobExecutor } from './agent-job-executor.js'
import { PUBLISHED_SLACK_SKILL_NAME, PUBLISHED_SLACK_SKILL_VERSION } from './slack-execution-context.js'
import type { DispatchWorkItem } from '../core/types.js'
import { MemoryFileSystem } from '../../tests/support/memory-filesystem.js'
import { withTestRunnerResources } from '../../tests/support/test-resources.js'

function it(name: string, body: (fileSystem: MemoryFileSystem) => Promise<void>): void {
  vitestIt(name, async () => {
    const fileSystem = new MemoryFileSystem()
    try {
      await withTestRunnerResources(async () => await body(fileSystem), { fileSystem })
    } finally {
      await fileSystem.deleteDirectory('/')
      if (fileSystem.exists('/')) throw new Error('agent job test filesystem was not cleaned up')
    }
  })
}

describe('AgentJobExecutor attachment delivery', () => {
  it('delivers an attachment-only input to the runtime as a readable workspace file', async (fileSystem) => {
    const workDir = '/virtual/mohist-agent-job-attachment'
    const runTurn = vi.fn(async (request: { prompt: string; fileParts?: readonly unknown[] }) => ({
      ok: true as const,
      value: {
        facts: {
          finalAssistantText: 'received',
          runtimeSessionId: 'runtime-1',
          workDir,
        },
        diagnostics: [],
      },
      diagnostics: [],
    }))
    const connection = {
      runnerId: 'runner-1',
      getAgentSession: vi.fn(async () => ({ runtime: 'opencode', runtimeSessionId: null, workDir })),
      openAgentInputAttachment: vi.fn(async () => ({
        bytes: new TextEncoder().encode('attachment contents'),
        contentType: 'text/plain',
        contentDisposition: null,
      })),
      openAgentSession: vi.fn(async () => null),
      attachAgentSession: vi.fn(async () => null),
      agentSessionRuntimeEvents: vi.fn(async () => []),
    }
    const runtime = {
      ready: () => true,
      diagnostic: () => null,
      runTurn,
    }
    const work: DispatchWorkItem = {
      workflowRunId: '',
      workId: 'work-1',
      workType: 'agent-job',
      ownerKind: 'agent-job',
      projectId: 'project-1',
      agentJobId: 'job-1',
      agentSessionId: 'session-1',
      initialInputId: 'input-1',
      initialTurnId: 'turn-1',
      variables: { workspace: { path: workDir } },
      with: {
        runtime: 'opencode',
        attachments: [{ id: 'attachment-1', name: 'notes.txt', contentType: 'text/plain', size: 19 }],
      },
    }

    const result = await new AgentJobExecutor(
      connection as never,
      { openCode: runtime as never, pi: null },
      workDir,
    ).execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(connection.openAgentInputAttachment).toHaveBeenCalledWith(
      'project-1',
      'session-1',
      'input-1',
      'attachment-1',
      expect.any(AbortSignal),
    )
    expect(runTurn).toHaveBeenCalledOnce()
    const request = runTurn.mock.calls[0]?.[0]
    expect(request.prompt).toContain('[mohist-attachments]')
    expect(request.prompt).toContain('notes.txt')
    expect(request.prompt).not.toContain('Please read')
    expect(request.fileParts).toBeUndefined()
    expect(await fileSystem.readText(join(workDir, '.mohist/attachments/input-1/attachment-1/notes.txt'))).toBe(
      'attachment contents',
    )
  })

  it('passes delivered images to the OpenCode runtime as native file parts', async (fileSystem) => {
    const workDir = '/virtual/mohist-agent-job-image'
    const runTurn = vi.fn(async (request: { prompt: string; fileParts?: readonly unknown[] }) => ({
      ok: true as const,
      value: {
        facts: {
          finalAssistantText: 'received',
          runtimeSessionId: 'runtime-1',
          workDir,
        },
        diagnostics: [],
      },
      diagnostics: [],
    }))
    const connection = {
      runnerId: 'runner-1',
      getAgentSession: vi.fn(async () => ({ runtime: 'opencode', runtimeSessionId: null, workDir })),
      openAgentInputAttachment: vi.fn(async () => ({
        bytes: new Uint8Array([1, 2, 3]),
        contentType: 'image/png',
        contentDisposition: null,
      })),
      openAgentSession: vi.fn(async () => null),
      attachAgentSession: vi.fn(async () => null),
      agentSessionRuntimeEvents: vi.fn(async () => []),
    }
    const runtime = {
      ready: () => true,
      diagnostic: () => null,
      runTurn,
    }
    const work: DispatchWorkItem = {
      workflowRunId: '',
      workId: 'work-1',
      workType: 'agent-job',
      ownerKind: 'agent-job',
      projectId: 'project-1',
      agentJobId: 'job-1',
      agentSessionId: 'session-1',
      initialInputId: 'input-1',
      initialTurnId: 'turn-1',
      variables: { workspace: { path: workDir } },
      with: {
        prompt: 'inspect the image',
        runtime: 'opencode',
        attachments: [{ id: 'attachment-1', name: 'diagram.png', contentType: 'image/png', size: 3 }],
      },
    }

    const result = await new AgentJobExecutor(
      connection as never,
      { openCode: runtime as never, pi: null },
      workDir,
    ).execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(runTurn.mock.calls[0]?.[0].fileParts).toEqual([
      {
        mime: 'image/png',
        filename: 'diagram.png',
        url: 'data:image/png;base64,AQID',
      },
    ])
  })
})

describe('AgentJobExecutor transport metadata classification', () => {
  it('does not surface executionSource as unknown Pi options', async (fileSystem) => {
    const workDir = '/virtual/mohist-agent-job-pi-source'
    const runTurn = vi.fn(async (_request: { prompt: string; options?: { unknownKeys?: readonly string[] } }) => ({
      ok: true as const,
      value: {
        facts: { finalAssistantText: 'ok', runtimeSessionId: 'rt-1', workDir },
        diagnostics: [],
      },
      diagnostics: [],
    }))
    const connection = {
      runnerId: 'runner-1',
      getAgentSession: vi.fn(async () => ({ runtime: 'pi', runtimeSessionId: 'rt-existing', workDir })),
      openAgentSession: vi.fn(async () => null),
      attachAgentSession: vi.fn(async () => null),
      agentSessionRuntimeEvents: vi.fn(async () => []),
    }
    const piRuntime = {
      ready: () => true,
      diagnostic: () => null,
      createSession: vi.fn(),
      runTurn,
    }
    const work: DispatchWorkItem = {
      workflowRunId: '',
      workId: 'work-2',
      workType: 'agent-job',
      ownerKind: 'agent-job',
      projectId: 'project-1',
      agentJobId: 'job-2',
      agentSessionId: 'session-1',
      initialInputId: 'input-1',
      initialTurnId: 'turn-1',
      with: { prompt: 'PI_MIGRATION_SMOKE_OK', runtime: 'pi', executionSource: 'non-slack' },
    }

    const result = await new AgentJobExecutor(
      connection as never,
      { openCode: null, pi: piRuntime as never },
      workDir,
    ).execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(runTurn).toHaveBeenCalledOnce()
    expect(runTurn.mock.calls[0]?.[0].options?.unknownKeys).toBeUndefined()
  })

  it('does not surface a Slack execution source with context as unknown OpenCode options', async (fileSystem) => {
    const workDir = '/virtual/mohist-agent-job-slack-source'
    const runTurn = vi.fn(async (_request: { prompt: string; options?: { unknownKeys?: readonly string[] } }) => ({
      ok: true as const,
      value: {
        facts: { finalAssistantText: 'ok', runtimeSessionId: 'rt-2', workDir },
        diagnostics: [],
      },
      diagnostics: [],
    }))
    const connection = {
      runnerId: 'runner-1',
      getAgentSession: vi.fn(async () => ({ runtime: 'opencode', runtimeSessionId: 'rt-existing', workDir })),
      openAgentSession: vi.fn(async () => null),
      attachAgentSession: vi.fn(async () => null),
      agentSessionRuntimeEvents: vi.fn(async () => []),
    }
    const openCodeRuntime = {
      ready: () => true,
      diagnostic: () => null,
      runTurn,
    }
    const work: DispatchWorkItem = {
      workflowRunId: '',
      workId: 'work-3',
      workType: 'agent-job',
      ownerKind: 'agent-job',
      projectId: 'project-1',
      agentJobId: 'job-3',
      agentSessionId: 'session-1',
      initialInputId: 'input-1',
      initialTurnId: 'turn-1',
      with: {
        prompt: 'reply in thread',
        runtime: 'opencode',
        executionSource: 'slack',
        slackExecutionContext: slackExecutionContext(),
      },
    }

    const result = await new AgentJobExecutor(
      connection as never,
      { openCode: openCodeRuntime as never, pi: null },
      workDir,
    ).execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(runTurn).toHaveBeenCalledOnce()
    expect(runTurn.mock.calls[0]?.[0].options?.unknownKeys).toBeUndefined()
  })

  it('still forwards genuinely unknown payload keys as unknown options', async (fileSystem) => {
    const workDir = '/virtual/mohist-agent-job-rogue-key'
    const runTurn = vi.fn(async (_request: { prompt: string; options?: { unknownKeys?: readonly string[] } }) => ({
      ok: true as const,
      value: {
        facts: { finalAssistantText: 'ok', runtimeSessionId: 'rt-3', workDir },
        diagnostics: [],
      },
      diagnostics: [],
    }))
    const connection = {
      runnerId: 'runner-1',
      getAgentSession: vi.fn(async () => ({ runtime: 'pi', runtimeSessionId: 'rt-existing', workDir })),
      openAgentSession: vi.fn(async () => null),
      attachAgentSession: vi.fn(async () => null),
      agentSessionRuntimeEvents: vi.fn(async () => []),
    }
    const piRuntime = {
      ready: () => true,
      diagnostic: () => null,
      createSession: vi.fn(),
      runTurn,
    }
    const work: DispatchWorkItem = {
      workflowRunId: '',
      workId: 'work-4',
      workType: 'agent-job',
      ownerKind: 'agent-job',
      projectId: 'project-1',
      agentJobId: 'job-4',
      agentSessionId: 'session-1',
      initialInputId: 'input-1',
      initialTurnId: 'turn-1',
      with: { prompt: 'hi', runtime: 'pi', executionSource: 'non-slack', rogueKey: 'x' },
    }

    const result = await new AgentJobExecutor(
      connection as never,
      { openCode: null, pi: piRuntime as never },
      workDir,
    ).execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(runTurn).toHaveBeenCalledOnce()
    expect(runTurn.mock.calls[0]?.[0].options?.unknownKeys).toEqual(['rogueKey'])
  })
})

function slackExecutionContext() {
  const instructions = readFileSync(
    new URL(
      '../../../server/src/Mohist.Server/Agent/Services/Assets/mohist-slack-collaboration.skill.md',
      import.meta.url,
    ),
    'utf8',
  )
  return {
    version: 1,
    replyAnchor: {
      workspaceId: 'T1',
      conversationId: 'C1',
      threadRootMessageId: '100.0',
      triggeringMessageId: '101.0',
      initiatingMemberId: 'U1',
      connectionId: 'connection_1',
      sessionId: 'session_1',
      dispatchRef: 'dispatch_1',
    },
    collaborationSkill: {
      name: PUBLISHED_SLACK_SKILL_NAME,
      version: PUBLISHED_SLACK_SKILL_VERSION,
      instructions,
      contentHash: createHash('sha256').update(instructions, 'utf8').digest('hex'),
    },
  }
}
