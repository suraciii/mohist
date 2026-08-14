import '@testing-library/jest-dom'
import { beforeEach, describe, expect, it } from 'vitest'
import { cleanup, waitFor } from '@testing-library/react'
import { mocks, renderSelector, resetIssueModelSelectorTestState } from './IssueModelSelectorTestSupport'

beforeEach(() => {
  cleanup()
  resetIssueModelSelectorTestState()
})

describe('IssueModelSelector Run-bound runtime', () => {
  it('uses the Run-bound runtime when an active Run has an Agent Action', async () => {
    mocks.useWorkflowRunDetail.mockReturnValue({
      data: {
        status: { workflowRunId: 'run-42', status: 'running' },
        workflowProfileId: 'mohist/github-pr',
        agentAction: 'mohist/pi',
        agentRuntime: 'pi',
      },
      isLoading: false,
      error: null,
    })

    renderSelector({ workflowRunId: 'run-42' })

    await waitFor(() => expect(mocks.useWorkflowRunDetail).toHaveBeenCalledWith('run-42'))
    expect(mocks.useAvailableModelIds).toHaveBeenCalledWith('pi')
  })

  it('fails closed when an active Run has an unknown Agent Action', async () => {
    mocks.useWorkflowRunDetail.mockReturnValue({
      data: {
        status: { workflowRunId: 'run-42', status: 'running' },
        workflowProfileId: 'mohist/github-pr',
        agentAction: 'team/custom-agent',
        agentRuntime: null,
      },
      isLoading: false,
      error: null,
    })

    renderSelector({ workflowRunId: 'run-42' })

    await waitFor(() => expect(mocks.useAvailableModelIds).toHaveBeenCalledWith(null))
    expect(mocks.useAvailableModelIds).not.toHaveBeenCalledWith('opencode')
  })

  it('uses the Run-bound Profile runtime when a legacy active Run has no Agent Action', async () => {
    mocks.useWorkflowProfiles.mockReturnValue({
      data: [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true, agentRuntime: 'opencode' },
        { id: 'team/pi', displayName: 'Pi', description: '', isDefault: false, agentRuntime: 'pi' },
      ],
    })
    mocks.useWorkflowRunDetail.mockReturnValue({
      data: {
        status: { workflowRunId: 'run-42', status: 'running' },
        workflowProfileId: 'team/pi',
        agentAction: null,
        agentRuntime: null,
      },
      isLoading: false,
      error: null,
    })

    renderSelector({ workflowRunId: 'run-42' })

    await waitFor(() => expect(mocks.useAvailableModelIds).toHaveBeenCalledWith('pi'))
    expect(mocks.useAvailableModelIds).not.toHaveBeenCalledWith('opencode')
  })

  it.each([
    { isLoading: true, error: null },
    { isLoading: false, error: new Error('run detail unavailable') },
  ])('fails closed while active Run detail is unavailable', async ({ isLoading, error }) => {
    mocks.useWorkflowRunDetail.mockReturnValue({ data: undefined, isLoading, error })

    renderSelector({ workflowRunId: 'run-42' })

    await waitFor(() => expect(mocks.useAvailableModelIds).toHaveBeenCalledWith(null))
    expect(mocks.useAvailableModelIds).not.toHaveBeenCalledWith('opencode')
  })

  it('returns to the effective Profile runtime after the Run is terminal', async () => {
    mocks.useWorkflowRunDetail.mockReturnValue({
      data: {
        status: { workflowRunId: 'run-42', status: 'completed' },
        workflowProfileId: 'mohist/github-pr',
        agentAction: 'mohist/pi',
        agentRuntime: 'pi',
      },
      isLoading: false,
      error: null,
    })

    renderSelector({ workflowRunId: 'run-42' })

    await waitFor(() => expect(mocks.useAvailableModelIds).toHaveBeenCalledWith('opencode'))
    expect(mocks.useAvailableModelIds).not.toHaveBeenCalledWith('pi')
  })
})
