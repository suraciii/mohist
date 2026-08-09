import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import {
  makeAgent,
  makeWorkspace,
  renderPage,
  resetState,
  state,
} from '../../../../tests/support/agent-session-composer-test-support'

describe('AgentSessionComposer workspace loading', () => {
  beforeEach(() => {
    resetState()
    state.agentsData = [makeAgent('agent-1')]
    state.repositoriesData = [{
      name: 'main',
      gitUrl: 'https://example.test/main.git',
      baseBranch: 'main',
      isDefault: true,
    }]
    state.workspacesData = [makeWorkspace('workspace-1', ['main'])]
    state.workspacesError = true
  })

  afterEach(() => {
    cleanup()
  })

  it('fails closed on a workspace query 500 and retries without losing scope or prompt', async () => {
    renderPage(['/agent-sessions/new?agent=agent-1&workspace=workspace-1&repo=main'])

    const prompt = await screen.findByTestId('prompt-textarea')
    fireEvent.change(prompt, { target: { value: 'Keep this workspace scope' } })

    expect(screen.getByTestId('composer-workspaces-error')).toHaveTextContent('Workspaces failed to load.')
    expect(screen.getByTestId('launch-button')).toBeDisabled()

    fireEvent.click(screen.getByTestId('retry-composer-workspaces'))

    await waitFor(() => {
      expect(state.workspaceRetryCalls).toBe(1)
      expect(screen.queryByTestId('composer-workspaces-error')).not.toBeInTheDocument()
    })
    expect(screen.getByTestId('context-ref-chip-workspace')).toHaveTextContent('workspace-1')
    expect(screen.getByTestId('context-ref-chip-repository')).toHaveTextContent('main')
    expect(screen.getByTestId('prompt-textarea')).toHaveValue('Keep this workspace scope')
    expect(screen.getByTestId('launch-button')).not.toBeDisabled()
  })

  it('does not inherit repository error or retry state after shared reset', () => {
    state.repositoriesError = true
    state.repositoryRetryCalls = 4
    resetState()

    expect(state.repositoriesError).toBe(false)
    expect(state.repositoryRetryCalls).toBe(0)
  })
})
