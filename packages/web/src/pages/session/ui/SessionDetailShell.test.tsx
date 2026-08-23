import '@testing-library/jest-dom'
import { fireEvent, render, screen, cleanup } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SessionMetadata } from '../../../entities/coder-session'
import type { TimelineFact, TimelineItem } from '../../../entities/session'
import type { UnifiedSessionDataSourceResult } from '../data/useUnifiedSessionDataSource'
import { SessionDetailShell, type SessionDetailShellComponents } from './SessionDetailShell'

const sourceId = 'part:tool-1'

const fact: TimelineFact = {
  sourceId,
  source: 'transcript',
  order: 1,
  occurredAt: '2026-08-03T10:00:00.000Z',
  kind: 'tool',
  raw: { sourceId, command: 'mo issue start 42' },
}

const item: TimelineItem = {
  id: 'tool-1',
  sourceIds: [sourceId],
  occurredAt: fact.occurredAt,
  renderClass: 'domain-action',
  summary: '启动了 Issue #42',
  salience: 'high',
  isTerminal: true,
}

function makeData(): UnifiedSessionDataSourceResult {
  const meta: SessionMetadata = {
    sessionId: 'session-1',
    sessionName: 'Session one',
    source: 'agent-launch',
    agentId: 'agent-1',
    agentName: 'Reviewer',
    workflowRunId: null,
    runtimeSessionId: 'runtime-1',
    runtime: 'opencode',
    executionId: null,
    title: 'Session one',
    activity: 'idle',
    model: 'model',
    stage: null,
    createdAt: '2026-08-03T09:00:00.000Z',
    completedAt: null,
    lastActivityAt: fact.occurredAt,
    failureReason: null,
    inputs: [{ id: 'input-1', sequence: 1, source: 'web', acceptance: 'accepted' }],
    turns: [{ id: 'turn-1', sequence: 1, inputIds: ['input-1'], status: 'completed' }],
    recoveryHistory: [{ type: 'reset', recordedAt: '2026-08-03T10:01:00.000Z', reason: 'reset' }],
  }

  const data: UnifiedSessionDataSourceResult = {
    isLoading: false,
    isError: false,
    notFound: false,
    sessionKey: 'session-1',
    runtimeSessionId: 'runtime-1',
    meta,
    transcriptResponse: null,
    transcriptView: 'public',
    setTranscriptView: (view) => {
      data.transcriptView = typeof view === 'function' ? view(data.transcriptView) : view
    },
    transcriptViewLoading: false,
    launchObservation: null,
    initialTurns: [],
    statusKind: 'idle',
    isRunning: false,
    canFollowup: true,
    followupIsPending: false,
    followupStatus: null,
    sendFollowup: vi.fn(),
    supportsInputAttachments: false,
    projectId: 'project-1',
    stop: null,
    contextWindowUsed: null,
    contextWindowSize: null,
    contextUsagePercent: null,
    healthStatus: null,
    hasRecoveryActions: false,
    recoveryAvailable: false,
    recoverySessionName: 'Session one',
    recoverySessionId: 'session-1',
    recoveryHistory: meta.recoveryHistory ?? null,
    metadataQueryKey: ['unified-session', 'project-1', 'session-1'] as const,
    transcriptQueryKey: ['unified-session', 'project-1', 'session-1', 'transcript', 'runtime-1', 'public'] as const,
    handleRecoverySuccess: vi.fn(),
    backPath: '/agents',
    backLabel: 'Agents',
    workflowContextPath: undefined,
    workflowContextLabel: undefined,
    sessionTurns: [],
    transcriptVersion: 0,
    scrollToBottom: vi.fn(),
    newContentAvailable: false,
    setIsNearBottom: vi.fn(),
    isFinalizing: false,
    isThinking: false,
    isStreaming: false,
    facts: [fact],
    items: [item],
    entries: [item],
    currentActivity: { state: 'idle', label: '空闲' },
    resolveTimelineReference: () => '/Project/issues/42',
    issueNumber: 42,
  }
  return data
}

function makeComponents(): Partial<SessionDetailShellComponents> {
  return {
    SessionTranscriptLayout: ((props: any) => {
      const values =
        props.viewMode === 'raw'
          ? props.facts.map((value: TimelineFact) => value.sourceId)
          : props.entries.map((value: TimelineItem) => value.sourceIds[0])
      return (
        <div data-testid="timeline-fixture" data-view={props.viewMode}>
          {values.map((value: string) => (
            <div key={value} data-timeline-source-id={value}>
              {value}
            </div>
          ))}
        </div>
      )
    }) as SessionDetailShellComponents['SessionTranscriptLayout'],
    SessionFollowupComposer: (() => (
      <div data-testid="followup-fixture" />
    )) as SessionDetailShellComponents['SessionFollowupComposer'],
  }
}

describe('SessionDetailShell timeline integration', () => {
  afterEach(() => cleanup())

  it('removes duplicated input and recovery first-screen regions after timeline facts cover them', () => {
    render(
      <MemoryRouter>
        <SessionDetailShell data={makeData()} components={makeComponents()} />
      </MemoryRouter>,
    )

    expect(screen.getByTestId('timeline-fixture')).toHaveTextContent(sourceId)
    expect(screen.queryByTestId('session-input-turn-evidence')).not.toBeInTheDocument()
    expect(screen.queryByTestId('session-recovery-history')).not.toBeInTheDocument()
  })

  it('switches between summary and raw views on the same source anchor', () => {
    const rect = { top: 0, bottom: 100, left: 0, right: 100, width: 100, height: 100, x: 0, y: 0, toJSON: () => ({}) }
    const rectSpy = vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(rect as DOMRect)
    const scrollSpy = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(() => {})

    const data = makeData()
    const { rerender } = render(
      <MemoryRouter>
        <SessionDetailShell data={data} components={makeComponents()} />
      </MemoryRouter>,
    )

    fireEvent.click(screen.getByTestId('session-timeline-raw-trigger'))
    rerender(
      <MemoryRouter>
        <SessionDetailShell data={data} components={makeComponents()} />
      </MemoryRouter>,
    )

    expect(screen.getByTestId('timeline-fixture')).toHaveAttribute('data-view', 'raw')
    expect(scrollSpy).toHaveBeenCalledTimes(1)
    expect(scrollSpy.mock.instances[0]).toHaveAttribute('data-timeline-source-id', sourceId)

    rectSpy.mockRestore()
    scrollSpy.mockRestore()
  })
})
