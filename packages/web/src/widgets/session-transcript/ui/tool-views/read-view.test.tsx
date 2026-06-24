// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { ReadContentView } from './read-view'

describe('ReadContentView', () => {
  it('renders file basename from JSON input', () => {
    render(
      <ReadContentView
        input={JSON.stringify({ filePath: '/repo/src/components/Button.tsx' })}
        output="content"
      />,
    )

    expect(screen.getByText('Reading')).toBeInTheDocument()
    expect(screen.getByText('Button.tsx')).toBeInTheDocument()
  })

  it('falls back to raw input as file path', () => {
    render(<ReadContentView input="raw-path.txt" />)

    expect(screen.getByText('raw-path.txt')).toBeInTheDocument()
  })

  it('omits output pre when no output is provided', () => {
    const { container } = render(
      <ReadContentView input={JSON.stringify({ filePath: '/a.ts' })} />,
    )

    const pres = container.querySelectorAll('pre')
    expect(pres).toHaveLength(0)
  })

  it('truncates output to 8 lines', () => {
    const { container } = render(
      <ReadContentView
        input={JSON.stringify({ filePath: '/a.ts' })}
        output={Array.from({ length: 12 }, (_, i) => `line${i + 1}`).join('\n')}
      />,
    )

    const pre = container.querySelector('pre')
    expect(pre?.textContent).toContain('line1')
    expect(pre?.textContent).toContain('line8')
    expect(pre?.textContent).not.toContain('line9')
    expect(pre?.textContent).toContain('...')
  })
})
