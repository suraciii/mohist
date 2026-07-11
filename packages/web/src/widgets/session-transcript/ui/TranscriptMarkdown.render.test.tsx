import '@testing-library/jest-dom'
import { render } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { TranscriptMarkdown } from './TranscriptMarkdown'

function cleanupHighlightStyleTag() {
  const existing = document.getElementById('transcript-md-highlight-styles')
  if (existing) existing.remove()
}

beforeEach(() => {
  cleanupHighlightStyleTag()
})

afterEach(() => {
  cleanupHighlightStyleTag()
})

describe('TranscriptMarkdown wrapper', () => {
  it('renders non-code markdown content as before (paragraphs, inline code, links)', () => {
    const content = 'A paragraph with **bold**, *italic* and `inline` code plus a [link](https://example.com).'
    const { container } = render(<TranscriptMarkdown content={content} />)

    const wrapper = container.querySelector('.transcript-md')
    expect(wrapper).not.toBeNull()

    expect(wrapper?.querySelector('p')).not.toBeNull()
    expect(wrapper?.textContent).toContain('bold')
    expect(wrapper?.textContent).toContain('italic')
    expect(wrapper?.textContent).toContain('inline')

    const inlineCode = wrapper?.querySelector('p code')
    expect(inlineCode).not.toBeNull()
    expect(inlineCode?.className).toContain('bg-gray-100')
    expect(inlineCode?.className).toContain('[overflow-wrap:anywhere]')

    const anchor = wrapper?.querySelector('a')
    expect(anchor).toHaveAttribute('href', 'https://example.com')
  })

  it('renders fenced code blocks with rehype-highlight hljs token spans', () => {
    const content = '```ts\nconst greet = (name: string) => `hi ${name}`\n```'
    const { container } = render(<TranscriptMarkdown content={content} />)

    const wrapper = container.querySelector('.transcript-md')
    expect(wrapper).not.toBeNull()

    const pre = wrapper?.querySelector('pre')
    expect(pre).not.toBeNull()
    expect(pre?.className).toContain('overflow-x-auto')
    expect(pre?.className).toContain('max-w-full')

    const code = wrapper?.querySelector('pre code')
    expect(code).not.toBeNull()
    expect(code?.className).toContain('hljs')
    expect(code?.className).toContain('language-ts')

    const highlightedTokens = wrapper?.querySelectorAll('pre code span.hljs-keyword, pre code span.hljs-string, pre code span.hljs-title, pre code span.hljs-built_in, pre code span.hljs-attr')
    expect(highlightedTokens && highlightedTokens.length).toBeGreaterThan(0)
  })

  it('still renders fenced code blocks without a language tag as block code', () => {
    const content = '```\nplain code\n```'
    const { container } = render(<TranscriptMarkdown content={content} />)

    const pre = container.querySelector('.transcript-md pre')
    expect(pre).not.toBeNull()
    expect(pre?.className).toContain('overflow-x-auto')
    expect(pre?.className).toContain('max-w-full')

    const code = container.querySelector('.transcript-md pre code')
    expect(code).not.toBeNull()
    expect(code?.textContent).toContain('plain code')
  })

  it('injects a scoped <style> tag under document.head that wraps hljs rules under .transcript-md', () => {
    render(<TranscriptMarkdown content="hello" />)

    const style = document.getElementById('transcript-md-highlight-styles')
    expect(style).not.toBeNull()
    expect(style?.tagName).toBe('STYLE')
    expect(style?.getAttribute('data-transcript-md-highlight')).not.toBeNull()

    const css = style?.textContent ?? ''
    expect(css.length).toBeGreaterThan(0)
    expect(css).toContain('.transcript-md')
    expect(css).toMatch(/\.transcript-md\s+\.hljs\s*\{/)
    expect(css).toMatch(/\.transcript-md\s+\.hljs-keyword/)
    expect(css).not.toContain('}}')
  })

  it('does not inject a duplicate <style> tag when multiple TranscriptMarkdown instances are mounted', () => {
    render(
      <div>
        <TranscriptMarkdown content="one" />
        <TranscriptMarkdown content="two" />
      </div>,
    )

    const styles = document.querySelectorAll('#transcript-md-highlight-styles')
    expect(styles.length).toBe(1)
  })

  it('does not register hljs-* styles outside the wrapper class (does not affect MarkdownReader or sibling markup)', () => {
    const { container } = render(
      <div>
        <div className="sibling-surface" data-testid="sibling">
          <pre><code className="hljs language-ts">unrelated</code></pre>
        </div>
        <TranscriptMarkdown content={'```ts\nconst x = 1\n```'} />
      </div>,
    )

    const sibling = container.querySelector('[data-testid="sibling"]')
    const css = document.getElementById('transcript-md-highlight-styles')?.textContent ?? ''

    const unwrappedHljsSelector = css.split('}').some((segment) => {
      const head = segment.split('{')[0]?.trim() ?? ''
      if (!head.includes('.hljs')) return false
      return !head.includes('.transcript-md')
    })
    expect(unwrappedHljsSelector).toBe(false)

    expect(sibling).not.toBeNull()
  })
})
