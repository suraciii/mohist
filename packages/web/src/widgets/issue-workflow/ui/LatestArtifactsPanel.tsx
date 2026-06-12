import { useState } from 'react'
import { FileIcon, FolderIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { ArtifactContentViewer } from './ArtifactContentViewer'
import { useIssueWorkflowArtifacts, type WorkflowArtifact, type WorkflowArtifactDirectory } from '../../../entities/issue'

interface LatestArtifactsPanelProps {
  issueNumber: number
  workflowRunId?: string | null
}

function isDirectoryArtifact(artifact: WorkflowArtifact | WorkflowArtifactDirectory): artifact is WorkflowArtifactDirectory {
  return artifact.kind === 'directory'
}

function ArtifactItem({
  artifact,
  onClick,
}: {
  artifact: WorkflowArtifact | WorkflowArtifactDirectory
  onClick: () => void
}) {
  const displayName = artifact.displayName ?? artifact.path
  return (
    <Button
      variant="ghost"
      onClick={onClick}
      className="w-full justify-start h-auto py-2 px-2 text-left font-normal"
      data-testid="latest-artifact-item"
    >
      {isDirectoryArtifact(artifact) ? (
        <FolderIcon className="h-4 w-4 flex-shrink-0 text-amber-500 mr-2" />
      ) : (
        <FileIcon className="h-4 w-4 flex-shrink-0 text-blue-500 mr-2" />
      )}
      <span className="flex-1 truncate text-sm">{displayName}</span>
      {isDirectoryArtifact(artifact) && (
        <span className="text-xs text-muted-foreground flex-shrink-0">{(artifact.entries?.length ?? 0)} files</span>
      )}
    </Button>
  )
}

export function LatestArtifactsPanel({ issueNumber, workflowRunId }: LatestArtifactsPanelProps) {
  const { data: artifacts, isLoading, error } = useIssueWorkflowArtifacts(issueNumber, {}, !!workflowRunId)
  const [selectedArtifactId, setSelectedArtifactId] = useState<string | null>(null)

  const selectedArtifact = artifacts?.find((a) => a.artifactId === selectedArtifactId)

  if (!workflowRunId) return null

  return (
    <div className="rounded-lg border bg-card p-4 space-y-3">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-semibold text-card-foreground">Latest Artifacts</h3>
        {isLoading && <span className="text-xs text-muted-foreground">Loading...</span>}
      </div>

      {error && (
        <div className="text-xs text-red-600 bg-red-50 rounded-md px-3 py-2">
          Failed to load artifacts
        </div>
      )}

      {!isLoading && !error && (!artifacts || artifacts.length === 0) && (
        <div className="text-xs text-muted-foreground">No recorded artifacts yet.</div>
      )}

      {artifacts && artifacts.length > 0 && (
        <div className="space-y-1" data-testid="latest-artifacts-list">
          {artifacts.map((artifact) => (
            <ArtifactItem
              key={artifact.artifactId}
              artifact={artifact}
              onClick={() => setSelectedArtifactId(artifact.artifactId)}
            />
          ))}
        </div>
      )}

      {selectedArtifact && (
        <ArtifactContentViewer
          issueNumber={issueNumber}
          artifactId={selectedArtifact.artifactId}
          path={selectedArtifact.path}
          open={selectedArtifactId !== null}
          onOpenChange={(open) => {
            if (!open) setSelectedArtifactId(null)
          }}
        />
      )}
    </div>
  )
}
