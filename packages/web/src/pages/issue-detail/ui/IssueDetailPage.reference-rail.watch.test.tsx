import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, screen, waitFor, within } from '@testing-library/react'
import { DEFAULT_RECOVERY, mockMatchMedia, makeIssue, renderPage } from './_issueDetailReferenceRailTestUtils'
import { mockIssue, mountIssueDetail } from './_issueDetailMsw'

mountIssueDetail({ issue: makeIssue() })

beforeEach(() => {
  mockMatchMedia(false)
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('IssueDetailPage reference-rail — watching and muted read-only cards', () => {
  it('renders the watching card when the issue has watching entries', async () => {
    mockIssue(makeIssue({
      watching: [
        { agentId: 'agent_watch_1', state: 'watching', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z' },
        { agentId: 'agent_watch_2', state: 'watching', createdAt: '2026-01-02T00:00:00Z', updatedAt: '2026-01-02T00:00:00Z' },
      ],
      muted: [
        { agentId: 'agent_muted_1', state: 'muted', createdAt: '2026-01-03T00:00:00Z', updatedAt: '2026-01-03T00:00:00Z' },
      ],
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const watchingCard = await waitFor(() => screen.getByTestId('reference-rail-watching'))
    expect(watchingCard.dataset.collapsed).toBe('false')
    const watchingBody = within(watchingCard).getByTestId('reference-rail-watching-body')
    expect(within(watchingBody).getAllByTestId('issue-watch-watching-entry')).toHaveLength(2)
    expect(within(watchingBody).getByText('agent_watch_1')).toBeTruthy()
    expect(within(watchingBody).getByText('agent_watch_2')).toBeTruthy()

    const mutedCard = await waitFor(() => screen.getByTestId('reference-rail-muted'))
    expect(mutedCard.dataset.collapsed).toBe('false')
    const mutedBody = within(mutedCard).getByTestId('reference-rail-muted-body')
    expect(within(mutedBody).getAllByTestId('issue-watch-muted-entry')).toHaveLength(1)
    expect(within(mutedBody).getByText('agent_muted_1')).toBeTruthy()
  })

  it('does not render the watching/muted cards when the issue has no entries', async () => {
    mockIssue(makeIssue({ recovery: DEFAULT_RECOVERY }))

    renderPage()

    await waitFor(() => screen.getByTestId('reference-rail'))
    expect(screen.queryByTestId('reference-rail-watching')).toBeNull()
    expect(screen.queryByTestId('reference-rail-muted')).toBeNull()
  })

  it('does not provide any in-page add/remove controls for the watch surface', async () => {
    mockIssue(makeIssue({
      watching: [
        { agentId: 'agent_watch_only', state: 'watching', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z' },
      ],
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const watchingCard = await waitFor(() => screen.getByTestId('reference-rail-watching'))
    expect(within(watchingCard).queryByRole('button', { name: /add/i })).toBeNull()
    expect(within(watchingCard).queryByRole('button', { name: /remove/i })).toBeNull()
    expect(within(watchingCard).queryByRole('button', { name: /unwatch/i })).toBeNull()
    expect(within(watchingCard).queryByRole('button', { name: /unmute/i })).toBeNull()
  })
})
