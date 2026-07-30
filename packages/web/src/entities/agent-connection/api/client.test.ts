import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { getConnectionDiagnostic } from './client'

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
