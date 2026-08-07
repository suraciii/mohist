import { beforeEach, describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { act, fireEvent, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { baseRender } from './test-utils'
import { useMswServer } from './support/msw'
import App from '../src/app/App'

const TEST_PROJECT = {
  id: 'test-project',
  name: 'demo',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
  repositories: [],
}

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
}

let sessionActive = true
let loginFails = false
let failProjectsOnce = false
const loginRequests: Request[] = []
const logoutRequests: Request[] = []

function renderApp() {
  const queryClient = createQueryClient()
  const result = baseRender(
    <QueryClientProvider client={queryClient}>
      <App />
    </QueryClientProvider>,
  )
  return { ...result, queryClient }
}

useMswServer(
  http.get('*/api/auth/session', () => {
    console.log('DBG session GET, sessionActive=', sessionActive)
    return sessionActive
      ? HttpResponse.json({ success: true })
      : HttpResponse.json({ success: false, error: 'Authentication required.', code: 'unauthorized' }, { status: 401 })
  }),
  http.post('*/api/auth/session', async ({ request }) => {
    loginRequests.push(request)
    if (loginFails) {
      return HttpResponse.json({ success: false, error: 'Invalid token.', code: 'unauthorized' }, { status: 401 })
    }
    sessionActive = true
    return HttpResponse.json({ success: true })
  }),
  http.delete('*/api/auth/session', ({ request }) => {
    logoutRequests.push(request)
    sessionActive = false
    return HttpResponse.json({ success: true })
  }),
  http.get('*/api/projects', () => {
    if (failProjectsOnce) {
      failProjectsOnce = false
      sessionActive = false
      return HttpResponse.json({ success: false, error: 'Authentication required.', code: 'unauthorized' }, { status: 401 })
    }
    return HttpResponse.json({ success: true, data: [TEST_PROJECT] })
  }),
  http.get('*/api/projects/:projectId/inbox', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/projects/:projectId/inbox/unread-count', () =>
    HttpResponse.json({ success: true, data: { unreadCount: 0 } }),
  ),
  http.get('*/api/projects/:projectId/issues', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/projects/:projectId/repositories', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/projects/:projectId/workflow-profile', ({ params }) =>
    HttpResponse.json({
      success: true,
      data: { projectId: params.projectId, defaultTemplateId: null, disabledWorkflowProfileIds: [] },
    }),
  ),
  http.get('*/api/projects/:projectId/variables', () =>
    HttpResponse.json({ success: true, data: { vars: {}, stages: {} } }),
  ),
  http.get('*/api/projects/:projectId/opencode/models', () =>
    HttpResponse.json({ success: true, data: { models: [], modelVariants: {} } }),
  ),
  http.get('*/api/workflow-templates/system', () =>
    HttpResponse.json({ success: true, data: [] }),
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
    HttpResponse.json({ success: true, data: { mode: 'local-opencode', command: 'opencode', model: null, note: '' } }),
  ),
  http.get('*/api/projects/:projectId/agent/activity', () =>
    HttpResponse.json({
      success: true,
      data: { summary: { active: 0, waiting: 0, completed: 0, failed: 0, slots: { active: 0, max: 8 } }, sessions: [], waiting: [] },
    }),
  ),
  http.get('*/api/projects/:projectId/agent/usage', () =>
    HttpResponse.json({
      success: true,
      data: { rangeFrom: '2026-01-01T00:00:00Z', rangeTo: '2026-01-31T23:59:59Z', bucketGranularity: 'day', buckets: [], cumulativeCostPerShip: [] },
    }),
  ),
  http.get('*/api/projects/:projectId/agent/status', () =>
    HttpResponse.json({ success: true, data: { running: false, activeAgents: [], capacity: { active: 0, max: 8 } } }),
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
    HttpResponse.json({ success: true, data: { window: null, stages: [], flowEfficiencyRatio: null, waitBreakout: null } }),
  ),
  http.get('*/api/system/info', () =>
    HttpResponse.json({
      success: true,
      data: {
        running: { version: '1.0.0', gitHash: 'test-hash', startedAt: '2026-01-01T00:00:00Z' },
        source: { path: '/repo', branch: 'master', head: 'test-hash', dirty: false },
        install: { mode: 'local-source', serviceManager: null, serverUnit: null, runnerUnit: null, reason: null },
        update: { status: 'up-to-date', available: false, reason: null },
        services: { server: 'active', runner: 'active' },
        paths: { db: '/db', config: '/config', opencode: '/opencode', logs: '/logs' },
      },
    }),
  ),
  http.get('*/api/system/update/status', () =>
    HttpResponse.json({ success: true, data: { hasJob: false, job: null } }),
  ),
  http.get('*/logs/tail', () =>
    HttpResponse.json({
      success: true,
      data: { lines: [], loading: false, error: null, cursor: null, nextCursor: null, source: null, unavailable: false, expectedLocation: null, reason: null, truncated: false, reset: false },
    }),
  ),
)

beforeEach(() => {
  sessionActive = true
  loginFails = false
  failProjectsOnce = false
  loginRequests.length = 0
  logoutRequests.length = 0
})

describe('Web login', () => {
  it('presents the login page when no session exists', async () => {
    sessionActive = false
    renderApp()

    expect(await screen.findByTestId('login-page')).toBeInTheDocument()
    expect(screen.queryByTestId('header-new-issue')).not.toBeInTheDocument()
  })

  it('exchanges a pasted operator token for the app shell', async () => {
    sessionActive = false
    renderApp()

    const input = await screen.findByLabelText('Operator token')
    fireEvent.change(input, { target: { value: 'moh_admin_spec-token' } })
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByTestId('header-new-issue')).toBeInTheDocument()
    expect(screen.queryByTestId('login-page')).not.toBeInTheDocument()
    expect(loginRequests).toHaveLength(1)
    const body = await loginRequests[0].json()
    expect(body).toEqual({ token: 'moh_admin_spec-token' })
  })

  it('shows an inline error for an invalid token and stays on the login page', async () => {
    sessionActive = false
    loginFails = true
    renderApp()

    const input = await screen.findByLabelText('Operator token')
    fireEvent.change(input, { target: { value: 'not-a-valid-token' } })
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid token.')
    expect(screen.getByTestId('login-page')).toBeInTheDocument()
    expect(loginRequests).toHaveLength(1)
  })

  it('returns to the login page when a business request hits 401', async () => {
    const { queryClient } = renderApp()
    expect(await screen.findByTestId('header-new-issue')).toBeInTheDocument()

    failProjectsOnce = true
    await act(async () => {
      await queryClient.invalidateQueries({ queryKey: ['projects'] })
    })

    expect(await screen.findByTestId('login-page')).toBeInTheDocument()
    expect(screen.queryByTestId('header-new-issue')).not.toBeInTheDocument()
  })

  it('logs out through the server and returns to the login page', async () => {
    sessionActive = true
    renderApp()

    fireEvent.click(await screen.findByTestId('header-logout'))

    expect(await screen.findByTestId('login-page')).toBeInTheDocument()
    expect(logoutRequests).toHaveLength(1)
    expect(logoutRequests[0].method).toBe('DELETE')
    expect(screen.queryByTestId('header-new-issue')).not.toBeInTheDocument()
  })
})
