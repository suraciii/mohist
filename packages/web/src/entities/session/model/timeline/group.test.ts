import { describe, expect, it } from 'vitest'
import { groupTimelineItems } from './group'
import { isTimelineGroup, type TimelineItem } from './types'

function item(id: string, renderClass: TimelineItem['renderClass'], groupKey?: string): TimelineItem {
  return {
    id,
    sourceIds: [id],
    occurredAt: '2026-08-03T10:00:00.000Z',
    renderClass,
    summary: `${renderClass} ${id} -> 通过`,
    salience: renderClass === 'error' ? 'critical' : renderClass === 'domain-action' ? 'high' : renderClass === 'status' ? 'quiet' : 'low',
    groupKey,
    isTerminal: true,
  }
}

describe('groupTimelineItems', () => {
  it('groups only continuous runs of three low-salience candidates', () => {
    const entries = groupTimelineItems([
      item('read-1', 'file-read', 'files'),
      item('read-2', 'file-read', 'files'),
      item('read-3', 'file-read', 'files'),
      item('failed', 'error'),
      item('read-4', 'file-read', 'files'),
      item('read-5', 'file-read', 'files'),
      item('read-6', 'file-read', 'files'),
    ])

    expect(entries).toHaveLength(3)
    expect(isTimelineGroup(entries[0]!)).toBe(true)
    expect(entries[1]).toMatchObject({ id: 'failed', renderClass: 'error' })
    expect(isTimelineGroup(entries[2]!)).toBe(true)
  })

  it('keeps distinct group keys and never-group classes independent', () => {
    const entries = groupTimelineItems([
      item('tool-1', 'tool', 'alpha'),
      item('tool-2', 'tool', 'beta'),
      item('tool-3', 'tool', 'alpha'),
      item('edit-1', 'file-edit'),
      item('edit-2', 'file-edit'),
      item('edit-3', 'file-edit'),
      item('input', 'input'),
      item('message', 'message'),
      item('domain', 'domain-action'),
      item('status', 'status'),
      item('boundary', 'boundary'),
      item('suppressed', 'suppressed'),
    ])

    expect(entries).toHaveLength(12)
    expect(entries.some(isTimelineGroup)).toBe(false)
  })

  it('summarizes grouped shells and generic tools', () => {
    const entries = groupTimelineItems([
      item('shell-1', 'shell', 'shell'),
      item('shell-2', 'shell', 'shell'),
      item('shell-3', 'shell', 'shell'),
      item('tool-1', 'tool', 'custom'),
      item('tool-2', 'tool', 'custom'),
      item('tool-3', 'tool', 'custom'),
    ])

    expect(entries.map(entry => entry.summary)).toEqual(['运行了 3 个命令', '执行了 3 个工具'])
  })
})
