// @vitest-environment jsdom
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

  it('calls onChange when a model option receives a native pointerdown (mouse click)', async () => {
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

    expect(onChange).toHaveBeenCalledWith('opencode-go/minimax-m3')
  })

  it('updates the trigger to show the selected model name and full id after a pointerdown selection', async () => {
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

    fireEvent.pointerDown(option)

    expect(onChange).toHaveBeenCalledWith('opencode/minimax-m3-free')

    rerender(<Wrapper value="opencode/minimax-m3-free" />)

    await waitFor(() => {
      const trigger = screen.getByRole('button', { name: /minimax-m3-free/i })
      expect(trigger.textContent).toContain('opencode/minimax-m3-free')
    })
  })

  it('uses event delegation: pointerdown on a child span of a model option still triggers onChange', async () => {
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

    fireEvent.pointerDown(childSpan)

    expect(onChange).toHaveBeenCalledWith('minimax-coding-plan/minimax-m3')
  })

  it('still triggers onChange via keyboard Enter on the highlighted model option', async () => {
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

    fireEvent.keyDown(search, { key: 'Enter' })

    expect(onChange).toHaveBeenCalledWith('minimax-coding-plan/minimax-m3')
  })

  it('still filters the model list when typing in the search input', async () => {
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

  it('still calls onClear when the X clear button is clicked', () => {
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
})
