import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, vi } from 'vitest'
import { ModelSelect } from './ModelSelect'

export const models = ['anthropic/claude', 'openai/gpt-4']
export const modelVariants = { 'anthropic/claude': ['low', 'medium', 'high', 'max'] }

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

export function renderVariantSelect(props: Partial<React.ComponentProps<typeof ModelSelect>> = {}) {
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

export async function openVariantSelect() {
  fireEvent.click(screen.getByRole('button', { name: /Opencode default|claude|gpt/i }))
  return await waitFor(() => screen.getByPlaceholderText('Search models...'))
}
