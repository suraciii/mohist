import '@testing-library/jest-dom'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { TimelineFact } from '@/entities/session'
import { RawTimelineView } from './RawTimelineView'

function makeFacts(): TimelineFact[] {
  return [
    {
      sourceId: 'raw-source-1',
      source: 'transcript',
      order: 2,
      occurredAt: '2026-01-01T00:00:02.000Z',
      kind: 'tool',
      raw: { command: 'mo issue start 42', output: 'unchanged payload' },
    },
    {
      sourceId: 'raw-source-2',
      source: 'live',
      order: 1,
      occurredAt: '2026-01-01T00:00:01.000Z',
      kind: 'message',
      raw: { text: 'live message', nested: { value: 2 } },
    },
  ]
}

describe('RawTimelineView', () => {
  it('renders one fact row in input order with collapsed, untouched raw payloads', () => {
    const facts = makeFacts()
    render(<RawTimelineView facts={facts} />)

    const rows = screen.getAllByTestId('raw-timeline-row')
    expect(rows).toHaveLength(facts.length)
    expect(rows.map((row) => row.getAttribute('data-timeline-source-id'))).toEqual([
      'raw-source-1',
      'raw-source-2',
    ])
    expect(within(rows[0]!).getByTestId('raw-timeline-source')).toHaveTextContent('transcript')
    expect(within(rows[0]!).getByText('tool')).toBeInTheDocument()
    expect(within(rows[0]!).getByText('raw-source-1')).toBeInTheDocument()
    expect(within(rows[0]!).getByText('2026-01-01T00:00:02.000Z')).toBeInTheDocument()
    expect(within(rows[0]!).getByTestId('raw-timeline-payload-details')).not.toHaveAttribute('open')

    fireEvent.click(within(rows[0]!).getByText('Show payload'))

    expect(within(rows[0]!).getByText(/unchanged payload/)).toBeInTheDocument()
    expect(within(rows[0]!).getByText(/mo issue start 42/)).toBeInTheDocument()
    expect(rows[1]).toHaveTextContent('message')
  })
})
