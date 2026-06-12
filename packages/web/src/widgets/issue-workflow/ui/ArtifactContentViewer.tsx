import { useState } from 'react'
import { FileIcon, FolderIcon, ArrowLeftIcon } from 'lucide-react'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/shared/ui/components/dialog'
import { Button } from '@/shared/ui/components/button'
import { useIssueWorkflowArtifactContent, type WorkflowArtifactDirectoryEntry } from '../../../entities/issue'

interface ArtifactContentViewerProps {
  issueNumber: number
  artifactId: string
  path?: string
  open: boolean
  onOpenChange: (open: boolean) => void
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
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

export function ArtifactContentViewer({ issueNumber, artifactId, path, open, onOpenChange }: ArtifactContentViewerProps) {
  const [selectedEntry, setSelectedEntry] = useState<WorkflowArtifactDirectoryEntry | null>(null)
  const { data, isLoading, error } = useIssueWorkflowArtifactContent(
    issueNumber,
    artifactId,
    { file: selectedEntry?.relativePath },
    open,
  )

  const title = selectedEntry
    ? `${path ?? 'artifact'} / ${selectedEntry.relativePath}`
    : (path ?? 'Artifact')

  return (
    <Dialog open={open} onOpenChange={(next) => {
      if (!next) setSelectedEntry(null)
      onOpenChange(next)
    }}>
      <DialogContent className="sm:max-w-4xl max-h-[80vh] overflow-hidden flex flex-col p-0">
        <DialogHeader className="px-4 pt-4">
          <div className="flex items-center gap-2">
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
          <p className="text-xs text-muted-foreground">Recorded artifact content</p>
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
              <pre className="whitespace-pre-wrap break-words font-mono text-xs text-gray-700 bg-gray-50 rounded-md p-3 border">
                {data.content}
              </pre>
            </div>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}
