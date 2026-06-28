import { createContext, useContext, useEffect, useRef, type ComponentProps } from 'react'
import Markdown, { type Components, type ExtraProps } from 'react-markdown'
import remarkGfm from 'remark-gfm'
import rehypeHighlight from 'rehype-highlight'
import { common } from 'lowlight'
import { cn } from '@/shared/lib/utils'
import { TRANSCRIPT_MD_HIGHLIGHT_CSS } from './transcript-markdown-theme'

type CodeProps = ComponentProps<'code'> & ExtraProps
type PreProps = ComponentProps<'pre'> & ExtraProps

const TRANSCRIPT_MD_CLASS = 'transcript-md'
const TRANSCRIPT_MD_HIGHLIGHT_STYLE_ID = 'transcript-md-highlight-styles'

const CodeBlockContext = createContext(false)

function TranscriptInlineCode({ children, className, ...props }: CodeProps) {
  return (
    <code
      className={cn(
        'rounded bg-gray-100 px-1 py-0.5 font-mono text-xs text-gray-800 [overflow-wrap:anywhere]',
        className,
      )}
      {...props}
    >
      {children}
    </code>
  )
}

function TranscriptCodeBlock({ children, className, ...props }: CodeProps) {
  const isBlock = useContext(CodeBlockContext)
  if (!isBlock) {
    return (
      <TranscriptInlineCode className={className} {...(props as CodeProps)}>
        {children}
      </TranscriptInlineCode>
    )
  }
  return (
    <code className={cn('font-mono text-xs', className)} {...props}>
      {children}
    </code>
  )
}

function TranscriptPre({ children, ...props }: PreProps) {
  return (
    <pre
      className="max-w-full overflow-x-auto rounded bg-gray-50 p-3 font-mono text-xs"
      {...props}
    >
      <CodeBlockContext.Provider value>{children}</CodeBlockContext.Provider>
    </pre>
  )
}

const components: Components = {
  code: (props) => <TranscriptCodeBlock {...(props as CodeProps)} />,
  pre: TranscriptPre as Components['pre'],
}

function useTranscriptMdHighlightStyles() {
  const injectedRef = useRef(false)
  useEffect(() => {
    if (typeof document === 'undefined') return
    if (document.getElementById(TRANSCRIPT_MD_HIGHLIGHT_STYLE_ID)) return
    const style = document.createElement('style')
    style.id = TRANSCRIPT_MD_HIGHLIGHT_STYLE_ID
    style.setAttribute('data-transcript-md-highlight', '')
    style.textContent = TRANSCRIPT_MD_HIGHLIGHT_CSS
    document.head.appendChild(style)
    injectedRef.current = true
  }, [])
}

export interface TranscriptMarkdownProps {
  content: string
  className?: string
}

export function TranscriptMarkdown({ content, className }: TranscriptMarkdownProps) {
  useTranscriptMdHighlightStyles()
  return (
    <div className={cn(TRANSCRIPT_MD_CLASS, 'text-sm text-gray-800 leading-relaxed', className)}>
      <Markdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[[rehypeHighlight, { languages: common }]]}
        components={components}
      >
        {content}
      </Markdown>
    </div>
  )
}