import { describe, expect, it, vi } from 'vitest'
import { cancelSessionMutationOptions } from './useCancelSessionMutation'

describe('cancelSessionMutationOptions', () => {
  it('reconciles the workflow session detail, transcript, and list queries', () => {
    const queryClient = { invalidateQueries: vi.fn() }

    cancelSessionMutationOptions('proj-1', queryClient).onSuccess(
      { state: 'not-cancellable' },
      { issueNumber: 42, sessionName: 'build' },
    )

    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['issues', 42, 'proj-1', 'coder-sessions'] })
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['issues', 42, 'proj-1', 'agent-session-metadata', 'build'] })
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['issues', 42, 'proj-1', 'agent-session-transcript', 'build'] })
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['workflow-runs'] })
  })
})
