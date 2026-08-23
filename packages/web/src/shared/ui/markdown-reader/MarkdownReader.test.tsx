import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MarkdownReader, type MarkdownAttachment } from './MarkdownReader'
import * as SharedUiBarrel from '@/shared/ui'
import { setScopedProperty, setScopedValue } from '../../../../tests/support/scoped-property'

type ResizeObserverCtor = new (callback: ResizeObserverCallback) => ResizeObserver

class StubResizeObserver {
  public observed: Element[] = []

  constructor(_callback: ResizeObserverCallback) {
    void _callback
  }

  observe(target: Element): void {
    this.observed.push(target)
  }

  unobserve(target: Element): void {
    this.observed = this.observed.filter((el) => el !== target)
  }

  disconnect(): void {
    this.observed = []
  }
}

function setupResizeObserver() {
  const instances: StubResizeObserver[] = []
  const spy = vi.fn(function (this: unknown, callback: ResizeObserverCallback) {
    const instance = new StubResizeObserver(callback)
    instances.push(instance)
    return instance
  }) as unknown as ResizeObserverCtor
  setScopedValue(globalThis, 'ResizeObserver', spy)
  return { instances, spy }
}

describe('MarkdownReader barrel export', () => {
  it('is importable from the shared UI barrel', () => {
    expect(SharedUiBarrel.MarkdownReader).toBeDefined()
    expect(SharedUiBarrel.MarkdownReader).toBe(MarkdownReader)
  })
})

describe('MarkdownReader default rendering', () => {
  afterEach(() => {
    cleanup()
  })

  it('renders paragraphs, headings, lists, links, emphasis, inline code, blockquotes and rules', () => {
    const content = [
      '# Document title',
      '',
      'A paragraph with **bold**, *italic*, ~~strike~~ and `inline` code plus a [link](https://example.com).',
      '',
      '- list item',
      '- second item',
      '',
      '1. ordered',
      '2. items',
      '',
      '> A blockquote spanning more than one word for content',
      '',
      '---',
    ].join('\n')

    render(<MarkdownReader content={content} baseHeadingLevel={2} />)

    expect(screen.getByRole('heading', { level: 2, name: 'Document title' })).toBeInTheDocument()
    expect(screen.getByText('list item')).toBeInTheDocument()
    expect(screen.getByText('ordered')).toBeInTheDocument()
    expect(screen.getByText('A blockquote spanning more than one word for content')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'link' })).toHaveAttribute('href', 'https://example.com')
    expect(screen.getByText('inline')).toBeInTheDocument()
  })

  it('renders fenced code blocks via the pre/code overrides', () => {
    const content = '```ts\nconst x = 1\n```'
    render(<MarkdownReader content={content} baseHeadingLevel={2} />)

    expect(screen.getByTestId('markdown-pre')).toBeInTheDocument()
    expect(screen.getByTestId('markdown-code-block')).toBeInTheDocument()
    expect(screen.getByTestId('markdown-code-block')).toHaveTextContent('const x = 1')
  })

  it('renders one-line fenced code as a block', () => {
    render(<MarkdownReader content={'```\nconst x = 1\n```'} baseHeadingLevel={2} />)

    const codeBlock = screen.getByTestId('markdown-code-block')
    expect(codeBlock).toHaveTextContent('const x = 1')
    expect(codeBlock.className).toContain('overflow-x-auto')
  })

  it('preserves the inline-code visual styling', () => {
    render(<MarkdownReader content="use `inline` here" baseHeadingLevel={2} />)

    const inline = screen.getByText('inline')
    expect(inline.tagName).toBe('CODE')
    expect(inline.className).toContain('px-1')
    expect(inline.className).toContain('py-0.5')
    expect(inline.className).toContain('bg-gray-100')
    expect(inline.className).toContain('rounded')
    expect(inline.className).toContain('text-xs')
    expect(inline.className).toContain('font-mono')
  })
})

describe('MarkdownReader attachment references', () => {
  afterEach(() => {
    cleanup()
  })

  const imageAttachment: MarkdownAttachment = {
    url: '/api/attachments/att_image/content',
    contentType: 'image/png',
    fileName: 'screenshot.png',
    size: 1536,
  }

  const fileAttachment: MarkdownAttachment = {
    url: '/api/attachments/att_file/content',
    contentType: 'text/plain',
    fileName: 'debug.log',
    size: 2048,
  }

  it('keeps the sanitized default path when no resolver is supplied', () => {
    render(<MarkdownReader content="See [log](att:file) and ![shot](att:image)." baseHeadingLevel={2} />)

    expect(screen.getByText('log')).toBeInTheDocument()
    expect(screen.getByRole('img', { name: 'shot' })).not.toHaveAttribute('src')
    expect(screen.queryByTestId('markdown-attachment-file-card')).not.toBeInTheDocument()
    expect(screen.queryByTestId('markdown-attachment-fallback')).not.toBeInTheDocument()
  })

  it('renders image att references inline and opens a dismissible lightbox', () => {
    render(
      <MarkdownReader
        content="Before\n\n![shot](att:image)\n\nAfter"
        baseHeadingLevel={2}
        resolveAttachment={(id) => (id === 'image' ? imageAttachment : null)}
      />,
    )

    const trigger = screen.getByTestId('markdown-attachment-image-trigger')
    const image = screen.getByRole('img', { name: 'shot' })
    expect(image).toHaveAttribute('src', imageAttachment.url)
    expect(trigger).toBeInTheDocument()

    fireEvent.click(trigger)
    expect(screen.getByTestId('markdown-attachment-lightbox')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Preview screenshot.png' })).toBeInTheDocument()

    fireEvent.click(screen.getByTestId('markdown-attachment-lightbox'))
    expect(screen.queryByTestId('markdown-attachment-lightbox')).not.toBeInTheDocument()
  })

  it('renders non-image att references as downloadable file cards', () => {
    render(
      <MarkdownReader
        content="[debug log](att:file)"
        baseHeadingLevel={2}
        resolveAttachment={(id) => (id === 'file' ? fileAttachment : null)}
      />,
    )

    const card = screen.getByTestId('markdown-attachment-file-card')
    expect(card).toHaveAttribute('href', fileAttachment.url)
    expect(card).toHaveAttribute('download', fileAttachment.fileName)
    expect(card).toHaveTextContent('debug.log')
    expect(card).toHaveTextContent('2.0 KB')
    expect(card).toHaveTextContent('text/plain')
    expect(screen.queryByRole('link', { name: 'debug log' })).not.toBeInTheDocument()
  })

  it('renders unresolved att references as safe fallbacks without using untrusted URLs', () => {
    const resolveAttachment = vi.fn(() => null)
    render(
      <MarkdownReader
        content="Missing [file](att:missing) and ![image](att:gone)."
        baseHeadingLevel={2}
        resolveAttachment={resolveAttachment}
      />,
    )

    expect(resolveAttachment).toHaveBeenCalledWith('missing')
    expect(resolveAttachment).toHaveBeenCalledWith('gone')
    expect(screen.getAllByTestId('markdown-attachment-fallback')).toHaveLength(2)
    expect(screen.getByText('Attachment unavailable: missing')).toBeInTheDocument()
    expect(screen.getByText('Attachment unavailable: gone')).toBeInTheDocument()
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })
})

describe('MarkdownReader heading remap', () => {
  afterEach(() => {
    cleanup()
  })

  it('shifts rendered heading elements by baseHeadingLevel', () => {
    const content = ['# Title', '## Section', '### Subsection'].join('\n')
    render(<MarkdownReader content={content} baseHeadingLevel={3} />)

    expect(screen.getByRole('heading', { level: 3, name: 'Title' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 4, name: 'Section' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 5, name: 'Subsection' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { level: 1 })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { level: 2 })).not.toBeInTheDocument()
  })

  it('clamps rendered heading elements at h6', () => {
    const content = ['# A', '## B', '### C', '#### D', '##### E', '###### F'].join('\n')
    render(<MarkdownReader content={content} baseHeadingLevel={5} />)

    expect(screen.getByRole('heading', { level: 5, name: 'A' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 6, name: 'B' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 6, name: 'C' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 6, name: 'D' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 6, name: 'E' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 6, name: 'F' })).toBeInTheDocument()
  })

  it('does not render an h1 for an embedded "# heading" when baseHeadingLevel > 1', () => {
    render(
      <div>
        <h1>Page title</h1>
        <MarkdownReader content="# Embedded title" baseHeadingLevel={2} />
      </div>,
    )

    const h1s = screen.getAllByRole('heading', { level: 1 })
    expect(h1s).toHaveLength(1)
    expect(h1s[0]).toHaveTextContent('Page title')
    expect(screen.getByRole('heading', { level: 2, name: 'Embedded title' })).toBeInTheDocument()
  })
})

describe('MarkdownReader code-block scrolling affordance', () => {
  afterEach(() => {
    cleanup()
  })

  it('marks a long code line and its pre wrapper as horizontally scrollable', () => {
    const longLine = 'a'.repeat(2000)
    const content = '```\n' + longLine + '\n```'

    render(<MarkdownReader content={content} baseHeadingLevel={2} />)

    const codeEl = screen.getByTestId('markdown-code-block')
    expect(codeEl.className).toContain('overflow-x-auto')

    const preEl = screen.getByTestId('markdown-pre')
    expect(preEl.className).toContain('overflow-x-auto')
  })
})

describe('MarkdownReader table containment', () => {
  afterEach(() => {
    cleanup()
  })

  it('wraps a wide table in a horizontally scrollable container', () => {
    const header = '| ' + Array.from({ length: 12 }, (_, i) => `col-${i + 1}`).join(' | ') + ' |'
    const separator = '| ' + Array.from({ length: 12 }, () => '---').join(' | ') + ' |'
    const row = '| ' + Array.from({ length: 12 }, (_, i) => `value-${i + 1}`).join(' | ') + ' |'
    const content = [header, separator, row, row, row].join('\n')

    render(<MarkdownReader content={content} baseHeadingLevel={2} />)

    const wrapper = screen.getByTestId('markdown-table-wrapper')
    expect(wrapper.className).toContain('overflow-x-auto')

    const table = wrapper.querySelector('table')
    expect(table).not.toBeNull()
    expect((table as HTMLTableElement).className).toContain('min-w-full')
  })
})

describe('MarkdownReader long link and inline-code wrapping', () => {
  afterEach(() => {
    cleanup()
  })

  it('applies overflow-wrap to a long bare URL or link', () => {
    const longUrl = 'https://example.com/' + 'segment-'.repeat(60) + 'end'
    const content = `See [link](${longUrl}) or the bare URL ${longUrl}.`

    render(<MarkdownReader content={content} baseHeadingLevel={2} />)

    const anchors = screen.getAllByRole('link')
    for (const anchor of anchors) {
      expect(anchor.className).toContain('overflow-wrap:anywhere')
    }
  })

  it('applies overflow-wrap to long inline code paths', () => {
    const longPath = '/home/' + 'very-long-directory-name-'.repeat(20) + 'final-file.ts'
    const content = `Reference: \`${longPath}\` here.`

    render(<MarkdownReader content={content} baseHeadingLevel={2} />)

    const inline = screen.getByText(longPath)
    expect(inline.tagName).toBe('CODE')
    expect(inline.className).toContain('overflow-wrap:anywhere')
  })
})

describe('MarkdownReader collapsible vs full modes', () => {
  function withSimulatedHeight(scrollHeight: number, run: () => void) {
    setScopedProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get() {
        return scrollHeight
      },
    })
    setScopedProperty(HTMLElement.prototype, 'clientHeight', {
      configurable: true,
      get() {
        return 0
      },
    })
    run()
  }

  beforeEach(() => {
    setupResizeObserver()
  })

  afterEach(() => {
    cleanup()
  })

  function makeTallContent() {
    return Array.from({ length: 60 }, (_, i) => `Paragraph number ${i + 1} with some content.`).join('\n\n')
  }

  it('collapses long content in collapsible mode, expands, and collapses again', () => {
    withSimulatedHeight(1200, () => {
      const { unmount } = render(
        <MarkdownReader content={makeTallContent()} mode="collapsible" collapsedHeight={600} baseHeadingLevel={2} />,
      )

      const body = screen.getByTestId('markdown-reader-body')
      expect(body.getAttribute('data-overflow')).toBe('constrained')
      expect(body.style.maxHeight).toBe('600px')
      expect(screen.getByTestId('markdown-reader-gradient')).toBeInTheDocument()

      const expandButton = screen.getByTestId('markdown-expand-control')
      expect(expandButton).toHaveTextContent('Expand')

      fireEvent.click(expandButton)
      expect(screen.getByTestId('markdown-reader-body').getAttribute('data-overflow')).toBe('free')
      expect(screen.queryByTestId('markdown-reader-gradient')).not.toBeInTheDocument()
      const collapseButton = screen.getByTestId('markdown-collapse-control')
      expect(collapseButton).toHaveTextContent('Collapse')

      fireEvent.click(collapseButton)
      expect(screen.getByTestId('markdown-reader-body').getAttribute('data-overflow')).toBe('constrained')
      expect(screen.getByTestId('markdown-reader-gradient')).toBeInTheDocument()
      expect(screen.getByTestId('markdown-expand-control')).toBeInTheDocument()

      unmount()
    })
  })

  it('does not render an Expand control in collapsible mode for short content', () => {
    withSimulatedHeight(100, () => {
      render(<MarkdownReader content="Short content." mode="collapsible" collapsedHeight={600} baseHeadingLevel={2} />)

      expect(screen.queryByTestId('markdown-expand-control')).not.toBeInTheDocument()
      expect(screen.queryByTestId('markdown-collapse-control')).not.toBeInTheDocument()
      expect(screen.getByTestId('markdown-reader-body').getAttribute('data-overflow')).toBe('free')
    })
  })

  it('never renders a Reader-level collapse control in full mode', () => {
    withSimulatedHeight(1200, () => {
      render(<MarkdownReader content={makeTallContent()} mode="full" baseHeadingLevel={2} />)

      expect(screen.queryByTestId('markdown-expand-control')).not.toBeInTheDocument()
      expect(screen.queryByTestId('markdown-collapse-control')).not.toBeInTheDocument()
      expect(screen.getByTestId('markdown-reader-body').getAttribute('data-overflow')).toBe('free')
    })
  })
})
