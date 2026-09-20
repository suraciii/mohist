import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import { server, useMswServer } from '../../../../tests/support/msw'
import { ConnectionDiagnosticPage } from './ConnectionDiagnosticPage'
import type { ConnectionDiagnostic, ConnectionDiagnosticFacts } from '../../../entities/agent-connection'

useMswServer()

const PROJECT = {
  id: 'proj-1',
  name: 'Test',
  createdAt: '2026-01-01T00:00:00.000Z',
  updatedAt: '2026-01-01T00:00:00.000Z',
  repositories: [],
}

function makeFacts(overrides: Partial<ConnectionDiagnosticFacts> = {}): ConnectionDiagnosticFacts {
  return {
    setupProgress: 'create_app_credentials',
    desiredState: 'enabled',
    connectionHealth: 'healthy',
    healthReason: null,
    credentialStatus: 'unknown',
    adapterOnline: true,
    ownerAvailability: 'not_configured',
    agentReadiness: 'unknown',
    identity: {
      verificationStatus: 'not_yet_verified',
      verifiedBotName: null,
      botName: 'derived-bot',
      agentName: 'Writer',
      verifiedBotIconUrl: null,
      avatarHash: null,
      driftKinds: [],
    },
    offlineGapAt: null,
    ...overrides,
  }
}

function makeDiagnostic(overrides: Partial<ConnectionDiagnostic> = {}): ConnectionDiagnostic {
  return {
    primaryState: 'setup_incomplete',
    reason: "Slack setup is incomplete at 'create_app_credentials'.",
    nextAction: 'Advance the current setup step.',
    facts: makeFacts(),
    ...overrides,
  }
}

function makeDetail(nextAction = 'reconcile_create') {
  return {
    connection: {
      id: 'conn-1',
      projectId: 'proj-1',
      agentId: 'agent-1',
      providerKind: 'slack',
      workspaceTeamId: '',
      appId: '',
      botUserId: '',
      botName: 'derived-bot',
      avatarHash: null,
      verifiedBotName: null,
      verifiedBotIconUrl: null,
      setupProgress: 'create_app_credentials',
      desiredState: 'enabled',
      connectionHealth: 'healthy',
      healthReason: null,
      agentReadiness: 'unknown',
      ownerSlackUserId: null,
      lastHeartbeatAt: null,
      createdAt: '2026-06-01T00:00:00.000Z',
      updatedAt: '2026-06-01T00:00:00.000Z',
      deletedAt: null,
    },
    managedApp: {
      appLifecycle: 'create_unknown',
      authorization: 'pending_admin',
      manifestState: 'desired',
      transportKind: 'socket',
      transportReadiness: 'not_ready',
      nextAction,
      bindingState: 'pending',
      installUrl: nextAction === 'approve_install' ? 'https://api.slack.com/apps/A1/oauth' : null,
      unknownOutcome: 'timeout',
      errorClass: 'timeout',
      deletedAt: null,
    },
  } as const
}

function makeQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function renderPage() {
  const queryClient = makeQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[PROJECT]}>
        <MemoryRouter initialEntries={['/Test/connections/conn-1']}>
          <Routes>
            <Route path="/:projectName/connections/:connectionId" element={<ConnectionDiagnosticPage />} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

afterEach(cleanup)

describe('ConnectionDiagnosticPage — setup step rendering (MSW)', () => {
  beforeEach(() => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({ success: true, data: makeDiagnostic() }),
      ),
      http.get('*/api/projects/:projectId/slack-connections/:connectionId', () =>
        HttpResponse.json({ success: true, data: makeDetail() }),
      ),
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/access', () =>
        HttpResponse.json({
          success: true,
          data: { accessPolicy: 'owner_only', allowMembers: [], anyoneDisclosure: 'disclosure' },
        }),
      ),
    )
  })

  it('renders the managed setup progress without a manual app or credential form', async () => {
    renderPage()

    expect(await screen.findByTestId('connection-setup-step-list')).toBeInTheDocument()
    expect(await screen.findByTestId('connection-setup-managed-progress')).toBeInTheDocument()
    expect(screen.queryByTestId('connection-setup-identity-preview')).not.toBeInTheDocument()
    expect(screen.queryByTestId('connection-setup-credential-form')).not.toBeInTheDocument()
    expect(await screen.findByTestId('managed-agent-app-status')).toHaveTextContent('reconcile create')
    expect(screen.getByTestId('managed-agent-app-status')).toHaveTextContent('timeout')
  })

  it('keeps the install approval link and exact protected host command available after navigation', async () => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId', () =>
        HttpResponse.json({ success: true, data: makeDetail('approve_install') }),
      ),
    )

    renderPage()

    const link = await screen.findByTestId('managed-agent-app-install-link')
    expect(link).toHaveAttribute('href', 'https://api.slack.com/apps/A1/oauth')
    expect(link).toHaveAttribute('target', '_blank')
    expect(screen.getByTestId('managed-agent-app-local-step')).toHaveTextContent('mo slack install-agent agent-1')
    expect(screen.getByTestId('managed-agent-app-local-step')).toHaveTextContent('--credentials-file')
  })

  it('renders the waiting-for-service step while preserving setup progress', async () => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({
          success: true,
          data: makeDiagnostic({
            primaryState: 'setup_incomplete',
            reason: "Slack setup is incomplete at 'waiting_for_slack_service'.",
            nextAction: 'Advance the current setup step.',
            facts: makeFacts({ setupProgress: 'waiting_for_slack_service' }),
          }),
        }),
      ),
    )

    renderPage()

    expect(await screen.findByTestId('connection-setup-waiting-for-service')).toBeInTheDocument()
    expect(screen.getByTestId('connection-setup-step-waiting_for_slack_service')).toHaveAttribute(
      'data-state',
      'current',
    )
    expect(screen.getByTestId('connection-setup-step-create_app_credentials')).toHaveAttribute('data-state', 'done')
  })

  it('a step completed elsewhere is reflected in the Web on the next refetch', async () => {
    let diagnosticProgress = 'create_app_credentials'
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({
          success: true,
          data: makeDiagnostic({
            facts: makeFacts({ setupProgress: diagnosticProgress }),
          }),
        }),
      ),
    )

    const queryClient = makeQueryClient()
    render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[PROJECT]}>
          <MemoryRouter initialEntries={['/Test/connections/conn-1']}>
            <Routes>
              <Route path="/:projectName/connections/:connectionId" element={<ConnectionDiagnosticPage />} />
            </Routes>
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(await screen.findByTestId('connection-setup-step-list')).toBeInTheDocument()
    expect(screen.getByTestId('connection-setup-step-create_app_credentials')).toHaveAttribute('data-state', 'current')

    diagnosticProgress = 'waiting_for_slack_service'

    await act(async () => {
      await queryClient.invalidateQueries({ queryKey: ['agent-connection-diagnostic', 'proj-1', 'conn-1'] })
    })

    await waitFor(() => {
      expect(screen.getByTestId('connection-setup-step-waiting_for_slack_service')).toHaveAttribute(
        'data-state',
        'current',
      )
    })
    expect(screen.getByTestId('connection-setup-step-create_app_credentials')).toHaveAttribute('data-state', 'done')
  })

  it('agent not Ready keeps Connection setup progress and reports it via the summary', async () => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({
          success: true,
          data: {
            primaryState: 'agent_needs_setup',
            reason: 'The bound Agent is missing required runtime configuration.',
            nextAction: 'Configure Agent runtime/model.',
            facts: makeFacts({
              setupProgress: 'complete',
              agentReadiness: 'needs_setup',
              credentialStatus: 'valid',
            }),
          },
        }),
      ),
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/deliveries', () =>
        HttpResponse.json({ success: true, data: { entries: [] } }),
      ),
    )

    renderPage()

    expect(await screen.findByTestId('connection-diagnostic-next-action')).toHaveTextContent(/agent/i)
    expect(screen.getByTestId('connection-diagnostic-primary-state')).toHaveTextContent(/agent/i)
    const facts = screen.getByTestId('connection-diagnostic-facts')
    expect(facts).toHaveTextContent(/complete/i)
    expect(facts).toHaveTextContent(/needs.?setup/i)
    expect(screen.queryByTestId('connection-setup-step-list')).not.toBeInTheDocument()
  })
})
