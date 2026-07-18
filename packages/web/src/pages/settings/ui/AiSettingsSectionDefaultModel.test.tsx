import '@testing-library/jest-dom'
import { screen, waitFor } from '../../../../tests/test-utils'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it } from 'vitest'
import { useMswServer } from '../../../../tests/support/msw'
import {
  aiSettingsSectionHandlers,
  arrangeLoaded,
  patchCaptures,
  renderSection,
  resetAiSettingsSectionTestState,
} from './AiSettingsSectionTestSupport'

useMswServer(...aiSettingsSectionHandlers)
beforeEach(resetAiSettingsSectionTestState)

describe('AiSettingsSection default-model row click', () => {
  it('re-clicking the already-selected default model row fires the mutation with variant: null to clear any stale default variant', async () => {
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      defaultVariant: 'high',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    const user = userEvent.setup()
    renderSection()

    const defaultModelButton = await screen.findByRole('button', { name: /Default Coder Agent Model/i })
    await user.click(defaultModelButton)

    const claudeRow = await waitFor(
      () => document.querySelector('[data-model-id="anthropic/claude-3"]') as HTMLElement,
    )
    await user.click(claudeRow)

    await waitFor(() => {
      expect(patchCaptures.length).toBe(1)
    })
    const body = patchCaptures[0]
    const agent = (body.vars as Record<string, unknown>)?.agent as Record<string, unknown>
    expect(agent?.model).toBe('anthropic/claude-3')
    expect(agent?.variant).toBeNull()
    expect(Object.prototype.hasOwnProperty.call(agent ?? {}, 'variant')).toBe(true)
  })

  it('selecting a different default model row fires the mutation with the new model and variant: null', async () => {
    arrangeLoaded({
      defaultModel: 'openai/gpt-4',
      defaultVariant: 'high',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    const user = userEvent.setup()
    renderSection()

    const defaultModelButton = await screen.findByRole('button', { name: /Default Coder Agent Model/i })
    await user.click(defaultModelButton)

    const claudeRow = await waitFor(
      () => document.querySelector('[data-model-id="anthropic/claude-3"]') as HTMLElement,
    )
    await user.click(claudeRow)

    await waitFor(() => {
      expect(patchCaptures.length).toBe(1)
    })
    const body = patchCaptures[0]
    const agent = (body.vars as Record<string, unknown>)?.agent as Record<string, unknown>
    expect(agent?.model).toBe('anthropic/claude-3')
    expect(agent?.variant).toBeNull()
    expect(Object.prototype.hasOwnProperty.call(agent ?? {}, 'variant')).toBe(true)
  })

  it('clicking a default model row fires the mutation with variant: null even when no prior variant was stored', async () => {
    arrangeLoaded({
      defaultModel: 'openai/gpt-4',
      defaultVariant: null,
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    const user = userEvent.setup()
    renderSection()

    const defaultModelButton = await screen.findByRole('button', { name: /Default Coder Agent Model/i })
    await user.click(defaultModelButton)

    const claudeRow = await waitFor(
      () => document.querySelector('[data-model-id="anthropic/claude-3"]') as HTMLElement,
    )
    await user.click(claudeRow)

    await waitFor(() => {
      expect(patchCaptures.length).toBe(1)
    })
    const body = patchCaptures[0]
    const agent = (body.vars as Record<string, unknown>)?.agent as Record<string, unknown>
    expect(agent?.model).toBe('anthropic/claude-3')
    expect(agent?.variant).toBeNull()
  })
})
