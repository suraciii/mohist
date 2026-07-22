import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { QueryClient } from '@tanstack/react-query'
import {
  createIssueDetailMutationOptions,
  type IssueDetailMutationDependencies,
  type UseIssueDetailMutationsOptions,
} from './useIssueDetailMutations'
import { issueDetailKeys, issueListKeys } from '../../../entities/issue/api/query-keys'

interface MutationConfig {
  mutationFn: (...args: unknown[]) => unknown
  onSuccess?: (...args: unknown[]) => void
  onError?: (...args: unknown[]) => void
}

const invalidateQueriesMock = vi.fn()

const apiMocks = {
  startIssue: vi.fn<IssueDetailMutationDependencies['startIssue']>(),
  approveIssue: vi.fn<IssueDetailMutationDependencies['approveIssue']>(),
  requestChangesIssue: vi.fn<IssueDetailMutationDependencies['requestChangesIssue']>(),
  updateIssue: vi.fn<IssueDetailMutationDependencies['updateIssue']>(),
  closeIssue: vi.fn<IssueDetailMutationDependencies['closeIssue']>(),
  markIssueDone: vi.fn<IssueDetailMutationDependencies['markIssueDone']>(),
  reopenIssue: vi.fn<IssueDetailMutationDependencies['reopenIssue']>(),
  resumeIssue: vi.fn<IssueDetailMutationDependencies['resumeIssue']>(),
  retryIssue: vi.fn<IssueDetailMutationDependencies['retryIssue']>(),
  rerunIssue: vi.fn<IssueDetailMutationDependencies['rerunIssue']>(),
  forceStopIssue: vi.fn<IssueDetailMutationDependencies['forceStopIssue']>(),
  stopIssue: vi.fn<IssueDetailMutationDependencies['stopIssue']>(),
  addPrerequisite: vi.fn<IssueDetailMutationDependencies['addPrerequisite']>(),
  removePrerequisite: vi.fn<IssueDetailMutationDependencies['removePrerequisite']>(),
  addComment: vi.fn<IssueDetailMutationDependencies['addComment']>(),
  deleteComment: vi.fn<IssueDetailMutationDependencies['deleteComment']>(),
  extractAttachmentIds: vi.fn<IssueDetailMutationDependencies['extractAttachmentIds']>(),
  invalidateApprovalWait: vi.fn<IssueDetailMutationDependencies['invalidateApprovalWait']>(),
}

let mutationConfigs: ReturnType<typeof createIssueDetailMutationOptions>

const listInvalidation = { queryKey: issueListKeys.project('proj-x') }
const detailInvalidation = { queryKey: issueDetailKeys.detail('proj-x', 7), exact: true }

const queryClient = {
  invalidateQueries: invalidateQueriesMock,
} as unknown as QueryClient

beforeEach(() => {
  invalidateQueriesMock.mockReset()
  for (const fn of Object.values(apiMocks)) fn.mockReset()
  invalidateQueriesMock.mockResolvedValue(undefined)
  apiMocks.extractAttachmentIds.mockImplementation((body: string) => [`att:${body.split('att:')[1] ?? ''}`])
  apiMocks.invalidateApprovalWait.mockImplementation((client) => {
    client.invalidateQueries({ queryKey: ['issues', 'metrics', 'approval-wait'] })
  })
})

function getMutationConfigs(): MutationConfig[] {
  return Object.values(mutationConfigs) as MutationConfig[]
}

const expectedMutationCount = 16

function arrange(options: UseIssueDetailMutationsOptions) {
  mutationConfigs = createIssueDetailMutationOptions(options, queryClient, apiMocks)
}

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
  it('registers exactly 16 useMutation calls', () => {
    arrange({ issueNumber: 42, projectId: 'proj-1' })
    expect(getMutationConfigs()).toHaveLength(expectedMutationCount)
  })

  it('start: invalidates ["issues"] + ["agent-status"] on success, and ["issues"] on "waiting for" error', () => {
    arrange({ issueNumber: 7, projectId: 'proj-x' })
    const start = findMutationByApiCall(apiMocks.startIssue)

    expect(apiMocks.startIssue).toHaveBeenCalledWith(7, 'proj-x')

    invalidateQueriesMock.mockClear()
    start.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })

    invalidateQueriesMock.mockClear()
    const waitingErr = new Error('Issue is waiting for #99')
    start.onError?.(waitingErr)
    expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)

    invalidateQueriesMock.mockClear()
    start.onError?.(new Error('other failure'))
    expect(invalidateQueriesMock).not.toHaveBeenCalled()
  })

  it('markReady: invalidates ["issues"] + ["issues", issueNumber] on success', () => {
    arrange({ issueNumber: 7, projectId: 'proj-x' })
    const markReady = findMutationByApiCall(apiMocks.updateIssue)

    expect(apiMocks.updateIssue).toHaveBeenCalledWith(7, { isDraft: false }, 'proj-x')

    invalidateQueriesMock.mockClear()
    markReady.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith(detailInvalidation)
  })

  it('approve / send-back: call approval APIs and invalidate runtime plus approval wait queries', () => {
    arrange({ issueNumber: 7, projectId: 'proj-x' })

    const approve = findMutationByApiCall(apiMocks.approveIssue)
    expect(apiMocks.approveIssue).toHaveBeenCalledWith(7, 'proj-x')

    invalidateQueriesMock.mockClear()
    approve.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith(detailInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues', 'metrics', 'approval-wait'] })

    const sendBack = findMutationByApiCallWithArg(apiMocks.requestChangesIssue, {
      stage: 'check',
      body: 'Please update the implementation.',
    })
    expect(apiMocks.requestChangesIssue).toHaveBeenCalledWith(
      7,
      { stage: 'check', body: 'Please update the implementation.' },
      'proj-x',
    )

    invalidateQueriesMock.mockClear()
    sendBack.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith(detailInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['issues', 'metrics', 'approval-wait'] })
  })

  it('addPrerequisite: passes the prerequisite number and invalidates ["issues"] + ["issues", issueNumber] on success', () => {
    arrange({ issueNumber: 7, projectId: 'proj-x' })
    const add = findMutationByApiCallWithArg(apiMocks.addPrerequisite, 99)

    expect(apiMocks.addPrerequisite).toHaveBeenCalledWith(7, 99, 'proj-x')

    invalidateQueriesMock.mockClear()
    add.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith(detailInvalidation)
  })

  it('removePrerequisite: passes the prerequisite number and invalidates ["issues"] + ["issues", issueNumber] on success', () => {
    arrange({ issueNumber: 7, projectId: 'proj-x' })
    const remove = findMutationByApiCallWithArg(apiMocks.removePrerequisite, 99)

    expect(apiMocks.removePrerequisite).toHaveBeenCalledWith(7, 99, 'proj-x')

    invalidateQueriesMock.mockClear()
    remove.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith(detailInvalidation)
  })

  it('close: invalidates the scoped list and detail only on success', () => {
    arrange({ issueNumber: 7, projectId: 'proj-x' })
    const close = findMutationByApiCall(apiMocks.closeIssue)

    expect(apiMocks.closeIssue).toHaveBeenCalledWith(7, 'proj-x')

    invalidateQueriesMock.mockClear()
    close.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledTimes(2)
    expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith(detailInvalidation)
  })

  it('done: calls the manual completion API and invalidates list plus detail', () => {
    arrange({ issueNumber: 7, projectId: 'proj-x' })
    const done = findMutationByApiCall(apiMocks.markIssueDone)

    expect(apiMocks.markIssueDone).toHaveBeenCalledWith(7, 'proj-x')

    invalidateQueriesMock.mockClear()
    done.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith(detailInvalidation)
  })

  it('reopen: invalidates the scoped list and detail only on success', () => {
    arrange({ issueNumber: 7, projectId: 'proj-x' })
    const reopen = findMutationByApiCall(apiMocks.reopenIssue)

    expect(apiMocks.reopenIssue).toHaveBeenCalledWith(7, 'proj-x')

    invalidateQueriesMock.mockClear()
    reopen.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledTimes(2)
    expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith(detailInvalidation)
  })

  it('resume / retry / rerun: each invalidate ["issues"] + ["agent-status"] on success', () => {
    arrange({ issueNumber: 7, projectId: 'proj-x' })

    for (const apiFn of [apiMocks.resumeIssue, apiMocks.retryIssue, apiMocks.rerunIssue]) {
      invalidateQueriesMock.mockClear()
      apiFn.mockClear()
      const m = findMutationByApiCall(apiFn)
      expect(apiFn).toHaveBeenCalledWith(7, 'proj-x')
      m.onSuccess?.()
      expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)
      expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    }
  })

  it('forceStop: invalidates ["issues"] + ["agent-status"] on success, then fires onForceStopSuccess callback', () => {
    const onForceStopSuccess = vi.fn()
    arrange({
      issueNumber: 7,
      projectId: 'proj-x',
      onForceStopSuccess,
    })
    const forceStop = findMutationByApiCall(apiMocks.forceStopIssue)

    expect(apiMocks.forceStopIssue).toHaveBeenCalledWith(7, 'proj-x')

    invalidateQueriesMock.mockClear()
    forceStop.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(onForceStopSuccess).toHaveBeenCalledTimes(1)
  })

  it('forceStop: succeeds without onForceStopSuccess callback (no TypeError)', () => {
    arrange({ issueNumber: 7, projectId: 'proj-x' })
    const forceStop = findMutationByApiCall(apiMocks.forceStopIssue)
    invalidateQueriesMock.mockClear()
    expect(() => forceStop.onSuccess?.()).not.toThrow()
    expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
  })

  it('stop: invalidates ["issues"] + ["agent-status"] on success, then fires onStopSuccess callback', () => {
    const onStopSuccess = vi.fn()
    arrange({
      issueNumber: 7,
      projectId: 'proj-x',
      onStopSuccess,
    })
    const stop = findMutationByApiCall(apiMocks.stopIssue)

    expect(apiMocks.stopIssue).toHaveBeenCalledWith(7, 'proj-x')

    invalidateQueriesMock.mockClear()
    stop.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith(listInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(onStopSuccess).toHaveBeenCalledTimes(1)
  })

  it('addComment: passes author, body, and extracted attachment ids, invalidates the issue on success, then fires onAddCommentSuccess', () => {
    const onAddCommentSuccess = vi.fn()
    arrange({
      issueNumber: 7,
      projectId: 'proj-x',
      onAddCommentSuccess,
    })
    const addComment = findMutationByApiCallWithArg(apiMocks.addComment, { author: 'Ada', body: 'look at att:abc-123' })

    expect(apiMocks.extractAttachmentIds).toHaveBeenCalledWith('look at att:abc-123')
    expect(apiMocks.addComment).toHaveBeenCalledWith(7, 'Ada', 'look at att:abc-123', 'proj-x', ['att:abc-123'])

    invalidateQueriesMock.mockClear()
    addComment.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith(detailInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledTimes(1)
    expect(onAddCommentSuccess).toHaveBeenCalledTimes(1)
  })

  it('deleteComment: passes the comment id, invalidates ["issues", issueNumber] on success, then fires onDeleteCommentSuccess', () => {
    const onDeleteCommentSuccess = vi.fn()
    arrange({
      issueNumber: 7,
      projectId: 'proj-x',
      onDeleteCommentSuccess,
    })
    const deleteComment = findMutationByApiCallWithArg(apiMocks.deleteComment, 'comment-99')

    expect(apiMocks.deleteComment).toHaveBeenCalledWith(7, 'comment-99', 'proj-x')

    invalidateQueriesMock.mockClear()
    deleteComment.onSuccess?.()
    expect(invalidateQueriesMock).toHaveBeenCalledWith(detailInvalidation)
    expect(invalidateQueriesMock).toHaveBeenCalledTimes(1)
    expect(onDeleteCommentSuccess).toHaveBeenCalledTimes(1)
  })

  it('deleteComment: on error fires onDeleteCommentError with an Error instance carrying the message', () => {
    const onDeleteCommentError = vi.fn()
    arrange({
      issueNumber: 7,
      projectId: 'proj-x',
      onDeleteCommentError,
    })
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
    arrange({ issueNumber: 7, projectId: 'proj-x' })
    const deleteComment = findMutationByApiCallWithArg(apiMocks.deleteComment, 'comment-99')

    invalidateQueriesMock.mockClear()
    deleteComment.onError?.(new Error('boom'))
    expect(invalidateQueriesMock).not.toHaveBeenCalled()
  })

  it('mutations accept undefined projectId without TypeError on success', () => {
    arrange({ issueNumber: 7, projectId: undefined })
    expect(() => {
      for (const config of getMutationConfigs()) {
        invalidateQueriesMock.mockClear()
        config.onSuccess?.()
      }
    }).not.toThrow()
  })
})
