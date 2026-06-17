import { createContext, useContext, useEffect, useMemo, useRef, useState, type ComponentProps } from 'react'
import Markdown, { type Components, type ExtraProps } from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { Button } from '@/shared/ui/components/button'
import { cn } from '@/shared/lib/utils'
import {
  buildHeadingOverrides,
  collectHeadings,
  type HeadingEntry,
  type HeadingLevel,
  type HeadingSlugger,
} from './heading-remap'
import { MarkdownTableWrapper, MarkdownTable } from './markdown-table'
import { CopyCodeButton, extractTextFromChildren } from './copy-code-button'

export type MarkdownReaderMode = 'full' | 'collapsible'

export type MarkdownReaderProps = {
  content: string
  baseHeadingLevel?: HeadingLevel
  mode?: MarkdownReaderMode
  collapsedHeight?: number
  showToc?: boolean
  showHeadingAnchors?: boolean
  showCopyCode?: boolean
  className?: string
}

type CodeProps = ComponentProps<'code'> & ExtraProps
type PreProps = ComponentProps<'pre'> & ExtraProps
type AnchorProps = ComponentProps<'a'> & ExtraProps
type TableSectionProps = ComponentProps<'thead'> & ExtraProps
type TableRowProps = ComponentProps<'tr'> & ExtraProps
type TableCellProps = ComponentProps<'th'> & ExtraProps
type QuoteProps = ComponentProps<'blockquote'> & ExtraProps
type HrProps = ComponentProps<'hr'> & ExtraProps
type ParagraphProps = ComponentProps<'p'> & ExtraProps
type UlProps = ComponentProps<'ul'> & ExtraProps
type OlProps = ComponentProps<'ol'> & ExtraProps
type LiProps = ComponentProps<'li'> & ExtraProps

const CodeBlockContext = createContext(false)

function MarkdownCodeBlock({
  children,
  className,
  node,
  showCopyCode,
  ...props
}: CodeProps & { showCopyCode: boolean }) {
  const isBlock = useContext(CodeBlockContext)
  if (!isBlock) {
    return (
      <code
        className="px-1 py-0.5 bg-gray-100 rounded text-gray-800 text-xs font-mono [overflow-wrap:anywhere]"
        {...props}
      >
        {children}
      </code>
    )
  }
  const text = extractTextFromChildren(children)
  return (
    <code
      data-testid="markdown-code-block"
      className={cn('block overflow-x-auto rounded bg-gray-50 p-3 text-xs font-mono', className)}
      {...props}
    >
      {children}
      {showCopyCode && <CopyCodeButton text={text} />}
    </code>
  )
}

function MarkdownPre({ children, ...props }: PreProps) {
  return (
    <pre
      data-testid="markdown-pre"
      className="relative my-3 overflow-x-auto rounded bg-gray-50 p-3 text-xs font-mono"
      {...props}
    >
      <CodeBlockContext.Provider value>
        {children}
      </CodeBlockContext.Provider>
    </pre>
  )
}

function MarkdownLink({ children, href, ...props }: AnchorProps) {
  return (
    <a
      href={href}
      className="text-blue-600 underline-offset-2 hover:underline [overflow-wrap:anywhere]"
      {...props}
    >
      {children}
    </a>
  )
}

function MarkdownHr(props: HrProps) {
  return <hr className="my-4 border-gray-200" {...props} />
}

function MarkdownParagraph({ children, ...props }: ParagraphProps) {
  return (
    <p className="my-3 leading-relaxed" {...props}>
      {children}
    </p>
  )
}

function buildReaderComponents(
  slugger: HeadingSlugger,
  base: HeadingLevel,
  showCopyCode: boolean,
  showAnchors: boolean,
): Components {
  const headings = buildHeadingOverrides({ base, showAnchors, slugger })
  return {
    ...headings,
    code: (props) => <MarkdownCodeBlock {...props} showCopyCode={showCopyCode} />,
    pre: MarkdownPre as Components['pre'],
    a: MarkdownLink as Components['a'],
    table: (({ children, ...props }: TableSectionProps) => (
      <MarkdownTableWrapper {...(props as { node?: unknown })}>
        <MarkdownTable>{children}</MarkdownTable>
      </MarkdownTableWrapper>
    )) as Components['table'],
    thead: (({ children, ...props }: TableSectionProps) => (
      <thead className="bg-gray-50" {...props}>
        {children}
      </thead>
    )) as Components['thead'],
    tbody: (({ children, ...props }: TableSectionProps) => (
      <tbody {...props}>{children}</tbody>
    )) as Components['tbody'],
    tr: (({ children, ...props }: TableRowProps) => (
      <tr className="border-b border-gray-100 last:border-0" {...props}>
        {children}
      </tr>
    )) as Components['tr'],
    th: (({ children, ...props }: TableCellProps) => (
      <th className="border-b border-gray-200 px-3 py-2 text-left font-semibold text-gray-700" {...props}>
        {children}
      </th>
    )) as Components['th'],
    td: (({ children, ...props }: TableCellProps) => (
      <td className="border-b border-gray-100 px-3 py-2 align-top text-gray-800 [overflow-wrap:anywhere]" {...props}>
        {children}
      </td>
    )) as Components['td'],
    blockquote: (({ children, ...props }: QuoteProps) => (
      <blockquote
        className="my-3 border-l-4 border-gray-200 bg-gray-50/60 pl-3 pr-2 py-1 italic text-gray-700"
        {...props}
      >
        {children}
      </blockquote>
    )) as Components['blockquote'],
    hr: MarkdownHr as Components['hr'],
    p: MarkdownParagraph as Components['p'],
    ul: (({ children, ...props }: UlProps) => (
      <ul className="my-3 list-disc pl-6 space-y-1" {...props}>
        {children}
      </ul>
    )) as Components['ul'],
    ol: (({ children, ...props }: OlProps) => (
      <ol className="my-3 list-decimal pl-6 space-y-1" {...props}>
        {children}
      </ol>
    )) as Components['ol'],
    li: (({ children, ...props }: LiProps) => (
      <li className="leading-relaxed" {...props}>
        {children}
      </li>
    )) as Components['li'],
  }
}

function MarkdownToc({ entries }: { entries: HeadingEntry[] }) {
  if (entries.length === 0) return null
  return (
    <nav
      data-testid="markdown-toc"
      aria-label="Table of contents"
      className="my-3 rounded-md border border-gray-200 bg-gray-50/60 p-3 text-xs"
    >
      <p className="mb-2 font-semibold uppercase tracking-wide text-gray-500">On this page</p>
      <ul className="space-y-1">
        {entries.map((entry) => (
          <li key={entry.id} style={{ paddingLeft: `${(entry.level - 1) * 12}px` }}>
            <a
              href={`#${entry.id}`}
              className="text-gray-700 hover:text-blue-600"
              data-testid={`markdown-toc-link-${entry.id}`}
            >
              {entry.text}
            </a>
          </li>
        ))}
      </ul>
    </nav>
  )
}

export function MarkdownReader({
  content,
  baseHeadingLevel = 2,
  mode = 'full',
  collapsedHeight = 600,
  showToc = false,
  showHeadingAnchors = false,
  showCopyCode = false,
  className,
}: MarkdownReaderProps) {
  const isCollapsible = mode === 'collapsible'
  const [expanded, setExpanded] = useState(false)
  const [measuredOverflow, setMeasuredOverflow] = useState(false)
  const bodyRef = useRef<HTMLDivElement | null>(null)

  const { entries, slugger } = useMemo(
    () => collectHeadings(content, baseHeadingLevel),
    [content, baseHeadingLevel],
  )

  const components = useMemo(
    () => buildReaderComponents(slugger, baseHeadingLevel, showCopyCode, showHeadingAnchors),
    [slugger, baseHeadingLevel, showCopyCode, showHeadingAnchors],
  )

  useEffect(() => {
    if (!isCollapsible) {
      setMeasuredOverflow(false)
      setExpanded(false)
      return
    }
    setExpanded(false)
    setMeasuredOverflow(false)
    const element = bodyRef.current
    if (!element) return

    const measure = () => {
      const next = element.scrollHeight > collapsedHeight
      setMeasuredOverflow((prev) => (prev === next ? prev : next))
    }

    measure()
    if (typeof ResizeObserver !== 'undefined') {
      const observer = new ResizeObserver(() => measure())
      observer.observe(element)
      return () => observer.disconnect()
    }
    return undefined
  }, [content, isCollapsible, collapsedHeight])

  const showCollapse = isCollapsible && measuredOverflow
  const collapseActive = isCollapsible && expanded
  const shouldConstrain = isCollapsible && measuredOverflow && !expanded

  return (
    <div
      data-testid="markdown-reader"
      data-mode={mode}
      data-base-heading-level={baseHeadingLevel}
      className={cn('markdown-reader prose prose-sm max-w-none text-gray-800', className)}
    >
      {showToc && entries.length > 0 && <MarkdownToc entries={entries} />}
      <div
        ref={bodyRef}
        data-testid="markdown-reader-body"
        data-overflow={shouldConstrain ? 'constrained' : 'free'}
        className={cn(
          'relative',
          shouldConstrain && 'overflow-hidden',
        )}
        style={shouldConstrain ? { maxHeight: `${collapsedHeight}px` } : undefined}
      >
        <Markdown remarkPlugins={[remarkGfm]} components={components}>
          {content}
        </Markdown>
        {shouldConstrain && (
          <div
            data-testid="markdown-reader-gradient"
            aria-hidden="true"
            className="pointer-events-none absolute bottom-0 left-0 right-0 h-20 bg-gradient-to-t from-white to-transparent"
          />
        )}
      </div>
      {showCollapse && (
        <div className="mt-2">
          <Button
            variant="link"
            size="xs"
            onClick={() => setExpanded((value) => !value)}
            data-testid={collapseActive ? 'markdown-collapse-control' : 'markdown-expand-control'}
          >
            {collapseActive ? 'Collapse' : 'Expand'}
          </Button>
        </div>
      )}
    </div>
  )
}
