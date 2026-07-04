import { beforeEach, describe, expect, it, vi } from 'vitest'
import { renderHook } from '@testing-library/react'
import { useIssueDetailMutations } from './useIssueDetailMutations'

interface MutationConfig {
  mutationFn: (...args: unknown[]) => unknown
  onSuccess?: (...args: unknown[]) => void
  onError?: (...args: unknown[]) => void
}

const useMutationMock = vi.fn()
const useQueryClientMock = vi.fn()
const invalidateQueriesMock = vi.fn()

const apiMocks = {
  startIssue: vi.fn(),
  approveIssue: vi.fn(),
  rejectIssue: vi.fn(),
  updateIssue: vi.fn(),
  closeIssue: vi.fn(),
  reopenIssue: vi.fn(),
  resumeIssue: vi.fn(),
  retryIssue: vi.fn(),
  rerunIssue: vi.fn(),
  forceStopIssue: vi.fn(),
  stopIssue: vi.fn(),
  addPrerequisite: vi.fn(),
  removePrerequisite: vi.fn(),
  addComment: vi.fn(),
  deleteComment: vi.fn(),
  extractAttachmentIds: vi.fn(),
}

vi.mock('@tanstack/react-query', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@tanstack/react-query')>()),
  useMutation: (...args: unknown[]) => useMutationMock(...args),
  useQueryClient: () => useQueryClientMock(),
}))

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    addComment: (...args: unknown[]) => apiMocks.addComment(...args),
    addPrerequisite: (...args: unknown[]) => apiMocks.addPrerequisite(...args),
    approveIssue: (...args: unknown[]) => apiMocks.approveIssue(...args),
    closeIssue: (...args: unknown[]) => apiMocks.closeIssue(...args),
    deleteComment: (...args: unknown[]) => apiMocks.deleteComment(...args),
    extractAttachmentIds: (...args: unknown[]) => apiMocks.extractAttachmentIds(...args),
    forceStopIssue: (...args: unknown[]) => apiMocks.forceStopIssue(...args),
    removePrerequisite: (...args: unknown[]) => apiMocks.removePrerequisite(...args),
    rejectIssue: (...args: unknown[]) => apiMocks.rejectIssue(...args),
    reopenIssue: (...args: unknown[]) => apiMocks.reopenIssue(...args),
    rerunIssue: (...args: unknown[]) => apiMocks.rerunIssue(...args),
    resumeIssue: (...args: unknown[]) => apiMocks.resumeIssue(...args),
    retryIssue: (...args: unknown[]) => apiMocks.retryIssue(...args),
    startIssue: (...args: unknown[]) => apiMocks.startIssue(...args),
    stopIssue: (...args: unknown[]) => apiMocks.stopIssue(...args),
    updateIssue: (...args: unknown[]) => apiMocks.updateIssue(...args),
  }
})

beforeEach(() => {
  useMutationMock.mockReset()
  useQueryClientMock.mockReset()
  invalidateQueriesMock.mockReset()
  for (const fn of Object.values(apiMocks)) fn.mockReset()
  invalidateQueriesMock.mockResolvedValue(undefined)
  useQueryClientMock.mockReturnValue({ invalidateQueries: invalidateQueriesMock })
  // Each call returns its config object so we can call onSuccess/onError later
  useMutationMock.mockImplementation((config: MutationConfig) => config)
  apiMocks.extractAttachmentIds.mockImplementation((body: string) => [`att:${body.split('att:')[1] ?? ''}`])
})

function getMutationConfigs(): MutationConfig[] {
  return useMutationMock.mock.calls.map((call) => call[0] as MutationConfig)
}

const expectedMutationCount = 15

function findMutationByApiCall(apiMock: ReturnType<typeof vi.fn>): MutationConfig {
  // Call every mutationFn with no args; the one that ultimately invokes
  // `apiMock` (the entity API mock) is the matching mutation.
  for (const config of getMutationConfigs()) {
    apiMock.mockClear()
    try {
      (config.mutationFn as () => unknown)()
    } catch {
      // some mutationFns need args; skip and try by checking config
    }
    if (apiMock.mock.calls.length > 0) return config
  }
  throw new Error(`no mutation matched apiMock ${apiMock.getMockName()}`)
}

function findMutationByApiCallWithArg(apiMock: ReturnType<typeof vi.fn>, arg: unknown): MutationConfig {
  for (const config of getMutationConfigs()) {
    apiMock.mockClear()
    try {
      (config.mutationFn as (a: unknown) => unknown)(arg)
    } catch {
      // skip
    }
    if (apiMock.mock.calls.length > 0) return config
  }
  throw new Error(`no mutation matched apiMock ${apiMock.getMockName()}`)
}

describe('useIssueDetailMutations', () => {
  it('registers exactly 15 useMutation calls', () => {
    renderHook(() => useIssueDetailMutations({ issueNumber: 42, projectId: 'proj-1' }))
    expect(useMutationMock).toHaveBeenCalledTimes(expectedMutationCount)
  })

  it('start: invalidates ["issues"] + ["agent-status"] on success, and ["issues"] on "waiting for" error', () => {
    renderHook(() => useIssueDetailMutations({ issueNumber: 7, projectId: 'proj-x' }))
    const start = findMutationByApiCall(apiMocks.startIssue)

    expect(apiMocks.startIssue).toHaveBeenCalledWith(7, 'proj-x')

    invalidateQueriesMock.mockClear()
    start.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })

    invalidateQueriesMock.mockClear()
    const waitingErr = new Error('Issue is waiting for #99')
    start.onError?.(waitingErr)
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues'] })

    invalidateQueriesMock.mockClear()
    start.onError?.(new Error('other failure'))
    expect(invalidateQueriesMock).not.toHaveBeenCalled()
  })

  it('markReady: invalidates ["issues"] + ["issues", issueNumber] on success', () => {
    renderHook(() => useIssueDetailMutations({ issueNumber: 7, projectId: 'proj-x' }))
    const markReady = findMutationByApiCall(apiMocks.updateIssue)

    expect(apiMocks.updateIssue).toHaveBeenCalledWith(7, { isDraft: false }, 'proj-x')

    invalidateQueriesMock.mockClear()
    markReady.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues', 7] })
  })

  it('approve / send-back: call approval APIs and invalidate runtime plus approval wait queries', () => {
    renderHook(() => useIssueDetailMutations({ issueNumber: 7, projectId: 'proj-x' }))

    const approve = findMutationByApiCall(apiMocks.approveIssue)
    expect(apiMocks.approveIssue).toHaveBeenCalledWith(7, 'proj-x')

    invalidateQueriesMock.mockClear()
    approve.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues', 7] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues', 'metrics', 'approval-wait'] })

    const sendBack = findMutationByApiCall(apiMocks.rejectIssue)
    expect(apiMocks.rejectIssue).toHaveBeenCalledWith(7, {}, 'proj-x')

    invalidateQueriesMock.mockClear()
    sendBack.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues', 7] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues', 'metrics', 'approval-wait'] })
  })

  it('addPrerequisite: passes the prerequisite number and invalidates ["issues"] + ["issues", issueNumber] on success', () => {
    renderHook(() => useIssueDetailMutations({ issueNumber: 7, projectId: 'proj-x' }))
    const add = findMutationByApiCallWithArg(apiMocks.addPrerequisite, 99)

    expect(apiMocks.addPrerequisite).toHaveBeenCalledWith(7, 99, 'proj-x')

    invalidateQueriesMock.mockClear()
    add.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues', 7] })
  })

  it('removePrerequisite: passes the prerequisite number and invalidates ["issues"] + ["issues", issueNumber] on success', () => {
    renderHook(() => useIssueDetailMutations({ issueNumber: 7, projectId: 'proj-x' }))
    const remove = findMutationByApiCallWithArg(apiMocks.removePrerequisite, 99)

    expect(apiMocks.removePrerequisite).toHaveBeenCalledWith(7, 99, 'proj-x')

    invalidateQueriesMock.mockClear()
    remove.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues', 7] })
  })

  it('close: invalidates ["issues"] only on success', () => {
    renderHook(() => useIssueDetailMutations({ issueNumber: 7, projectId: 'proj-x' }))
    const close = findMutationByApiCall(apiMocks.closeIssue)

    expect(apiMocks.closeIssue).toHaveBeenCalledWith(7, 'proj-x')

    invalidateQueriesMock.mockClear()
    close.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledTimes(1)
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues'] })
  })

  it('reopen: invalidates ["issues"] only on success', () => {
    renderHook(() => useIssueDetailMutations({ issueNumber: 7, projectId: 'proj-x' }))
    const reopen = findMutationByApiCall(apiMocks.reopenIssue)

    expect(apiMocks.reopenIssue).toHaveBeenCalledWith(7, 'proj-x')

    invalidateQueriesMock.mockClear()
    reopen.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledTimes(1)
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues'] })
  })

  it('resume / retry / rerun: each invalidate ["issues"] + ["agent-status"] on success', () => {
    renderHook(() => useIssueDetailMutations({ issueNumber: 7, projectId: 'proj-x' }))

    for (const apiFn of [apiMocks.resumeIssue, apiMocks.retryIssue, apiMocks.rerunIssue]) {
      invalidateQueriesMock.mockClear()
      apiFn.mockClear()
      const m = findMutationByApiCall(apiFn)
      expect(apiFn).toHaveBeenCalledWith(7, 'proj-x')
      m.onSuccess?.()
      expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues'] })
      expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    }
  })

  it('forceStop: invalidates ["issues"] + ["agent-status"] on success, then fires onForceStopSuccess callback', () => {
    const onForceStopSuccess = vi.fn()
    renderHook(() => useIssueDetailMutations({
      issueNumber: 7,
      projectId: 'proj-x',
      onForceStopSuccess,
    }))
    const forceStop = findMutationByApiCall(apiMocks.forceStopIssue)

    expect(apiMocks.forceStopIssue).toHaveBeenCalledWith(7, 'proj-x')

    invalidateQueriesMock.mockClear()
    forceStop.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(onForceStopSuccess).toHaveBeenCalledTimes(1)
  })

  it('forceStop: succeeds without onForceStopSuccess callback (no TypeError)', () => {
    renderHook(() => useIssueDetailMutations({ issueNumber: 7, projectId: 'proj-x' }))
    const forceStop = findMutationByApiCall(apiMocks.forceStopIssue)
    invalidateQueriesMock.mockClear()
    expect(() => forceStop.onSuccess?.()).not.toThrow()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
  })

  it('stop: invalidates ["issues"] + ["agent-status"] on success, then fires onStopSuccess callback', () => {
    const onStopSuccess = vi.fn()
    renderHook(() => useIssueDetailMutations({
      issueNumber: 7,
      projectId: 'proj-x',
      onStopSuccess,
    }))
    const stop = findMutationByApiCall(apiMocks.stopIssue)

    expect(apiMocks.stopIssue).toHaveBeenCalledWith(7, 'proj-x')

    invalidateQueriesMock.mockClear()
    stop.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(onStopSuccess).toHaveBeenCalledTimes(1)
  })

  it('addComment: passes the body + extracted attachment ids, invalidates ["issues", issueNumber] on success, then fires onAddCommentSuccess', () => {
    const onAddCommentSuccess = vi.fn()
    renderHook(() => useIssueDetailMutations({
      issueNumber: 7,
      projectId: 'proj-x',
      onAddCommentSuccess,
    }))
    const addComment = findMutationByApiCallWithArg(apiMocks.addComment, 'look at att:abc-123')

    expect(apiMocks.extractAttachmentIds).toHaveBeenCalledWith('look at att:abc-123')
    expect(apiMocks.addComment).toHaveBeenCalledWith(7, 'look at att:abc-123', 'proj-x', ['att:abc-123'])

    invalidateQueriesMock.mockClear()
    addComment.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues', 7] })
    expect(invalidateQueriesMock).toHaveBeenCalledTimes(1)
    expect(onAddCommentSuccess).toHaveBeenCalledTimes(1)
  })

  it('deleteComment: passes the comment id, invalidates ["issues", issueNumber] on success, then fires onDeleteCommentSuccess', () => {
    const onDeleteCommentSuccess = vi.fn()
    renderHook(() => useIssueDetailMutations({
      issueNumber: 7,
      projectId: 'proj-x',
      onDeleteCommentSuccess,
    }))
    const deleteComment = findMutationByApiCallWithArg(apiMocks.deleteComment, 'comment-99')

    expect(apiMocks.deleteComment).toHaveBeenCalledWith(7, 'comment-99', 'proj-x')

    invalidateQueriesMock.mockClear()
    deleteComment.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues', 7] })
    expect(invalidateQueriesMock).toHaveBeenCalledTimes(1)
    expect(onDeleteCommentSuccess).toHaveBeenCalledTimes(1)
  })

  it('deleteComment: on error fires onDeleteCommentError with an Error instance carrying the message', () => {
    const onDeleteCommentError = vi.fn()
    renderHook(() => useIssueDetailMutations({
      issueNumber: 7,
      projectId: 'proj-x',
      onDeleteCommentError,
    }))
    const deleteComment = findMutationByApiCallWithArg(apiMocks.deleteComment, 'comment-99')

    deleteComment.onError?.(new Error('boom'))
    expect(onDeleteCommentError).toHaveBeenCalledTimes(1)
    const arg = onDeleteCommentError.mock.calls[0][0] as Error
    expect(arg).toBeInstanceOf(Error)
    expect(arg.message).toBe('boom')

    onDeleteCommentError.mockClear()
    deleteComment.onError?.('string-not-error')
    expect(onDeleteCommentError).toHaveBeenCalledTimes(1)
    const fallback = onDeleteCommentError.mock.calls[0][0] as Error
    expect(fallback).toBeInstanceOf(Error)
    expect(fallback.message).toBe('Failed to delete comment')
  })

  it('deleteComment: on error does NOT invalidate the issues query (cache invalidation lives only in onSuccess)', () => {
    renderHook(() => useIssueDetailMutations({ issueNumber: 7, projectId: 'proj-x' }))
    const deleteComment = findMutationByApiCallWithArg(apiMocks.deleteComment, 'comment-99')

    invalidateQueriesMock.mockClear()
    deleteComment.onError?.(new Error('boom'))
    expect(invalidateQueriesMock).not.toHaveBeenCalled()
  })

  it('mutations accept undefined projectId without TypeError on success', () => {
    renderHook(() => useIssueDetailMutations({ issueNumber: 7, projectId: undefined }))
    expect(() => {
      for (const config of getMutationConfigs()) {
        invalidateQueriesMock.mockClear()
        config.onSuccess?.()
      }
    }).not.toThrow()
  })
})
