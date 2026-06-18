// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { useRef, type ReactNode } from 'react'
import { MarkdownReader, type MarkdownAttachment } from './MarkdownReader'
import * as SharedUiBarrel from '@/shared/ui'

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

function OverflowProbe({ children }: { children: ReactNode }) {
  const containerRef = useRef<HTMLDivElement | null>(null)

  return (
    <div
      ref={containerRef}
      data-testid="overflow-probe"
      className="w-[400px] overflow-x-hidden border"
    >
      {children}
    </div>
  )
}

function setupResizeObserver() {
  const instances: StubResizeObserver[] = []
  const spy = vi.fn(function (this: unknown, callback: ResizeObserverCallback) {
    const instance = new StubResizeObserver(callback)
    instances.push(instance)
    return instance
  }) as unknown as ResizeObserverCtor
  ;(globalThis as { ResizeObserver?: ResizeObserverCtor }).ResizeObserver = spy
  return { instances, spy }
}

function teardownResizeObserver() {
  delete (globalThis as { ResizeObserver?: ResizeObserverCtor }).ResizeObserver
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

  it('renders one-line fenced code as a block with copy-code support', () => {
    render(<MarkdownReader content={'```\nconst x = 1\n```'} baseHeadingLevel={2} showCopyCode />)

    const codeBlock = screen.getByTestId('markdown-code-block')
    expect(codeBlock).toHaveTextContent('const x = 1')
    expect(codeBlock.className).toContain('overflow-x-auto')
    expect(screen.getByTestId('markdown-copy-code')).toBeInTheDocument()
  })

  it('preserves the inline-code visual styling from the previous MarkdownContent', () => {
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
    render(<MarkdownReader content="Missing [file](att:missing) and ![image](att:gone)." baseHeadingLevel={2} resolveAttachment={resolveAttachment} />)

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

describe('MarkdownReader code-block overflow', () => {
  beforeEach(() => {
    setupResizeObserver()
  })

  afterEach(() => {
    cleanup()
    teardownResizeObserver()
  })

  it('does not produce page-level horizontal overflow for a long code line', () => {
    const longLine = 'a'.repeat(2000)
    const content = '```\n' + longLine + '\n```'

    const originalScrollWidth = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollWidth')
    const originalClientWidth = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'clientWidth')

    const measured = { code: { scrollWidth: 0, clientWidth: 0 }, pre: { scrollWidth: 0, clientWidth: 0 }, probe: { scrollWidth: 0, clientWidth: 0 } }

    Object.defineProperty(HTMLElement.prototype, 'scrollWidth', {
      configurable: true,
      get() {
        const testId = this.getAttribute?.('data-testid')
        if (testId === 'markdown-code-block') return measured.code.scrollWidth
        if (testId === 'markdown-pre') return measured.pre.scrollWidth
        if (testId === 'overflow-probe') return measured.probe.scrollWidth
        return 0
      },
    })
    Object.defineProperty(HTMLElement.prototype, 'clientWidth', {
      configurable: true,
      get() {
        const testId = this.getAttribute?.('data-testid')
        if (testId === 'markdown-code-block') return measured.code.clientWidth
        if (testId === 'markdown-pre') return measured.pre.clientWidth
        if (testId === 'overflow-probe') return measured.probe.clientWidth
        return 0
      },
    })

    try {
      render(
        <OverflowProbe>
          <MarkdownReader content={content} baseHeadingLevel={2} />
        </OverflowProbe>,
      )

      measured.code.scrollWidth = 5000
      measured.code.clientWidth = 300
      measured.pre.scrollWidth = 5000
      measured.pre.clientWidth = 300
      measured.probe.scrollWidth = 400
      measured.probe.clientWidth = 400

      const probe = screen.getByTestId('overflow-probe')
      expect(probe.scrollWidth).toBe(probe.clientWidth)

      const codeEl = screen.getByTestId('markdown-code-block')
      expect(codeEl.scrollWidth).toBeGreaterThan(codeEl.clientWidth)
      expect(codeEl.className).toContain('overflow-x-auto')

      const preEl = screen.getByTestId('markdown-pre')
      expect(preEl.scrollWidth).toBeGreaterThan(preEl.clientWidth)
      expect(preEl.className).toContain('overflow-x-auto')
    } finally {
      if (originalScrollWidth) {
        Object.defineProperty(HTMLElement.prototype, 'scrollWidth', originalScrollWidth)
      } else {
        Object.defineProperty(HTMLElement.prototype, 'scrollWidth', { configurable: true, value: 0 })
      }
      if (originalClientWidth) {
        Object.defineProperty(HTMLElement.prototype, 'clientWidth', originalClientWidth)
      } else {
        Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, value: 0 })
      }
    }
  })
})

describe('MarkdownReader copy-code affordance', () => {
  afterEach(() => {
    cleanup()
  })

  it('renders a copy affordance when showCopyCode is enabled and writes the block text to the clipboard', () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    const originalClipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard')
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    })

    try {
      const content = '```ts\nconst greet = () => "hi"\n```'
      render(<MarkdownReader content={content} baseHeadingLevel={2} showCopyCode />)

      const button = screen.getByTestId('markdown-copy-code')
      expect(button).toBeInTheDocument()

      fireEvent.click(button)

      expect(writeText).toHaveBeenCalledTimes(1)
      const written = writeText.mock.calls[0]?.[0] as string
      expect(written.replace(/\n$/, '')).toBe('const greet = () => "hi"')
    } finally {
      if (originalClipboard) {
        Object.defineProperty(navigator, 'clipboard', originalClipboard)
      } else {
        Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined })
      }
    }
  })

  it('does not render a copy affordance when showCopyCode is omitted', () => {
    render(<MarkdownReader content="```ts\nconst x = 1\n```" baseHeadingLevel={2} />)
    expect(screen.queryByTestId('markdown-copy-code')).not.toBeInTheDocument()
  })
})

describe('MarkdownReader table containment', () => {
  afterEach(() => {
    cleanup()
  })

  it('wraps a wide table in a horizontally scrollable container and does not expand the page', () => {
    const header = '| ' + Array.from({ length: 12 }, (_, i) => `col-${i + 1}`).join(' | ') + ' |'
    const separator = '| ' + Array.from({ length: 12 }, () => '---').join(' | ') + ' |'
    const row = '| ' + Array.from({ length: 12 }, (_, i) => `value-${i + 1}`).join(' | ') + ' |'
    const content = [header, separator, row, row, row].join('\n')

    render(
      <OverflowProbe>
        <MarkdownReader content={content} baseHeadingLevel={2} />
      </OverflowProbe>,
    )

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
    const originalScrollHeight = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollHeight')
    const originalClientHeight = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'clientHeight')
    Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get() {
        return scrollHeight
      },
    })
    Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
      configurable: true,
      get() {
        return 0
      },
    })
    try {
      run()
    } finally {
      if (originalScrollHeight) {
        Object.defineProperty(HTMLElement.prototype, 'scrollHeight', originalScrollHeight)
      }
      if (originalClientHeight) {
        Object.defineProperty(HTMLElement.prototype, 'clientHeight', originalClientHeight)
      }
    }
  }

  beforeEach(() => {
    setupResizeObserver()
  })

  afterEach(() => {
    cleanup()
    teardownResizeObserver()
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
      render(
        <MarkdownReader content="Short content." mode="collapsible" collapsedHeight={600} baseHeadingLevel={2} />,
      )

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

describe('MarkdownReader optional affordances', () => {
  afterEach(() => {
    cleanup()
  })

  it('renders a table of contents when showToc is enabled', () => {
    const content = ['# One', '## Two', '### Three'].join('\n')
    render(<MarkdownReader content={content} baseHeadingLevel={2} showToc />)

    const toc = screen.getByTestId('markdown-toc')
    expect(toc).toBeInTheDocument()
    expect(toc).toHaveTextContent('One')
    expect(toc).toHaveTextContent('Two')
    expect(toc).toHaveTextContent('Three')
  })

  it('renders heading anchors when showHeadingAnchors is enabled', () => {
    const content = '# Anchor target'
    render(<MarkdownReader content={content} baseHeadingLevel={2} showHeadingAnchors />)

    const heading = screen.getByRole('heading', { level: 2, name: /Anchor target/ })
    const anchor = heading.querySelector('a')
    expect(anchor).not.toBeNull()
    expect(anchor).toHaveAttribute('href', '#anchor-target')
  })

  it('generates unique anchors and TOC links for duplicate heading text', () => {
    render(<MarkdownReader content={'# Details\n\n## Details'} baseHeadingLevel={2} showToc showHeadingAnchors />)

    const headings = screen.getAllByRole('heading', { name: /Details/ })
    expect(headings.map((heading) => heading.id)).toEqual(['details', 'details-1'])

    expect(screen.getByTestId('markdown-toc-link-details')).toHaveAttribute('href', '#details')
    expect(screen.getByTestId('markdown-toc-link-details-1')).toHaveAttribute('href', '#details-1')
  })

  it('keeps TOC links aligned with formatted heading anchors', () => {
    render(<MarkdownReader content={'# **Details**\n\n## **Details**'} baseHeadingLevel={2} showToc showHeadingAnchors />)

    const headings = screen.getAllByRole('heading', { name: /Details/ })
    expect(headings.map((heading) => heading.id)).toEqual(['details', 'details-1'])

    expect(screen.getByTestId('markdown-toc-link-details')).toHaveAttribute('href', '#details')
    expect(screen.getByTestId('markdown-toc-link-details-1')).toHaveAttribute('href', '#details-1')
  })

  it('does not render TOC or anchors when both are omitted', () => {
    const content = '# Quiet heading'
    render(<MarkdownReader content={content} baseHeadingLevel={2} />)

    expect(screen.queryByTestId('markdown-toc')).not.toBeInTheDocument()
    const heading = screen.getByRole('heading', { level: 2, name: 'Quiet heading' })
    expect(heading.querySelector('a')).toBeNull()
  })
})
