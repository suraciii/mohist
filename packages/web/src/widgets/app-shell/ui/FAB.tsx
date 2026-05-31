import { Button } from '@/shared/ui/components/button'

interface Props {
  onClick: () => void
}

export function FAB({ onClick }: Props) {
  return (
    <Button
      onClick={onClick}
      size="icon"
      className="fixed bottom-20 right-4 md:hidden z-40 h-14 w-14 rounded-full shadow-lg min-h-[44px] min-w-[44px]"
      aria-label="New Issue"
    >
      <svg className="h-6 w-6" viewBox="0 0 20 20" fill="currentColor">
        <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
      </svg>
    </Button>
  )
}
