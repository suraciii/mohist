import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
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
      installUrl: null,
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

describe('ConnectionDiagnosticPage — configure & owner-claim handoff actions (MSW)', () => {
  beforeEach(() => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({ success: true, data: makeDiagnostic() }),
      ),
      http.get('*/api/projects/:projectId/slack-connections/:connectionId', () =>
        HttpResponse.json({ success: true, data: makeDetail() }),
      ),
    )
  })

  it('issues no claim code from the page at the claim step', async () => {
    const claimCalls: Array<{ method: string; pathname: string }> = []
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({
          success: true,
          data: makeDiagnostic({
            primaryState: 'setup_incomplete',
            reason: "Slack setup is incomplete at 'claim_owner'.",
            nextAction: 'Claim the Owner from the Mohist host.',
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
          data: { code: 'NEVER-ISSUED', expiresAt: '2026-08-01T01:00:00.000Z' },
        })
      }),
    )

    renderPage()

    const action = await screen.findByTestId('connection-setup-primary-action')
    expect(action).toHaveAttribute('data-action', 'claim_owner')
    expect(screen.getByTestId('connection-setup-host-command')).toHaveTextContent('mo slack claim-owner conn-1')
    expect(claimCalls).toEqual([])
    expect(document.body.textContent ?? '').not.toContain('NEVER-ISSUED')
  })

  it('service offline retains setup progress and surfaces the single next step', async () => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({
          success: true,
          data: {
            primaryState: 'service_offline',
            reason: 'Slack service could not be reached.',
            nextAction: 'Start mohist-slack / check Slack connectivity.',
            facts: makeFacts({
              setupProgress: 'waiting_for_slack_service',
              connectionHealth: 'unhealthy',
              healthReason: 'mohist-slack service offline',
              adapterOnline: false,
            }),
          },
        }),
      ),
    )

    renderPage()

    expect(await screen.findByTestId('connection-setup-step-list')).toBeInTheDocument()
    expect(screen.getByTestId('connection-setup-step-waiting_for_slack_service')).toHaveAttribute(
      'data-state',
      'current',
    )
    expect(screen.getByTestId('connection-diagnostic-primary-state')).toHaveTextContent(/service/i)
    expect(screen.getByTestId('connection-diagnostic-next-action')).toHaveTextContent(/mohist-slack/i)
    expect(screen.getByTestId('connection-diagnostic-facts')).toHaveTextContent(/setup/i)
  })

  it('invalid credentials retain setup progress and point to reconfiguration', async () => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({
          success: true,
          data: {
            primaryState: 'credentials_invalid',
            reason: 'Stored Slack credentials failed verification.',
            nextAction: 'Rotate credentials.',
            facts: makeFacts({
              setupProgress: 'fix_slack_setup',
              connectionHealth: 'unhealthy',
              healthReason: 'invalid_auth',
              credentialStatus: 'invalid',
            }),
          },
        }),
      ),
    )

    renderPage()

    expect(await screen.findByTestId('connection-setup-step-list')).toBeInTheDocument()
    expect(screen.getByTestId('connection-setup-step-fix_slack_setup')).toHaveAttribute('data-state', 'current')
    expect(screen.getByTestId('connection-diagnostic-next-action')).toHaveTextContent(/rotate|reconfigur/i)
    expect(screen.getByTestId('connection-diagnostic-facts')).toHaveTextContent(/credential status/i)
  })
})

describe('ConnectionDiagnosticPage — access policy (MSW)', () => {
  beforeEach(() => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({
          success: true,
          data: makeDiagnostic({ facts: makeFacts({ setupProgress: 'complete' }) }),
        }),
      ),
      http.get('*/api/projects/:projectId/slack-connections/:connectionId', () =>
        HttpResponse.json({ success: true, data: makeDetail() }),
      ),
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/access', () =>
        HttpResponse.json({
          success: true,
          data: {
            accessPolicy: 'allowlist',
            allowMembers: ['U_EXISTING'],
            anyoneDisclosure: 'Invoking this Bot grants the Agent authority.',
          },
        }),
      ),
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/deliveries', () =>
        HttpResponse.json({ success: true, data: { entries: [] } }),
      ),
    )
  })

  it('loads access state and POSTs the replaced allowlist to /manage-access', async () => {
    const manageCalls: Array<{ method: string; pathname: string; body: unknown }> = []
    server.use(
      http.post('*/api/projects/:projectId/slack-connections/:connectionId/manage-access', async ({ request }) => {
        const text = await request.text()
        let parsed: unknown = text
        try {
          parsed = JSON.parse(text)
        } catch {
          /* keep raw */
        }
        const url = new URL(request.url)
        manageCalls.push({ method: request.method, pathname: url.pathname, body: parsed })
        return HttpResponse.json({
          success: true,
          data: { ...makeDetail().connection, accessPolicy: 'owner_only' },
        })
      }),
    )

    const user = userEvent.setup()
    renderPage()

    const section = await screen.findByTestId('connection-access-policy-section')
    expect(section).toBeInTheDocument()
    expect(await screen.findByTestId('connection-access-policy-radio-allowlist')).toBeChecked()
    expect(screen.getByTestId('connection-access-policy-chip')).toHaveAttribute('data-slack-user-id', 'U_EXISTING')

    await user.click(screen.getByTestId('connection-access-policy-radio-owner_only'))
    await user.click(screen.getByTestId('connection-access-policy-submit'))

    await waitFor(() => expect(manageCalls).toHaveLength(1))
    expect(manageCalls[0].method).toBe('POST')
    expect(manageCalls[0].pathname).toBe('/api/projects/proj-1/slack-connections/conn-1/manage-access')
    expect(manageCalls[0].body).toEqual({ accessPolicy: 'owner_only', allowMembers: [] })
  })
})
