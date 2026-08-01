import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import { server, useMswServer } from '../../../../tests/support/msw'
import { ConnectionDiagnosticPage } from './ConnectionDiagnosticPage'
import type {
  AgentConnectionClaimOwnerResponse,
  ConnectionDiagnostic,
  ConnectionDiagnosticFacts,
} from '../../../entities/agent-connection'

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

function makeDetail() {
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
    botName: 'derived-bot',
    appDescription: 'Derived description for the Agent',
    slackAppCreationReference: 'https://api.slack.com/apps?new_app=1',
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
            <Route
              path="/:projectName/connections/:connectionId"
              element={<ConnectionDiagnosticPage />}
            />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

afterEach(cleanup)

describe('ConnectionDiagnosticPage — configure & claim-owner actions (MSW)', () => {
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

  it('POSTs credentials only in the request body to /configure and the masked input never displays the tokens', async () => {
    const configureCalls: Array<{ method: string; pathname: string; body: unknown; url: string }> = []
    server.use(
      http.post(
        '*/api/projects/:projectId/slack-connections/:connectionId/configure',
        async ({ request }) => {
          const text = await request.text()
          let parsed: unknown = text
          try { parsed = JSON.parse(text) } catch { /* keep raw */ }
          const url = new URL(request.url)
          configureCalls.push({
            method: request.method,
            pathname: url.pathname,
            body: parsed,
            url: url.toString(),
          })
          return HttpResponse.json({
            success: true,
            data: { ...makeDetail().connection, setupProgress: 'waiting_for_slack_service' },
          })
        },
      ),
    )

    const user = userEvent.setup()
    renderPage()

    const appInput = (await screen.findByLabelText('App token')) as HTMLInputElement
    const botInput = screen.getByLabelText('Bot token') as HTMLInputElement
    expect(appInput.type).toBe('password')
    expect(botInput.type).toBe('password')

    await user.type(appInput, 'xapp-1-A-SECRET-CONFIGURE')
    await user.type(botInput, 'xoxb-1-B-SECRET-CONFIGURE')

    await user.click(screen.getByTestId('connection-setup-credential-form-submit'))

    await waitFor(() => {
      expect(configureCalls).toHaveLength(1)
    })
    expect(configureCalls[0].method).toBe('POST')
    expect(configureCalls[0].pathname).toBe('/api/projects/proj-1/slack-connections/conn-1/configure')
    expect(configureCalls[0].body).toEqual({
      appToken: 'xapp-1-A-SECRET-CONFIGURE',
      botToken: 'xoxb-1-B-SECRET-CONFIGURE',
    })
    expect(configureCalls[0].url).not.toContain('xapp-')
    expect(configureCalls[0].url).not.toContain('xoxb-')

    expect(document.body.textContent ?? '').not.toContain('xapp-1-A-SECRET-CONFIGURE')
    expect(document.body.textContent ?? '').not.toContain('xoxb-1-B-SECRET-CONFIGURE')
    expect(appInput.value).toBe('')
    expect(botInput.value).toBe('')
    expect(window.localStorage.length).toBe(0)
    expect(window.sessionStorage.length).toBe(0)
  })

  it('does not provide any reveal or show toggle for the credentials', async () => {
    renderPage()

    await screen.findByTestId('connection-setup-credential-form')
    expect(screen.queryByRole('button', { name: /reveal|show/i })).not.toBeInTheDocument()
  })

  it('never exposes the configure token in the URL, sessionStorage, or localStorage across the submit cycle', async () => {
    let savedConfigures = 0
    server.use(
      http.post('*/api/projects/:projectId/slack-connections/:connectionId/configure', () => {
        savedConfigures += 1
        return HttpResponse.json({
          success: true,
          data: { ...makeDetail().connection, setupProgress: 'waiting_for_slack_service' },
        })
      }),
    )

    const user = userEvent.setup()
    const setItemSpy = vi.spyOn(Storage.prototype, 'setItem')
    const originalHref = window.location.href

    renderPage()

    const appInput = (await screen.findByLabelText('App token')) as HTMLInputElement
    await user.type(appInput, 'xapp-1-A-PERSIST')
    await user.type(screen.getByLabelText('Bot token'), 'xoxb-1-B-PERSIST')
    fireEvent.click(screen.getByTestId('connection-setup-credential-form-submit'))

    await waitFor(() => {
      expect(savedConfigures).toBe(1)
    })

    expect(window.location.href).toBe(originalHref)
    expect(window.localStorage.length).toBe(0)
    expect(window.sessionStorage.length).toBe(0)
    for (const call of setItemSpy.mock.calls) {
      const payload = call[1]
      if (typeof payload === 'string') {
        expect(payload).not.toContain('xapp-1-A-PERSIST')
        expect(payload).not.toContain('xoxb-1-B-PERSIST')
      }
    }
    setItemSpy.mockRestore()
  })

  it('shows the claim-owner code once and regenerating POSTs again (server-side supersedes)', async () => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({
          success: true,
          data: makeDiagnostic({
            primaryState: 'setup_incomplete',
            reason: "Slack setup is incomplete at 'claim_owner'.",
            nextAction: 'Advance the current setup step.',
            facts: makeFacts({ setupProgress: 'claim_owner' }),
          }),
        }),
      ),
    )
    const claimCalls: Array<{ pathname: string }> = []
    let lastCode: AgentConnectionClaimOwnerResponse = {
      code: 'CLAIM-CODE-1',
      expiresAt: '2026-08-01T01:00:00.000Z',
    }
    server.use(
      http.post(
        '*/api/projects/:projectId/slack-connections/:connectionId/claim-owner',
        ({ request }) => {
          claimCalls.push({ pathname: new URL(request.url).pathname })
          const response: AgentConnectionClaimOwnerResponse = lastCode
          lastCode = { code: 'CLAIM-CODE-2', expiresAt: '2026-08-01T02:00:00.000Z' }
          return HttpResponse.json({ success: true, data: response })
        },
      ),
    )

    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByTestId('connection-setup-claim-owner-generate'))

    expect(await screen.findByTestId('connection-setup-claim-owner-code')).toHaveTextContent('CLAIM-CODE-1')
    expect(claimCalls).toHaveLength(1)

    await user.click(screen.getByTestId('connection-setup-claim-owner-generate'))

    await waitFor(() => {
      expect(claimCalls).toHaveLength(2)
    })
    expect(claimCalls[0].pathname).toBe('/api/projects/proj-1/slack-connections/conn-1/claim-owner')
  })

  it('discards the displayed code on unmount', async () => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', () =>
        HttpResponse.json({
          success: true,
          data: makeDiagnostic({
            primaryState: 'setup_incomplete',
            reason: "Slack setup is incomplete at 'claim_owner'.",
            nextAction: 'Advance the current setup step.',
            facts: makeFacts({ setupProgress: 'claim_owner' }),
          }),
        }),
      ),
      http.post('*/api/projects/:projectId/slack-connections/:connectionId/claim-owner', () =>
        HttpResponse.json({
          success: true,
          data: { code: 'SHOULD-BE-DISCARDED', expiresAt: '2026-08-01T01:00:00.000Z' },
        }),
      ),
    )

    const user = userEvent.setup()
    const { unmount } = renderPage()

    await user.click(await screen.findByTestId('connection-setup-claim-owner-generate'))
    expect(await screen.findByTestId('connection-setup-claim-owner-code')).toHaveTextContent('SHOULD-BE-DISCARDED')

    unmount()
    expect(document.body.textContent ?? '').not.toContain('SHOULD-BE-DISCARDED')
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
    expect(screen.getByTestId('connection-setup-step-waiting_for_slack_service')).toHaveAttribute('data-state', 'current')
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
    )
  })

  it('loads access state and POSTs the replaced allowlist to /manage-access', async () => {
    const manageCalls: Array<{ method: string; pathname: string; body: unknown }> = []
    server.use(
      http.post(
        '*/api/projects/:projectId/slack-connections/:connectionId/manage-access',
        async ({ request }) => {
          const text = await request.text()
          let parsed: unknown = text
          try { parsed = JSON.parse(text) } catch { /* keep raw */ }
          const url = new URL(request.url)
          manageCalls.push({ method: request.method, pathname: url.pathname, body: parsed })
          return HttpResponse.json({
            success: true,
            data: { ...makeDetail().connection, accessPolicy: 'owner_only' },
          })
        },
      ),
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
