import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { Link, MemoryRouter, Route, Routes } from 'react-router-dom'
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

function makeDetail(nextAction = 'rerun_install') {
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

function renderPageAt(entry: string) {
  const queryClient = makeQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[PROJECT]}>
        <MemoryRouter initialEntries={[entry]}>
          <Link to="/Test">Leave the connection</Link>
          <Routes>
            <Route path="/:projectName/connections/:connectionId" element={<ConnectionDiagnosticPage />} />
            <Route path="/:projectName" element={<Link to="/Test/connections/conn-1">Back to the connection</Link>} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function renderPage() {
  return renderPageAt('/Test/connections/conn-1')
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
    expect(await screen.findByTestId('connection-setup-primary-action')).toHaveAttribute('data-action', 'host_command')

    const user = userEvent.setup()
    await user.click(screen.getByText('App facts'))
    expect(screen.getByTestId('managed-agent-app-status')).toHaveTextContent('rerun install')
    expect(screen.getByTestId('managed-agent-app-status')).toHaveTextContent('timeout')
  })

  it('names the Slack target instead of an internal connection id', async () => {
    renderPage()

    expect(await screen.findByTestId('connection-diagnostic-target')).toHaveTextContent('derived-bot')
    expect(screen.getByTestId('connection-diagnostic-target')).not.toHaveTextContent('conn-1')

    const user = userEvent.setup()
    await user.click(screen.getByText('Supporting facts'))
    expect(screen.getByTestId('connection-diagnostic-facts')).toHaveTextContent('conn-1')
  })

  it('prefers the verified Bot name over the configured one', async () => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({
          success: true,
          data: makeDiagnostic({
            facts: makeFacts({
              identity: {
                verificationStatus: 'verified',
                verifiedBotName: 'Verified Bot',
                botName: 'derived-bot',
                agentName: 'Writer',
                verifiedBotIconUrl: null,
                avatarHash: null,
                driftKinds: [],
              },
            }),
          }),
        }),
      ),
    )

    renderPage()

    expect(await screen.findByTestId('connection-diagnostic-target')).toHaveTextContent('Verified Bot')
  })

  it('keeps the install approval link as the single action and persists it across navigation', async () => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId', () =>
        HttpResponse.json({ success: true, data: makeDetail('approve_install') }),
      ),
    )

    const user = userEvent.setup()
    renderPage()

    const action = await screen.findByTestId('connection-setup-primary-action')
    expect(action).toHaveAttribute('data-action', 'approve_install')
    expect(action).toHaveAttribute('href', 'https://api.slack.com/apps/A1/oauth')
    expect(action).toHaveAttribute('target', '_blank')
    expect(screen.queryByTestId('connection-setup-host-command')).not.toBeInTheDocument()

    await user.click(screen.getByText('Leave the connection'))
    expect(await screen.findByText('Back to the connection')).toBeInTheDocument()

    await user.click(screen.getByText('Back to the connection'))
    const restored = await screen.findByTestId('connection-setup-primary-action')
    expect(restored).toHaveAttribute('data-action', 'approve_install')
    expect(restored).toHaveAttribute('href', 'https://api.slack.com/apps/A1/oauth')
  })

  it('shows the protected host command as the single action at the credential step', async () => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId', () =>
        HttpResponse.json({ success: true, data: makeDetail('provide_credentials') }),
      ),
    )

    renderPage()

    const action = await screen.findByTestId('connection-setup-primary-action')
    expect(action).toHaveAttribute('data-action', 'host_command')
    expect(screen.getByTestId('connection-setup-host-command')).toHaveTextContent(
      'mo slack install-agent agent-1 --project Test',
    )
    expect(action).toHaveTextContent('--credentials-file')
    expect(action.querySelector('a')).toBeNull()
  })

  it('projects Owner claim as the one next action instead of App readiness', async () => {
    const claimCalls: Array<{ method: string; pathname: string }> = []
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({
          success: true,
          data: makeDiagnostic({
            primaryState: 'setup_incomplete',
            reason: "Slack setup is incomplete at 'claim_owner'.",
            nextAction: 'Claim the Owner from the Mohist host.',
            facts: makeFacts({
              setupProgress: 'claim_owner',
              identity: {
                verificationStatus: 'verified',
                verifiedBotName: 'Writer',
                botName: 'derived-bot',
                agentName: 'Writer',
                verifiedBotIconUrl: null,
                avatarHash: null,
                driftKinds: [],
              },
            }),
          }),
        }),
      ),
      http.get('*/api/projects/:projectId/slack-connections/:connectionId', () =>
        HttpResponse.json({ success: true, data: makeDetail('claim_owner') }),
      ),
      http.post('*/api/projects/:projectId/slack-connections/:connectionId/claim-owner', ({ request }) => {
        claimCalls.push({ method: request.method, pathname: new URL(request.url).pathname })
        return HttpResponse.json({
          success: true,
          data: { code: 'CLAIM-CODE-1', expiresAt: '2026-08-01T01:00:00.000Z' },
        })
      }),
    )

    renderPage()

    const action = await screen.findByTestId('connection-setup-primary-action')
    expect(action).toHaveAttribute('data-action', 'claim_owner')
    expect(screen.getByTestId('connection-setup-host-command')).toHaveTextContent(
      'mo slack claim-owner conn-1 --project Test',
    )
    expect(screen.getByTestId('connection-setup-claim-destination')).toHaveTextContent(
      'direct message with the writer bot',
    )
    // A pending claim is never presented as completed setup.
    expect(screen.getByTestId('connection-setup-step-claim_owner')).toHaveAttribute('data-state', 'current')
    expect(screen.getByTestId('connection-setup-step-complete')).toHaveAttribute('data-state', 'pending')
    expect(screen.queryByTestId('connection-setup-step-list')).toBeInTheDocument()

    // The code itself never reaches the page: no request issues one and no element renders one.
    expect(claimCalls).toEqual([])
    expect(document.body.textContent ?? '').not.toContain('CLAIM-CODE-1')
    expect(document.querySelectorAll('button')).toHaveLength(0)
  })

  it('keeps a pending claim free of regeneration on refetch', async () => {
    const claimCalls: Array<{ method: string; pathname: string }> = []
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({
          success: true,
          data: makeDiagnostic({
            facts: makeFacts({ setupProgress: 'claim_owner' }),
          }),
        }),
      ),
      http.get('*/api/projects/:projectId/slack-connections/:connectionId', () =>
        HttpResponse.json({ success: true, data: makeDetail('claim_owner') }),
      ),
      http.post('*/api/projects/:projectId/slack-connections/:connectionId/claim-owner', ({ request }) => {
        claimCalls.push({ method: request.method, pathname: new URL(request.url).pathname })
        return HttpResponse.json({
          success: true,
          data: { code: 'CLAIM-CODE-1', expiresAt: '2026-08-01T01:00:00.000Z' },
        })
      }),
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

    expect(await screen.findByTestId('connection-setup-primary-action')).toHaveAttribute('data-action', 'claim_owner')

    await act(async () => {
      await queryClient.invalidateQueries({ queryKey: ['agent-connection-diagnostic', 'proj-1', 'conn-1'] })
      await queryClient.invalidateQueries({ queryKey: ['agent-connection', 'proj-1', 'conn-1'] })
    })

    await waitFor(() => {
      expect(screen.getByTestId('connection-setup-primary-action')).toHaveAttribute('data-action', 'claim_owner')
    })
    expect(claimCalls).toEqual([])
  })

  it('separates a claimed Connection from an Agent that cannot execute', async () => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({
          success: true,
          data: makeDiagnostic({
            primaryState: 'agent_needs_setup',
            reason: 'The bound Agent is not ready to accept new work.',
            nextAction: 'Review the Agent execution settings.',
            facts: makeFacts({
              setupProgress: 'complete',
              agentReadiness: 'needs_setup',
              credentialStatus: 'valid',
              agentExecutability: {
                state: 'not-configured',
                gaps: [
                  {
                    code: 'runtime_missing',
                    message: 'No Runtime is configured for this Agent.',
                    nextAction: 'Choose a Runtime in Agent settings.',
                    fixEntryPoint: {
                      label: 'Agent settings',
                      path: '/agents/agent-1',
                      command: 'mo agent edit agent-1',
                    },
                  },
                ],
                pendingLaunchNote: null,
              },
            }),
          }),
        }),
      ),
      http.get('*/api/projects/:projectId/slack-connections/:connectionId', () =>
        HttpResponse.json({ success: true, data: makeDetail('repair_agent') }),
      ),
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/deliveries', () =>
        HttpResponse.json({ success: true, data: { entries: [] } }),
      ),
    )

    renderPage()

    const action = await screen.findByTestId('connection-setup-primary-action')
    expect(action).toHaveAttribute('data-action', 'repair_agent')
    expect(screen.getByTestId('connection-setup-agent-repair-link')).toHaveAttribute('href', '/Test/agents/agent-1')
    const limitation = screen.getByTestId('connection-agent-executability')
    expect(limitation).toHaveTextContent('No Runtime is configured for this Agent.')
    expect(limitation).toHaveTextContent('Choose a Runtime in Agent settings.')
    // The limitation is separate from the completed Connection setup, and the
    // projected repair action stays the one executable action on the page.
    expect(limitation.querySelector('a')).toBeNull()
    expect(screen.getByTestId('connection-diagnostic-facts')).toHaveTextContent(/complete/i)
  })

  it('collects no credential and renders no token value on the setup page', async () => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId', () =>
        HttpResponse.json({ success: true, data: makeDetail('provide_credentials') }),
      ),
    )

    renderPage()

    expect(await screen.findByTestId('connection-setup-primary-action')).toBeInTheDocument()
    expect(document.querySelectorAll('input, textarea, select')).toHaveLength(0)
    expect(document.body.textContent ?? '').not.toMatch(/xoxb-|xapp-|xoxp-|botToken|appLevelToken/)
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
