import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { SessionDetailShell } from '../src/pages/session/ui/SessionDetailShell'
import type { UnifiedSessionDataSourceResult } from '../src/pages/session/data/useUnifiedSessionDataSource'

function makeData(sessionKey: string): UnifiedSessionDataSourceResult {
  return {
    isLoading: false,
    isError: false,
    notFound: false,
    sessionKey,
    runtimeSessionId: `runtime-${sessionKey}`,
    meta: {
      sessionId: `session-${sessionKey}`,
      sessionName: sessionKey,
      runtimeSessionId: `runtime-${sessionKey}`,
      executionId: null,
      title: `${sessionKey} session`,
      source: 'agent-launch',
      model: 'model',
      stage: 'build',
      createdAt: '2026-06-15T10:00:00.000Z',
      completedAt: null,
      lastActivityAt: '2026-06-15T11:30:00.000Z',
      lastDataAt: '2026-06-15T11:30:00.000Z',
    },
    transcriptResponse: null,
    transcriptView: 'public',
    setTranscriptView: vi.fn(),
    transcriptViewLoading: false,
    launchObservation: null,
    initialTurns: [],
    statusKind: 'active',
    isRunning: true,
    canFollowup: true,
    supportsInputAttachments: true,
    projectId: 'project-1',
    followupIsPending: false,
    followupStatus: null,
    sendFollowup: vi.fn(async () => ({ status: 'accepted' as const })),
    stop: null,
    contextWindowUsed: null,
    contextWindowSize: null,
    contextUsagePercent: null,
    healthStatus: null,
    hasRecoveryActions: false,
    recoveryAvailable: false,
    recoverySessionName: sessionKey,
    recoverySessionId: sessionKey,
    recoveryHistory: null,
    metadataQueryKey: ['unified-session', 'project-1', `session-${sessionKey}`] as const,
    transcriptQueryKey: [
      'unified-session',
      'project-1',
      `session-${sessionKey}`,
      'transcript',
      `runtime-${sessionKey}`,
      'public',
    ] as const,
    handleRecoverySuccess: () => {},
    backPath: '/issues/123',
    backLabel: 'Issue #123',
    workflowContextPath: undefined,
    workflowContextLabel: undefined,
    sessionTurns: [],
    transcriptVersion: 0,
    scrollToBottom: () => {},
    newContentAvailable: false,
    setIsNearBottom: () => {},
    isFinalizing: false,
    isThinking: false,
    isStreaming: false,
    facts: [],
    items: [],
    entries: [],
    currentActivity: { state: 'unknown', label: '状态未知' },
    resolveTimelineReference: () => null,
    issueNumber: 123,
  }
}

function renderShell(data: UnifiedSessionDataSourceResult) {
  return render(
    <MemoryRouter>
      <SessionDetailShell
        data={data}
        components={{
          SessionTranscriptLayout: () => <div />,
          SessionRecoveryActions: () => <div />,
          ContextHealthBar: () => <div />,
          CompactionLineageLink: () => <div />,
        }}
      />
    </MemoryRouter>,
  )
}

describe('SessionDetailShell followup queue', () => {
  it('does not carry a queued followup into a different session', async () => {
    const firstSession = makeData('build')
    const page = renderShell(firstSession)

    fireEvent.change(screen.getByTestId('session-followup-input'), { target: { value: 'Continue' } })
    fireEvent.click(screen.getByTestId('session-followup-send'))

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-state', 'queued')
    })

    page.rerender(
      <MemoryRouter>
        <SessionDetailShell
          data={makeData('check')}
          components={{
            SessionTranscriptLayout: () => <div />,
            SessionRecoveryActions: () => <div />,
            ContextHealthBar: () => <div />,
            CompactionLineageLink: () => <div />,
          }}
        />
      </MemoryRouter>,
    )

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-state', 'interactive')
    })
    expect(screen.getByTestId('session-followup-input')).not.toBeDisabled()
  })
})
