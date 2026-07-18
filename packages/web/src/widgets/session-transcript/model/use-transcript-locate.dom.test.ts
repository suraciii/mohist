import { describe, expect, it, vi } from 'vitest'
import { renderHook, act } from '@testing-library/react'
import { useTranscriptLocate } from './use-transcript-locate'

describe('useTranscriptLocate', () => {
  it('expands before locating and highlights after scrolling', () => {
    vi.useFakeTimers()
    const container = document.createElement('div')
    const row = document.createElement('div')
    row.dataset.toolCallId = 'tool:1'
    container.append(row)
    const scrollContainerRef = { current: container }
    const expand = vi.fn()
    const highlight = vi.fn()
    const { result } = renderHook(() => useTranscriptLocate({ scrollContainerRef }))
    result.current.expansionRegistry.set('group', expand)
    result.current.highlightRegistry.set('tool:1', highlight)
    vi.spyOn(row, 'scrollIntoView').mockImplementation(() => {})
    act(() => result.current.locate({ toolCallId: 'tool:1', groupId: 'group' }))
    expect(expand).toHaveBeenCalled()
    act(() => vi.runOnlyPendingTimers())
    expect(row.scrollIntoView).toHaveBeenCalledWith({ block: 'center' })
    expect(highlight).toHaveBeenCalledWith(true)
    vi.useRealTimers()
  })
})
