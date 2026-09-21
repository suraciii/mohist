import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { getAgentConnection, getConnectionDiagnostic, installManagedSlackAgent, listAgentConnections } from './client'

useMswServer()

const CONNECTION_FIXTURE = {
  id: 'conn-1',
  projectId: 'proj-1',
  agentId: 'agent-1',
  providerKind: 'slack',
  workspaceTeamId: '',
  appId: '',
  botUserId: '',
  botName: 'preview',
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
} as const

describe('getConnectionDiagnostic', () => {
  it('gets the project-scoped diagnostic with an escaped connection id', async () => {
    const paths: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId/diagnostic', ({ request }) => {
        paths.push(new URL(request.url).pathname)
        return HttpResponse.json({
          success: true,
          data: {
            primaryState: 'healthy',
            reason: 'Ready',
            nextAction: 'No action needed.',
            facts: {},
          },
        })
      }),
    )

    const diagnostic = await getConnectionDiagnostic('proj-1', 'connection/a')

    expect(paths).toEqual(['/api/projects/proj-1/slack-connections/connection%2Fa/diagnostic'])
    expect(diagnostic.primaryState).toBe('healthy')
  })
})

describe('listAgentConnections', () => {
  it('lists project-scoped slack connections', async () => {
    const paths: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/slack-connections', ({ request }) => {
        paths.push(new URL(request.url).pathname)
        return HttpResponse.json({
          success: true,
          data: [
            {
              ...CONNECTION_FIXTURE,
              id: 'conn-1',
            },
          ],
        })
      }),
    )

    const connections = await listAgentConnections('proj-1')

    expect(paths).toEqual(['/api/projects/proj-1/slack-connections'])
    expect(connections).toHaveLength(1)
    expect(connections[0].id).toBe('conn-1')
    expect(connections[0].agentId).toBe('agent-1')
    expect(connections[0].setupProgress).toBe('create_app_credentials')
  })
})

describe('installManagedSlackAgent', () => {
  it('POSTs only the Agent selection to the managed setup route', async () => {
    const calls: Array<{ method: string; pathname: string; body: unknown }> = []
    server.use(
      http.post('*/api/projects/:projectId/slack-manager/install-agent', async ({ request }) => {
        const text = await request.text()
        let parsed: unknown = text
        try {
          parsed = JSON.parse(text)
        } catch {
          /* keep raw */
        }
        calls.push({ method: request.method, pathname: new URL(request.url).pathname, body: parsed })
        return HttpResponse.json(
          {
            success: true,
            data: {
              connection: { ...CONNECTION_FIXTURE, id: 'conn_new', botName: 'derived-bot' },
              agentApp: {
                appLifecycle: 'created',
                authorization: 'pending',
                runtimeCredentialValidationState: 'not_provided',
                bindingState: 'unbound',
                manifestState: 'applied',
                transportReadiness: 'not_ready',
                nextAction: 'approve_install',
                installUrl: null,
                unknownOutcome: null,
                errorClass: null,
              },
              nextAction: 'approve_install',
              errorClass: null,
            },
          },
          { status: 201 },
        )
      }),
    )

    const response = await installManagedSlackAgent('proj-1', 'agent-1')

    expect(calls).toHaveLength(1)
    expect(calls[0].method).toBe('POST')
    expect(calls[0].pathname).toBe('/api/projects/proj-1/slack-manager/install-agent')
    expect(calls[0].body).toEqual({ agentId: 'agent-1' })
    expect(response.connection.id).toBe('conn_new')
    expect(response.nextAction).toBe('approve_install')
  })
})

describe('getAgentConnection', () => {
  it('fetches the connection detail used by diagnostics', async () => {
    const paths: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId', ({ request }) => {
        paths.push(new URL(request.url).pathname)
        return HttpResponse.json({
          success: true,
          data: {
            connection: CONNECTION_FIXTURE,
          },
        })
      }),
    )

    const detail = await getAgentConnection('proj-1', 'conn-1')

    expect(paths).toEqual(['/api/projects/proj-1/slack-connections/conn-1'])
    expect(detail.connection.id).toBe('conn-1')
  })
})
