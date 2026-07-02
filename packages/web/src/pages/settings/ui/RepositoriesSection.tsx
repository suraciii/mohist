import { useEffect, useRef, useState } from 'react'
import { useRepositories, useAddRepository, useRemoveRepository, useSetDefaultRepository } from '../../../entities/project'
import { AlertDialog } from '@/shared/ui/components/alert-dialog'
import { Button } from '@/shared/ui/components/button'
import { CardSection } from '@/shared/ui/components/card-section'
import { Input } from '@/shared/ui/components/input'
import { Label } from '@/shared/ui/components/label'
import type { SettingsSearchEntry } from '@/features/settings-search'
import { getSectionMeta } from '../lib/sections'
import { SectionState } from './SectionState'
import { SettingsSection } from './SettingsSection'

interface Props {
  projectId: string
}

export const REVEAL_REPOSITORY_ADD_FORM_EVENT = 'mohist:settings:reveal-repository-add-form'

export const REPOSITORIES_DESCRIPTORS: SettingsSearchEntry[] = [
  {
    tab: 'repositories',
    label: 'Repository name',
    description: 'Display name for a new repository in this project.',
    placeholder: 'e.g. frontend',
    focusTargetId: 'repository-add-name',
    revealEvent: REVEAL_REPOSITORY_ADD_FORM_EVENT,
  },
  {
    tab: 'repositories',
    label: 'Repository base branch',
    description: 'Default branch checked out when work begins on this repository.',
    placeholder: 'main',
    focusTargetId: 'repository-add-branch',
    revealEvent: REVEAL_REPOSITORY_ADD_FORM_EVENT,
  },
  {
    tab: 'repositories',
    label: 'Repository Git URL',
    description: 'Remote URL for cloning the repository.',
    placeholder: 'https://github.com/org/repo.git',
    focusTargetId: 'repository-add-giturl',
    revealEvent: REVEAL_REPOSITORY_ADD_FORM_EVENT,
  },
]

export function RepositoriesSection({ projectId }: Props) {
  const { data: repositories, isLoading } = useRepositories(projectId)
  const addRepo = useAddRepository()
  const removeRepo = useRemoveRepository()
  const setDefault = useSetDefaultRepository()
  const { label: sectionLabel } = getSectionMeta('repositories')

  const [newName, setNewName] = useState('')
  const [newGitUrl, setNewGitUrl] = useState('')
  const [newBranch, setNewBranch] = useState('main')
  const [showForm, setShowForm] = useState(false)
  const [pendingRemoveName, setPendingRemoveName] = useState<string | null>(null)
  const nameInputRef = useRef<HTMLInputElement>(null)
  const repositoryList = repositories ?? []
  const hasRepositories = repositoryList.length > 0

  useEffect(() => {
    if (showForm) {
      nameInputRef.current?.focus()
    }
  }, [showForm])

  useEffect(() => {
    function revealAddForm() {
      setShowForm(true)
    }
    window.addEventListener(REVEAL_REPOSITORY_ADD_FORM_EVENT, revealAddForm)
    return () => window.removeEventListener(REVEAL_REPOSITORY_ADD_FORM_EVENT, revealAddForm)
  }, [])

  function handleAdd() {
    if (!newName.trim() || !newGitUrl.trim()) return
    addRepo.mutate({
      projectId,
      data: { name: newName.trim(), gitUrl: newGitUrl.trim(), baseBranch: newBranch },
    })
    setNewName('')
    setNewGitUrl('')
    setNewBranch('main')
  }

  function requestRemove(repoName: string) {
    setPendingRemoveName(repoName)
  }

  function cancelRemove() {
    if (removeRepo.isPending) return
    setPendingRemoveName(null)
  }

  function confirmRemove() {
    if (!pendingRemoveName) return
    const repoName = pendingRemoveName
    removeRepo.mutate(
      { projectId, repoName },
      {
        onSuccess: () => {
          setPendingRemoveName(null)
        },
        onError: () => {
          setPendingRemoveName(null)
        },
      },
    )
  }

  return (
    <SettingsSection title={sectionLabel}>
      <div data-testid="repositories-section">
        {isLoading ? (
          <SectionState variant="loading" skeletonRows={2} />
        ) : !hasRepositories ? (
          <SectionState
            variant="empty"
            title="Repositories"
            description="No repositories configured for this project."
          >
            {!showForm && (
              <Button onClick={() => setShowForm(true)} size="sm" className="mt-3">
                Add your first repository
              </Button>
            )}
          </SectionState>
        ) : (
          <div className="space-y-2" data-testid="repositories-list">
            {repositoryList.map((repo) => (
              <CardSection
                key={repo.name}
                data-testid={`repository-${repo.name}`}
                data-repository-default={repo.isDefault ? 'true' : 'false'}
                tone={repo.isDefault ? 'blue' : 'default'}
                className="flex items-center justify-between p-3"
              >
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <span
                      className="text-sm font-medium text-foreground"
                      data-testid={`repository-name-${repo.name}`}
                    >
                      {repo.name}
                    </span>
                    {repo.isDefault && (
                      <span
                        data-testid={`repository-default-badge-${repo.name}`}
                        className="rounded-full bg-blue-100 px-2 py-0.5 text-xs text-blue-700"
                      >
                        default
                      </span>
                    )}
                  </div>
                  <div
                    className="mt-0.5 text-xs text-muted-foreground truncate"
                    data-testid={`repository-giturl-${repo.name}`}
                  >
                    {repo.gitUrl}
                    {repo.baseBranch !== 'main' && ` · ${repo.baseBranch}`}
                  </div>
                  {repo.baseBranch !== 'main' && (
                    <div
                      className="mt-0.5 text-xs text-muted-foreground"
                      data-testid={`repository-basebranch-${repo.name}`}
                    >
                      base branch: {repo.baseBranch}
                    </div>
                  )}
                </div>
                <div className="flex items-center gap-1 ml-2 shrink-0">
                  {!repo.isDefault && (
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => setDefault.mutate({ projectId, repoName: repo.name })}
                      className="min-h-11 px-3 py-2 text-xs"
                      data-testid={`repository-set-default-${repo.name}`}
                    >
                      Set default
                    </Button>
                  )}
                  {!repo.isDefault && (
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => requestRemove(repo.name)}
                      className="min-h-11 px-3 py-2 text-xs text-red-700 hover:text-red-800 hover:bg-red-50"
                      data-testid={`repository-remove-${repo.name}`}
                    >
                      Remove
                    </Button>
                  )}
                </div>
              </CardSection>
            ))}
          </div>
        )}
        {(hasRepositories || showForm) && (
          <CardSection
            title="Add Repository"
            titleAs="h3"
            className="p-3"
            data-testid="repository-add-form"
          >
            <div className="space-y-2">
              <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                <div className="min-w-0">
                  <Label className="text-xs" htmlFor="repository-add-name">Name</Label>
                  <Input
                    ref={nameInputRef}
                    id="repository-add-name"
                    value={newName}
                    onChange={(e) => setNewName(e.target.value)}
                    placeholder="e.g. frontend"
                    className="min-h-11 text-sm"
                    data-testid="repository-add-name"
                  />
                </div>
                <div className="min-w-0">
                  <Label className="text-xs" htmlFor="repository-add-branch">Base Branch</Label>
                  <Input
                    id="repository-add-branch"
                    value={newBranch}
                    onChange={(e) => setNewBranch(e.target.value)}
                    placeholder="main"
                    className="min-h-11 text-sm"
                    data-testid="repository-add-branch"
                  />
                </div>
              </div>
              <div className="min-w-0">
                <Label className="text-xs" htmlFor="repository-add-giturl">Git URL</Label>
                <Input
                  id="repository-add-giturl"
                  value={newGitUrl}
                  onChange={(e) => setNewGitUrl(e.target.value)}
                  placeholder="https://github.com/org/repo.git"
                  className="min-h-11 text-sm"
                  data-testid="repository-add-giturl"
                />
              </div>
              <Button
                onClick={handleAdd}
                disabled={!newName.trim() || !newGitUrl.trim() || addRepo.isPending}
                size="sm"
                className="w-full"
                data-testid="repository-add-submit"
              >
                {addRepo.isPending ? 'Adding...' : 'Add Repository'}
              </Button>
            </div>
          </CardSection>
        )}
      </div>

      <AlertDialog
        open={pendingRemoveName !== null}
        onOpenChange={(open) => {
          if (!open) cancelRemove()
        }}
        title="Remove this repository?"
        description={
          pendingRemoveName
            ? `The repository '${pendingRemoveName}' will be removed from this project. This action cannot be undone.`
            : 'This repository will be removed from this project.'
        }
        confirmLabel={removeRepo.isPending ? 'Removing...' : 'Remove'}
        cancelLabel="Cancel"
        tone="destructive"
        loading={removeRepo.isPending}
        onConfirm={confirmRemove}
        data-testid="repository-remove-alert"
      />
    </SettingsSection>
  )
}
