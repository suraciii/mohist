import * as React from 'react'

import { projectApiPath } from '@/shared/api/client'
import { cn } from '@/shared/lib/utils'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'

export interface UploadedAttachment {
  id: string
  fileName: string
  contentType: string
  size: number
}

export interface UploadAttachmentOptions {
  projectId: string
  file: File
  onProgress: (progress: number) => void
}

export type UploadAttachment = (options: UploadAttachmentOptions) => Promise<UploadedAttachment>

interface AttachmentComposerProps extends Omit<React.ComponentProps<'textarea'>, 'onChange' | 'value'> {
  projectId: string
  value: string
  onChange: (value: string) => void
  uploadAttachment?: UploadAttachment
}

type ComposerAttachment = {
  localId: string
  id: string | null
  fileName: string
  contentType: string
  size: number
  previewUrl: string | null
  progress: number
  status: 'uploading' | 'complete' | 'failed'
}

export function uploadAttachmentToProject({ projectId, file, onProgress }: UploadAttachmentOptions) {
  return new Promise<UploadedAttachment>((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    const form = new FormData()
    form.append('file', file)

    xhr.upload.onprogress = (event) => {
      if (event.lengthComputable) {
        onProgress(Math.round((event.loaded / event.total) * 100))
      }
    }
    xhr.onload = () => {
      if (xhr.status < 200 || xhr.status >= 300) {
        reject(new Error(xhr.responseText || `Upload failed: ${xhr.status}`))
        return
      }
      try {
        const parsed = JSON.parse(xhr.responseText) as { success?: boolean; data?: UploadedAttachment; error?: string } | UploadedAttachment
        if ('success' in parsed) {
          if (!parsed.success || !parsed.data) throw new Error(parsed.error || 'Upload failed')
          resolve(parsed.data)
        } else if (isUploadedAttachment(parsed)) {
          resolve(parsed)
        } else {
          throw new Error('Invalid upload response')
        }
        onProgress(100)
      } catch (error) {
        reject(error)
      }
    }
    xhr.onerror = () => reject(new Error('Upload failed'))
    xhr.open('POST', `/api${projectApiPath(projectId, '/attachments')}`)
    xhr.send(form)
  })
}

export function AttachmentComposer({
  projectId,
  value,
  onChange,
  uploadAttachment = uploadAttachmentToProject,
  className,
  ...textareaProps
}: AttachmentComposerProps) {
  const textareaRef = React.useRef<HTMLTextAreaElement | null>(null)
  const fileInputRef = React.useRef<HTMLInputElement | null>(null)
  const objectUrlsRef = React.useRef<string[]>([])
  const [attachments, setAttachments] = React.useState<ComposerAttachment[]>([])
  const [isDragActive, setIsDragActive] = React.useState(false)

  React.useEffect(() => {
    return () => {
      for (const url of objectUrlsRef.current) URL.revokeObjectURL(url)
    }
  }, [])

  const insertReference = React.useCallback((reference: string) => {
    const textarea = textareaRef.current
    const start = textarea?.selectionStart ?? value.length
    const end = textarea?.selectionEnd ?? value.length
    const nextValue = `${value.slice(0, start)}${reference}${value.slice(end)}`
    onChange(nextValue)
    requestAnimationFrame(() => {
      textarea?.focus()
      textarea?.setSelectionRange(start + reference.length, start + reference.length)
    })
  }, [onChange, value])

  const addFiles = React.useCallback((files: FileList | File[]) => {
    for (const file of Array.from(files)) {
      const localId = `${Date.now()}-${Math.random().toString(36).slice(2)}`
      const isImage = file.type.startsWith('image/')
      const previewUrl = isImage ? URL.createObjectURL(file) : null
      if (previewUrl) objectUrlsRef.current.push(previewUrl)

      setAttachments((current) => [...current, {
        localId,
        id: null,
        fileName: file.name,
        contentType: file.type || 'application/octet-stream',
        size: file.size,
        previewUrl,
        progress: 0,
        status: 'uploading',
      }])

      uploadAttachment({
        projectId,
        file,
        onProgress: (progress) => {
          setAttachments((current) => current.map((attachment) => attachment.localId === localId
            ? { ...attachment, progress: Math.max(0, Math.min(100, progress)) }
            : attachment))
        },
      }).then((uploaded) => {
        setAttachments((current) => current.map((attachment) => attachment.localId === localId
          ? { ...attachment, id: uploaded.id, fileName: uploaded.fileName, contentType: uploaded.contentType, size: uploaded.size, progress: 100, status: 'complete' }
          : attachment))
        insertReference(formatAttachmentReference(uploaded))
      }).catch(() => {
        setAttachments((current) => current.map((attachment) => attachment.localId === localId
          ? { ...attachment, status: 'failed' }
          : attachment))
      })
    }
  }, [insertReference, projectId, uploadAttachment])

  const removeAttachment = React.useCallback((attachment: ComposerAttachment) => {
    setAttachments((current) => current.filter((item) => item.localId !== attachment.localId))
    if (attachment.previewUrl) URL.revokeObjectURL(attachment.previewUrl)
    if (attachment.id) onChange(stripAttachmentReference(value, attachment.id))
  }, [onChange, value])

  return (
    <div
      className={cn('relative rounded-lg border border-border bg-card p-3', className)}
      onDragOver={(event) => {
        event.preventDefault()
        setIsDragActive(true)
      }}
      onDragLeave={(event) => {
        if (event.currentTarget.contains(event.relatedTarget as Node | null)) return
        setIsDragActive(false)
      }}
      onDrop={(event) => {
        event.preventDefault()
        setIsDragActive(false)
        if (event.dataTransfer.files.length) addFiles(event.dataTransfer.files)
      }}
    >
      {isDragActive ? (
        <div className="pointer-events-none absolute inset-1 z-10 flex items-center justify-center rounded-lg border-2 border-dashed border-primary bg-primary/10 text-sm font-medium text-primary">
          Drop files to attach
        </div>
      ) : null}
      <Textarea
        ref={textareaRef}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        onPaste={(event) => {
          const files = Array.from(event.clipboardData.files)
          if (!files.length) return
          event.preventDefault()
          addFiles(files)
        }}
        className="min-h-32 resize-y border-0 bg-transparent px-0 py-0 shadow-none focus-visible:ring-0"
        {...textareaProps}
      />
      {attachments.length ? (
        <div className="mt-3 flex flex-wrap gap-2" aria-label="Attachments">
          {attachments.map((attachment) => (
            <AttachmentChip key={attachment.localId} attachment={attachment} onRemove={() => removeAttachment(attachment)} />
          ))}
        </div>
      ) : null}
      <div className="mt-3 flex items-center justify-between border-t border-border pt-3">
        <span className="text-xs text-muted-foreground">Attach files by browsing, pasting, or dropping them here.</span>
        <input
          ref={fileInputRef}
          type="file"
          multiple
          className="sr-only"
          aria-label="Choose attachment files"
          onChange={(event) => {
            if (event.target.files?.length) addFiles(event.target.files)
            event.currentTarget.value = ''
          }}
        />
        <Button type="button" variant="outline" size="sm" onClick={() => fileInputRef.current?.click()}>
          Browse
        </Button>
      </div>
    </div>
  )
}

function AttachmentChip({ attachment, onRemove }: { attachment: ComposerAttachment; onRemove: () => void }) {
  const isImage = attachment.contentType.startsWith('image/')
  return (
    <div className="relative flex min-w-52 max-w-72 items-center gap-2 overflow-hidden rounded-lg border border-border bg-background px-2 py-2 shadow-sm">
      {isImage && attachment.previewUrl ? (
        <img src={attachment.previewUrl} alt="" className="size-10 rounded-md object-cover" data-testid="attachment-thumbnail" />
      ) : (
        <span className="flex size-10 items-center justify-center rounded-md bg-primary/10 text-[0.65rem] font-semibold uppercase text-primary" data-testid="attachment-extension-badge">
          {extensionFor(attachment.fileName)}
        </span>
      )}
      <span className="min-w-0 flex-1">
        <span className="block truncate text-sm font-medium text-foreground">{attachment.fileName}</span>
        <span className="block text-xs text-muted-foreground">{formatSize(attachment.size)}</span>
      </span>
      <button type="button" className="rounded-full px-1.5 text-muted-foreground hover:bg-muted hover:text-foreground" aria-label={`Remove ${attachment.fileName}`} onClick={onRemove}>
        x
      </button>
      {attachment.status === 'uploading' ? (
        <span className="absolute inset-x-0 bottom-0 h-1 bg-primary/15" aria-label={`Uploading ${attachment.fileName}`}>
          <span className="block h-full bg-primary transition-all" style={{ width: `${attachment.progress}%` }} data-testid="attachment-progress" />
        </span>
      ) : null}
    </div>
  )
}

function formatAttachmentReference(attachment: UploadedAttachment) {
  const label = attachment.fileName.replace(/[\[\]\n\r]/g, ' ').trim() || 'attachment'
  return attachment.contentType.startsWith('image/') ? `![${label}](att:${attachment.id})` : `[${label}](att:${attachment.id})`
}

export function stripAttachmentReference(value: string, attachmentId: string) {
  const escapedId = escapeRegExp(attachmentId)
  return value
    .replace(new RegExp(`!?\\[[^\\]]*\\]\\(att:${escapedId}\\)`, 'g'), '')
    .replace(/[ \t]+\n/g, '\n')
}

function extensionFor(fileName: string) {
  const extension = fileName.split('.').pop()
  return extension && extension !== fileName ? extension.slice(0, 4) : 'file'
}

function formatSize(size: number) {
  if (size < 1024) return `${size} B`
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`
  return `${(size / 1024 / 1024).toFixed(1)} MB`
}

function escapeRegExp(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

function isUploadedAttachment(value: unknown): value is UploadedAttachment {
  return Boolean(value)
    && typeof value === 'object'
    && typeof (value as UploadedAttachment).id === 'string'
    && typeof (value as UploadedAttachment).fileName === 'string'
    && typeof (value as UploadedAttachment).contentType === 'string'
    && typeof (value as UploadedAttachment).size === 'number'
}
