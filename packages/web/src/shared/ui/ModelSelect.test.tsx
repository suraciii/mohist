import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { ModelSelect, describeModel } from './ModelSelect'

describe('describeModel', () => {
  it('splits a qualified id into name, fullId, and provider', () => {
    expect(describeModel('minimax-coding-plan/minimax-m3')).toEqual({
      id: 'minimax-coding-plan/minimax-m3',
      name: 'minimax-m3',
      fullId: 'minimax-coding-plan/minimax-m3',
      provider: 'minimax-coding-plan',
    })
  })

  it('treats an unqualified id as its own name with no provider', () => {
    expect(describeModel('gpt-5.4')).toEqual({
      id: 'gpt-5.4',
      name: 'gpt-5.4',
      fullId: 'gpt-5.4',
      provider: null,
    })
  })

  it('handles only the first slash as the provider separator', () => {
    expect(describeModel('foo/bar/baz')).toEqual({
      id: 'foo/bar/baz',
      name: 'bar/baz',
      fullId: 'foo/bar/baz',
      provider: 'foo',
    })
  })
})

describe('ModelSelect trigger display', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('shows the model name as primary text when a model is selected', () => {
    render(
      <ModelSelect
        value="minimax-coding-plan/minimax-m3"
        placeholder="Opencode default"
        models={['minimax-coding-plan/minimax-m3', 'opencode-go/minimax-m3', 'opencode/minimax-m3-free']}
        onChange={() => {}}
      />
    )

    const trigger = screen.getByRole('button', { name: /minimax-m3/i })
    expect(trigger).toBeTruthy()
    expect(trigger.textContent).toContain('minimax-coding-plan/minimax-m3')
  })

  it('disambiguates the selected model by showing the full id alongside the name', () => {
    render(
      <ModelSelect
        value="opencode-go/minimax-m3"
        placeholder="Opencode default"
        models={['minimax-coding-plan/minimax-m3', 'opencode-go/minimax-m3', 'opencode/minimax-m3-free']}
        onChange={() => {}}
      />
    )

    const trigger = screen.getByRole('button', { name: /minimax-m3/i })
    expect(trigger.textContent).toContain('opencode-go/minimax-m3')
    expect(trigger.textContent).not.toContain('minimax-coding-plan/minimax-m3')
    expect(trigger.textContent).not.toContain('opencode/minimax-m3-free')
  })

  it('falls back to the full id in the title attribute so the user can hover for the canonical id', () => {
    render(
      <ModelSelect
        value="minimax-coding-plan/minimax-m3"
        placeholder="Opencode default"
        models={['minimax-coding-plan/minimax-m3']}
        onChange={() => {}}
      />
    )

    const fullId = screen.getByText('minimax-coding-plan/minimax-m3')
    expect(fullId.getAttribute('title')).toBe('minimax-coding-plan/minimax-m3')
  })

  it('shows the placeholder when no value is selected', () => {
    render(
      <ModelSelect
        value={null}
        placeholder="Opencode default"
        models={['minimax-coding-plan/minimax-m3']}
        onChange={() => {}}
      />
    )

    const trigger = screen.getByRole('button', { name: 'Opencode default' })
    expect(trigger).toBeTruthy()
  })
})

describe('ModelSelect popover selection', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  const models = [
    'minimax-coding-plan/minimax-m3',
    'opencode-go/minimax-m3',
    'opencode/minimax-m3-free',
  ]

  function openPopover() {
    const trigger = screen.getByRole('button', { name: /Opencode default|minimax-m3/i })
    fireEvent.click(trigger)
  }

  it('calls onChange on click and closes popover', async () => {
    const onChange = vi.fn()
    render(
      <ModelSelect
        value={null}
        placeholder="Opencode default"
        models={models}
        onChange={onChange}
      />,
    )

    openPopover()

    const option = await waitFor(() => {
      const el = document.querySelector('[data-model-id="opencode-go/minimax-m3"]')
      if (!el) throw new Error('option not rendered yet')
      return el as HTMLElement
    })

    fireEvent.click(option)

    expect(onChange).toHaveBeenCalledWith('opencode-go/minimax-m3')
    await waitFor(() => {
      expect(document.querySelector('[data-model-id]')).toBeNull()
    })
  })

  it('does NOT select on pointerdown alone', async () => {
    const onChange = vi.fn()
    render(
      <ModelSelect
        value={null}
        placeholder="Opencode default"
        models={models}
        onChange={onChange}
      />,
    )

    openPopover()

    const option = await waitFor(() => {
      const el = document.querySelector('[data-model-id="opencode-go/minimax-m3"]')
      if (!el) throw new Error('option not rendered yet')
      return el as HTMLElement
    })

    fireEvent.pointerDown(option)

    expect(onChange).not.toHaveBeenCalled()
  })

  it('does NOT invoke onChange on press-move-release (no-select scenario)', async () => {
    const onChange = vi.fn()
    render(
      <ModelSelect
        value={null}
        placeholder="Opencode default"
        models={models}
        onChange={onChange}
      />,
    )

    openPopover()

    const option = await waitFor(() => {
      const el = document.querySelector('[data-model-id="opencode-go/minimax-m3"]')
      if (!el) throw new Error('option not rendered yet')
      return el as HTMLElement
    })

    fireEvent.pointerDown(option)
    fireEvent.pointerUp(document.body)

    expect(onChange).not.toHaveBeenCalled()
    expect(document.querySelector('[data-model-id]')).toBeTruthy()
  })

  it('updates the trigger to show the selected model name and full id after a click selection', async () => {
    const onChange = vi.fn()
    const Wrapper = ({ value }: { value: string | null }) => (
      <ModelSelect
        value={value}
        placeholder="Opencode default"
        models={models}
        onChange={onChange}
      />
    )
    const { rerender } = render(<Wrapper value={null} />)

    openPopover()

    const option = await waitFor(() => {
      const el = document.querySelector('[data-model-id="opencode/minimax-m3-free"]')
      if (!el) throw new Error('option not rendered yet')
      return el as HTMLElement
    })

    fireEvent.click(option)

    expect(onChange).toHaveBeenCalledWith('opencode/minimax-m3-free')

    rerender(<Wrapper value="opencode/minimax-m3-free" />)

    await waitFor(() => {
      const trigger = screen.getByRole('button', { name: /minimax-m3-free/i })
      expect(trigger.textContent).toContain('opencode/minimax-m3-free')
    })
  })

  it('selects by clicking a child element of the option via cmd event bubbling', async () => {
    const onChange = vi.fn()
    render(
      <ModelSelect
        value={null}
        placeholder="Opencode default"
        models={models}
        onChange={onChange}
      />,
    )

    openPopover()

    const option = await waitFor(() => {
      const el = document.querySelector('[data-model-id="minimax-coding-plan/minimax-m3"]')
      if (!el) throw new Error('option not rendered yet')
      return el as HTMLElement
    })

    const childSpan = option.querySelector('span') as HTMLElement
    expect(childSpan).toBeTruthy()

    fireEvent.click(childSpan)

    expect(onChange).toHaveBeenCalledWith('minimax-coding-plan/minimax-m3')
  })

  it('selects the first highlighted model via keyboard Enter', async () => {
    const onChange = vi.fn()
    render(
      <ModelSelect
        value={null}
        placeholder="Opencode default"
        models={models}
        onChange={onChange}
      />,
    )

    openPopover()

    const search = await waitFor(() => screen.getByPlaceholderText('Search models...'))
    expect(search).toBeTruthy()

    fireEvent.keyDown(search, { key: 'Enter' })

    expect(onChange).toHaveBeenCalledWith('minimax-coding-plan/minimax-m3')
  })

  it('filters the model list when typing in the search input', async () => {
    render(
      <ModelSelect
        value={null}
        placeholder="Opencode default"
        models={models}
        onChange={() => {}}
      />,
    )

    openPopover()

    const search = await waitFor(() => screen.getByPlaceholderText('Search models...'))

    fireEvent.change(search, { target: { value: 'free' } })

    await waitFor(() => {
      expect(document.querySelector('[data-model-id="minimax-coding-plan/minimax-m3"]')).toBeNull()
      expect(document.querySelector('[data-model-id="opencode-go/minimax-m3"]')).toBeNull()
      expect(document.querySelector('[data-model-id="opencode/minimax-m3-free"]')).toBeTruthy()
    })
  })

  it('calls onClear when the X clear button is clicked', () => {
    const onChange = vi.fn()
    const onClear = vi.fn()
    render(
      <ModelSelect
        value="minimax-coding-plan/minimax-m3"
        placeholder="Opencode default"
        models={models}
        onChange={onChange}
        onClear={onClear}
        allowClear
      />,
    )

    const clearButton = screen.getByRole('button', { name: 'Clear' })
    fireEvent.click(clearButton)

    expect(onClear).toHaveBeenCalledTimes(1)
    expect(onChange).not.toHaveBeenCalled()
  })

  it('closes without selecting when Escape is pressed', async () => {
    const onChange = vi.fn()
    render(
      <ModelSelect
        value={null}
        placeholder="Opencode default"
        models={models}
        onChange={onChange}
      />,
    )

    openPopover()

    const search = await waitFor(() => screen.getByPlaceholderText('Search models...'))
    fireEvent.keyDown(search, { key: 'Escape' })

    await waitFor(() => {
      expect(screen.queryByPlaceholderText('Search models...')).toBeNull()
    })
    expect(onChange).not.toHaveBeenCalled()
  })

  it('keyboard Home/End jump to first/last filtered option', async () => {
    render(
      <ModelSelect
        value={null}
        placeholder="Opencode default"
        models={models}
        onChange={() => {}}
      />,
    )

    openPopover()

    const search = await waitFor(() => screen.getByPlaceholderText('Search models...'))

    fireEvent.keyDown(search, { key: 'End' })

    await waitFor(() => {
      const items = document.querySelectorAll('[cmdk-item]')
      expect(items.length).toBe(3)
      const lastItem = items[items.length - 1]
      expect(lastItem.getAttribute('aria-selected')).toBe('true')
      expect(lastItem.getAttribute('data-model-id')).toBe('opencode/minimax-m3-free')
    })

    fireEvent.keyDown(search, { key: 'Home' })

    await waitFor(() => {
      const items = document.querySelectorAll('[cmdk-item]')
      const firstItem = items[0]
      expect(firstItem.getAttribute('aria-selected')).toBe('true')
      expect(firstItem.getAttribute('data-model-id')).toBe('minimax-coding-plan/minimax-m3')
    })
  })
})

describe('ModelSelect accessibility', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  const models = ['anthropic/claude', 'openai/gpt-4']

  it('option list exposes listbox role and options expose option role', async () => {
    render(
      <ModelSelect
        value={null}
        placeholder="Opencode default"
        models={models}
        onChange={() => {}}
      />
    )

    fireEvent.click(screen.getByRole('button', { name: 'Opencode default' }))

    await waitFor(() => {
      const listbox = document.querySelector('[role="listbox"]')
      expect(listbox).toBeTruthy()
    })

    const options = document.querySelectorAll('[role="option"]')
    expect(options.length).toBe(2)
  })

  it('selected option has aria-selected true and others have false', async () => {
    render(
      <ModelSelect
        value="anthropic/claude"
        placeholder="Opencode default"
        models={models}
        onChange={() => {}}
      />
    )

    fireEvent.click(screen.getByRole('button', { name: /claude/i }))

    await waitFor(() => {
      const claudeOption = document.querySelector('[data-model-id="anthropic/claude"]')
      expect(claudeOption?.getAttribute('aria-selected')).toBe('true')
    })

    const gptOption = document.querySelector('[data-model-id="openai/gpt-4"]')
    expect(gptOption?.getAttribute('aria-selected')).toBe('false')
  })

  it('combobox input has aria-controls and aria-expanded', async () => {
    render(
      <ModelSelect
        value={null}
        placeholder="Opencode default"
        models={models}
        onChange={() => {}}
      />
    )

    fireEvent.click(screen.getByRole('button', { name: 'Opencode default' }))

    const search = await waitFor(() => screen.getByPlaceholderText('Search models...'))
    expect(search.getAttribute('aria-expanded')).toBe('true')
    expect(search.getAttribute('aria-controls')).toBeTruthy()
  })

  it('aria-activedescendant updates when active option changes', async () => {
    render(
      <ModelSelect
        value={null}
        placeholder="Opencode default"
        models={models}
        onChange={() => {}}
      />
    )

    fireEvent.click(screen.getByRole('button', { name: 'Opencode default' }))

    const search = await waitFor(() => screen.getByPlaceholderText('Search models...'))

    const input = search
    const initialDescendant = input.getAttribute('aria-activedescendant')

    fireEvent.keyDown(search, { key: 'ArrowDown' })

    await waitFor(() => {
      const updatedDescendant = input.getAttribute('aria-activedescendant')
      expect(updatedDescendant).toBeTruthy()
      expect(updatedDescendant).not.toBe(initialDescendant)
    })
  })

  it('CommandList has overscroll-y-contain and CommandGroup heading has sticky classes', async () => {
    render(
      <ModelSelect
        value={null}
        placeholder="Opencode default"
        models={['anthropic/claude', 'openai/gpt-4']}
        onChange={() => {}}
      />
    )

    fireEvent.click(screen.getByRole('button', { name: 'Opencode default' }))

    await waitFor(() => {
      const list = document.querySelector('[role="listbox"]')
      expect(list).toBeTruthy()
      expect(list!.className).toContain('overscroll-y-contain')

      const group = document.querySelector('[data-slot="command-group"]')
      expect(group).toBeTruthy()
      expect(group!.className).toContain('[[cmdk-group-heading]]:sticky')
      expect(group!.className).toContain('[[cmdk-group-heading]]:top-0')
      expect(group!.className).toContain('[[cmdk-group-heading]]:z-10')
      expect(group!.className).toContain('[[cmdk-group-heading]]:bg-muted')
    })
  })
})

describe('ModelSelect inline variant chips', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  const models = ['anthropic/claude', 'openai/gpt-4']
  const modelVariants = { 'anthropic/claude': ['low', 'medium', 'high', 'max'] }

  function renderVariantSelect(props: Partial<React.ComponentProps<typeof ModelSelect>> = {}) {
    const onChange = vi.fn()
    const onChangeVariant = vi.fn()
    const view = render(
      <ModelSelect
        id="test-model"
        value={props.value ?? null}
        valueVariant={props.valueVariant}
        placeholder="Opencode default"
        models={models}
        modelVariants={modelVariants}
        onChange={onChange}
        onChangeVariant={onChangeVariant}
        size={props.size}
      />,
    )
    return { ...view, onChange, onChangeVariant }
  }

  async function openVariantSelect() {
    fireEvent.click(screen.getByRole('button', { name: /Opencode default|claude|gpt/i }))
    return await waitFor(() => screen.getByPlaceholderText('Search models...'))
  }

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
