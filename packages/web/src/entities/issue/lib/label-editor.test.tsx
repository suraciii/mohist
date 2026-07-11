import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { LabelEditor } from './label-editor'
import type { LabelMap } from '../model/labels'

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

function renderEditor(value: LabelMap = {}, onChange: (next: LabelMap) => void = vi.fn()) {
  return render(<LabelEditor value={value} onChange={onChange} inputIdPrefix="test-label" />)
}

function setKeyValue(key: string, value: string) {
  fireEvent.change(screen.getByTestId('label-editor-key-input'), { target: { value: key } })
  fireEvent.change(screen.getByTestId('label-editor-value-input'), { target: { value: value } })
}

describe('LabelEditor - happy path', () => {
  it('renders the empty hint when no labels are present', () => {
    renderEditor({})
    expect(screen.getByTestId('label-editor-empty')).toBeInTheDocument()
  })

  it('submits a valid key=value pair and surfaces it as an entry', async () => {
    const onChange = vi.fn()
    renderEditor({}, onChange)

    setKeyValue('stream', 'frontend')
    fireEvent.click(screen.getByTestId('label-editor-submit'))

    await waitFor(() => {
      expect(onChange).toHaveBeenCalledWith({ stream: 'frontend' })
    })
    expect(screen.queryByTestId('label-editor-error')).not.toBeInTheDocument()
  })

  it('renders existing label entries in sorted order', () => {
    renderEditor({ stream: 'frontend', module: 'auth' })

    const entries = screen.getAllByTestId(/^label-editor-entry-/)
    expect(entries).toHaveLength(2)
    expect(screen.getByTestId('label-editor-entry-module')).toHaveTextContent('module=auth')
    expect(screen.getByTestId('label-editor-entry-stream')).toHaveTextContent('stream=frontend')
  })
})

describe('LabelEditor - pre-submit validation', () => {
  it('rejects an uppercase key with a clear inline error and blocks submit', async () => {
    const onChange = vi.fn()
    renderEditor({}, onChange)

    setKeyValue('Stream', 'frontend')
    fireEvent.click(screen.getByTestId('label-editor-submit'))

    await waitFor(() => {
      expect(screen.getByTestId('label-editor-error')).toBeInTheDocument()
    })
    expect(screen.getByTestId('label-editor-error').textContent).toMatch(/lowercase/)
    expect(onChange).not.toHaveBeenCalled()
  })

  it('rejects a key with internal whitespace', async () => {
    const onChange = vi.fn()
    renderEditor({}, onChange)

    setKeyValue('stream frontend', 'backend')
    fireEvent.click(screen.getByTestId('label-editor-submit'))

    await waitFor(() => {
      expect(screen.getByTestId('label-editor-error')).toBeInTheDocument()
    })
    expect(onChange).not.toHaveBeenCalled()
  })

  it('rejects a key with leading whitespace', async () => {
    const onChange = vi.fn()
    renderEditor({}, onChange)

    setKeyValue(' stream', 'backend')
    fireEvent.click(screen.getByTestId('label-editor-submit'))

    await waitFor(() => {
      expect(screen.getByTestId('label-editor-error')).toBeInTheDocument()
    })
    expect(onChange).not.toHaveBeenCalled()
  })

  it('rejects a key with a leading dash', async () => {
    const onChange = vi.fn()
    renderEditor({}, onChange)

    setKeyValue('-stream', 'frontend')
    fireEvent.click(screen.getByTestId('label-editor-submit'))

    await waitFor(() => {
      expect(screen.getByTestId('label-editor-error')).toBeInTheDocument()
    })
    expect(onChange).not.toHaveBeenCalled()
  })

  it('rejects a key with a trailing dash', async () => {
    const onChange = vi.fn()
    renderEditor({}, onChange)

    setKeyValue('stream-', 'frontend')
    fireEvent.click(screen.getByTestId('label-editor-submit'))

    await waitFor(() => {
      expect(screen.getByTestId('label-editor-error')).toBeInTheDocument()
    })
    expect(onChange).not.toHaveBeenCalled()
  })

  it('rejects an empty value with a clear inline error and blocks submit', async () => {
    const onChange = vi.fn()
    renderEditor({}, onChange)

    setKeyValue('stream', '')
    fireEvent.click(screen.getByTestId('label-editor-submit'))

    await waitFor(() => {
      expect(screen.getByTestId('label-editor-error')).toBeInTheDocument()
    })
    expect(screen.getByTestId('label-editor-error').textContent).toMatch(/required/i)
    expect(onChange).not.toHaveBeenCalled()
  })

  it('rejects a whitespace-only value', async () => {
    const onChange = vi.fn()
    renderEditor({}, onChange)

    setKeyValue('stream', '   ')
    fireEvent.click(screen.getByTestId('label-editor-submit'))

    await waitFor(() => {
      expect(screen.getByTestId('label-editor-error')).toBeInTheDocument()
    })
    expect(onChange).not.toHaveBeenCalled()
  })

  it('clears the inline error as soon as the user fixes the key', async () => {
    renderEditor({}, vi.fn())

    setKeyValue('Bad-Key', 'frontend')
    fireEvent.click(screen.getByTestId('label-editor-submit'))
    await waitFor(() => {
      expect(screen.getByTestId('label-editor-error')).toBeInTheDocument()
    })

    fireEvent.change(screen.getByTestId('label-editor-key-input'), { target: { value: 'good-key' } })

    await waitFor(() => {
      expect(screen.queryByTestId('label-editor-error')).not.toBeInTheDocument()
    })
  })
})

describe('LabelEditor - edit and remove', () => {
  it('removes a label when its remove button is clicked', () => {
    const onChange = vi.fn()
    renderEditor({ stream: 'frontend' }, onChange)

    fireEvent.click(screen.getByTestId('label-editor-remove-stream'))

    expect(onChange).toHaveBeenCalledWith({})
  })

  it('updates an existing label value via the edit flow', async () => {
    const onChange = vi.fn()
    renderEditor({ stream: 'frontend' }, onChange)

    fireEvent.click(screen.getByTestId('label-editor-edit-stream'))

    expect(screen.getByTestId('label-editor-editing-stream')).toBeInTheDocument()

    const keyInput = screen.getByTestId('label-editor-key-input') as HTMLInputElement
    const valueInput = screen.getByTestId('label-editor-value-input') as HTMLInputElement
    expect(keyInput.value).toBe('stream')
    expect(valueInput.value).toBe('frontend')

    fireEvent.change(valueInput, { target: { value: 'backend' } })
    fireEvent.click(screen.getByTestId('label-editor-submit'))

    await waitFor(() => {
      expect(onChange).toHaveBeenCalledWith({ stream: 'backend' })
    })
  })
})
