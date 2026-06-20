// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { VariantPicker, resolveVariantAgainstModel, variantListFor } from './VariantPicker'

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('VariantPicker', () => {
  it('hides the picker entirely when no model is selected', () => {
    const { container } = render(
      <VariantPicker
        id="pick"
        modelId={null}
        modelVariants={['low', 'high']}
        value={null}
        onChange={() => {}}
      />,
    )

    expect(container.firstChild).toBeNull()
  })

  it('hides the picker when the selected model has no variants', () => {
    const { container } = render(
      <VariantPicker
        id="pick"
        modelId="openai/gpt-4"
        modelVariants={[]}
        value={null}
        onChange={() => {}}
      />,
    )

    expect(container.firstChild).toBeNull()
  })

  it('hides the picker when the variants prop is undefined', () => {
    const { container } = render(
      <VariantPicker
        id="pick"
        modelId="openai/gpt-4"
        modelVariants={undefined}
        value={null}
        onChange={() => {}}
      />,
    )

    expect(container.firstChild).toBeNull()
  })

  it('shows only the variants reported for the selected model', async () => {
    const onChange = vi.fn()
    render(
      <VariantPicker
        id="pick"
        modelId="anthropic/claude"
        modelVariants={['low', 'medium', 'high', 'max']}
        value={null}
        onChange={onChange}
      />,
    )

    const trigger = screen.getByTestId('pick-variant-trigger')
    fireEvent.click(trigger)

    const list = await waitFor(() => screen.getByRole('listbox'))
    const options = Array.from(list.querySelectorAll('[role="option"]')).map((el) => el.textContent?.trim())
    expect(options).toEqual(['Default', 'low', 'medium', 'high', 'max'])
  })

  it('filters out unsupported variants when a stored variant does not match the model', async () => {
    render(
      <VariantPicker
        id="pick"
        modelId="anthropic/claude"
        modelVariants={['low', 'medium', 'high']}
        value={null}
        onChange={() => {}}
      />,
    )

    const trigger = screen.getByTestId('pick-variant-trigger')
    fireEvent.click(trigger)

    const list = await waitFor(() => screen.getByRole('listbox'))
    const options = Array.from(list.querySelectorAll('[role="option"]')).map((el) => el.textContent?.trim())
    expect(options).toEqual(['Default', 'low', 'medium', 'high'])
    expect(options).not.toContain('max')
  })

  it('shows the stored variant as the selected value when supported', async () => {
    render(
      <VariantPicker
        id="pick"
        modelId="anthropic/claude"
        modelVariants={['low', 'medium', 'high']}
        value="high"
        onChange={() => {}}
      />,
    )

    const trigger = screen.getByTestId('pick-variant-trigger')
    expect(trigger).toHaveTextContent('high')

    fireEvent.click(trigger)
    const list = await waitFor(() => screen.getByRole('listbox'))
    const selectedOption = list.querySelector('[role="option"][data-highlighted]')
    expect(selectedOption?.textContent?.trim()).toBe('high')
  })

  it('falls back to the placeholder when a stored variant is not in the supported list', () => {
    render(
      <VariantPicker
        id="pick"
        modelId="anthropic/claude"
        modelVariants={['low', 'medium']}
        value="max"
        onChange={() => {}}
      />,
    )

    const trigger = screen.getByTestId('pick-variant-trigger')
    expect(trigger).toHaveTextContent('Variant')
  })

  it('calls onChange with the chosen variant', async () => {
    const onChange = vi.fn()
    render(
      <VariantPicker
        id="pick"
        modelId="anthropic/claude"
        modelVariants={['low', 'medium', 'high']}
        value="low"
        onChange={onChange}
      />,
    )

    fireEvent.click(screen.getByTestId('pick-variant-trigger'))
    const list = await waitFor(() => screen.getByRole('listbox'))
    const highOption = Array.from(list.querySelectorAll('[role="option"]'))
      .find((el) => el.textContent?.trim() === 'high') as HTMLElement
    expect(highOption).toBeDefined()
    fireEvent.pointerDown(highOption, { button: 0 })
    fireEvent.click(highOption)

    expect(onChange).toHaveBeenCalledWith('high')
  })

  it('calls onChange with null when the user picks Default', async () => {
    const onChange = vi.fn()
    render(
      <VariantPicker
        id="pick"
        modelId="anthropic/claude"
        modelVariants={['low', 'high']}
        value="high"
        onChange={onChange}
      />,
    )

    fireEvent.click(screen.getByTestId('pick-variant-trigger'))
    const list = await waitFor(() => screen.getByRole('listbox'))
    const defaultOption = Array.from(list.querySelectorAll('[role="option"]'))
      .find((el) => el.textContent?.trim() === 'Default') as HTMLElement
    expect(defaultOption).toBeDefined()
    fireEvent.pointerDown(defaultOption, { button: 0 })
    fireEvent.click(defaultOption)

    expect(onChange).toHaveBeenCalledWith(null)
  })

  it('does not throw when no onChange handler is provided', async () => {
    render(
      <VariantPicker
        id="pick"
        modelId="anthropic/claude"
        modelVariants={['low', 'high']}
        value={null}
      />,
    )

    fireEvent.click(screen.getByTestId('pick-variant-trigger'))
    const list = await waitFor(() => screen.getByRole('listbox'))
    const highOption = Array.from(list.querySelectorAll('[role="option"]'))
      .find((el) => el.textContent?.trim() === 'high') as HTMLElement
    expect(highOption).toBeDefined()
    fireEvent.pointerDown(highOption, { button: 0 })
    fireEvent.click(highOption)
  })
})

describe('resolveVariantAgainstModel', () => {
  const variantsMap = {
    'anthropic/claude': ['low', 'medium', 'high'],
    'openai/gpt-4': [],
    'google/gemini': ['low', 'high'],
  }

  it('returns the stored variant when the model supports it', () => {
    expect(resolveVariantAgainstModel('high', 'anthropic/claude', variantsMap)).toBe('high')
  })

  it('drops a stored variant the new model does not support', () => {
    expect(resolveVariantAgainstModel('max', 'anthropic/claude', variantsMap)).toBeNull()
  })

  it('returns null when no model is selected', () => {
    expect(resolveVariantAgainstModel('high', null, variantsMap)).toBeNull()
  })

  it('returns null when the selected model has no variants', () => {
    expect(resolveVariantAgainstModel('high', 'openai/gpt-4', variantsMap)).toBeNull()
  })

  it('returns null when the variants map is null', () => {
    expect(resolveVariantAgainstModel('high', 'anthropic/claude', null)).toBeNull()
  })

  it('returns null when the stored variant is null/undefined', () => {
    expect(resolveVariantAgainstModel(null, 'anthropic/claude', variantsMap)).toBeNull()
    expect(resolveVariantAgainstModel(undefined, 'anthropic/claude', variantsMap)).toBeNull()
  })
})

describe('variantListFor', () => {
  const variantsMap = {
    'anthropic/claude': ['low', 'medium', 'high'],
  }

  it('returns the variants array for the model', () => {
    expect(variantListFor('anthropic/claude', variantsMap)).toEqual(['low', 'medium', 'high'])
  })

  it('returns an empty array for an unknown model', () => {
    expect(variantListFor('unknown/model', variantsMap)).toEqual([])
  })

  it('returns an empty array for a null model id', () => {
    expect(variantListFor(null, variantsMap)).toEqual([])
  })

  it('returns an empty array for an undefined model id', () => {
    expect(variantListFor(undefined, variantsMap)).toEqual([])
  })

  it('returns an empty array when the variants map is null', () => {
    expect(variantListFor('anthropic/claude', null)).toEqual([])
  })
})
