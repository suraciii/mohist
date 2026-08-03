import '@testing-library/jest-dom'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import type { TimelineItem } from '@/entities/session'
import { TimelineItemRow } from './TimelineItemRow'

function makeItem(overrides: Partial<TimelineItem> = {}): TimelineItem {
  return {
    id: 'item-1',
    sourceIds: ['source-1', 'source-1-update'],
    occurredAt: '2026-01-01T00:00:00.000Z',
    renderClass: 'error',
    summary: '运行了命令 -> 失败',
    salience: 'critical',
    detail: {
      input: { command: 'false' },
      output: 'secret output',
      diff: [{ path: 'src/app.ts', additions: 2, deletions: 1 }],
      error: 'exit 1',
      raw: { command: 'false', output: 'secret output' },
    },
    isTerminal: true,
    ...overrides,
  }
}

describe('TimelineItemRow', () => {
  it('renders a prominent failure row with collapsed details and every source anchor', () => {
    const { container } = render(<TimelineItemRow item={makeItem()} />)
    const row = screen.getByTestId('timeline-item-row')
    const details = within(row).getByTestId('timeline-item-details')

    expect(row).toHaveAttribute('data-timeline-source-id', 'source-1')
    expect(row).toHaveAttribute('data-timeline-render-class', 'error')
    expect(row).toHaveClass('bg-danger-subtle', 'border-l-4')
    expect(details).not.toHaveAttribute('open')
    expect([...container.querySelectorAll('[data-timeline-source-id]')].map((node) => node.getAttribute('data-timeline-source-id'))).toEqual([
      'source-1',
      'source-1-update',
    ])

    fireEvent.click(within(details).getByText('Show details'))

    expect(details).toHaveAttribute('open')
    expect(within(details).getByText('secret output')).toBeInTheDocument()
    expect(within(details).getByText(/src\/app\.ts/)).toBeInTheDocument()
  })

  it('turns a resolved semantic reference into a project link without resolving routes itself', () => {
    const resolveReference = vi.fn(() => '/projects/project-1/issues/42')
    const item = makeItem({
      id: 'domain-action-1',
      sourceIds: ['domain-source-1'],
      renderClass: 'domain-action',
      summary: '启动了 Issue #42',
      salience: 'high',
      reference: { kind: 'issue', label: 'Issue #42', issueNumber: 42 },
      detail: { raw: { command: 'mo issue start 42' } },
    })

    render(
      <MemoryRouter>
        <TimelineItemRow item={item} resolveReference={resolveReference} />
      </MemoryRouter>,
    )

    expect(resolveReference).toHaveBeenCalledWith(item.reference)
    expect(screen.getByRole('link', { name: item.summary })).toHaveAttribute(
      'href',
      '/projects/project-1/issues/42',
    )
  })
})
