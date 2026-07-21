import { useState } from 'react'
import { FileIcon, FolderIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { ArtifactContentViewer, type ArtifactContentHook } from './ArtifactContentViewer'
import {
  useIssueWorkflowArtifacts,
  type WorkflowArtifact,
  type WorkflowArtifactDirectory,
} from '../../../entities/issue'

export type ArtifactOpenerMode = 'full' | 'compact'

export type ArtifactOpenerArtifactsHook = (
  ...args: Parameters<typeof useIssueWorkflowArtifacts>
) => Pick<ReturnType<typeof useIssueWorkflowArtifacts>, 'data' | 'isLoading' | 'error'>

export interface ArtifactOpenerProps {
  issueNumber: number
  workflowRunId?: string | null
  mode: ArtifactOpenerMode
  compactLimit?: number
  artifactsHook?: ArtifactOpenerArtifactsHook
  contentHook?: ArtifactContentHook
  evidenceSummary?: string
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
  mode,
  compactLimit = 3,
  artifactsHook = useIssueWorkflowArtifacts,
  contentHook,
  evidenceSummary,
}: ArtifactOpenerProps) {
  const { data: artifacts, isLoading, error } = artifactsHook(issueNumber, {}, !!workflowRunId)
  const [selectedArtifactId, setSelectedArtifactId] = useState<string | null>(null)

  if (!workflowRunId) return null

  const visibleArtifacts = mode === 'compact' && compactLimit > 0
    ? (artifacts ?? []).slice(0, compactLimit)
    : (artifacts ?? [])
  const selectedArtifact = artifacts?.find((a) => a.artifactId === selectedArtifactId)
  const listTestId = mode === 'compact' ? 'runtime-evidence-list' : 'latest-artifacts-list'

  if (mode === 'compact') {
    if (isLoading && (!artifacts || artifacts.length === 0)) return null
    if (error) return null
    if (visibleArtifacts.length === 0) return null
    return (
      <div
        className="mt-3 rounded-md border border-border bg-muted/50 p-2"
        data-testid="runtime-evidence"
        data-mode={mode}
        data-summary={evidenceSummary}
      >
        <div className="mb-1 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          Plan / check evidence
        </div>
        <div
          className="space-y-1"
          data-testid={listTestId}
          data-mode={mode}
        >
          {visibleArtifacts.map((artifact) => (
            <ArtifactItemRow
              key={artifact.artifactId}
              artifact={artifact}
              onClick={() => setSelectedArtifactId(artifact.artifactId)}
            />
          ))}
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
        </div>
      </div>
    )
  }

  return (
    <div className="rounded-lg border bg-card p-4 space-y-3">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-semibold text-card-foreground">Latest Artifacts</h3>
        {isLoading && <span className="text-xs text-muted-foreground">Loading...</span>}
      </div>

      {error && (
        <div className="text-xs text-danger bg-danger-subtle border border-danger-border rounded-md px-3 py-2">
          Failed to load artifacts
        </div>
      )}

      {!isLoading && !error && (!artifacts || artifacts.length === 0) && (
        <div className="text-xs text-muted-foreground">No recorded artifacts yet.</div>
      )}

      {artifacts && artifacts.length > 0 && (
        <div className="space-y-1" data-testid={listTestId}>
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
    </div>
  )
}
