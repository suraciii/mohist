import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import {
  claimAgentConnectionOwner,
  configureAgentConnection,
  createAgentConnection,
  getAgentConnection,
  getConnectionDiagnostic,
  listAgentConnections,
} from './client'

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

describe('createAgentConnection', () => {
  it('POSTs the agent id and returns the create response with derived preview', async () => {
    const calls: Array<{ method: string; pathname: string; body: unknown }> = []
    server.use(
      http.post('*/api/projects/:projectId/slack-connections', async ({ request }) => {
        const text = await request.text()
        let parsed: unknown = text
        try { parsed = JSON.parse(text) } catch { /* keep raw */ }
        calls.push({ method: request.method, pathname: new URL(request.url).pathname, body: parsed })
        return HttpResponse.json({
          success: true,
          data: {
            connection: { ...CONNECTION_FIXTURE, id: 'conn_new', botName: 'derived-bot' },
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

describe('getAgentConnection', () => {
  it('fetches the connection detail with the derived identity preview', async () => {
    const paths: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/slack-connections/:connectionId', ({ request }) => {
        paths.push(new URL(request.url).pathname)
        return HttpResponse.json({
          success: true,
          data: {
            connection: CONNECTION_FIXTURE,
            botName: 'derived-bot',
            appDescription: 'Derived from Agent description',
            slackAppCreationReference: 'https://api.slack.com/apps?new_app=1',
          },
        })
      }),
    )

    const detail = await getAgentConnection('proj-1', 'conn-1')

    expect(paths).toEqual(['/api/projects/proj-1/slack-connections/conn-1'])
    expect(detail.botName).toBe('derived-bot')
    expect(detail.appDescription).toBe('Derived from Agent description')
    expect(detail.slackAppCreationReference).toBe('https://api.slack.com/apps?new_app=1')
  })
})

describe('configureAgentConnection', () => {
  it('POSTs the credentials in the body to /configure and never puts tokens in the URL', async () => {
    const calls: Array<{ method: string; pathname: string; body: unknown; url: string }> = []
    server.use(
      http.post('*/api/projects/:projectId/slack-connections/:connectionId/configure', async ({ request }) => {
        const text = await request.text()
        let parsed: unknown = text
        try { parsed = JSON.parse(text) } catch { /* keep raw */ }
        const url = new URL(request.url)
        calls.push({
          method: request.method,
          pathname: url.pathname,
          body: parsed,
          url: url.toString(),
        })
        return HttpResponse.json({
          success: true,
          data: { ...CONNECTION_FIXTURE, setupProgress: 'waiting_for_slack_service' },
        })
      }),
    )

    await configureAgentConnection('proj-1', 'conn-1', {
      appToken: 'xapp-1-A-SECRET',
      botToken: 'xoxb-1-B-SECRET',
    })

    expect(calls).toHaveLength(1)
    expect(calls[0].method).toBe('POST')
    expect(calls[0].pathname).toBe('/api/projects/proj-1/slack-connections/conn-1/configure')
    expect(calls[0].body).toEqual({
      appToken: 'xapp-1-A-SECRET',
      botToken: 'xoxb-1-B-SECRET',
    })
    expect(calls[0].url).not.toContain('xapp-')
    expect(calls[0].url).not.toContain('xoxb-')
  })
})

describe('claimAgentConnectionOwner', () => {
  it('POSTs to /claim-owner and returns the one-time code', async () => {
    const paths: string[] = []
    server.use(
      http.post('*/api/projects/:projectId/slack-connections/:connectionId/claim-owner', ({ request }) => {
        paths.push(new URL(request.url).pathname)
        return HttpResponse.json({
          success: true,
          data: { code: 'CLAIM-CODE-12345', expiresAt: '2026-08-01T01:00:00.000Z' },
        })
      }),
    )

    const response = await claimAgentConnectionOwner('proj-1', 'conn-1')

    expect(paths).toEqual(['/api/projects/proj-1/slack-connections/conn-1/claim-owner'])
    expect(response.code).toBe('CLAIM-CODE-12345')
    expect(response.expiresAt).toBe('2026-08-01T01:00:00.000Z')
  })
})
