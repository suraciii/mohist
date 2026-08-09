import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import {
  makeAgent,
  mockAgent,
  renderPage,
  resetState,
  state,
} from './AgentDetailPage.test-support'

describe('AgentDetailPage Actions card', () => {
  beforeEach(resetState)

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('for an active agent, the Archive button does not open the Edit dialog on click', async () => {
    mockAgent(makeAgent({ status: 'active' }))
    renderPage()
    const archiveBtn = await screen.findByTestId('agent-detail-archive-btn')
    fireEvent.click(archiveBtn)
    expect(screen.queryByTestId('agent-profile-editor')).not.toBeInTheDocument()
  })

  it('for an active agent, clicking the Archive button opens a confirm dialog (not a direct archive)', async () => {
    mockAgent(makeAgent({ status: 'active' }))
    renderPage()
    fireEvent.click(await screen.findByTestId('agent-detail-archive-btn'))
    expect(screen.getByTestId('agent-detail-archive-confirm-dialog')).toBeInTheDocument()
    expect(screen.getByTestId('agent-detail-archive-confirm')).toBeInTheDocument()
    expect(screen.getByTestId('agent-detail-archive-cancel')).toBeInTheDocument()
  })

  it('cancelling the archive confirm does NOT archive', async () => {
    mockAgent(makeAgent({ status: 'active' }))
    renderPage()
    fireEvent.click(await screen.findByTestId('agent-detail-archive-btn'))
    fireEvent.click(screen.getByTestId('agent-detail-archive-cancel'))
    expect(screen.queryByTestId('agent-detail-archive-confirm-dialog')).not.toBeInTheDocument()
    expect(state.archiveCalls).toHaveLength(0)
  })

  it('confirming the archive invokes useArchiveAgent.mutate with the agent id and closes the confirm dialog', async () => {
    mockAgent(makeAgent({ status: 'active' }))
    renderPage()
    fireEvent.click(await screen.findByTestId('agent-detail-archive-btn'))
    fireEvent.click(screen.getByTestId('agent-detail-archive-confirm'))
    await waitFor(() => {
      expect(state.archiveCalls).toHaveLength(1)
      expect(screen.queryByTestId('agent-detail-archive-confirm-dialog')).not.toBeInTheDocument()
    })
    expect(state.archiveCalls[0]).toBe('agent-1')
  })

  it('for an archived agent, the static archived notice is replaced by an Unarchive control', async () => {
    mockAgent(makeAgent({ status: 'archived' }))
    renderPage()
    await screen.findByTestId('agent-detail-page')
    expect(screen.queryByText(/this agent is archived and cannot be launched/i)).not.toBeInTheDocument()
    expect(screen.getByTestId('agent-detail-unarchive-btn')).toBeInTheDocument()
    expect(screen.queryByTestId('agent-detail-archive-btn')).not.toBeInTheDocument()
  })

  it('for an archived agent, clicking the Unarchive control invokes useUnarchiveAgent with the agent id', async () => {
    mockAgent(makeAgent({ status: 'archived' }))
    renderPage()
    fireEvent.click(await screen.findByTestId('agent-detail-unarchive-btn'))
    await waitFor(() => {
      expect(state.unarchiveCalls).toEqual(['agent-1'])
      expect(screen.getByTestId('agent-detail-unarchive-btn')).not.toBeDisabled()
    })
  })

  it('for an active agent, the Unarchive control is NOT rendered (no mismatch)', async () => {
    mockAgent(makeAgent({ status: 'active' }))
    renderPage()
    await screen.findByTestId('agent-detail-page')
    expect(screen.queryByTestId('agent-detail-unarchive-btn')).not.toBeInTheDocument()
  })

  it('for an archived agent, the New Session control remains disabled (archived-cannot-launch invariant)', async () => {
    mockAgent(makeAgent({ status: 'archived' }))
    renderPage()
    const newSessionBtn = await screen.findByTestId('agent-detail-new-session')
    expect(newSessionBtn).toBeDisabled()
  })
})
