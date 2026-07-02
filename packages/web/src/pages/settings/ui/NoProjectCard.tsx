import { useCallback, useState } from 'react'
import { PlusIcon } from 'lucide-react'
import { CreateProjectDialog } from '@/widgets/create-project-dialog/ui/CreateProjectDialog'
import { Button } from '@/shared/ui/components/button'
import { SectionState } from './SectionState'

export const REVEAL_PROJECT_SWITCHER_EVENT = 'mohist:sidebar:open-project-switcher'

interface NoProjectCardProps {
  /** Title shown above the dashed box. */
  title?: string
}

/**
 * Shared "no project selected" card for settings sections that require a project.
 * Renders a dashed-box empty state with two CTAs:
 * - **Select project** — dispatches a `reveal-project-switcher` window event the
 *   AppSidebar listens for and opens/focuses the project dropdown.
 * - **Create Project** — opens the inline CreateProjectDialog (same entry point
 *   used by ProjectGuard and DashboardPage).
 */
export function NoProjectCard({ title }: NoProjectCardProps) {
  const [createOpen, setCreateOpen] = useState(false)

  const handleSelectProject = useCallback(() => {
    window.dispatchEvent(new CustomEvent(REVEAL_PROJECT_SWITCHER_EVENT))
  }, [])

  return (
    <>
      <SectionState
        variant="no-project"
        title={title}
        action={
          <Button
            variant="outline"
            size="sm"
            onClick={handleSelectProject}
            data-testid="no-project-select-button"
          >
            Select project
          </Button>
        }
      >
        <Button
          size="sm"
          onClick={() => setCreateOpen(true)}
          data-testid="no-project-create-button"
        >
          <PlusIcon className="size-4" />
          Create Project
        </Button>
      </SectionState>
      <CreateProjectDialog open={createOpen} onClose={() => setCreateOpen(false)} />
    </>
  )
}
