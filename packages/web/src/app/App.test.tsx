import '@testing-library/jest-dom'
import type { ComponentType } from 'react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { useMswServer } from '../../tests/support/msw'
import { ProjectProvider } from '../entities/project'
import { UnifiedSessionPage } from '../pages/session'
import { AppContent } from './App'

const TEST_PROJECT = {
  id: 'test-project',
  name: 'demo',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
  repositories: [],
}

let logRequests = 0
let inboxRequests = 0
let projectsData = [TEST_PROJECT]

useMswServer(
  http.get('*/api/projects', () =>
    HttpResponse.json({ success: true, data: projectsData }),
  ),
  http.get('*/api/projects/:projectId/sessions/:sessionId', ({ params }) =>
    HttpResponse.json({
      success: true,
      data: {
        id: String(params.sessionId),
        source: 'agent-launch',
        runtimeSessionId: 'runtime-session',
        runtime: 'opencode',
        activity: 'idle',
        createdAt: '2026-01-01T00:00:00.000Z',
        lastActivityAt: '2026-01-01T00:00:00.000Z',
        model: 'openai/gpt-5.6',
        resolvedModel: 'openai/gpt-5.6',
        failureCategory: null,
        failureReason: null,
        toolCallCount: 0,
        toolErrorCount: 0,
        agentId: 'agent-1',
        agentName: 'reviewer',
        origin: 'web',
        targetId: 'agent-1',
        contextRefs: { workspaceName: 'cli-current' },
        usage: {},
        recoveryAvailable: false,
        inputs: [],
        turns: [],
        recoveryHistory: [],
      },
    }),
  ),
  http.get('*/api/projects/:projectId/sessions/:sessionId/transcript', () =>
    HttpResponse.json({
      success: true,
      data: { turns: [], partCount: 0, lastActivityAt: '2026-01-01T00:00:00.000Z' },
    }),
  ),
  http.get('*/api/projects/:projectId/inbox', () => {
    inboxRequests++
    return HttpResponse.json({ success: true, data: [] })
  }),
  http.get('*/api/projects/:projectId/inbox/unread-count', () =>
    HttpResponse.json({ success: true, data: { unreadCount: 0 } }),
  ),
  http.get('*/api/projects/:projectId/issues', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/projects/:projectId/repositories', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/projects/:projectId/workflow-profile/default', ({ params }) =>
    HttpResponse.json({
      success: true,
      data: {
        projectId: params.projectId,
        defaultWorkflowProfileId: 'mohist/local',
        disabledWorkflowProfileIds: [],
      },
    }),
  ),
  http.get('*/api/projects/:projectId/variables', () =>
    HttpResponse.json({ success: true, data: { vars: {}, stages: {} } }),
  ),
  http.get('*/api/projects/:projectId/opencode/models', () =>
    HttpResponse.json({ success: true, data: { models: [], modelVariants: {} } }),
  ),
  http.get('*/api/projects/:projectId/workflow-profiles', ({ params }) =>
    HttpResponse.json({
      success: true,
      data: [{
        projectId: params.projectId,
        profileId: 'mohist/local',
        name: 'Default',
        description: '',
        sourceProvenance: 'BuiltIn',
        isBuiltIn: true,
        definitionSource: null,
        agentRuntime: 'opencode',
      }],
    }),
  ),
  http.get('*/api/issue-templates', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/config', () =>
    HttpResponse.json({
      success: true,
      data: {
        agentTimeout: 600,
        taskTimeout: 600,
        stageTimeout: 3600,
        maxConcurrentAgents: 3,
        maxGracePeriods: 3,
        pollInterval: 5000,
        logLevel: 'INFO',
      },
    }),
  ),
  http.get('*/api/opencode/runtime', () =>
    HttpResponse.json({
      success: true,
      data: { mode: 'local-opencode', command: 'opencode', model: null, note: '' },
    }),
  ),
  http.get('*/api/projects/:projectId/agent/activity', () =>
    HttpResponse.json({
      success: true,
      data: {
        summary: {
          active: 0,
          waiting: 0,
          completed: 0,
          failed: 0,
          slots: { active: 0, max: 8 },
        },
        sessions: [],
        waiting: [],
      },
    }),
  ),
  http.get('*/api/projects/:projectId/agent/usage', () =>
    HttpResponse.json({
      success: true,
      data: {
        rangeFrom: '2026-01-01T00:00:00Z',
        rangeTo: '2026-01-31T23:59:59Z',
        bucketGranularity: 'day',
        buckets: [],
        cumulativeCostPerShip: [],
      },
    }),
  ),
  http.get('*/api/projects/:projectId/agent/status', () =>
    HttpResponse.json({
      success: true,
      data: { running: false, activeAgents: [], capacity: { active: 0, max: 8 } },
    }),
  ),
  http.get('*/api/projects/:projectId/agent/cost', () =>
    HttpResponse.json({
      success: true,
      data: {
        totalCost: { amount: null, currency: null, sampleCount: 0 },
        todayCost: { amount: null, currency: null, sampleCount: 0 },
        doneIssuesCount: 0,
        costPerShip: { amount: null, currency: null, sampleCount: 0 },
      },
    }),
  ),
  http.get('*/api/projects/:projectId/issues/metrics/completion', () =>
    HttpResponse.json({ success: true, data: { bucket: 'day', window: null, buckets: [] } }),
  ),
  http.get('*/api/projects/:projectId/issues/metrics/delivery-time', () =>
    HttpResponse.json({ success: true, data: { points: [] } }),
  ),
  http.get('*/api/projects/:projectId/issues/metrics/quality', () =>
    HttpResponse.json({ success: true, data: { window: null } }),
  ),
  http.get('*/api/projects/:projectId/issues/metrics/stage-duration', () =>
    HttpResponse.json({
      success: true,
      data: { window: null, stages: [], flowEfficiencyRatio: null, waitBreakout: null },
    }),
  ),
  http.get('*/api/system/info', () =>
    HttpResponse.json({
      success: true,
      data: {
        running: { version: '1.0.0', gitHash: 'test-hash', startedAt: '2026-01-01T00:00:00Z' },
        source: { path: '/repo', branch: 'master', head: 'test-hash', dirty: false },
        install: {
          mode: 'local-source',
          serviceManager: null,
          serverUnit: null,
          runnerUnit: null,
          reason: null,
        },
        update: { status: 'up-to-date', available: false, reason: null },
        services: { server: 'active', runner: 'active' },
        paths: { db: '/db', config: '/config', opencode: '/opencode', logs: '/logs' },
      },
    }),
  ),
  http.get('*/api/system/update/status', () =>
    HttpResponse.json({ success: true, data: { hasJob: false, job: null } }),
  ),
  http.get('*/logs/tail', () => {
    logRequests++
    return HttpResponse.json({
      success: true,
      data: {
        lines: [],
        loading: false,
        error: null,
        cursor: null,
        nextCursor: null,
        source: null,
        unavailable: false,
        expectedLocation: null,
        reason: null,
        truncated: false,
        reset: false,
      },
    })
  }),
)

beforeEach(() => {
  logRequests = 0
  inboxRequests = 0
  projectsData = [TEST_PROJECT]
})

function renderApp({ sessionPage }: { sessionPage?: ComponentType } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <BrowserRouter>
          <AppContent sessionPage={sessionPage} />
        </BrowserRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function getShellContentContainer(): HTMLElement | null {
  return document.querySelector<HTMLElement>(
    '[data-slot="sidebar-inset"] > .flex-1.min-h-0.flex.flex-col',
  )
}

describe('App shell bottom spacing for mobile bottom nav', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/demo')
  })

  afterEach(() => {
    cleanup()
    window.history.replaceState({}, '', '/')
    window.localStorage.clear()
  })

  it('uses a calc-based bottom padding that adds safe-area-inset-bottom on mobile', () => {
    renderApp()

    const shellContainer = getShellContentContainer()
    expect(shellContainer).not.toBeNull()

    const classNames = shellContainer!.className.split(/\s+/)
    expect(classNames).toContain('pb-[calc(3.5rem+env(safe-area-inset-bottom))]')
    expect(classNames).toContain('md:pb-0')
  })

  it('does not use the legacy pb-14 fixed padding', () => {
    renderApp()

    const shellContainer = getShellContentContainer()
    expect(shellContainer).not.toBeNull()

    const classNames = shellContainer!.className.split(/\s+/)
    expect(classNames).not.toContain('pb-14')
  })

  it('keeps application content shrinkable inside the sidebar inset', () => {
    renderApp()

    const shellContainer = getShellContentContainer()
    expect(shellContainer).not.toBeNull()

    const classNames = shellContainer!.className.split(/\s+/)
    expect(classNames).toContain('flex-1')
    expect(classNames).toContain('min-h-0')
    expect(classNames).toContain('min-w-0')
  })

  it('routes /:projectName/inbox to the project inbox page', async () => {
    window.history.replaceState({}, '', '/demo/inbox')

    renderApp()

    expect(await screen.findByTestId('inbox-title')).toHaveTextContent('Inbox')
    await waitFor(() => expect(inboxRequests).toBeGreaterThan(0))
  })

  it('routes /:projectName/insights to the Insights page and keeps the Dashboard unreachable on the same URL', async () => {
    window.history.replaceState({}, '', '/demo/insights')

    renderApp()

    expect(await screen.findByTestId('insights-title')).toHaveTextContent('Insights')
    expect(screen.queryByTestId('signal-summary')).not.toBeInTheDocument()
    expect(screen.queryByTestId('insights-signal-section')).not.toBeInTheDocument()
    expect(screen.getByTestId('insights-charts')).toBeInTheDocument()
    // Dashboard content must not appear on the insights route.
    expect(screen.queryByTestId('dashboard-page')).toBeNull()
  })

  it('routes /:projectName/sessions/:sessionId to UnifiedSessionPage', async () => {
    window.history.replaceState({}, '', '/demo/sessions/session-1')

    renderApp({ sessionPage: UnifiedSessionPage })

    expect(await screen.findByTestId('session-header')).toBeInTheDocument()
    expect(screen.getByTestId('session-header-session-id')).toHaveAttribute('data-session-id', 'session-1')
    expect(screen.getByTestId('session-workspace-link')).toHaveTextContent('Workspace: cli-current')
  })

  it('loads the canonical session deep link on a refresh-equivalent initial mount', async () => {
    window.history.replaceState({}, '', '/demo/sessions/session-refresh')

    renderApp({ sessionPage: UnifiedSessionPage })

    expect(await screen.findByTestId('session-header')).toBeInTheDocument()
    expect(screen.getByTestId('session-header-session-id')).toHaveAttribute('data-session-id', 'session-refresh')
    expect(screen.getByTestId('session-origin')).toHaveTextContent('Origin: web')
  })
})

describe('App routing split for settings scopes', () => {
  afterEach(() => {
    cleanup()
    window.history.replaceState({}, '', '/')
    window.localStorage.clear()
  })

  it('redirects legacy /:projectName/settings/ai (global section under project scope) to /settings/ai', async () => {
    window.history.replaceState({}, '', '/demo/settings/ai')

    renderApp()

    // After the replace navigation, the URL must be the application-scope URL.
    await waitFor(() => expect(window.location.pathname).toBe('/settings/ai'))
  })

  it('renders /settings/ai without showing the "No projects yet" gate even when projects exist', () => {
    window.history.replaceState({}, '', '/settings/ai')

    const { queryByText } = renderApp()

    expect(queryByText('No projects yet')).not.toBeInTheDocument()
  })

  it('does not redirect project-scoped /:projectName/settings/repositories', () => {
    window.history.replaceState({}, '', '/demo/settings/repositories')

    renderApp()

    // The project-scoped repositories URL must NOT be redirected to the
    // application scope (D2 risk: project URLs must stay project-scoped).
    expect(window.location.pathname).toBe('/demo/settings/repositories')
  })

  it('redirects legacy /:projectName/settings/agent, /system, /preferences to the application scope', async () => {
    for (const section of ['agent', 'system', 'preferences']) {
      window.history.replaceState({}, '', `/demo/settings/${section}`)
      renderApp()
      await waitFor(() => expect(window.location.pathname).toBe(`/settings/${section}`))
      cleanup()
    }
  })

  it('does not redirect /:projectName/settings/<project-section> for any project section', () => {
    for (const section of ['repositories', 'templates', 'label-catalog', 'workflows', 'inbox']) {
      window.history.replaceState({}, '', `/demo/settings/${section}`)
      renderApp()
      expect(window.location.pathname).toBe(`/demo/settings/${section}`)
      cleanup()
    }
  })

  it('keeps application settings reachable when no project exists (no "No projects yet" prompt)', () => {
    projectsData = []
    window.history.replaceState({}, '', '/settings/ai')

    const { queryByText } = renderApp()

    expect(queryByText('No projects yet')).not.toBeInTheDocument()
  })

  it('does not serve project settings from /settings/<project-section> when no project exists', async () => {
    projectsData = []
    window.history.replaceState({}, '', '/settings/repositories')

    const { queryByText, findByTestId, queryByTestId } = renderApp()

    expect(window.location.pathname).toBe('/settings/repositories')
    expect(queryByTestId('repositories-section')).not.toBeInTheDocument()
    expect(queryByText('No project selected')).not.toBeInTheDocument()
    expect(await findByTestId('no-project-select-button')).toBeInTheDocument()
    expect(queryByTestId('no-project-create-button')).toBeInTheDocument()
  })

  it('redirects /settings/<project-section> to the selected project scope once a project is available', async () => {
    window.history.replaceState({}, '', '/settings/repositories')

    renderApp()

    await waitFor(() => {
      expect(window.location.pathname).toBe('/demo/settings/repositories')
    })
  })

  it('clicking Logs from the sidebar renders the project-scoped Logs page', async () => {
    window.history.replaceState({}, '', '/demo')

    renderApp()

    await waitFor(() => {
      expect(screen.getByText('demo')).toBeInTheDocument()
    })
    fireEvent.click(screen.getByTestId('nav-logs'))

    await waitFor(() => {
      expect(window.location.pathname).toBe('/demo/logs')
    })
    await waitFor(() => {
      expect(logRequests).toBeGreaterThan(0)
      expect(screen.getByText('No matching logs')).toBeInTheDocument()
    })
  })

  it('renders /settings/<app-section> on the application scope (no project prefix) for every global section', () => {
    for (const section of ['ai', 'agent', 'system', 'preferences']) {
      window.history.replaceState({}, '', `/settings/${section}`)
      renderApp()
      expect(window.location.pathname).toBe(`/settings/${section}`)
      cleanup()
    }
  })
})
