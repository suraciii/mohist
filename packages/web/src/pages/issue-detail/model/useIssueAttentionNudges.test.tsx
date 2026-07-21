import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { toast } from 'sonner'
import { useIssueAttentionNudges } from './useIssueAttentionNudges'

const issueNumber = 14

function renderNudges(summary: Parameters<typeof useIssueAttentionNudges>[0]['summary']) {
  return renderHook(
    ({ currentSummary, currentIssueNumber }) => useIssueAttentionNudges({
      issueNumber: currentIssueNumber,
      summary: currentSummary,
    }),
    { initialProps: { currentSummary: summary, currentIssueNumber: issueNumber } },
  )
}

beforeEach(() => {
  vi.mocked(toast.info).mockClear()
  vi.mocked(toast.error).mockClear()
})

afterEach(() => {
  Object.defineProperty(window, 'innerWidth', { configurable: true, writable: true, value: 1280 })
})

describe('useIssueAttentionNudges', () => {
  it('nudges when the viewed issue enters approval-waiting', () => {
    const hook = renderNudges('running')

    act(() => hook.rerender({ currentSummary: 'approval-required', currentIssueNumber: issueNumber }))

    expect(vi.mocked(toast.info)).toHaveBeenCalledTimes(1)
    expect(vi.mocked(toast.info)).toHaveBeenCalledWith('Issue #14 needs approval')
    expect(vi.mocked(toast.error)).not.toHaveBeenCalled()
  })

  it('nudges when the viewed issue becomes blocked', () => {
    const hook = renderNudges('running')

    act(() => hook.rerender({ currentSummary: 'blocked', currentIssueNumber: issueNumber }))

    expect(vi.mocked(toast.error)).toHaveBeenCalledTimes(1)
    expect(vi.mocked(toast.error)).toHaveBeenCalledWith('Issue #14 is blocked')
    expect(vi.mocked(toast.info)).not.toHaveBeenCalled()
  })

  it('fires once for an approval transition and does not duplicate the global notice', () => {
    const hook = renderNudges('running')

    act(() => hook.rerender({ currentSummary: 'approval-required', currentIssueNumber: issueNumber }))
    act(() => hook.rerender({ currentSummary: 'approval-required', currentIssueNumber: issueNumber }))

    expect(vi.mocked(toast.info)).toHaveBeenCalledTimes(1)
  })

  it.each([
    'queued',
    'running',
    'done',
    'failed',
  ] as const)('does not toast for a %s-only transition', (summary) => {
    const hook = renderNudges('running')

    act(() => hook.rerender({ currentSummary: summary, currentIssueNumber: issueNumber }))

    expect(vi.mocked(toast.info)).not.toHaveBeenCalled()
    expect(vi.mocked(toast.error)).not.toHaveBeenCalled()
  })

  it('does not toast when arriving at an issue already awaiting approval or blocked', () => {
    const approvalHook = renderNudges('approval-required')
    const blockedHook = renderNudges('blocked')

    expect(approvalHook.result.current).toBeUndefined()
    expect(blockedHook.result.current).toBeUndefined()
    expect(vi.mocked(toast.info)).not.toHaveBeenCalled()
    expect(vi.mocked(toast.error)).not.toHaveBeenCalled()
  })

  it('resets the baseline when navigating to another issue', () => {
    const hook = renderNudges('running')

    act(() => hook.rerender({ currentSummary: 'approval-required', currentIssueNumber: 15 }))

    expect(vi.mocked(toast.info)).not.toHaveBeenCalled()
    expect(vi.mocked(toast.error)).not.toHaveBeenCalled()
  })

  it('fires the same nudge at phone width', () => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, writable: true, value: 390 })
    const hook = renderNudges('running')

    act(() => hook.rerender({ currentSummary: 'blocked', currentIssueNumber: issueNumber }))

    expect(vi.mocked(toast.error)).toHaveBeenCalledWith('Issue #14 is blocked')
  })
})
