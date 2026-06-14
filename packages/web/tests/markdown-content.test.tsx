// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MarkdownContent } from '../src/shared/ui/components/markdown-content'

describe('MarkdownContent', () => {
  it('renders headings, lists, and code blocks from markdown source', () => {
    const content = [
      '# Title',
      '',
      '- one',
      '- two',
      '',
      '```js',
      'console.log("hi")',
      '```',
    ].join('\n')

    render(<MarkdownContent content={content} />)

    expect(screen.getByRole('heading', { name: 'Title', level: 1 })).toBeInTheDocument()
    expect(screen.getByText('one')).toBeInTheDocument()
    expect(screen.getByText('two')).toBeInTheDocument()
    expect(screen.getByText('console.log("hi")')).toBeInTheDocument()
  })

  it('renders GitHub-flavoured tables', () => {
    const content = [
      '| a | b |',
      '| - | - |',
      '| 1 | 2 |',
    ].join('\n')

    render(<MarkdownContent content={content} />)

    expect(screen.getByRole('table')).toBeInTheDocument()
  })
})
