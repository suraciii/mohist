import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { act } from '@testing-library/react'
import { fireEvent, screen, waitFor } from './test-utils'
import { http, HttpResponse } from 'msw'
import { SessionPage } from '../src/pages/session/ui/SessionPage'
import type { AgentSessionMetadata, SessionTurn } from '../src/entities/coder-session'
import { useMswServer } from './support/msw'
import { renderWithQueryClient as renderPageWithQueryClient, queryClients } from './session-page-test-utils'
import { setScopedValue } from './support/scoped-property'

const FULL_SESSION_ID = 'session-abcdef0123456789'

const sessionMocks = {
  sessions: [] as any[],
  metadata: null as AgentSessionMetadata | null,
  turns: [] as SessionTurn[],
  params: { number: '123', sessionName: 'session-123' },
}

let clipboardWriteSpy: ReturnType<typeof vi.fn>

useMswServer(
  http.get('*/api/projects/:projectId/issues/:issueNumber/coder-sessions', () =>
    HttpResponse.json({ success: true, data: sessionMocks.sessions }),
  ),
  http.get('*/api/projects/:projectId/issues/:issueNumber/sessions/:sessionName/transcript', () =>
    HttpResponse.json({
      success: true,
      data: { turns: sessionMocks.turns, partCount: 0, lastActivityAt: null },
    }),
  ),
  http.get('*/api/projects/:projectId/issues/:issueNumber/sessions/:sessionName', () =>
    HttpResponse.json({ success: true, data: sessionMocks.metadata }),
  ),
  http.get('*/api/projects/:projectId/issues/:issueNumber', () =>
    HttpResponse.json({ success: true, data: null }),
  ),
  http.get('*/api/workflow-runs/:workflowRunId/sessions', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
)

function setupCompletedMetadata(overrides: Partial<AgentSessionMetadata> = {}): AgentSessionMetadata {
  return {
    id: FULL_SESSION_ID,
    sessionName: 'session-123',
    runtimeSessionId: 'rt-1',
    runtime: 'opencode',
    status: 'completed',
    statusKind: 'completed',
    model: 'claude-3-5-sonnet',
    stage: 'build',
    title: 'Test Session',
    createdAt: '2024-01-01T10:00:00.000Z',
    completedAt: '2024-01-01T11:00:00.000Z',
    lastActivityAt: '2024-01-01T10:05:00.000Z',
    lastDataAt: '2024-01-01T10:05:00.000Z',
    changedFiles: [],
    metadata: { eventCount: 5, toolCount: 2 },
    eventSummary: {
      resolvedModel: 'claude-3-5-sonnet',
      toolCallCount: 2,
      toolErrorCount: 0,
    },
    ...overrides,
  } as AgentSessionMetadata
}

function renderSessionPage() {
  return renderPageWithQueryClient(<SessionPage />, '/issues/123/workflow/sessions/session-123')
}

beforeEach(() => {
  clipboardWriteSpy = vi.fn().mockResolvedValue(undefined)
  setScopedValue(navigator, 'clipboard', { writeText: clipboardWriteSpy })
  setScopedValue(Element.prototype, 'scrollTo', vi.fn())
  sessionMocks.sessions = [{
    id: FULL_SESSION_ID,
    sessionName: 'session-123',
    runtimeSessionId: 'rt-1',
    status: 'completed',
    createdAt: '2024-01-01T10:00:00.000Z',
    completedAt: '2024-01-01T11:00:00.000Z',
    model: 'claude-3-5-sonnet',
    runtime: 'opencode',
    stage: 'build',
    title: 'Test Session',
  }]
  sessionMocks.metadata = setupCompletedMetadata()
  sessionMocks.turns = []
  sessionMocks.params = { number: '123', sessionName: 'session-123' }
})

afterEach(() => {
  vi.useRealTimers()
  for (const queryClient of queryClients) queryClient.clear()
  queryClients.length = 0
})

describe('Session header — session-id copy control', () => {
  it('renders the session-id control with the full id in aria-label and data-session-id', async () => {
    renderSessionPage()

    const control = await screen.findByTestId('session-header-session-id')
    expect(control.getAttribute('data-session-id')).toBe(FULL_SESSION_ID)
    expect(control.getAttribute('aria-label')).toBe(`Copy session id ${FULL_SESSION_ID}`)
    expect(control.getAttribute('title')).toBe(FULL_SESSION_ID)
    expect(control.textContent?.trim()).toContain(FULL_SESSION_ID.slice(0, 8))
  })

  it('calls navigator.clipboard.writeText with the full session id on click', async () => {
    renderSessionPage()

    const control = await screen.findByTestId('session-header-session-id')

    await act(async () => {
      fireEvent.click(control)
    })

    expect(clipboardWriteSpy).toHaveBeenCalledTimes(1)
    expect(clipboardWriteSpy).toHaveBeenCalledWith(FULL_SESSION_ID)
  })

  it('renders a transient "Copied!" state for ~1.5s, then resets', async () => {
    renderSessionPage()

    const control = await screen.findByTestId('session-header-session-id')
    expect(control.getAttribute('data-copy-state')).toBe('idle')

    vi.useFakeTimers()

    await act(async () => {
      fireEvent.click(control)
    })
    expect(control.getAttribute('data-copy-state')).toBe('copied')
    expect(control.getAttribute('aria-label')).toBe('Copied!')
    expect(control.textContent).toContain('Copied!')

    await act(async () => {
      vi.advanceTimersByTime(1499)
    })
    expect(control.getAttribute('data-copy-state')).toBe('copied')

    await act(async () => {
      vi.advanceTimersByTime(1)
    })
    expect(control.getAttribute('data-copy-state')).toBe('idle')
    expect(control.getAttribute('aria-label')).toBe(`Copy session id ${FULL_SESSION_ID}`)
  })

  it('keeps the pinned tooltip with the full id open when navigator.clipboard is unavailable', async () => {
    setScopedValue(navigator, 'clipboard', undefined)

    renderSessionPage()

    const control = await screen.findByTestId('session-header-session-id')

    vi.useFakeTimers()

    await act(async () => {
      fireEvent.click(control)
    })

    expect(control.getAttribute('data-copy-state')).toBe('failed')
    expect(control.getAttribute('data-tooltip-pinned')).toBe('true')

    const tooltip = screen.getByTestId('session-header-session-id-tooltip')
    expect(tooltip).toBeInTheDocument()
    expect(tooltip.textContent).toBe(FULL_SESSION_ID)
    expect(tooltip.getAttribute('role')).toBe('tooltip')
    expect(clipboardWriteSpy).not.toHaveBeenCalled()

    await act(async () => {
      vi.advanceTimersByTime(1500)
    })
    expect(control.getAttribute('data-copy-state')).toBe('idle')
    expect(screen.queryByTestId('session-header-session-id-tooltip')).not.toBeInTheDocument()
  })

  it('pins the tooltip when clipboard.writeText rejects, exposing the full id for manual copy', async () => {
    const rejectingSpy = vi.fn().mockRejectedValue(new Error('not allowed'))
    setScopedValue(navigator, 'clipboard', { writeText: rejectingSpy })

    renderSessionPage()

    const control = await screen.findByTestId('session-header-session-id')

    await act(async () => {
      fireEvent.click(control)
    })

    expect(rejectingSpy).toHaveBeenCalledWith(FULL_SESSION_ID)
    await waitFor(() => {
      expect(control.getAttribute('data-copy-state')).toBe('failed')
    })

    const tooltip = screen.getByTestId('session-header-session-id-tooltip')
    expect(tooltip.textContent).toBe(FULL_SESSION_ID)
    expect(control.getAttribute('data-tooltip-pinned')).toBe('true')
  })
})
