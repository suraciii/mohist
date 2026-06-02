// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
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
