import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import * as React from 'react'

import { AttachmentComposer, stripAttachmentReference, type UploadAttachment } from './AttachmentComposer'

describe('AttachmentComposer', () => {
  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
  })

  it('uploads browse selections and inserts an image reference', async () => {
    const uploadAttachment = vi.fn<UploadAttachment>(async ({ file, onProgress }) => {
      onProgress(100)
      return { id: 'att_image', fileName: file.name, contentType: file.type, size: file.size }
    })
    render(<ControlledAttachmentComposer uploadAttachment={uploadAttachment} />)

    const file = new File(['image'], 'screen.png', { type: 'image/png' })
    fireEvent.change(screen.getByLabelText('Choose attachment files'), { target: { files: [file] } })

    await waitFor(() => expect(uploadAttachment).toHaveBeenCalledTimes(1))
    await screen.findByText('screen.png')
    expect(screen.getByTestId('composer-value').textContent).toBe('![screen.png](att:att_image)')
  })

  it('uploads pasted files through the same flow', async () => {
    const uploadAttachment = vi.fn<UploadAttachment>(async ({ file }) => ({
      id: 'att_paste',
      fileName: file.name,
      contentType: file.type,
      size: file.size,
    }))
    const { container } = render(<ControlledAttachmentComposer uploadAttachment={uploadAttachment} />)

    const file = new File(['log'], 'error.log', { type: 'text/plain' })
    fireEvent.paste(within(container).getByRole('textbox'), { clipboardData: { files: [file] } })

    await waitFor(() => expect(uploadAttachment).toHaveBeenCalledTimes(1))
    expect(screen.getByTestId('composer-value').textContent).toBe('[error.log](att:att_paste)')
  })

  it('shows a drag overlay and uploads dropped files', async () => {
    const uploadAttachment = vi.fn<UploadAttachment>(async ({ file }) => ({
      id: 'att_drop',
      fileName: file.name,
      contentType: file.type,
      size: file.size,
    }))
    const { container } = render(<ControlledAttachmentComposer uploadAttachment={uploadAttachment} />)

    const file = new File(['pdf'], 'brief.pdf', { type: 'application/pdf' })
    const card = within(container).getByRole('textbox').parentElement as HTMLElement
    fireEvent.dragOver(card, { dataTransfer: { files: [file] } })
    expect(screen.getByText('Drop files to attach')).toBeTruthy()
    fireEvent.drop(card, { dataTransfer: { files: [file] } })

    await waitFor(() => expect(uploadAttachment).toHaveBeenCalledTimes(1))
    expect(screen.queryByText('Drop files to attach')).toBeNull()
    expect(screen.getByTestId('composer-value').textContent).toBe('[brief.pdf](att:att_drop)')
  })

  it('renders image thumbnails, file badges, sizes, and remove controls', async () => {
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:image-preview')
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {})
    const uploadAttachment = vi.fn<UploadAttachment>(async ({ file }) => ({
      id: `att_${file.name}`,
      fileName: file.name,
      contentType: file.type,
      size: file.size,
    }))
    render(<ControlledAttachmentComposer uploadAttachment={uploadAttachment} />)

    fireEvent.change(screen.getByLabelText('Choose attachment files'), {
      target: {
        files: [
          new File(['image'], 'photo.jpg', { type: 'image/jpeg' }),
          new File(['abc'], 'notes.txt', { type: 'text/plain' }),
        ],
      },
    })

    await screen.findByText('photo.jpg')
    await screen.findByText('notes.txt')
    expect(screen.getByTestId('attachment-thumbnail')).toBeTruthy()
    expect(screen.getByTestId('attachment-extension-badge').textContent).toBe('txt')
    expect(screen.getByText('5 B')).toBeTruthy()
    expect(screen.getByText('3 B')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Remove photo.jpg' })).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Remove notes.txt' })).toBeTruthy()
  })

  it('shows live progress while an upload is in flight', async () => {
    const deferred = { resolve: null as null | (() => void) }
    const uploadAttachment = vi.fn<UploadAttachment>(({ file, onProgress }) => {
      onProgress(42)
      return new Promise<void>((resolve) => {
        deferred.resolve = resolve
      }).then(() => ({ id: 'att_progress', fileName: file.name, contentType: file.type, size: file.size }))
    })
    render(<ControlledAttachmentComposer uploadAttachment={uploadAttachment} />)

    fireEvent.change(screen.getByLabelText('Choose attachment files'), {
      target: { files: [new File(['abc'], 'slow.txt', { type: 'text/plain' })] },
    })

    await waitFor(() => expect(screen.getByTestId('attachment-progress').style.width).toBe('42%'))
    if (!deferred.resolve) throw new Error('Upload promise was not created')
    deferred.resolve()
    await waitFor(() => expect(screen.queryByTestId('attachment-progress')).toBeNull())
  })

  it('strips attachment references when removing a completed attachment', async () => {
    const uploadAttachment = vi.fn<UploadAttachment>(async ({ file }) => ({
      id: 'att_strip',
      fileName: file.name,
      contentType: file.type,
      size: file.size,
    }))
    render(<ControlledAttachmentComposer uploadAttachment={uploadAttachment} initialValue="Before " />)

    fireEvent.change(screen.getByLabelText('Choose attachment files'), {
      target: { files: [new File(['abc'], 'notes.txt', { type: 'text/plain' })] },
    })

    await waitFor(() => expect(screen.getByTestId('composer-value').textContent).toBe('[notes.txt](att:att_strip)Before '))
    fireEvent.click(screen.getByRole('button', { name: 'Remove notes.txt' }))
    expect(screen.getByTestId('composer-value').textContent).toBe('Before ')
  })
})

describe('stripAttachmentReference', () => {
  it('removes every matching image or link reference', () => {
    expect(stripAttachmentReference('![a](att:att_1) x [b](att:att_1) [c](att:att_2)', 'att_1')).toBe(' x  [c](att:att_2)')
  })
})

function ControlledAttachmentComposer({ uploadAttachment, initialValue = '' }: { uploadAttachment: UploadAttachment; initialValue?: string }) {
  const [value, setValue] = React.useState(initialValue)
  return (
    <>
      <AttachmentComposer projectId="proj_1" value={value} onChange={setValue} uploadAttachment={uploadAttachment} aria-label="Body" />
      <output data-testid="composer-value">{value}</output>
    </>
  )
}
