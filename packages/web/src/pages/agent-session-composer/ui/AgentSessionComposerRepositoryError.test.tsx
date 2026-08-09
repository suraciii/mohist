import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import {
  makeAgent,
  renderPage,
  resetState,
  state,
} from '../../../../tests/support/agent-session-composer-test-support'

describe('AgentSessionComposer repository loading', () => {
  beforeEach(() => {
    resetState()
    state.agentsData = [makeAgent('agent-1')]
    state.repositoriesError = true
  })

  afterEach(() => {
    cleanup()
  })

  it('fails closed and retries without losing scope or prompt', async () => {
    renderPage(['/agent-sessions/new?agent=agent-1&workspace=workspace-1'])

    const prompt = await screen.findByTestId('prompt-textarea')
    fireEvent.change(prompt, { target: { value: 'Keep this prompt' } })

    expect(screen.getByTestId('context-ref-chip-workspace')).toHaveTextContent('workspace-1')
    expect(screen.getByTestId('composer-repositories-error')).toHaveTextContent('Repositories failed to load.')
    expect(screen.getByTestId('launch-button')).toBeDisabled()

    fireEvent.click(screen.getByTestId('retry-composer-repositories'))

    await waitFor(() => {
      expect(state.repositoryRetryCalls).toBe(1)
      expect(screen.queryByTestId('composer-repositories-error')).not.toBeInTheDocument()
    })
    expect(screen.getByTestId('context-ref-chip-workspace')).toHaveTextContent('workspace-1')
    expect(screen.getByTestId('prompt-textarea')).toHaveValue('Keep this prompt')
    expect(screen.getByTestId('launch-button')).not.toBeDisabled()
  })
})
