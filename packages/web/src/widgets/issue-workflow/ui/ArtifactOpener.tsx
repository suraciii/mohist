import { useState } from 'react'
import { FileIcon, FolderIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { ArtifactContentViewer, type ArtifactContentHook } from './ArtifactContentViewer'
import {
  useIssueWorkflowArtifacts,
  type WorkflowArtifact,
  type WorkflowArtifactDirectory,
} from '../../../entities/issue'

export type ArtifactOpenerArtifactsHook = (
  ...args: Parameters<typeof useIssueWorkflowArtifacts>
) => Pick<ReturnType<typeof useIssueWorkflowArtifacts>, 'data' | 'isLoading' | 'error'>

export interface ArtifactOpenerProps {
  issueNumber: number
  workflowRunId?: string | null
  artifactsHook?: ArtifactOpenerArtifactsHook
  contentHook?: ArtifactContentHook
}

export type { ArtifactContentHook }

function isDirectoryArtifact(artifact: WorkflowArtifact | WorkflowArtifactDirectory): artifact is WorkflowArtifactDirectory {
  return artifact.kind === 'directory'
}

function ArtifactItemRow({
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
        <FolderIcon className="h-4 w-4 flex-shrink-0 text-warning mr-2" />
      ) : (
        <FileIcon className="h-4 w-4 flex-shrink-0 text-info mr-2" />
      )}
      <span className="flex-1 truncate text-sm">{displayName}</span>
      {isDirectoryArtifact(artifact) && (
        <span className="text-xs text-muted-foreground flex-shrink-0">{(artifact.entries?.length ?? 0)} files</span>
      )}
    </Button>
  )
}

export function ArtifactOpener({
  issueNumber,
  workflowRunId,
  artifactsHook = useIssueWorkflowArtifacts,
  contentHook,
}: ArtifactOpenerProps) {
  const { data: artifacts, isLoading, error } = artifactsHook(issueNumber, {}, !!workflowRunId)
  const [selectedArtifactId, setSelectedArtifactId] = useState<string | null>(null)

  const selectedArtifact = artifacts?.find((a) => a.artifactId === selectedArtifactId)

  return (
    <section id="artifacts" className="space-y-3 scroll-mt-20" data-testid="latest-artifacts-panel" aria-label="Artifacts">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-semibold text-foreground">Artifacts</h2>
        {workflowRunId && isLoading && <span className="text-xs text-muted-foreground">Loading...</span>}
      </div>

      {workflowRunId && error && (
        <div className="text-xs text-danger bg-danger-subtle border border-danger-border rounded-md px-3 py-2">
          Failed to load artifacts
        </div>
      )}

      {(!workflowRunId || (!isLoading && !error && (!artifacts || artifacts.length === 0))) && (
        <div className="text-xs text-muted-foreground">
          {workflowRunId ? 'No recorded artifacts yet.' : 'No workflow run or recorded artifacts yet.'}
        </div>
      )}

      {artifacts && artifacts.length > 0 && (
        <div className="space-y-1" data-testid="latest-artifacts-list">
          {artifacts.map((artifact) => (
            <ArtifactItemRow
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
          size={selectedArtifact.size}
          artifactKind={selectedArtifact.kind}
          open={selectedArtifactId !== null}
          contentHook={contentHook}
          onOpenChange={(open) => {
            if (!open) setSelectedArtifactId(null)
          }}
        />
      )}
    </section>
  )
}
