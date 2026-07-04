import { describe, expect, it } from 'vitest'
import { mergeTaskLogDelta } from './TaskLogPanel'
import { makeLine, makePage, makeEnvelope } from './_taskLogPanelTestUtils'

describe('mergeTaskLogDelta — pure merge', () => {
  it('appends unseen entries and sorts by seq while deduping existing seqs', () => {
    const page = makePage([
      makeLine({ seq: 5, text: 'a' }),
      makeLine({ seq: 6, text: 'b' }),
    ])
    const delta = makeEnvelope([
      { seq: 6, text: 'dup' },
      { seq: 3, text: 'out-of-order' },
      { seq: 7, text: 'c' },
    ])
    const merged = mergeTaskLogDelta(page, delta)
    expect(merged.lines.map((l) => l.seq)).toEqual([3, 5, 6, 7])
    expect(merged.lines.map((l) => l.text)).toEqual(['out-of-order', 'a', 'b', 'c'])
  })

  it('keeps the truncated flag if either side is truncated', () => {
    const page = makePage([], true)
    const delta = makeEnvelope([{ seq: 1, text: 'a' }], { truncated: true })
    const merged = mergeTaskLogDelta(page, delta)
    expect(merged.truncated).toBe(true)
  })

  it('keeps equivalent page contents if nothing changes (no incoming entries, no truncate change)', () => {
    const page = makePage([
      makeLine({ seq: 1, text: 'a' }),
    ])
    const delta = makeEnvelope([{ seq: 1, text: 'dup' }], { truncated: false })
    const merged = mergeTaskLogDelta(page, delta)
    expect(merged.lines).toEqual(page.lines)
    expect(merged.truncated).toBe(page.truncated)
  })

  it('keeps only the retained tail when live deltas grow beyond the panel limit', () => {
    const page = makePage(
      Array.from({ length: 5000 }, (_, index) => makeLine({ seq: index + 1, text: `cached-${index + 1}` })),
    )
    const delta = makeEnvelope(
      Array.from({ length: 5 }, (_, index) => ({ seq: 5001 + index, text: `live-${5001 + index}` })),
    )

    const merged = mergeTaskLogDelta(page, delta)

    expect(merged.lines).toHaveLength(5000)
    expect(merged.lines[0].seq).toBe(6)
    expect(merged.lines[merged.lines.length - 1].seq).toBe(5005)
    expect(merged.truncated).toBe(true)
    expect(merged.nextCursor).toBeNull()
  })

  it('drops late low-seq deltas once the cache already contains a retained tail', () => {
    const page = makePage(
      Array.from({ length: 5000 }, (_, index) => makeLine({ seq: 1001 + index, text: `tail-${1001 + index}` })),
      true,
    )
    const delta = makeEnvelope([{ seq: 999, text: 'old-head' }])

    const merged = mergeTaskLogDelta(page, delta)

    expect(merged.lines).toHaveLength(5000)
    expect(merged.lines[0].seq).toBe(1001)
    expect(merged.lines[merged.lines.length - 1].seq).toBe(6000)
    expect(merged.lines.some((line) => line.seq === 999)).toBe(false)
    expect(merged.truncated).toBe(true)
  })

  it('mergeTaskLogDelta is byte-identical to Phase 1/2 — TaskLogLine shape stays {seq,timestamp,source,text} and no "kind" leaks in', () => {
    const page = makePage([makeLine({ seq: 1, text: 'a' })])
    const delta = makeEnvelope([{ seq: 99, source: 'session', text: 'fake-milestone' }])
    const merged = mergeTaskLogDelta(page, delta)
    expect(merged.lines.every((line) => 'seq' in line)).toBe(true)
    expect(merged.lines.map((line) => line.seq)).toEqual([1, 99])
    const keys = Object.keys(merged.lines[0]).sort()
    expect(keys).toEqual(['seq', 'source', 'text', 'timestamp'])
  })
})
