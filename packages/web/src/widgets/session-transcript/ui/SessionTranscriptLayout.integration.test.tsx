import '@testing-library/jest-dom'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { TimelineEntry, TimelineFact, TimelineGroup, TimelineItem } from '@/entities/session'
import { SessionTranscriptLayout } from './SessionTranscriptLayout'

const at = '2026-08-03T10:00:00.000Z'

function makeItem(id: string, sourceId: string, renderClass: TimelineItem['renderClass'], summary: string): TimelineItem {
  return {
    id,
    sourceIds: [sourceId],
    occurredAt: at,
    renderClass,
    summary,
    salience: renderClass === 'error' ? 'critical' : renderClass === 'domain-action' ? 'high' : 'normal',
    detail: { raw: { sourceId, id } },
    isTerminal: true,
  }
}

function makeFact(sourceId: string, kind: TimelineFact['kind'], order: number, raw: unknown = { sourceId }): TimelineFact {
  return { sourceId, source: 'transcript', order, occurredAt: at, kind, raw }
}

function makeReadGroup(id: string, sourceIds: string[]): TimelineGroup {
  return {
    id,
    renderClass: 'file-read',
    sourceIds,
    summary: `读取了 ${sourceIds.length} 个文件`,
    salience: 'low',
    items: sourceIds.map((sourceId, index) => makeItem(`${id}-${index}`, sourceId, 'file-read', `读取了 file-${index}.ts`)),
  }
}

function makeTimeline() {
  const facts = [
    makeFact('input-1', 'input', 1, { text: 'Review the change' }),
    makeFact('read-1', 'tool', 2, { path: 'before.ts' }),
    makeFact('read-2', 'tool', 3, { path: 'before.test.ts' }),
    makeFact('read-3', 'tool', 4, { path: 'before.css' }),
    makeFact('failure-1', 'error', 5, { message: 'Runner failed' }),
    makeFact('read-4', 'tool', 6, { path: 'after.ts' }),
    makeFact('read-5', 'tool', 7, { path: 'after.test.ts' }),
    makeFact('read-6', 'tool', 8, { path: 'after.css' }),
    makeFact('action-1', 'tool', 9, { command: 'mo issue start 42' }),
    makeFact('reset-1', 'boundary', 10, { reason: 'reset' }),
    makeFact('activity-1', 'status', 11, { activity: 'idle' }),
  ]
  const entries: TimelineEntry[] = [
    makeItem('input-1', 'input-1', 'input', '输入了 Review the change → accepted'),
    makeReadGroup('read-before', ['read-1', 'read-2', 'read-3']),
    makeItem('failure-1', 'failure-1', 'error', 'Runner failed'),
    makeReadGroup('read-after', ['read-4', 'read-5', 'read-6']),
    { ...makeItem('action-1', 'action-1', 'domain-action', '启动了 Issue #42'), reference: { kind: 'issue', label: 'Issue #42', issueNumber: 42 } },
    makeItem('reset-1', 'reset-1', 'boundary', '上下文已重置'),
    makeItem('activity-1', 'activity-1', 'status', '空闲'),
  ]
  return { facts, entries }
}

describe('SessionTranscriptLayout timeline integration', () => {
  afterEach(() => vi.restoreAllMocks())

  it('renders input, grouped reads, failure, later reads, domain action, reset, and activity in order', () => {
    const { facts, entries } = makeTimeline()
    render(
      <MemoryRouter>
        <SessionTranscriptLayout
          entries={entries}
          facts={facts}
          currentActivity={{ state: 'idle', label: '空闲' }}
          resolveReference={() => '/Project/issues/42'}
        />
      </MemoryRouter>,
    )

    const list = screen.getByTestId('timeline-item-list')
    const rows = Array.from(list.querySelectorAll<HTMLElement>('[data-timeline-source-id]'))
      .filter((row) => !row.classList.contains('sr-only'))
    const sourceIds = Array.from(list.querySelectorAll<HTMLElement>('[data-timeline-source-id]'))
      .map((row) => row.dataset.timelineSourceId)
    expect(rows.map((row) => row.dataset.timelineSourceId)).toEqual([
      'input-1', 'read-1', 'failure-1', 'read-4', 'action-1', 'reset-1', 'activity-1',
    ])
    expect(sourceIds).toEqual(facts.map((fact) => fact.sourceId))
    expect(screen.getAllByTestId('timeline-item-row').find((row) => row.getAttribute('data-timeline-render-class') === 'input')).toHaveTextContent('输入了 Review the change')
    expect(screen.getAllByTestId('timeline-group-row')).toHaveLength(2)
    expect(screen.getByText('Runner failed')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: '启动了 Issue #42' })).toHaveAttribute('href', '/Project/issues/42')
    expect(screen.getByText('上下文已重置')).toBeInTheDocument()
    expect(screen.getByTestId('timeline-current-activity')).toHaveAttribute('data-activity-state', 'idle')
  })

  it('keeps failures and domain actions outside collapsed read groups', () => {
    const { facts, entries } = makeTimeline()
    render(
      <MemoryRouter>
        <SessionTranscriptLayout entries={entries} facts={facts} currentActivity={{ state: 'idle', label: '空闲' }} />
      </MemoryRouter>,
    )

    expect(screen.getAllByTestId('timeline-item-row').find((row) => row.getAttribute('data-timeline-render-class') === 'input')).toHaveAttribute('data-timeline-render-class', 'input')
    expect(screen.getAllByTestId('timeline-item-row').some((row) => row.getAttribute('data-timeline-render-class') === 'error')).toBe(true)
    expect(screen.queryByText('读取了 file-0.ts')).not.toBeInTheDocument()

    fireEvent.click(screen.getAllByTestId('timeline-group-toggle')[0]!)
    expect(screen.getByText('读取了 file-0.ts')).toBeInTheDocument()
    expect(screen.getByText('Runner failed')).toBeInTheDocument()
  })

  it('switches the same facts to raw rows without changing order or payload', () => {
    const { facts, entries } = makeTimeline()
    const view = render(
      <MemoryRouter>
        <SessionTranscriptLayout entries={entries} facts={facts} currentActivity={{ state: 'idle', label: '空闲' }} />
      </MemoryRouter>,
    )

    view.rerender(
      <MemoryRouter>
        <SessionTranscriptLayout entries={entries} facts={facts} currentActivity={{ state: 'idle', label: '空闲' }} viewMode="raw" />
      </MemoryRouter>,
    )

    const rows = screen.getAllByTestId('raw-timeline-row')
    expect(rows).toHaveLength(facts.length)
    expect(rows.map((row) => row.getAttribute('data-timeline-source-id'))).toEqual(facts.map((fact) => fact.sourceId))
    expect(within(rows[0]!).getByTestId('raw-timeline-payload-details')).not.toHaveAttribute('open')
  })
})
