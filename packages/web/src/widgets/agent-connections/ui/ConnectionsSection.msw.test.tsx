import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import { server, useMswServer } from '../../../../tests/support/msw'
import { ConnectionsSection } from './ConnectionsSection'
import type { AgentInfo } from '../../../entities/agent'
import type { AgentConnectionDto } from '../../../entities/agent-connection'

useMswServer()

function makeAgent(overrides: Partial<AgentInfo> = {}): AgentInfo {
  return {
    id: 'agent-1',
    projectId: 'proj-1',
    name: 'Test Agent',
    description: '',
    instructions: '...',
    agentConfig: null,
    skills: [],
    maxConcurrentRuns: null,
    status: 'active',
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  }
}

function makeConnection(overrides: Partial<AgentConnectionDto> = {}): AgentConnectionDto {
  return {
    id: 'conn_msw',
    projectId: 'proj-1',
    agentId: 'agent-1',
    providerKind: 'slack',
    workspaceTeamId: '',
    appId: '',
    botUserId: '',
    botName: 'preview-bot',
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
    ...overrides,
  }
}

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function renderSection(agent: AgentInfo = makeAgent(), { withRoutes = false }: { withRoutes?: boolean } = {}) {
  const queryClient = createQueryClient()
  if (withRoutes) {
    return render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[{
          id: 'proj-1', name: 'Test',
          createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z',
          repositories: [],
        }]}>
          <MemoryRouter initialEntries={['/Test/agents/agent-1']}>
            <Routes>
              <Route
                path="/:projectName/agents/:agentId"
                element={<ConnectionsSection agent={agent} />}
              />
              <Route
                path="/:projectName/connections/:connectionId"
                element={<div data-testid="connection-page" />}
              />
            </Routes>
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )
  }
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1', name: 'Test',
        createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z',
        repositories: [],
      }]}>
        <MemoryRouter>
          <ConnectionsSection agent={agent} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('ConnectionsSection (MSW integration)', () => {
  beforeEach(() => {
    // Default: empty list, will be overridden per-test as needed.
    server.use(
      http.get('*/api/projects/:projectId/slack-connections', () =>
        HttpResponse.json({ success: true, data: [] }),
      ),
    )
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders the empty state when the list returns no connections for the agent', async () => {
    renderSection()
    expect(await screen.findByTestId('agent-connections-empty')).toBeInTheDocument()
  })

  it('renders connection rows returned by the list, filtered to the bound agent', async () => {
    server.use(
      http.get('*/api/projects/:projectId/slack-connections', () =>
        HttpResponse.json({
          success: true,
          data: [
            makeConnection({ id: 'conn_mine', agentId: 'agent-1', botName: 'mine' }),
            makeConnection({ id: 'conn_other', agentId: 'agent-2', botName: 'other' }),
          ],
        }),
      ),
    )

    renderSection()
    expect(await screen.findByTestId('agent-connection-row-conn_mine')).toBeInTheDocument()
    expect(screen.queryByTestId('agent-connection-row-conn_other')).not.toBeInTheDocument()
  })

  it('POSTs create on Add Slack and navigates to the new connection page', async () => {
    const createCalls: Array<{ method: string; body: unknown }> = []
    server.use(
      http.post('*/api/projects/:projectId/slack-connections', async ({ request }) => {
        const text = await request.text()
        let body: unknown = text
        try { body = JSON.parse(text) } catch { /* keep raw */ }
        createCalls.push({ method: request.method, body })
        return HttpResponse.json({
          success: true,
          data: {
            connection: makeConnection({ id: 'conn_created', agentId: 'agent-1', botName: 'preview-bot' }),
            botName: 'preview-bot',
            appDescription: 'A derived description',
            slackAppCreationReference: 'https://api.slack.com/apps?new_app=1',
          },
        }, { status: 201 })
      }),
    )

    renderSection(makeAgent(), { withRoutes: true })

    await act(async () => {
      fireEvent.click(await screen.findByTestId('agent-connections-add-slack'))
    })

    await waitFor(() => {
      expect(createCalls).toHaveLength(1)
    })
    expect(createCalls[0].method).toBe('POST')
    expect(createCalls[0].body).toEqual({ agentId: 'agent-1' })
    expect(await screen.findByTestId('connection-page')).toBeInTheDocument()
  })
})
