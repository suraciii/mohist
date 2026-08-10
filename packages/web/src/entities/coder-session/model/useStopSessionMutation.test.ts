import { describe, expect, it, vi } from 'vitest'
import { stopSessionMutationOptions } from './useStopSessionMutation'
import { issueWorkflowKeys } from '../../issue/api/query-keys'

describe('stopSessionMutationOptions', () => {
  it('reconciles the workflow session detail, transcript, and list queries', () => {
    const queryClient = { invalidateQueries: vi.fn() }

    stopSessionMutationOptions('proj-1', queryClient).onSuccess(
      { state: 'not-cancellable' },
      { issueNumber: 42, sessionName: 'build', turnId: 'turn-1' },
    )

    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueWorkflowKeys.session('proj-1', 42, 'coder-sessions'),
      exact: true,
    })
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueWorkflowKeys.session('proj-1', 42, 'session-metadata', 'build'),
      exact: true,
    })
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueWorkflowKeys.session('proj-1', 42, 'session-transcript', 'build'),
    })
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['workflow-runs'] })
  })
})
