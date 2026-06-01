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
  const [newPath, setNewPath] = useState('')
  const [newRemote, setNewRemote] = useState('')
  const [newBranch, setNewBranch] = useState('main')

  function handleAdd() {
    if (!newName.trim()) return
    if (!newPath.trim() && !newRemote.trim()) return
    addRepo.mutate({
      projectId,
      data: { name: newName.trim(), path: newPath || undefined, remote: newRemote || undefined, baseBranch: newBranch },
    })
    setNewName('')
    setNewPath('')
    setNewRemote('')
    setNewBranch('main')
  }

  return (
    <div className="space-y-4">
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
        <div className="space-y-2">
          {repositories.map((repo) => (
            <div
              key={repo.name}
              data-testid={`repository-${repo.name}`}
              className={`flex items-center justify-between rounded-lg border p-3 ${
                repo.isDefault
                  ? 'border-blue-200 bg-blue-50'
                  : 'border-border bg-card/50'
              }`}
            >
              <div className="min-w-0">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-medium text-foreground">{repo.name}</span>
                  {repo.isDefault && (
                    <span className="rounded-full bg-blue-100 px-2 py-0.5 text-xs text-blue-700">default</span>
                  )}
                </div>
                <div className="mt-0.5 text-xs text-muted-foreground truncate">
                  {repo.remote ? `remote: ${repo.remote}` : `path: ${repo.path}`}
                  {repo.baseBranch !== 'main' && ` · ${repo.baseBranch}`}
                </div>
              </div>
              <div className="flex items-center gap-1 ml-2 shrink-0">
                {!repo.isDefault && (
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => setDefault.mutate({ projectId, repoName: repo.name })}
                    className="text-xs h-7"
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
                  >
                    Remove
                  </Button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      <div className="rounded-lg border border-border bg-muted/50 p-3 space-y-2">
        <h4 className="text-xs font-medium text-foreground/80">Add Repository</h4>
        <div className="grid grid-cols-2 gap-2">
          <div>
            <Label className="text-xs">Name</Label>
            <Input
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              placeholder="e.g. frontend"
              className="h-8 text-sm"
            />
          </div>
          <div>
            <Label className="text-xs">Base Branch</Label>
            <Input
              value={newBranch}
              onChange={(e) => setNewBranch(e.target.value)}
              placeholder="main"
              className="h-8 text-sm"
            />
          </div>
        </div>
        <div>
          <Label className="text-xs">Local Path</Label>
          <Input
            value={newPath}
            onChange={(e) => setNewPath(e.target.value)}
            placeholder="/path/to/repo"
            className="h-8 text-sm"
          />
        </div>
        <div>
          <Label className="text-xs">Remote URL</Label>
          <Input
            value={newRemote}
            onChange={(e) => setNewRemote(e.target.value)}
            placeholder="https://github.com/org/repo.git"
            className="h-8 text-sm"
          />
        </div>
        <Button
          onClick={handleAdd}
          disabled={!newName.trim() || (!newPath.trim() && !newRemote.trim()) || addRepo.isPending}
          size="sm"
          className="w-full"
        >
          {addRepo.isPending ? 'Adding...' : 'Add Repository'}
        </Button>
      </div>
    </div>
  )
}
