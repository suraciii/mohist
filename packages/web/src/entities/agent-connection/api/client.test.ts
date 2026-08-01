import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { createAgentConnection, getConnectionDiagnostic, listAgentConnections } from './client'

useMswServer()

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

describe('createAgentConnection', () => {
  it('POSTs the agent id and returns the create response with derived preview', async () => {
    const calls: Array<{ method: string; pathname: string; body: unknown }> = []
    server.use(
      http.post('*/api/projects/:projectId/slack-connections', async ({ request, params }) => {
        const text = await request.text()
        let parsed: unknown = text
        try { parsed = JSON.parse(text) } catch { /* keep raw */ }
        calls.push({ method: request.method, pathname: new URL(request.url).pathname, body: parsed })
        return HttpResponse.json({
          success: true,
          data: {
            connection: {
              id: 'conn_new',
              projectId: params.projectId as string,
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
            appDescription: 'A description derived from the agent',
            slackAppCreationReference: 'https://api.slack.com/apps?new_app=1',
          },
        }, { status: 201 })
      }),
    )

    const response = await createAgentConnection('proj-1', { agentId: 'agent-1' })

    expect(calls).toHaveLength(1)
    expect(calls[0].method).toBe('POST')
    expect(calls[0].pathname).toBe('/api/projects/proj-1/slack-connections')
    expect(calls[0].body).toEqual({ agentId: 'agent-1' })
    expect(response.connection.id).toBe('conn_new')
    expect(response.botName).toBe('derived-bot')
    expect(response.appDescription).toBe('A description derived from the agent')
    expect(response.slackAppCreationReference).toBe('https://api.slack.com/apps?new_app=1')
  })
})
