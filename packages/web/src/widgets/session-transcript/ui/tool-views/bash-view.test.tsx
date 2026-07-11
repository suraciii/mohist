import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { BashContentView } from './bash-view'

describe('BashContentView', () => {
  it('renders command from JSON input and output', () => {
    const { container } = render(
      <BashContentView
        input={JSON.stringify({ command: 'ls -la' })}
        output={'file1\nfile2'}
        details={{ exitCode: 0 }}
      />,
    )

    expect(screen.getByText('Command')).toBeInTheDocument()
    expect(screen.getByText('Output')).toBeInTheDocument()
    const pres = container.querySelectorAll('pre')
    expect(pres[0]?.textContent).toBe('ls -la')
    expect(pres[1]?.textContent).toBe('file1\nfile2')
    expect(screen.getByText('success')).toBeInTheDocument()
  })

  it('falls back to raw input when not JSON', () => {
    const { container } = render(
      <BashContentView input="echo raw" />,
    )

    const pre = container.querySelector('pre')
    expect(pre?.textContent).toBe('echo raw')
  })

  it('uses outputPreview from details when provided', () => {
    const { container } = render(
      <BashContentView
        input={JSON.stringify({ command: 'pwd' })}
        output="long output that we do not want"
        details={{ outputPreview: 'short preview' }}
      />,
    )

    const pres = container.querySelectorAll('pre')
    expect(pres[1]?.textContent).toBe('short preview')
  })

  it('shows non-zero exit code badge', () => {
    render(
      <BashContentView
        input={JSON.stringify({ command: 'false' })}
        details={{ exitCode: 2 }}
      />,
    )

    expect(screen.getByText('exit 2')).toBeInTheDocument()
  })

  it('truncates output beyond 5 lines', () => {
    const { container } = render(
      <BashContentView
        input={JSON.stringify({ command: 'seq' })}
        output={'a\nb\nc\nd\ne\nf\ng'}
      />,
    )

    const pres = container.querySelectorAll('pre')
    expect(pres[1]?.textContent).toContain('...')
    expect(pres[1]?.textContent).not.toContain('g')
  })
})
