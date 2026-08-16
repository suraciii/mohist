import { createContext, useContext, useEffect, useMemo, useRef, useState, type ComponentProps } from 'react'
import { createPortal } from 'react-dom'
import Markdown, { defaultUrlTransform, type Components, type ExtraProps } from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { Button } from '@/shared/ui/components/button'
import { cn } from '@/shared/lib/utils'
import { buildHeadingOverrides, type HeadingLevel } from './heading-remap'
import { MarkdownTableWrapper, MarkdownTable } from './markdown-table'

export type MarkdownReaderMode = 'full' | 'collapsible'

export type MarkdownReaderProps = {
  content: string
  baseHeadingLevel?: HeadingLevel
  mode?: MarkdownReaderMode
  collapsedHeight?: number
  resolveAttachment?: (id: string) => MarkdownAttachment | null | undefined
  className?: string
}

export type MarkdownAttachment = {
  url: string
  contentType: string
  fileName: string
  size: number
}

type CodeProps = ComponentProps<'code'> & ExtraProps
type PreProps = ComponentProps<'pre'> & ExtraProps
type AnchorProps = ComponentProps<'a'> & ExtraProps
type ImageProps = ComponentProps<'img'> & ExtraProps
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
const ATTACHMENT_PREFIX = 'att:'

function isAttachmentHref(href: string | undefined): href is string {
  return typeof href === 'string' && href.startsWith(ATTACHMENT_PREFIX)
}

function getAttachmentId(href: string) {
  return href.slice(ATTACHMENT_PREFIX.length)
}

function isInlineImageAttachment(attachment: MarkdownAttachment) {
  return /^image\/(png|jpe?g|gif|webp)$/i.test(attachment.contentType)
}

function formatAttachmentSize(size: number) {
  if (!Number.isFinite(size) || size < 0) return 'Unknown size'
  if (size < 1024) return `${size} B`
  const units = ['KB', 'MB', 'GB']
  let value = size / 1024
  for (let index = 0; index < units.length; index += 1) {
    if (value < 1024 || index === units.length - 1) {
      return `${value >= 10 ? value.toFixed(0) : value.toFixed(1)} ${units[index]}`
    }
    value /= 1024
  }
  return `${size} B`
}

function MarkdownAttachmentFallback({ id }: { id: string }) {
  return (
    <span
      data-testid="markdown-attachment-fallback"
      className="inline-flex items-center rounded border border-amber-200 bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-800"
    >
      Attachment unavailable: {id}
    </span>
  )
}

function MarkdownCodeBlock({
  children,
  className,
  node,
  ...props
}: CodeProps) {
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
  return (
    <code
      data-testid="markdown-code-block"
      className={cn('block overflow-x-auto rounded bg-gray-50 p-3 text-xs font-mono', className)}
      {...props}
    >
      {children}
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

function MarkdownLink({ children, href, resolveAttachment, ...props }: AnchorProps & { resolveAttachment?: MarkdownReaderProps['resolveAttachment'] }) {
  if (isAttachmentHref(href) && resolveAttachment) {
    const id = getAttachmentId(href)
    const attachment = resolveAttachment(id)
    if (!attachment) return <MarkdownAttachmentFallback id={id} />

    return (
      <a
        href={attachment.url}
        download={attachment.fileName}
        data-testid="markdown-attachment-file-card"
        className="my-3 flex items-center gap-3 rounded-lg border border-gray-200 bg-white p-3 text-left no-underline shadow-sm transition hover:border-blue-200 hover:bg-blue-50/40"
        {...props}
      >
        <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-md bg-gray-100 text-xs font-semibold uppercase text-gray-600">
          {attachment.fileName.split('.').pop()?.slice(0, 4) || 'file'}
        </span>
        <span className="min-w-0 flex-1">
          <span className="block truncate text-sm font-medium text-gray-900">{attachment.fileName || children}</span>
          <span className="mt-0.5 block text-xs text-gray-500">
            {formatAttachmentSize(attachment.size)} · {attachment.contentType || 'application/octet-stream'}
          </span>
        </span>
      </a>
    )
  }

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

function MarkdownImage({ alt, src, resolveAttachment, onOpenLightbox, ...props }: ImageProps & {
  resolveAttachment?: MarkdownReaderProps['resolveAttachment']
  onOpenLightbox: (attachment: MarkdownAttachment) => void
}) {
  if (isAttachmentHref(src) && resolveAttachment) {
    const id = getAttachmentId(src)
    const attachment = resolveAttachment(id)
    if (!attachment || !isInlineImageAttachment(attachment)) return <MarkdownAttachmentFallback id={id} />

    return (
      <button
        type="button"
        data-testid="markdown-attachment-image-trigger"
        className="my-3 block max-w-full cursor-zoom-in rounded-lg border border-gray-200 bg-white p-0 shadow-sm"
        onClick={() => onOpenLightbox(attachment)}
      >
        <img
          src={attachment.url}
          alt={alt || attachment.fileName}
          className="max-h-[520px] max-w-full rounded-lg object-contain"
          {...props}
        />
      </button>
    )
  }

  return <img alt={alt} src={src || undefined} {...props} />
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
  base: HeadingLevel,
  resolveAttachment: MarkdownReaderProps['resolveAttachment'],
  onOpenLightbox: (attachment: MarkdownAttachment) => void,
): Components {
  const headings = buildHeadingOverrides({ base })
  return {
    ...headings,
    code: (props) => <MarkdownCodeBlock {...props} />,
    pre: MarkdownPre as Components['pre'],
    a: (props) => <MarkdownLink {...props} resolveAttachment={resolveAttachment} />,
    img: (props) => <MarkdownImage {...props} resolveAttachment={resolveAttachment} onOpenLightbox={onOpenLightbox} />,
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

function MarkdownAttachmentLightbox({ attachment, onDismiss }: { attachment: MarkdownAttachment; onDismiss: () => void }) {
  return createPortal(
    <div
      role="dialog"
      aria-modal="true"
      aria-label={`Preview ${attachment.fileName}`}
      data-testid="markdown-attachment-lightbox"
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/85 p-4"
      onClick={onDismiss}
    >
      <img
        src={attachment.url}
        alt={attachment.fileName}
        className="max-h-full max-w-full rounded-lg object-contain shadow-2xl"
      />
    </div>,
    document.body,
  )
}

export function MarkdownReader({
  content,
  baseHeadingLevel = 2,
  mode = 'full',
  collapsedHeight = 600,
  resolveAttachment,
  className,
}: MarkdownReaderProps) {
  const isCollapsible = mode === 'collapsible'
  const [expanded, setExpanded] = useState(false)
  const [measuredOverflow, setMeasuredOverflow] = useState(false)
  const [lightboxAttachment, setLightboxAttachment] = useState<MarkdownAttachment | null>(null)
  const bodyRef = useRef<HTMLDivElement | null>(null)

  const components = useMemo(
    () => buildReaderComponents(baseHeadingLevel, resolveAttachment, setLightboxAttachment),
    [baseHeadingLevel, resolveAttachment],
  )
  const urlTransform = useMemo(
    () => (resolveAttachment
      ? (url: string) => (isAttachmentHref(url) ? url : defaultUrlTransform(url))
      : undefined),
    [resolveAttachment],
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
        <Markdown remarkPlugins={[remarkGfm]} components={components} urlTransform={urlTransform}>
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
      {lightboxAttachment && (
        <MarkdownAttachmentLightbox attachment={lightboxAttachment} onDismiss={() => setLightboxAttachment(null)} />
      )}
    </div>
  )
}
