import '@testing-library/jest-dom'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { TimelineGroup, TimelineItem } from '@/entities/session'
import { TimelineGroupRow } from './TimelineGroupRow'

function makeItem(id: string, sourceId: string, summary: string): TimelineItem {
  return {
    id,
    sourceIds: [sourceId],
    occurredAt: '2026-01-01T00:00:00.000Z',
    renderClass: 'file-read',
    summary,
    salience: 'low',
    detail: { raw: { path: summary } },
    isTerminal: true,
  }
}

function makeGroup(): TimelineGroup {
  return {
    id: 'group-read-1',
    renderClass: 'file-read',
    sourceIds: ['read-source-1', 'read-source-2', 'read-source-3'],
    summary: '读取了 3 个文件',
    salience: 'low',
    items: [
      makeItem('read-1', 'read-source-1', '读取了 src/one.ts'),
      makeItem('read-2', 'read-source-2', '读取了 src/two.ts'),
      makeItem('read-3', 'read-source-3', '读取了 src/three.ts'),
    ],
  }
}

describe('TimelineGroupRow', () => {
  it('keeps child source anchors available while collapsed and reveals children in order when expanded', () => {
    const { container } = render(<TimelineGroupRow group={makeGroup()} />)
    const group = screen.getByTestId('timeline-group-row')
    const toggle = within(group).getByTestId('timeline-group-toggle')

    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByTestId('timeline-group-items')).toBeNull()
    expect([...container.querySelectorAll('[data-timeline-source-id]')].map((node) => node.getAttribute('data-timeline-source-id'))).toEqual([
      'read-source-1',
      'read-source-2',
      'read-source-3',
    ])

    fireEvent.click(toggle)

    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    const children = screen.getByTestId('timeline-group-items')
    expect(within(children).getAllByTestId('timeline-item-row').map((row) => row.textContent)).toEqual([
      expect.stringContaining('读取了 src/one.ts'),
      expect.stringContaining('读取了 src/two.ts'),
      expect.stringContaining('读取了 src/three.ts'),
    ])
  })
})
