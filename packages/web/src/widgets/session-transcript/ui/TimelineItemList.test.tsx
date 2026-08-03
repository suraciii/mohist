import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import type { TimelineGroup, TimelineItem } from '@/entities/session'
import { RawTimelineView } from './RawTimelineView'
import { TimelineItemList } from './TimelineItemList'

function makeItem(id: string, sourceId: string, renderClass: TimelineItem['renderClass']): TimelineItem {
  return {
    id,
    sourceIds: [sourceId],
    occurredAt: '2026-01-01T00:00:00.000Z',
    renderClass,
    summary: renderClass === 'domain-action' ? '启动了 Issue #42' : `条目 ${id}`,
    salience: renderClass === 'domain-action' ? 'high' : 'low',
    detail: { raw: { id, sourceId } },
    isTerminal: true,
    reference: renderClass === 'domain-action'
      ? { kind: 'issue', label: 'Issue #42', issueNumber: 42 }
      : undefined,
  }
}

function makeGroup(): TimelineGroup {
  return {
    id: 'group-1',
    renderClass: 'file-read',
    sourceIds: ['group-source-1', 'group-source-2', 'group-source-3'],
    summary: '读取了 3 个文件',
    salience: 'low',
    items: [
      makeItem('group-item-1', 'group-source-1', 'file-read'),
      makeItem('group-item-2', 'group-source-2', 'file-read'),
      makeItem('group-item-3', 'group-source-3', 'file-read'),
    ],
  }
}

describe('TimelineItemList', () => {
  it('dispatches existing entries without classifying or grouping them in the view', () => {
    const entries = [
      makeItem('read-1', 'source-1', 'file-read'),
      makeItem('read-2', 'source-2', 'file-read'),
      makeItem('read-3', 'source-3', 'file-read'),
    ]

    render(<TimelineItemList entries={entries} />)

    expect(screen.getAllByTestId('timeline-item-row')).toHaveLength(3)
    expect(screen.queryByTestId('timeline-group-row')).toBeNull()
  })

  it('renders group and item entries with a shared source anchor across summary and raw views', () => {
    const domainItem = makeItem('domain-1', 'domain-source-1', 'domain-action')

    render(
      <MemoryRouter>
        <TimelineItemList
          entries={[makeGroup(), domainItem]}
          resolveReference={() => '/projects/project-1/issues/42'}
        />
        <RawTimelineView
          facts={[
            {
              sourceId: 'group-source-2',
              source: 'transcript',
              order: 1,
              occurredAt: '2026-01-01T00:00:00.000Z',
              kind: 'tool',
              raw: { path: 'src/two.ts' },
            },
          ]}
        />
      </MemoryRouter>,
    )

    expect(screen.getByTestId('timeline-group-row')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: '启动了 Issue #42' })).toHaveAttribute(
      'href',
      '/projects/project-1/issues/42',
    )
    expect(document.querySelectorAll('[data-timeline-source-id="group-source-2"]')).toHaveLength(2)
  })
})
