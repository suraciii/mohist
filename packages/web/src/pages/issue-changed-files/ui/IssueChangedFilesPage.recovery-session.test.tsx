import { describe, expect, it, vi } from 'vitest'
import { act } from '@testing-library/react'
import {
  fireEvent,
  screen,
  useIssueChangedFilesPageFixture,
  waitFor,
} from './IssueChangedFilesPage.fixture'

const { renderPage, state } = useIssueChangedFilesPageFixture()

async function flushQueryNotifications() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1000)
  })
}

describe('IssueChangedFilesPage related-session recovery', () => {
  it('renders the session link targeting the project-scoped session route when a session is resolved', async () => {
    state.diffData = { available: false, reason: 'runner_unavailable', message: '' }
    renderPage()
    await screen.findByTestId('issue-files-recovery-surface')
    const sessionLink = await screen.findByTestId('issue-files-recovery-session')
    expect(sessionLink).toBeTruthy()
    expect(screen.getByTestId('issue-files-recovery-retry')).toBeTruthy()
    expect(screen.getByTestId('issue-files-recovery-return')).toBeTruthy()
    fireEvent.click(sessionLink)
    await waitFor(() => {
      expect(screen.getByTestId('current-path').textContent)
        .toBe('/Test%20Project/sessions/session-1')
    })
    expect(screen.getByTestId('session-page-stub')).toBeTruthy()
  })

  it('encodes the session name in the link path (encodeURIComponent)', async () => {
    state.diffData = { available: false, reason: 'runner_unavailable', message: '' }
    state.sessionsData = [{
      ...(state.sessionsData[0] as Record<string, unknown>),
      status: 'running',
      sessionName: 'session with spaces & symbols/abc',
    }]
    renderPage()
    await screen.findByTestId('issue-files-recovery-surface')
    const sessionLink = await screen.findByTestId('issue-files-recovery-session')
    fireEvent.click(sessionLink)
    await waitFor(() => {
      expect(screen.getByTestId('current-path').textContent)
        .toBe('/Test%20Project/sessions/session-1')
    })
  })

  it('does not enable the workflow-run sessions query when there is no workflowRunId', async () => {
    state.issueError = true
    state.issueData = undefined
    renderPage()
    await screen.findByTestId('issue-files-recovery-surface')
    await waitFor(() => {
      expect(state.issueRequestCount).toBe(1)
      expect(state.diffRequestCount).toBe(1)
      expect(state.commitsRequestCount).toBe(1)
    })
    expect(state.sessionsRequestCount).toBe(0)
    expect(state.sessionsResponseCount).toBe(0)
    expect(screen.queryByTestId('issue-files-recovery-session')).toBeNull()
    expect(screen.getByTestId('issue-files-recovery-retry')).toBeTruthy()
    expect(screen.getByTestId('issue-files-recovery-return')).toBeTruthy()
  })

  it('omits the session link when no session is resolved', async () => {
    state.diffData = { available: false, reason: 'runner_unavailable', message: '' }
    state.sessionsData = []
    state.blockSessions = true
    renderPage()
    await screen.findByTestId('issue-files-recovery-surface')
    await waitFor(() => expect(state.sessionsRequestCount).toBe(1))
    await state.releaseSessionResponses()
    expect(state.sessionsResponseCount).toBe(1)
    vi.useFakeTimers()
    try {
      await flushQueryNotifications()
      expect(state.getSessionsQueryStatus()).toBe('success')
    } finally {
      vi.useRealTimers()
    }
    expect(screen.queryByTestId('issue-files-recovery-session')).toBeNull()
    expect(screen.getByTestId('issue-files-recovery-retry')).toBeTruthy()
    expect(screen.getByTestId('issue-files-recovery-return')).toBeTruthy()
  })

  it.each(['completed', 'failed', 'cancelled'])('opens a known %s session', async (status) => {
    state.diffData = { available: false, reason: 'runner_unavailable', message: '' }
    state.sessionsData = [{
      ...(state.sessionsData[0] as Record<string, unknown>),
      status,
      sessionName: `${status}-session`,
    }]
    renderPage()
    await screen.findByTestId('issue-files-recovery-surface')
    fireEvent.click(await screen.findByTestId('issue-files-recovery-session'))
    await waitFor(() => {
      expect(screen.getByTestId('current-path').textContent)
        .toBe(`/Test%20Project/sessions/session-1`)
    })
    expect(screen.getByTestId('session-page-stub')).toBeTruthy()
  })

  it('prefers a live session over an earlier terminal session', async () => {
    state.diffData = { available: false, reason: 'runner_unavailable', message: '' }
    state.sessionsData = [
      {
        ...(state.sessionsData[0] as Record<string, unknown>),
        status: 'completed',
        activity: 'idle',
        sessionName: 'terminal-session',
        createdAt: '2026-07-10T00:00:00.000Z',
      },
      {
        ...(state.sessionsData[0] as Record<string, unknown>),
        id: 'session-2',
        status: 'running',
        activity: 'active',
        sessionName: 'live-session',
        createdAt: '2026-07-12T00:00:00.000Z',
      },
    ]
    renderPage()
    await screen.findByTestId('issue-files-recovery-surface')
    fireEvent.click(await screen.findByTestId('issue-files-recovery-session'))
    await waitFor(() => {
      expect(screen.getByTestId('current-path').textContent)
        .toBe('/Test%20Project/sessions/session-2')
    })
  })

  it('selects the earliest known session when none are live', async () => {
    state.diffData = { available: false, reason: 'runner_unavailable', message: '' }
    state.sessionsData = [
      {
        ...(state.sessionsData[0] as Record<string, unknown>),
        status: 'failed',
        sessionName: 'later-session',
        createdAt: '2026-07-12T00:00:00.000Z',
      },
      {
        ...(state.sessionsData[0] as Record<string, unknown>),
        id: 'session-2',
        status: 'completed',
        sessionName: 'earlier-session',
        createdAt: '2026-07-10T00:00:00.000Z',
      },
    ]
    renderPage()
    await screen.findByTestId('issue-files-recovery-surface')
    fireEvent.click(await screen.findByTestId('issue-files-recovery-session'))
    await waitFor(() => {
      expect(screen.getByTestId('current-path').textContent)
        .toBe('/Test%20Project/sessions/session-2')
    })
  })
})
