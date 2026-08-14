import type { AgentSessionTranscriptResponse, SessionMetadata } from '../../../entities/coder-session'
import type { TimelineFact } from '../../../entities/session'

export interface SessionExportContext {
  projectId?: string | null
  sessionId: string
  inputId?: string | null
  turnId?: string | null
  jobId?: string | null
  view: 'public' | 'raw'
}

export interface SessionExportInput {
  exportedAt: string
  context: SessionExportContext
  metadata: SessionMetadata | null
  transcript: AgentSessionTranscriptResponse | null
  timeline: readonly TimelineFact[]
}

export interface SessionExportDocument {
  version: 1
  exportedAt: string
  context: SessionExportContext
  metadata: SessionMetadata | null
  transcript: AgentSessionTranscriptResponse | null
  timeline: readonly TimelineFact[]
}

export function buildSessionExport(input: SessionExportInput): SessionExportDocument {
  return {
    version: 1,
    exportedAt: input.exportedAt,
    context: input.context,
    metadata: input.metadata,
    transcript: input.transcript,
    timeline: input.timeline,
  }
}

export function downloadSessionExport(
  exportDocument: SessionExportDocument,
  filename = `session-${exportDocument.context.sessionId}.json`,
): boolean {
  if (typeof window === 'undefined' || typeof globalThis.document === 'undefined' || typeof Blob === 'undefined') return false
  const createObjectUrl = window.URL?.createObjectURL
  if (typeof createObjectUrl !== 'function') return false

  const url = createObjectUrl.call(window.URL, new Blob([JSON.stringify(exportDocument, null, 2)], { type: 'application/json' }))
  const anchor = globalThis.document.createElement('a')
  anchor.href = url
  anchor.download = filename
  anchor.click()
  anchor.remove()
  window.URL.revokeObjectURL?.(url)
  return true
}
