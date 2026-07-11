import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { SearchContentView } from './search-view'

describe('SearchContentView', () => {
  it('renders pattern and type label from JSON input', () => {
    render(
      <SearchContentView
        input={JSON.stringify({ pattern: 'TODO', type: 'js' })}
        output={JSON.stringify(['a', 'b'])}
      />,
    )

    expect(screen.getByText('Searching')).toBeInTheDocument()
    expect(screen.getByText('TODO')).toBeInTheDocument()
    expect(screen.getByText('(js)')).toBeInTheDocument()
  })

  it('truncates results to 5 entries', () => {
    const { container } = render(
      <SearchContentView
        input={JSON.stringify({ pattern: 'foo' })}
        output={JSON.stringify(['r1', 'r2', 'r3', 'r4', 'r5', 'r6', 'r7'])}
      />,
    )

    expect(container.textContent).toContain('r1')
    expect(container.textContent).toContain('r5')
    expect(container.textContent).not.toContain('r6')
    expect(container.textContent).toContain('...')
  })

  it('formats object results as file:line', () => {
    const { container } = render(
      <SearchContentView
        input={JSON.stringify({ pattern: 'x' })}
        output={JSON.stringify([{ file: 'a.ts', line: 7 }])}
      />,
    )

    const pres = container.querySelectorAll('pre')
    expect(pres[0]?.textContent).toBe('a.ts:7')
  })

  it('falls back to truncated raw output on JSON parse failure', () => {
    const { container } = render(
      <SearchContentView
        input={JSON.stringify({ pattern: 'x' })}
        output="not json but plain text"
      />,
    )

    const pre = container.querySelector('pre')
    expect(pre?.textContent).toBe('not json but plain text')
  })
})
