import { useEffect, useRef } from 'react'

const DEFAULT_TIMEOUT_MS = 5000

export interface UseConfirmOutsideClickOptions {
  confirming: boolean
  setConfirming: (value: boolean) => void
  timeoutMs?: number
}

export function useConfirmOutsideClick({
  confirming,
  setConfirming,
  timeoutMs = DEFAULT_TIMEOUT_MS,
}: UseConfirmOutsideClickOptions): React.RefObject<HTMLDivElement> {
  const panelRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    if (!confirming) return
    const timer = setTimeout(() => setConfirming(false), timeoutMs)
    const handleClickOutside = (event: MouseEvent) => {
      if (panelRef.current && !panelRef.current.contains(event.target as Node)) {
        setConfirming(false)
      }
    }
    document.addEventListener('mousedown', handleClickOutside)
    return () => {
      clearTimeout(timer)
      document.removeEventListener('mousedown', handleClickOutside)
    }
  }, [confirming, setConfirming, timeoutMs])

  return panelRef as React.RefObject<HTMLDivElement>
}