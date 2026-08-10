import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, renderHook } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import { ProjectProvider } from '../../../entities/project'
import { useUnifiedSessionDataSource, type UnifiedSessionDataSourceDependencies } from './useUnifiedSessionDataSource'
import type { AgentSessionTranscriptResponse, SessionFollowupResult, SessionTurn, UnifiedSessionSummaryDto } from '../../../entities/coder-session'
import type { TurnControlResult } from '../../../entities/agent'

const TEST_PROJECT = {
  id: 'proj-1',
  name: 'Test',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  repositories: [],
}

function makeSummary(overrides: Partial<UnifiedSessionSummaryDto> = {}): UnifiedSessionSummaryDto {
  return {
    id: 'session-1',
    source: 'agent-launch',
    runtimeSessionId: 'runtime-1',
    runtime: 'opencode',
    activity: 'idle',
    createdAt: '2026-07-31T10:00:00.000Z',
    lastActivityAt: '2026-07-31T10:01:00.000Z',
    model: 'configured-model',
    resolvedModel: 'resolved-model',
    failureCategory: null,
    failureReason: null,
    toolCallCount: 0,
    toolErrorCount: 0,
    agentId: 'agent-1',
    agentName: 'Reviewer',
    contextRefs: null,
    usage: { contextWindowUsed: 100, contextWindowSize: 1000, contextUsagePercent: 10, healthStatus: 'healthy' },
    recoveryAvailable: true,
    currentTurnId: null,
    inputs: null,
    turns: null,
    ...overrides,
  }
}

interface CapturedFollowup { sessionId: string; text: string; attachments: string[] | undefined; idempotencyKey: string }
interface CapturedTurnControl { sessionId: string; turnId: string }

let followupSequence: SessionFollowupResult[] = []
const followupCalls: CapturedFollowup[] = []
let turnControlSequence: TurnControlResult[] = []
const turnControlCalls: CapturedTurnControl[] = []

const followupMock = {
  mutateAsync: vi.fn(async (input: CapturedFollowup): Promise<SessionFollowupResult> => {
    followupCalls.push(input)
    const next = followupSequence.length > 0
      ? followupSequence.shift()!
      : { status: 'accepted' as const, inputId: 'input-1', turnId: 'turn-1' }
    return next
  }),
  isPending: false,
}

const turnControlMock = {
  mutate: vi.fn((input: CapturedTurnControl, options?: { onSuccess?: (result: TurnControlResult) => void }) => {
    turnControlCalls.push(input)
    const next = turnControlSequence.length > 0 ? turnControlSequence.shift()! : { state: 'cancelled' }
    options?.onSuccess?.(next)
  }),
  isPending: false,
}

function makeDependencies(overrides: Partial<UnifiedSessionDataSourceDependencies> = {}): UnifiedSessionDataSourceDependencies {
  return {
    useSessionTranscript: (() => ({
      turns: [],
      transcriptVersion: 0,
      scrollToBottom: vi.fn(),
      newContentAvailable: false,
      setIsNearBottom: vi.fn(),
      isFinalizing: false,
      isThinking: false,
      isStreaming: false,
    })) as never,
    useUnifiedSessionSummary: (() => ({ data: makeSummary(), isLoading: false, isError: false })) as never,
    useUnifiedSessionTranscript: (() => ({ data: { turns: [], partCount: 0, lastActivityAt: null } as AgentSessionTranscriptResponse })) as never,
    useGenericFollowup: (() => followupMock) as never,
    useGenericTurnControl: (() => turnControlMock) as never,
    ...overrides,
  }
}

const queryClients: QueryClient[] = []

function createQueryClient() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } },
  })
}

function renderUnifiedHook(
  dependencies: UnifiedSessionDataSourceDependencies,
  initialEntry = '/sessions/session-1',
  qc?: QueryClient,
) {
  const queryClient = qc ?? createQueryClient()
  queryClients.push(queryClient)
  return {
    queryClient,
    ...renderHook(() => useUnifiedSessionDataSource(dependencies), {
      wrapper: ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>
          <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
            <MemoryRouter initialEntries={[initialEntry]}>
              <Routes>
                <Route path="/sessions/:sessionId" element={children} />
              </Routes>
            </MemoryRouter>
          </ProjectProvider>
        </QueryClientProvider>
      ),
    }),
  }
}

beforeEach(() => {
  followupSequence = []
  followupCalls.length = 0
  followupMock.mutateAsync.mockClear()
  turnControlSequence = []
  turnControlCalls.length = 0
  turnControlMock.mutate.mockClear()
})

afterEach(() => {
  cleanup()
  for (const qc of queryClients) qc.clear()
  queryClients.length = 0
})

describe('useUnifiedSessionDataSource — both Session sources', () => {
  it('drives follow-up and turn control for an agent-launch Session through the same canonical APIs', async () => {
    const deps = makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({
          activity: 'active',
          currentTurnId: 'turn-agent',
          turns: [{ id: 'turn-agent', sequence: 1, inputIds: [], status: 'queued' }],
        }),
        isLoading: false,
        isError: false,
      })) as never,
    })
    const { result } = renderUnifiedHook(deps)

    await act(async () => {
      await result.current.sendFollowup('hello')
    })
    expect(followupCalls[0].sessionId).toBe('session-1')

    act(() => {
      result.current.stop?.mutate()
    })
    expect(turnControlCalls[0].sessionId).toBe('session-1')
    expect(turnControlCalls[0]).toEqual({ sessionId: 'session-1', turnId: 'turn-agent' })
  })

  it('drives follow-up and turn control for a workflow Session through the same canonical APIs', async () => {
    const deps = makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({
          id: 'workflow-session-1',
          source: 'workflow',
          activity: 'active',
          currentTurnId: 'turn-workflow',
          agentId: null,
          agentName: null,
          workflowRunId: 'run-1',
          sessionName: 'build',
          contextRefs: { issueNumber: 42 },
          turns: [{ id: 'turn-workflow', sequence: 1, inputIds: [], status: 'executing' }],
        }),
        isLoading: false,
        isError: false,
      })) as never,
    })
    const { result } = renderUnifiedHook(deps, '/sessions/workflow-session-1')

    await act(async () => {
      await result.current.sendFollowup('continue workflow')
    })
    expect(followupCalls[0].sessionId).toBe('workflow-session-1')

    act(() => {
      result.current.stop?.mutate()
    })
    expect(turnControlCalls[0].sessionId).toBe('workflow-session-1')
    expect(turnControlCalls[0]).toEqual({ sessionId: 'workflow-session-1', turnId: 'turn-workflow' })
  })

  it('exposes one derived timeline and resolves semantic Issue references through project routing', () => {
    const transcriptTurn: SessionTurn = {
      id: 'turn-1',
      startedAt: '2026-07-31T10:00:00.000Z',
      completedAt: '2026-07-31T10:01:00.000Z',
      user: {
        role: 'mohist',
        text: 'Review the change',
        kind: 'task',
        sentAt: '2026-07-31T10:00:00.000Z',
      },
      assistant: [{
        id: 'part-1',
        type: 'tool',
        tool: {
          toolCallId: 'tool-1',
          toolName: 'bash',
          status: 'completed',
          rawInput: 'mo issue start 42',
          rawOutput: 'ok',
          completedAt: '2026-07-31T10:01:00.000Z',
          startedAt: '2026-07-31T10:00:30.000Z',
        },
      }],
    }
    const { result } = renderUnifiedHook(makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({
          activity: 'idle',
          contextRefs: { issueNumber: 42 },
          inputs: [{ id: 'input-1', sequence: 1, source: 'web', acceptance: 'accepted' }],
          turns: [{ id: 'turn-1', sequence: 1, inputIds: ['input-1'], status: 'completed' }],
        }),
        isLoading: false,
        isError: false,
      })) as never,
      useSessionTranscript: (() => ({
        turns: [transcriptTurn],
        liveDetails: [],
        transcriptVersion: 0,
        scrollToBottom: vi.fn(),
        newContentAvailable: false,
        setIsNearBottom: vi.fn(),
        isFinalizing: false,
        isThinking: false,
        isStreaming: false,
      })) as never,
    }))

    expect(result.current.facts?.map((fact) => fact.sourceId)).toEqual(expect.arrayContaining(['input:input-1', 'part:part-1', 'summary:activity']))
    expect(result.current.items?.some((item) => item.renderClass === 'domain-action')).toBe(true)
    expect(result.current.entries?.length).toBeGreaterThan(0)
    expect(result.current.currentActivity).toMatchObject({ state: 'idle', label: '空闲' })
    expect(result.current.resolveTimelineReference?.({ kind: 'issue', label: 'Issue #42', issueNumber: 42 })).toBe('/Test/issues/42')
    expect(result.current.resolveTimelineReference?.({ kind: 'workflow', label: 'Workflow', workflowRunId: 'run-1' })).toBeNull()
  })
})
