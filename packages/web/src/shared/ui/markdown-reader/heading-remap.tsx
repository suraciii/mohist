import type { ComponentType, HTMLAttributes } from 'react'
import type { Components, ExtraProps } from 'react-markdown'

export type HeadingLevel = 1 | 2 | 3 | 4 | 5 | 6

export type HeadingRemapOptions = {
  base: HeadingLevel
  showAnchors?: boolean
  slugger?: HeadingSlugger
}

export type HeadingSlugger = {
  slug(text: string): string
}

export type HeadingSlugQueue = HeadingSlugger & {
  enqueue(text: string): string
}

const clampHeading = (level: number): HeadingLevel => {
  if (level <= 1) return 1
  if (level >= 6) return 6
  return level as HeadingLevel
}

export function remapHeadingLevel(original: HeadingLevel, base: HeadingLevel): HeadingLevel {
  return clampHeading(original + (base - 1))
}

export function defaultSlugify(text: string): string {
  const normalized = text
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9\s-]+/g, '')
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '')
  return normalized || 'section'
}

export function createHeadingSlugger(): HeadingSlugger {
  const counts = new Map<string, number>()
  return {
    slug(text: string): string {
      const base = defaultSlugify(text)
      const count = counts.get(base) ?? 0
      const id = count === 0 ? base : `${base}-${count}`
      counts.set(base, count + 1)
      return id
    },
  }
}

export function createHeadingSlugQueue(): HeadingSlugQueue {
  const slugger = createHeadingSlugger()
  const queue = new Map<string, string[]>()
  return {
    enqueue(text: string): string {
      const id = slugger.slug(text)
      const key = defaultSlugify(text)
      const ids = queue.get(key) ?? []
      ids.push(id)
      queue.set(key, ids)
      return id
    },
    slug(text: string): string {
      const ids = queue.get(defaultSlugify(text))
      const id = ids?.shift()
      if (id) return id
      return slugger.slug(text)
    },
  }
}

function extractText(node: unknown): string {
  if (node == null || typeof node === 'boolean') return ''
  if (typeof node === 'string' || typeof node === 'number') return String(node)
  if (Array.isArray(node)) return node.map(extractText).join('')
  if (typeof node === 'object' && node !== null && 'props' in node) {
    const props = (node as { props?: { children?: unknown } }).props
    return extractText(props?.children)
  }
  return ''
}

type HeadingProps = HTMLAttributes<HTMLHeadingElement> & ExtraProps

function makeHeading(
  originalLevel: HeadingLevel,
  options: HeadingRemapOptions,
): ComponentType<HeadingProps> {
  const targetLevel = remapHeadingLevel(originalLevel, options.base)

  const Heading = ({ children, node: _node, ...props }: HeadingProps) => {
    const text = extractText(children)
    const id = options.slugger ? options.slugger.slug(text) : props.id
    const shouldAnchor = options.showAnchors ?? false
    const Tag = `h${targetLevel}` as `h${HeadingLevel}`

    if (shouldAnchor && id) {
      return (
        <Tag id={id} data-heading-level={targetLevel} data-original-level={originalLevel} {...props}>
          <a
            href={`#${id}`}
            data-testid={`heading-anchor-${id}`}
            className="mr-2 text-muted-foreground/60 hover:text-muted-foreground no-underline"
            aria-label={`Link to ${text}`}
          >
            #
          </a>
          {children}
        </Tag>
      )
    }

    return (
      <Tag id={id} data-heading-level={targetLevel} data-original-level={originalLevel} {...props}>
        {children}
      </Tag>
    )
  }

  return Heading
}

export function buildHeadingOverrides(options: HeadingRemapOptions): Pick<Components, 'h1' | 'h2' | 'h3' | 'h4' | 'h5' | 'h6'> {
  return {
    h1: makeHeading(1, options),
    h2: makeHeading(2, options),
    h3: makeHeading(3, options),
    h4: makeHeading(4, options),
    h5: makeHeading(5, options),
    h6: makeHeading(6, options),
  }
}

export type HeadingEntry = {
  level: HeadingLevel
  text: string
  id: string
}

export function collectHeadings(
  content: string,
  base: HeadingLevel,
): { entries: HeadingEntry[]; slugger: HeadingSlugger } {
  const slugger = createHeadingSlugQueue()
  const lines = content.split(/\r?\n/)
  const entries: HeadingEntry[] = []
  let inFence = false

  for (const rawLine of lines) {
    const line = rawLine.trimEnd()
    if (/^\s*(```|~~~)/.test(line)) {
      inFence = !inFence
      continue
    }
    if (inFence) continue
    const match = /^(#{1,6})\s+(.*?)\s*#*\s*$/.exec(line)
    if (!match) continue
    const hashes = match[1].length as HeadingLevel
    const text = match[2]
    const level = remapHeadingLevel(hashes, base)
    entries.push({ level, text, id: slugger.enqueue(text) })
  }

  return { entries, slugger }
}
