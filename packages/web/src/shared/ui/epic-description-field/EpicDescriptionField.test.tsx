// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { useState } from 'react'
import { EpicDescriptionField } from './EpicDescriptionField'
import { EPIC_DESCRIPTION_TEMPLATE } from '@/shared/lib/epic-description-template'

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

function ControlledField({
  initial = '',
  showInsertAction = false,
  onChangeSpy,
}: {
  initial?: string
  showInsertAction?: boolean
  onChangeSpy?: (next: string) => void
}) {
  const [value, setValue] = useState(initial)
  return (
    <EpicDescriptionField
      id="epic-description"
      value={value}
      onChange={(next) => {
        setValue(next)
        onChangeSpy?.(next)
      }}
      showInsertAction={showInsertAction}
    />
  )
}

describe('EpicDescriptionField rendering', () => {
  it('renders a label associated with the textarea by id', () => {
    render(<ControlledField />)
    const label = screen.getByText('Description')
    expect(label.tagName).toBe('LABEL')
    expect(label.getAttribute('for')).toBe('epic-description')
    const textarea = screen.getByLabelText('Description')
    expect(textarea.tagName).toBe('TEXTAREA')
  })

  it('allows the label to be customized', () => {
    render(
      <EpicDescriptionField
        id="epic-description"
        label="Epic body"
        value=""
        onChange={() => {}}
      />,
    )
    expect(screen.getByText('Epic body')).toBeInTheDocument()
  })

  it('does not render the Insert action by default', () => {
    render(<ControlledField />)
    expect(screen.queryByRole('button', { name: 'Insert template' })).toBeNull()
  })

  it('renders the Insert action when showInsertAction is true', () => {
    render(<ControlledField showInsertAction />)
    expect(screen.getByRole('button', { name: 'Insert template' })).toBeInTheDocument()
  })

  it('supports a custom insert action label', () => {
    render(
      <EpicDescriptionField
        id="epic-description"
        value="seed"
        onChange={() => {}}
        showInsertAction
        insertActionLabel="Use Goal/Background/Non-goals/Scope scaffold"
      />,
    )
    expect(
      screen.getByRole('button', { name: 'Use Goal/Background/Non-goals/Scope scaffold' }),
    ).toBeInTheDocument()
  })

  it('reflects the controlled value into the textarea', () => {
    render(<ControlledField initial="Existing markdown body" />)
    const textarea = screen.getByLabelText('Description') as HTMLTextAreaElement
    expect(textarea.value).toBe('Existing markdown body')
  })

  it('emits onChange with the new value as the user types', () => {
    const onChange = vi.fn()
    render(
      <EpicDescriptionField
        id="epic-description"
        value=""
        onChange={onChange}
        showInsertAction
      />,
    )
    const textarea = screen.getByLabelText('Description')
    fireEvent.change(textarea, { target: { value: 'New text' } })
    expect(onChange).toHaveBeenCalledWith('New text')
  })
})

describe('EpicDescriptionField insert behavior', () => {
  it('sets the value to the template when Insert is clicked on an empty value', () => {
    const onChange = vi.fn()
    render(
      <EpicDescriptionField
        id="epic-description"
        value=""
        onChange={onChange}
        showInsertAction
      />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Insert template' }))
    expect(onChange).toHaveBeenCalledTimes(1)
    expect(onChange).toHaveBeenCalledWith(EPIC_DESCRIPTION_TEMPLATE)
  })

  it('preserves existing user text when Insert is clicked on a non-empty value', () => {
    const onChange = vi.fn()
    render(
      <EpicDescriptionField
        id="epic-description"
        value="Existing notes"
        onChange={onChange}
        showInsertAction
      />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Insert template' }))
    expect(onChange).toHaveBeenCalledTimes(1)
    const next = onChange.mock.calls[0][0] as string
    expect(next.startsWith('Existing notes')).toBe(true)
    expect(next).toContain(EPIC_DESCRIPTION_TEMPLATE)
    expect(next).toContain('## Goal')
    expect(next).toContain('## Scope')
  })

  it('inserts a blank-line separator between existing text and the template', () => {
    const onChange = vi.fn()
    render(
      <EpicDescriptionField
        id="epic-description"
        value="Existing notes"
        onChange={onChange}
        showInsertAction
      />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Insert template' }))
    const next = onChange.mock.calls[0][0] as string
    expect(next).toBe(`Existing notes\n\n${EPIC_DESCRIPTION_TEMPLATE}`)
  })

  it('does not double-separate when the existing value already ends with a blank line', () => {
    const onChange = vi.fn()
    render(
      <EpicDescriptionField
        id="epic-description"
        value={'Existing notes\n\n'}
        onChange={onChange}
        showInsertAction
      />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Insert template' }))
    const next = onChange.mock.calls[0][0] as string
    expect(next).toBe(`Existing notes\n\n${EPIC_DESCRIPTION_TEMPLATE}`)
  })

  it('only adds a single newline when the existing value ends with one newline', () => {
    const onChange = vi.fn()
    render(
      <EpicDescriptionField
        id="epic-description"
        value={'Existing notes\n'}
        onChange={onChange}
        showInsertAction
      />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Insert template' }))
    const next = onChange.mock.calls[0][0] as string
    expect(next).toBe(`Existing notes\n\n${EPIC_DESCRIPTION_TEMPLATE}`)
  })

  it('does not modify the existing text content when inserting', () => {
    const onChange = vi.fn()
    render(
      <EpicDescriptionField
        id="epic-description"
        value={'## Goal\ndraft\n\n## Background\ncontext'}
        onChange={onChange}
        showInsertAction
      />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Insert template' }))
    const next = onChange.mock.calls[0][0] as string
    expect(next).toContain('## Goal\ndraft')
    expect(next).toContain('## Background\ncontext')
    expect(next).toContain(EPIC_DESCRIPTION_TEMPLATE)
  })
})

describe('EpicDescriptionField mobile-safe markup', () => {
  it('applies w-full max-w-full and break-words to the wrapper', () => {
    const { container } = render(<ControlledField showInsertAction />)
    const wrapper = container.firstElementChild as HTMLElement
    expect(wrapper.className).toContain('w-full')
    expect(wrapper.className).toContain('max-w-full')
    expect(wrapper.className).toContain('break-words')
  })

  it('keeps the wrapper width-bounded when the user types long unbroken content', () => {
    const longContent = 'a'.repeat(500)
    const { container } = render(
      <EpicDescriptionField
        id="epic-description"
        value={longContent}
        onChange={() => {}}
        showInsertAction
      />,
    )
    const wrapper = container.firstElementChild as HTMLElement
    expect(wrapper.className).toContain('w-full')
    expect(wrapper.className).toContain('max-w-full')
  })
})
