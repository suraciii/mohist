import { useState } from 'react'
import { useRepositories, useAddRepository, useRemoveRepository, useSetDefaultRepository } from '../../../entities/project'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Label } from '@/shared/ui/components/label'
import { SectionState } from './SectionState'

interface Props {
  projectId: string
}

export function RepositoriesSection({ projectId }: Props) {
  const { data: repositories, isLoading } = useRepositories(projectId)
  const addRepo = useAddRepository()
  const removeRepo = useRemoveRepository()
  const setDefault = useSetDefaultRepository()

  const [newName, setNewName] = useState('')
  const [newGitUrl, setNewGitUrl] = useState('')
  const [newBranch, setNewBranch] = useState('main')

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

  return (
    <div className="space-y-4" data-testid="repositories-section">
      <h3 className="text-sm font-medium text-foreground">Repositories</h3>

      {isLoading ? (
        <SectionState variant="loading" skeletonRows={2} />
      ) : !repositories || repositories.length === 0 ? (
        <SectionState
          variant="empty"
          title="Repositories"
          description="No repositories configured for this project."
        />
      ) : (
        <div className="space-y-2" data-testid="repositories-list">
          {repositories.map((repo) => (
            <div
              key={repo.name}
              data-testid={`repository-${repo.name}`}
              data-repository-default={repo.isDefault ? 'true' : 'false'}
              className={`flex items-center justify-between rounded-lg border p-3 ${
                repo.isDefault
                  ? 'border-blue-200 bg-blue-50'
                  : 'border-border bg-card/50'
              }`}
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
                    className="text-xs h-7"
                    data-testid={`repository-set-default-${repo.name}`}
                  >
                    Set default
                  </Button>
                )}
                {!repo.isDefault && (
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => removeRepo.mutate({ projectId, repoName: repo.name })}
                    className="text-xs h-7 text-red-600 hover:text-red-700 hover:bg-red-50"
                    data-testid={`repository-remove-${repo.name}`}
                  >
                    Remove
                  </Button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      <div
        className="rounded-lg border border-border bg-muted/50 p-3 space-y-2"
        data-testid="repository-add-form"
      >
        <h4 className="text-xs font-medium text-foreground/80">Add Repository</h4>
        <div className="grid grid-cols-2 gap-2">
          <div>
            <Label className="text-xs" htmlFor="repository-add-name">Name</Label>
            <Input
              id="repository-add-name"
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              placeholder="e.g. frontend"
              className="h-8 text-sm"
              data-testid="repository-add-name"
            />
          </div>
          <div>
            <Label className="text-xs" htmlFor="repository-add-branch">Base Branch</Label>
            <Input
              id="repository-add-branch"
              value={newBranch}
              onChange={(e) => setNewBranch(e.target.value)}
              placeholder="main"
              className="h-8 text-sm"
              data-testid="repository-add-branch"
            />
          </div>
        </div>
        <div>
          <Label className="text-xs" htmlFor="repository-add-giturl">Git URL</Label>
          <Input
            id="repository-add-giturl"
            value={newGitUrl}
            onChange={(e) => setNewGitUrl(e.target.value)}
            placeholder="https://github.com/org/repo.git"
            className="h-8 text-sm"
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
    </div>
  )
}
