import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'

import { makeAgent, makeWorkspace, renderPage, resetState, state } from '../../../../tests/support/agent-session-composer-test-support'
import { server, useMswServer } from '../../../../tests/support/msw'

describe('AgentSessionComposer workspace creation', () => {
  useMswServer()

  beforeEach(() => {
    resetState()
    state.agentsData = []
    state.availabilityData = []
    state.launchCalls.length = 0
    state.launchError = null
    state.launchFailuresRemaining = -1
    state.launchResponse = null
    state.repositoriesData = []
    state.workspacesData = []
    state.issuesData = []
    state.epicsData = []
  })

  afterEach(() => {
    cleanup()
  })

  it('selects a created workspace and removes a repository absent from returned membership', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.repositoriesData = [
      { name: 'main', gitUrl: 'https://example.test/main.git', baseBranch: 'main', isDefault: true },
      { name: 'other', gitUrl: 'https://example.test/other.git', baseBranch: 'main', isDefault: false },
    ]
    let requestBody: unknown
    server.use(
      http.get('*/api/projects/:projectId/repositories', () => HttpResponse.json({ success: true, data: state.repositoriesData })),
      http.post('*/api/projects/:projectId/workspaces', async ({ request }) => {
        requestBody = await request.json()
        return HttpResponse.json({
          success: true,
          data: makeWorkspace('created-workspace', ['main']),
        })
      }),
    )

    renderPage(['/agent-sessions/new?agent=agent-1&repo=other'])
    fireEvent.click(await screen.findByTestId('create-workspace-from-composer'))
    fireEvent.change(await screen.findByTestId('create-workspace-name'), { target: { value: 'created-workspace' } })
    fireEvent.click(screen.getByTestId('create-workspace-repository-main'))
    fireEvent.click(screen.getByTestId('create-workspace-repository-other'))
    fireEvent.click(screen.getByTestId('create-workspace-submit'))

    await waitFor(() => expect(screen.getByTestId('launch-workspace')).toHaveValue('created-workspace'))
    expect(screen.queryByTestId('context-ref-chip-repository')).not.toBeInTheDocument()
    expect(requestBody).toEqual({ name: 'created-workspace', repos: ['main'] })
  })
})
