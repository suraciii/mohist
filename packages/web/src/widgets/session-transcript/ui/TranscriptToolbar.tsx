import { useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { TurnTocList, type TurnTocEntry } from './TurnToc'

interface TranscriptToolbarProps {
  entries: TurnTocEntry[]
  activeIndex?: number | null
  onTocEntryActivate?: (entry: TurnTocEntry) => void
  rightSlot?: ReactNode
}

export function TranscriptToolbar({
  entries,
  activeIndex = null,
  onTocEntryActivate,
  rightSlot,
}: TranscriptToolbarProps) {
  const [open, setOpen] = useState(false)
  const containerRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    function handleDocumentClick(evt: MouseEvent) {
      if (!containerRef.current) return
      if (containerRef.current.contains(evt.target as Node)) return
      setOpen(false)
    }
    function handleEscape(evt: KeyboardEvent) {
      if (evt.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', handleDocumentClick)
    document.addEventListener('keydown', handleEscape)
    return () => {
      document.removeEventListener('mousedown', handleDocumentClick)
      document.removeEventListener('keydown', handleEscape)
    }
  }, [open])

  return (
    <div
      ref={containerRef}
      data-transcript-toolbar=""
      className="lg:hidden max-w-2xl mx-auto mb-2 flex items-center justify-between gap-2"
    >
      <div className="relative">
        <button
          type="button"
          data-transcript-toolbar-toc-trigger=""
          aria-expanded={open}
          aria-controls="transcript-toc-overlay"
          onClick={() => setOpen((prev) => !prev)}
          className="inline-flex items-center gap-1 px-2.5 py-1 text-xs rounded border border-gray-200 bg-white text-gray-700 hover:bg-gray-50 transition-colors"
        >
          <span>Turns</span>
          <span aria-hidden="true" className="text-[10px]">{open ? '▴' : '▾'}</span>
          {entries.length > 0 && (
            <span className="ml-1 text-gray-400 text-[10px]">({entries.length})</span>
          )}
        </button>

        {open && (
          <div
            id="transcript-toc-overlay"
            data-transcript-toolbar-toc-overlay=""
            role="dialog"
            aria-label="Session transcript table of contents"
            className="absolute left-0 top-full mt-1 z-20 w-72 max-w-[calc(100vw-2rem)] rounded border border-gray-200 bg-white shadow-lg p-1"
          >
            <TurnTocList
              entries={entries}
              activeIndex={activeIndex}
              onActivate={(entry) => {
                setOpen(false)
                onTocEntryActivate?.(entry)
              }}
            />
          </div>
        )}
      </div>

      <div className="flex items-center gap-2">{rightSlot}</div>
    </div>
  )
}