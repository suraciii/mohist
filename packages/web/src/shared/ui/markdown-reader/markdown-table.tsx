import type { HTMLAttributes, TableHTMLAttributes } from 'react'
import { cn } from '@/shared/lib/utils'

type DivProps = HTMLAttributes<HTMLDivElement> & { node?: unknown }
type TableProps = TableHTMLAttributes<HTMLTableElement> & { node?: unknown }

export function MarkdownTableWrapper({ children, className, ...props }: DivProps) {
  return (
    <div
      data-testid="markdown-table-wrapper"
      className={cn('my-4 overflow-x-auto rounded-md border border-gray-200', className)}
      {...props}
    >
      {children}
    </div>
  )
}

export function MarkdownTable({ children, className, ...props }: TableProps) {
  return (
    <table className={cn('block min-w-full text-sm', className)} {...props}>
      {children}
    </table>
  )
}
