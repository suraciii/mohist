import { describe, expect, it, vi } from 'vitest'
import {
  INBOX_HINT_IDENTITY_KEYS,
  applyInboxHint,
  isHighAttentionKind,
  parseInboxItemPersistedHint,
  shouldSuppressInAppNotice,
  type InboxItemPersistedHintPayload,
} from './inbox-effects'

function makeHint(overrides: Partial<InboxItemPersistedHintPayload> = {}): InboxItemPersistedHintPayload {
  return {
    itemId: 'inb-1',
    projectId: 'proj-1',
    kind: 'workflow_failed',
    issueNumber: 42,
    ...overrides,
  }
}

function makeQueryClient() {
  return { invalidateQueries: vi.fn() }
}

describe('parseInboxItemPersistedHint', () => {
  it('returns the typed payload for a valid hint', () => {
    expect(parseInboxItemPersistedHint(makeHint())).toEqual(makeHint())
  })

  it('returns null for non-object inputs', () => {
    expect(parseInboxItemPersistedHint(null)).toBeNull()
    expect(parseInboxItemPersistedHint(undefined)).toBeNull()
    expect(parseInboxItemPersistedHint('string')).toBeNull()
    expect(parseInboxItemPersistedHint(123)).toBeNull()
    expect(parseInboxItemPersistedHint([])).toBeNull()
  })

  it('returns null when itemId is missing or empty', () => {
    expect(parseInboxItemPersistedHint(makeHint({ itemId: '' }))).toBeNull()
    expect(parseInboxItemPersistedHint(makeHint({ itemId: undefined as unknown as string }))).toBeNull()
  })

  it('returns null when projectId is missing or empty', () => {
    expect(parseInboxItemPersistedHint(makeHint({ projectId: '' }))).toBeNull()
  })

  it('returns null when kind is missing or empty', () => {
    expect(parseInboxItemPersistedHint(makeHint({ kind: '' }))).toBeNull()
  })

  it('returns null when issueNumber is non-positive', () => {
    expect(parseInboxItemPersistedHint(makeHint({ issueNumber: 0 }))).toBeNull()
  })

  it('returns null when issueNumber is not a finite number', () => {
    expect(parseInboxItemPersistedHint(makeHint({ issueNumber: Number.NaN as unknown as number }))).toBeNull()
    expect(parseInboxItemPersistedHint(makeHint({ issueNumber: '42' as unknown as number }))).toBeNull()
  })

  it('does NOT trust additional fields beyond the canonical identity set', () => {
    const hint = parseInboxItemPersistedHint({
      ...makeHint(),
      extraField: 'should-be-ignored',
      isRead: true,
      issueTitle: 'should-be-ignored',
    })
    expect(hint).toEqual(makeHint())
    expect(Object.keys(hint ?? {}).sort()).toEqual([...INBOX_HINT_IDENTITY_KEYS].sort())
  })
})

describe('applyInboxHint', () => {
  it('invalidates the ["inbox", projectId] query key when the hint matches the current project', () => {
    const queryClient = makeQueryClient()

    const result = applyInboxHint(makeHint({ projectId: 'proj-1' }), queryClient, {
      currentProjectId: 'proj-1',
    })

    expect(result).toEqual({ applied: true, projectId: 'proj-1' })
    expect(queryClient.invalidateQueries).toHaveBeenCalledTimes(1)
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['inbox', 'proj-1'] })
  })

  it('does NOT invalidate when the hint targets a different project', () => {
    const queryClient = makeQueryClient()

    const result = applyInboxHint(makeHint({ projectId: 'proj-b' }), queryClient, {
      currentProjectId: 'proj-1',
    })

    expect(result).toEqual({ applied: false, projectId: null })
    expect(queryClient.invalidateQueries).not.toHaveBeenCalled()
  })

  it('does NOT invalidate when the session has no current project', () => {
    const queryClient = makeQueryClient()

    const result = applyInboxHint(makeHint({ projectId: 'proj-1' }), queryClient, {
      currentProjectId: null,
    })

    expect(result).toEqual({ applied: false, projectId: null })
    expect(queryClient.invalidateQueries).not.toHaveBeenCalled()
  })

  it('uses the exact projectId from the hint (no implicit prefixing)', () => {
    const queryClient = makeQueryClient()

    applyInboxHint(makeHint({ projectId: 'proj-with-dashes' }), queryClient, {
      currentProjectId: 'proj-with-dashes',
    })

    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['inbox', 'proj-with-dashes'] })
  })

  it('does NOT mutate the cache or push synthetic items (invalidation only)', () => {
    const queryClient = makeQueryClient()
    const setQueryDataSpy = vi.fn()
    const setQueriesDataSpy = vi.fn()
    const cancelQueriesSpy = vi.fn()
    const refetchQueriesSpy = vi.fn()
    const getQueryDataSpy = vi.fn()
    const queryClientFull = {
      ...queryClient,
      setQueryData: setQueryDataSpy,
      setQueriesData: setQueriesDataSpy,
      cancelQueries: cancelQueriesSpy,
      refetchQueries: refetchQueriesSpy,
      getQueryData: getQueryDataSpy,
    }

    applyInboxHint(makeHint(), queryClientFull, { currentProjectId: 'proj-1' })

    expect(queryClient.invalidateQueries).toHaveBeenCalledTimes(1)
    expect(setQueryDataSpy).not.toHaveBeenCalled()
    expect(setQueriesDataSpy).not.toHaveBeenCalled()
    expect(cancelQueriesSpy).not.toHaveBeenCalled()
    expect(refetchQueriesSpy).not.toHaveBeenCalled()
    expect(getQueryDataSpy).not.toHaveBeenCalled()
  })

  it('treats the hint as idempotent — two hints for the same project both invalidate', () => {
    const queryClient = makeQueryClient()

    applyInboxHint(makeHint({ itemId: 'inb-1' }), queryClient, { currentProjectId: 'proj-1' })
    applyInboxHint(makeHint({ itemId: 'inb-2' }), queryClient, { currentProjectId: 'proj-1' })

    expect(queryClient.invalidateQueries).toHaveBeenCalledTimes(2)
    expect(queryClient.invalidateQueries).toHaveBeenNthCalledWith(1, { queryKey: ['inbox', 'proj-1'] })
    expect(queryClient.invalidateQueries).toHaveBeenNthCalledWith(2, { queryKey: ['inbox', 'proj-1'] })
  })

  it('uses the injected invalidate callback when provided (test seam)', () => {
    const queryClient = makeQueryClient()
    const injectedInvalidate = vi.fn()

    const result = applyInboxHint(makeHint(), queryClient, {
      currentProjectId: 'proj-1',
      invalidate: injectedInvalidate,
    })

    expect(result).toEqual({ applied: true, projectId: 'proj-1' })
    expect(injectedInvalidate).toHaveBeenCalledWith('proj-1')
    expect(queryClient.invalidateQueries).not.toHaveBeenCalled()
  })
})

describe('isHighAttentionKind', () => {
  it('returns true for workflow_failed', () => {
    expect(isHighAttentionKind('workflow_failed')).toBe(true)
  })

  it('returns true for approval_requested', () => {
    expect(isHighAttentionKind('approval_requested')).toBe(true)
  })

  it('returns false for issue_started', () => {
    expect(isHighAttentionKind('issue_started')).toBe(false)
  })

  it('returns false for issue_completed', () => {
    expect(isHighAttentionKind('issue_completed')).toBe(false)
  })

  it('returns false for an unknown kind', () => {
    expect(isHighAttentionKind('workflow_started')).toBe(false)
  })
})

describe('shouldSuppressInAppNotice', () => {
  it('suppresses when the user is on the inbox page', () => {
    const hint = makeHint({ issueNumber: 42 })
    expect(shouldSuppressInAppNotice(hint, '/proj-1/inbox', null)).toBe(true)
  })

  it('suppresses when the user is on the inbox page with trailing slash', () => {
    const hint = makeHint({ issueNumber: 42 })
    expect(shouldSuppressInAppNotice(hint, '/proj-1/inbox/', null)).toBe(true)
  })

  it('suppresses when the user is viewing the same issue', () => {
    const hint = makeHint({ issueNumber: 42 })
    expect(shouldSuppressInAppNotice(hint, '/proj-1/issues/42', 42)).toBe(true)
  })

  it('does NOT suppress when viewing an unrelated issue number', () => {
    const hint = makeHint({ issueNumber: 42 })
    expect(shouldSuppressInAppNotice(hint, '/proj-1/issues/99', 99)).toBe(false)
  })

  it('does NOT suppress when on an unrelated page (not inbox, not issue)', () => {
    const hint = makeHint({ issueNumber: 42 })
    expect(shouldSuppressInAppNotice(hint, '/proj-1/dashboard', null)).toBe(false)
  })

  it('does NOT suppress when pathname is empty', () => {
    const hint = makeHint({ issueNumber: 42 })
    expect(shouldSuppressInAppNotice(hint, '', null)).toBe(false)
  })

  it('does NOT suppress when viewedIssueNumber is null even if issue matches nothing', () => {
    const hint = makeHint({ issueNumber: 42 })
    expect(shouldSuppressInAppNotice(hint, '/proj-1/issues/42', null)).toBe(false)
  })
})
