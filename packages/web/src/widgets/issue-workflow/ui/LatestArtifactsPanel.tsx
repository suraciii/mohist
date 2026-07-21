import { ArtifactOpener, type ArtifactContentHook, type ArtifactOpenerArtifactsHook } from './ArtifactOpener'

interface LatestArtifactsPanelProps {
  issueNumber: number
  workflowRunId?: string | null
  artifactsHook?: ArtifactOpenerArtifactsHook
  contentHook?: ArtifactContentHook
}

export type LatestArtifactsHook = ArtifactOpenerArtifactsHook

export function LatestArtifactsPanel({
  issueNumber,
  workflowRunId,
  artifactsHook,
  contentHook,
}: LatestArtifactsPanelProps) {
  return (
    <ArtifactOpener
      issueNumber={issueNumber}
      workflowRunId={workflowRunId}
      artifactsHook={artifactsHook}
      contentHook={contentHook}
    />
  )
}
