import { useEffect, useRef, useState, type MouseEvent } from 'react'
import { Button } from '@/shared/ui/components/button'
import { cn } from '@/shared/lib/utils'

export type CopyCodeButtonProps = {
  text: string
  className?: string
}

function getCodeText(node: unknown): string {
  if (node == null || typeof node === 'boolean') return ''
  if (typeof node === 'string' || typeof node === 'number') return String(node)
  if (Array.isArray(node)) return node.map(getCodeText).join('')
  if (typeof node === 'object' && node !== null && 'props' in node) {
    const props = (node as { props?: { children?: unknown } }).props
    return getCodeText(props?.children)
  }
  return ''
}

export function CopyCodeButton({ text, className }: CopyCodeButtonProps) {
  const [copied, setCopied] = useState(false)
  const resetTimerRef = useRef<number | null>(null)

  useEffect(() => () => {
    if (resetTimerRef.current !== null) window.clearTimeout(resetTimerRef.current)
  }, [])

  const handleClick = (event: MouseEvent<HTMLButtonElement>) => {
    event.stopPropagation()
    event.preventDefault()
    if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
      void navigator.clipboard.writeText(text).then(() => {
        setCopied(true)
        if (resetTimerRef.current !== null) window.clearTimeout(resetTimerRef.current)
        resetTimerRef.current = window.setTimeout(() => {
          resetTimerRef.current = null
          setCopied(false)
        }, 1500)
      })
    }
  }

  return (
    <Button
      type="button"
      variant="ghost"
      size="icon-xs"
      onClick={handleClick}
      data-testid="markdown-copy-code"
      aria-label={copied ? 'Copied' : 'Copy code'}
      className={cn('absolute top-1 right-1 h-6 w-6 rounded bg-white/80 text-gray-500 hover:bg-white hover:text-gray-800', className)}
    >
      {copied ? (
        <svg viewBox="0 0 20 20" fill="currentColor" className="h-3 w-3">
          <path fillRule="evenodd" d="M16.704 5.296a1 1 0 010 1.408l-7.997 8.005a1 1 0 01-1.408 0L3.296 10.71a1 1 0 011.408-1.408l3.299 3.295 7.293-7.301a1 1 0 011.408 0z" clipRule="evenodd" />
        </svg>
      ) : (
        <svg viewBox="0 0 20 20" fill="currentColor" className="h-3 w-3">
          <path d="M8 2a2 2 0 00-2 2v1H5a2 2 0 00-2 2v9a2 2 0 002 2h8a2 2 0 002-2v-1h1a2 2 0 002-2V6.414a2 2 0 00-.586-1.414l-2.414-2.414A2 2 0 0014.586 2H8zm5 3V4h1.586L16 5.414V6h-1.5A1.5 1.5 0 0113 4.5V5zM6 6h6v8H6V6z" />
        </svg>
      )}
    </Button>
  )
}

export function extractTextFromChildren(children: unknown): string {
  return getCodeText(children)
}
