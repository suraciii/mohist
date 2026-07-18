import '@testing-library/jest-dom'
import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { ModelSelect } from './ModelSelect'
import {
  models,
  modelVariants,
  openVariantSelect,
  renderVariantSelect,
} from './ModelSelectTestSupport'

describe('ModelSelect inline variant chips', () => {
  it('renders chips only on variant-capable rows and preserves them after search', async () => {
    renderVariantSelect()
    const search = await openVariantSelect()

    expect(screen.getByTestId('test-model-row-anthropic/claude-variant-low')).toBeTruthy()
    expect(screen.queryByTestId('test-model-row-openai/gpt-4-variant-low')).toBeNull()

    fireEvent.change(search, { target: { value: 'claude' } })
    await waitFor(() => {
      expect(screen.getByTestId('test-model-row-anthropic/claude-variant-high')).toBeTruthy()
      expect(document.querySelector('[data-model-id="openai/gpt-4"]')).toBeNull()
    })
  })

  it('selects model and variant on chip click and clears variant on body click', async () => {
    const { onChange, onChangeVariant } = renderVariantSelect({ value: 'anthropic/claude', valueVariant: 'high' })
    await openVariantSelect()

    fireEvent.click(screen.getByTestId('test-model-row-anthropic/claude-variant-medium'))
    expect(onChange).toHaveBeenCalledWith('anthropic/claude')
    expect(onChangeVariant).toHaveBeenCalledWith('medium')

    fireEvent.click(screen.getByRole('button', { name: /claude/i }))
    const row = await waitFor(() => document.querySelector('[data-model-id="anthropic/claude"]') as HTMLElement)
    fireEvent.click(row)
    expect(onChangeVariant).toHaveBeenCalledWith(null)
  })

  it('uses only the atomic callback for chip selection when provided', async () => {
    const onChange = vi.fn()
    const onChangeVariant = vi.fn()
    const onChangeModelVariant = vi.fn()
    render(
      <ModelSelect
        id="test-model"
        value={null}
        placeholder="Opencode default"
        models={models}
        modelVariants={modelVariants}
        onChange={onChange}
        onChangeVariant={onChangeVariant}
        onChangeModelVariant={onChangeModelVariant}
      />,
    )
    await openVariantSelect()

    fireEvent.click(screen.getByTestId('test-model-row-anthropic/claude-variant-medium'))

    expect(onChangeModelVariant).toHaveBeenCalledTimes(1)
    expect(onChangeModelVariant).toHaveBeenCalledWith('anthropic/claude', 'medium')
    expect(onChange).not.toHaveBeenCalled()
    expect(onChangeVariant).not.toHaveBeenCalled()
  })

  it('marks the active variant and leaves chips inactive when no variant is selected', async () => {
    const { rerender } = render(
      <ModelSelect
        id="test-model"
        value="anthropic/claude"
        valueVariant="medium"
        placeholder="Opencode default"
        models={models}
        modelVariants={modelVariants}
        onChange={() => {}}
        onChangeVariant={() => {}}
      />,
    )
    await openVariantSelect()
    expect(screen.getByTestId('test-model-row-anthropic/claude-variant-medium').getAttribute('data-variant-active')).toBe('true')

    rerender(
      <ModelSelect
        id="test-model"
        value="anthropic/claude"
        valueVariant={null}
        placeholder="Opencode default"
        models={models}
        modelVariants={modelVariants}
        onChange={() => {}}
        onChangeVariant={() => {}}
      />,
    )
    expect(screen.getByTestId('test-model-row-anthropic/claude-variant-medium').getAttribute('data-variant-active')).toBe('false')
  })

  it('moves keyboard focus into chips and selects the focused chip with Enter', async () => {
    const { onChange, onChangeVariant } = renderVariantSelect()
    const search = await openVariantSelect()

    fireEvent.keyDown(search, { key: 'ArrowRight' })
    await waitFor(() => expect(screen.getByTestId('test-model-row-anthropic/claude-variant-low')).toHaveFocus())

    fireEvent.keyDown(screen.getByTestId('test-model-row-anthropic/claude-variant-low'), { key: 'ArrowRight' })
    await waitFor(() => expect(screen.getByTestId('test-model-row-anthropic/claude-variant-medium')).toHaveFocus())

    fireEvent.keyDown(screen.getByTestId('test-model-row-anthropic/claude-variant-medium'), { key: 'Enter' })
    expect(onChange).toHaveBeenCalledWith('anthropic/claude')
    expect(onChangeVariant).toHaveBeenCalledWith('medium')
  })

  it('supports Tab into chips, Escape close, and compact 44px tap targets', async () => {
    renderVariantSelect({ size: 'compact' })
    const search = await openVariantSelect()

    fireEvent.keyDown(search, { key: 'Tab' })
    const lowChip = await waitFor(() => screen.getByTestId('test-model-row-anthropic/claude-variant-low'))
    expect(lowChip).toHaveFocus()
    expect(lowChip.className).toContain('min-h-11')
    expect(lowChip.className).toContain('min-w-11')

    fireEvent.keyDown(lowChip, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByPlaceholderText('Search models...')).toBeNull())
  })

  it('chip click stopPropagation prevents item body from also selecting', async () => {
    const onChange = vi.fn()
    const onChangeVariant = vi.fn()
    render(
      <ModelSelect
        id="test-model"
        value="openai/gpt-4"
        placeholder="Opencode default"
        models={models}
        modelVariants={modelVariants}
        onChange={onChange}
        onChangeVariant={onChangeVariant}
      />,
    )
    await openVariantSelect()

    fireEvent.click(screen.getByTestId('test-model-row-anthropic/claude-variant-low'))

    expect(onChange).toHaveBeenCalledWith('anthropic/claude')
    expect(onChangeVariant).toHaveBeenCalledWith('low')
    expect(onChange).toHaveBeenCalledTimes(1)
    expect(onChangeVariant).toHaveBeenCalledTimes(1)
  })
})
