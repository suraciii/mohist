import { useState, useEffect, useRef } from 'react'
import { FileIcon, FolderIcon, ArrowLeftIcon } from 'lucide-react'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/shared/ui/components/dialog'
import { Button } from '@/shared/ui/components/button'
import { ArtifactTextContent } from './ArtifactTextContent'
import { useIssueWorkflowArtifactContent, type WorkflowArtifact, type WorkflowArtifactDirectoryEntry } from '../../../entities/issue'

export interface ArtifactContentViewerProps {
  issueNumber: number
  artifactId: string
  path?: string
  size?: number | null
  artifactKind?: WorkflowArtifact['kind']
  open: boolean
  onOpenChange: (open: boolean) => void
  contentHook?: ArtifactContentHook
}

export type ArtifactContentHook = (
  ...args: Parameters<typeof useIssueWorkflowArtifactContent>
) => Pick<ReturnType<typeof useIssueWorkflowArtifactContent>, 'data' | 'isLoading' | 'error'>

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '0 B'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
  if (bytes < 1024 * 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`
  return `${(bytes / (1024 * 1024 * 1024 * 1024)).toFixed(1)} TB`
}

function DirectoryEntryList({
  entries,
  onSelect,
}: {
  entries: WorkflowArtifactDirectoryEntry[]
  onSelect: (entry: WorkflowArtifactDirectoryEntry) => void
}) {
  return (
    <div className="space-y-1">
      {entries.map((entry) => (
        <Button
          key={entry.relativePath}
          variant="ghost"
          onClick={() => onSelect(entry)}
          className="w-full justify-start h-auto py-2 px-2 text-left font-normal"
        >
          <FileIcon className="h-4 w-4 flex-shrink-0 text-muted-foreground mr-2" />
          <span className="flex-1 truncate text-sm">{entry.relativePath}</span>
          <span className="text-xs text-muted-foreground flex-shrink-0">{formatBytes(entry.size)}</span>
        </Button>
      ))}
    </div>
  )
}

export function ArtifactContentViewer({
  issueNumber,
  artifactId,
  path,
  size,
  artifactKind,
  open,
  onOpenChange,
  contentHook = useIssueWorkflowArtifactContent,
}: ArtifactContentViewerProps) {
  const [selectedEntry, setSelectedEntry] = useState<WorkflowArtifactDirectoryEntry | null>(null)
  const [copyStatus, setCopyStatus] = useState<'idle' | 'copied' | 'error'>('idle')
  const copyResetTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const { data, isLoading, error } = contentHook(
    issueNumber,
    artifactId,
    { file: selectedEntry?.relativePath, artifactKind },
    open,
  )

  const clearCopyResetTimer = () => {
    if (copyResetTimerRef.current !== null) {
      clearTimeout(copyResetTimerRef.current)
      copyResetTimerRef.current = null
    }
  }

  useEffect(() => {
    return () => {
      clearCopyResetTimer()
    }
  }, [])

  const setCopyStatusWithReset = (status: 'idle' | 'copied' | 'error') => {
    clearCopyResetTimer()
    setCopyStatus(status)
    if (status !== 'idle') {
      copyResetTimerRef.current = setTimeout(() => {
        setCopyStatus('idle')
        copyResetTimerRef.current = null
      }, 2000)
    }
  }

  const handleCopy = () => {
    if (!data || data.kind !== 'text') return
    if (!navigator.clipboard?.writeText) {
      setCopyStatusWithReset('error')
      return
    }
    navigator.clipboard.writeText(data.content).then(
      () => {
        setCopyStatusWithReset('copied')
      },
      () => {
        setCopyStatusWithReset('error')
      },
    )
  }

  const title = selectedEntry
    ? `${path ?? 'artifact'} / ${selectedEntry.relativePath}`
    : (path ?? 'Artifact')

  const sizeLabel = selectedEntry
    ? formatBytes(selectedEntry.size)
    : data?.kind === 'directory'
      ? `${(data.entries?.length ?? 0)} files · ${formatBytes(data.totalSize ?? 0)}`
      : size != null
        ? formatBytes(size)
        : 'Recorded artifact content'

  return (
    <Dialog open={open} onOpenChange={(next) => {
      if (!next) {
        clearCopyResetTimer()
        setSelectedEntry(null)
        setCopyStatus('idle')
      }
      onOpenChange(next)
    }}>
      <DialogContent className="sm:max-w-4xl max-h-[80vh] overflow-hidden flex flex-col p-0">
        <DialogHeader className="px-4 pt-4">
          <div className="flex items-center justify-between gap-4">
            <div className="flex items-center gap-2 min-w-0">
              {selectedEntry && (
                <Button
                  variant="ghost"
                  size="icon-sm"
                  onClick={() => setSelectedEntry(null)}
                  title="Back to directory"
                >
                  <ArrowLeftIcon className="h-4 w-4" />
                </Button>
              )}
              <DialogTitle className="text-sm font-medium truncate">{title}</DialogTitle>
            </div>
            {!isLoading && !error && data?.kind === 'text' && (
              <Button
                variant="ghost"
                size="sm"
                onClick={handleCopy}
                className="h-7 px-2 text-xs flex-shrink-0"
              >
                {copyStatus === 'copied' ? 'Copied!' : copyStatus === 'error' ? 'Unable to copy' : 'Copy'}
              </Button>
            )}
          </div>
          <p className="text-xs text-muted-foreground">{sizeLabel}</p>
        </DialogHeader>
        <div className="flex-1 overflow-auto px-4 pb-4 min-h-0">
          {isLoading && <div className="text-xs text-muted-foreground py-4">Loading artifact content...</div>}
          {error && (
            <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
              {error instanceof Error ? error.message : 'Failed to load artifact content'}
            </div>
          )}
          {!isLoading && !error && data?.kind === 'directory' && (
            <div className="space-y-3">
              <div className="flex items-center gap-2 text-sm text-muted-foreground">
                <FolderIcon className="h-4 w-4" />
                <span>{(data.entries?.length ?? 0)} files</span>
                <span>·</span>
                <span>{formatBytes(data.totalSize ?? 0)}</span>
              </div>
              {data.entries && data.entries.length > 0 ? (
                <DirectoryEntryList entries={data.entries} onSelect={setSelectedEntry} />
              ) : (
                <div className="text-xs text-muted-foreground">No files in directory artifact.</div>
              )}
            </div>
          )}
          {!isLoading && !error && data?.kind === 'text' && (
            <div className="space-y-2">
              {selectedEntry && (
                <div className="flex items-center gap-2 text-xs text-muted-foreground">
                  <FileIcon className="h-3.5 w-3.5" />
                  <span>{selectedEntry.relativePath}</span>
                  <span>·</span>
                  <span>{formatBytes(selectedEntry.size)}</span>
                </div>
              )}
              <ArtifactTextContent content={data.content} contentType={data.contentType} />
            </div>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}
