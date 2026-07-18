import '@testing-library/jest-dom'
import { fireEvent, render, screen, waitFor } from '../../../../tests/test-utils'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useMswServer } from '../../../../tests/support/msw'
import { AiSettingsSection } from './AiSettingsSection'

let _opencodeRuntime: { mode: string; command: string; model: string | null; note: string } = {
  mode: 'local',
  command: 'opencode',
  model: 'openai/gpt-4',
  note: '',
}
let _availableModels: { models: string[]; modelVariants: Record<string, string[]> } = {
  models: ['openai/gpt-4', 'anthropic/claude-3', 'google/gemini-2'],
  modelVariants: {},
}
let _workflowVariables: Record<string, unknown> = { vars: null, stages: null }
const patchCaptures: Array<Record<string, unknown>> = []

useMswServer(
  http.get('/api/opencode/runtime', () =>
    HttpResponse.json({ success: true, data: _opencodeRuntime }),
  ),
  http.get('/api/projects/:projectId/opencode/models', () =>
    HttpResponse.json({ success: true, data: _availableModels }),
  ),
  http.get('/api/projects/:projectId/workflow-profile/variables', () =>
    HttpResponse.json({ success: true, data: _workflowVariables }),
  ),
  http.patch('/api/projects/:projectId/workflow-profile/variables', async ({ request }) => {
    const body = await request.json()
    patchCaptures.push(body as Record<string, unknown>)
    return HttpResponse.json({ success: true, data: body })
  }),
)

function renderSection() {
  return render(<AiSettingsSection />)
}

interface ArrangeOptions {
  models?: string[]
  modelVariants?: Record<string, string[]>
  defaultModel?: string | null
  defaultVariant?: string | null
  stageModels?: Record<string, string> | null
  stageModelVariants?: Record<string, string> | null
}

function arrangeLoaded(options: ArrangeOptions = {}) {
  const models = options.models ?? ['openai/gpt-4', 'anthropic/claude-3', 'google/gemini-2']
  const modelVariants = options.modelVariants ?? {}

  _availableModels = { models, modelVariants }
  _opencodeRuntime = { mode: 'local', command: 'opencode', model: 'openai/gpt-4', note: '' }

  const vars: Record<string, unknown> = {}
  if (options.defaultModel) {
    const agent: Record<string, unknown> = { type: 'opencode', model: options.defaultModel }
    if (options.defaultVariant) agent.variant = options.defaultVariant
    vars.agent = agent
  }

  const stages: Record<string, { vars?: Record<string, unknown> | null }> = {}
  if (options.stageModels) {
    for (const [stage, model] of Object.entries(options.stageModels)) {
      const agent: Record<string, unknown> = { type: 'opencode', model }
      const variant = options.stageModelVariants?.[stage]
      if (variant) agent.variant = variant
      stages[stage] = { vars: { agent } }
    }
  }

  _workflowVariables = {
    vars: Object.keys(vars).length > 0 ? vars : null,
    stages: Object.keys(stages).length > 0 ? stages : null,
  }
}

describe('AiSettingsSection', () => {
  afterEach(() => {
    vi.clearAllMocks()
    patchCaptures.length = 0
    _availableModels = { models: ['openai/gpt-4', 'anthropic/claude-3', 'google/gemini-2'], modelVariants: {} }
    _opencodeRuntime = { mode: 'local', command: 'opencode', model: 'openai/gpt-4', note: '' }
    _workflowVariables = { vars: null, stages: null }
  })

  it('does not render the Runtime/Command/Models summary block', async () => {
    arrangeLoaded()
    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Default Coder Agent Model')).toBeInTheDocument()
    })

    expect(screen.queryByText('Runtime')).not.toBeInTheDocument()
    expect(screen.queryByText('Command')).not.toBeInTheDocument()
    expect(screen.queryByText('Models')).not.toBeInTheDocument()
    expect(
      screen.queryByText(/does not configure AI providers/i),
    ).not.toBeInTheDocument()
  })

  it('keeps the Default Coder Agent Model ModelSelect and shows the model-count hint', async () => {
    arrangeLoaded()
    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Default Coder Agent Model')).toBeInTheDocument()
    })

    expect(screen.getByText('3 models available')).toBeInTheDocument()
    expect(screen.getByText('Opencode default')).toBeInTheDocument()
  })

  it('keeps the Stage Model Overrides section available', async () => {
    arrangeLoaded()
    renderSection()

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Stage Model Overrides/i })).toBeInTheDocument()
    })
  })

  it('exposes and updates the Stage Model Overrides disclosure state', async () => {
    arrangeLoaded()
    const user = userEvent.setup()
    renderSection()

    const button = await screen.findByRole('button', { name: /Stage Model Overrides/i })
    expect(button).toHaveAttribute('aria-expanded', 'false')
    expect(button).toHaveAttribute('aria-controls', 'settings-stage-model-overrides')
    expect(document.getElementById('settings-stage-model-overrides')).not.toBeInTheDocument()

    await user.keyboard('[Tab]')
    await user.keyboard('[Tab]')
    expect(button).toHaveFocus()
    await user.keyboard('[Enter]')

    expect(button).toHaveAttribute('aria-expanded', 'true')
    expect(document.getElementById('settings-stage-model-overrides')).toBeInTheDocument()

    await user.keyboard(' ')
    expect(button).toHaveAttribute('aria-expanded', 'false')
    expect(document.getElementById('settings-stage-model-overrides')).not.toBeInTheDocument()
  })

  it('moves focus into ModelSelect search and supports Escape and arrow keys', async () => {
    arrangeLoaded()
    const user = userEvent.setup()
    renderSection()

    const defaultModelButton = await screen.findByRole('button', { name: /Default Coder Agent Model/i })
    await user.click(defaultModelButton)

    const searchInput = await screen.findByPlaceholderText('Search models...')
    expect(searchInput).toHaveFocus()

    await user.keyboard('[ArrowDown]')
    const geminiOption = document.querySelector('[data-model-id="google/gemini-2"]')
    expect(geminiOption).toBeTruthy()
    expect(geminiOption!.getAttribute('data-selected')).toBe('true')

    await user.keyboard('[Escape]')
    expect(screen.queryByPlaceholderText('Search models...')).not.toBeInTheDocument()
    expect(defaultModelButton).toHaveFocus()
  })

  it('lists executable pipeline stages including integrate and excluding fix', async () => {
    arrangeLoaded({
      stageModels: { plan: 'openai/gpt-4', build: 'openai/gpt-4', check: 'openai/gpt-4', integrate: 'openai/gpt-4' },
    })
    renderSection()

    const overridesButton = await screen.findByRole('button', { name: /Stage Model Overrides/i })
    fireEvent.click(overridesButton)

    const overrideRegion = document.getElementById('settings-stage-model-overrides')!
    expect(overrideRegion.textContent).toMatch(/integrate/)
    expect(overrideRegion.textContent).not.toMatch(/\bfix\b/)

    expect(document.getElementById('settings-stage-model-plan')).toBeInTheDocument()
    expect(document.getElementById('settings-stage-model-build')).toBeInTheDocument()
    expect(document.getElementById('settings-stage-model-check')).toBeInTheDocument()
    expect(document.getElementById('settings-stage-model-integrate')).toBeInTheDocument()
    expect(document.getElementById('settings-stage-model-fix')).toBeNull()
  })
})

describe('AiSettingsSection inline variant chips', () => {
  afterEach(() => {
    vi.clearAllMocks()
    patchCaptures.length = 0
    _availableModels = { models: ['openai/gpt-4', 'anthropic/claude-3', 'google/gemini-2'], modelVariants: {} }
    _opencodeRuntime = { mode: 'local', command: 'opencode', model: 'openai/gpt-4', note: '' }
    _workflowVariables = { vars: null, stages: null }
  })

  it('does not render a standalone variant picker next to the default model selector', async () => {
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high', 'max'] },
    })
    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Default Coder Agent Model')).toBeInTheDocument()
    })

    expect(screen.queryByTestId('settings-default-model-variant-trigger')).not.toBeInTheDocument()
  })

  it('renders inline variant chips on the default model row when the model has variants', async () => {
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high', 'max'] },
    })
    renderSection()

    const defaultModelButton = await screen.findByRole('button', { name: /Default Coder Agent Model/i })
    fireEvent.click(defaultModelButton)

    await waitFor(() => {
      for (const variant of ['low', 'medium', 'high', 'max']) {
        expect(
          document.querySelector(`[data-testid="settings-default-model-row-anthropic/claude-3-variant-${variant}"]`),
        ).toBeInTheDocument()
      }
    })
  })

  it('does not render variant chips on rows whose model has no variants', async () => {
    arrangeLoaded({
      modelVariants: { 'openai/gpt-4': [], 'anthropic/claude-3': ['low', 'high'] },
    })
    renderSection()

    const defaultModelButton = await screen.findByRole('button', { name: /Default Coder Agent Model/i })
    fireEvent.click(defaultModelButton)

    await waitFor(() => {
      expect(
        document.querySelector('[data-testid="settings-default-model-row-anthropic/claude-3-variant-low"]'),
      ).toBeInTheDocument()
    })
    expect(
      document.querySelector('[data-testid="settings-default-model-row-openai/gpt-4-variant-low"]'),
    ).toBeNull()
  })

  it('highlights the stored default variant as the active chip', async () => {
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      defaultVariant: 'high',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high', 'max'] },
    })
    renderSection()

    const trigger = await screen.findByRole('button', { name: /Default Coder Agent Model/i })
    expect(trigger.textContent).toContain('high')
  })

  it('does not mark any chip active when the stored default variant is not in the model variants', async () => {
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      defaultVariant: 'ultra',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    renderSection()

    const defaultModelButton = await screen.findByRole('button', { name: /Default Coder Agent Model/i })
    fireEvent.click(defaultModelButton)

    const highChip = await waitFor(() =>
      screen.getByTestId('settings-default-model-row-anthropic/claude-3-variant-high'),
    )
    expect(highChip).toBeInTheDocument()
    expect(highChip.getAttribute('data-variant-active')).toBe('false')
  })

  it('persists only model and variant through the default-model mutation when a chip is clicked', async () => {
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    const user = userEvent.setup()
    renderSection()

    const defaultModelButton = await screen.findByRole('button', { name: /Default Coder Agent Model/i })
    await user.click(defaultModelButton)

    const highChip = await screen.findByTestId(
      'settings-default-model-row-anthropic/claude-3-variant-high',
    )
    await user.click(highChip)

    await waitFor(() => {
      expect(patchCaptures.length).toBe(1)
    })
    const body = patchCaptures[0]
    const agent = (body.vars as Record<string, unknown>)?.agent as Record<string, unknown>
    expect(agent?.model).toBe('anthropic/claude-3')
    expect(agent?.variant).toBe('high')
    expect(screen.queryByPlaceholderText('Search models...')).not.toBeInTheDocument()
  })

  it('persists the clicked model and variant when the default model is unset', async () => {
    arrangeLoaded({
      defaultModel: null,
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    const user = userEvent.setup()
    renderSection()

    const defaultModelButton = await screen.findByRole('button', { name: /Default Coder Agent Model/i })
    await user.click(defaultModelButton)

    const highChip = await screen.findByTestId(
      'settings-default-model-row-anthropic/claude-3-variant-high',
    )
    await user.click(highChip)

    await waitFor(() => {
      expect(patchCaptures.length).toBe(1)
    })
    const body = patchCaptures[0]
    const agent = (body.vars as Record<string, unknown>)?.agent as Record<string, unknown>
    expect(agent?.model).toBe('anthropic/claude-3')
    expect(agent?.variant).toBe('high')
  })

  it('persists the clicked model and variant when choosing a different default model', async () => {
    arrangeLoaded({
      defaultModel: 'openai/gpt-4',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    const user = userEvent.setup()
    renderSection()

    const defaultModelButton = await screen.findByRole('button', { name: /Default Coder Agent Model/i })
    await user.click(defaultModelButton)

    const highChip = await screen.findByTestId(
      'settings-default-model-row-anthropic/claude-3-variant-high',
    )
    await user.click(highChip)

    await waitFor(() => {
      expect(patchCaptures.length).toBe(1)
    })
    const body = patchCaptures[0]
    const agent = (body.vars as Record<string, unknown>)?.agent as Record<string, unknown>
    expect(agent?.model).toBe('anthropic/claude-3')
    expect(agent?.variant).toBe('high')
  })

  it('does not render inline variant chips on a default model row whose model has no variants', async () => {
    arrangeLoaded({
      defaultModel: 'openai/gpt-4',
      modelVariants: { 'openai/gpt-4': [] },
    })
    renderSection()

    const defaultModelButton = await screen.findByRole('button', { name: /Default Coder Agent Model/i })
    fireEvent.click(defaultModelButton)

    await waitFor(() => {
      expect(
        document.querySelector('[data-model-id="openai/gpt-4"]'),
      ).toBeInTheDocument()
    })
    expect(
      document.querySelector('[data-testid="settings-default-model-row-openai/gpt-4-variant-low"]'),
    ).toBeNull()
    expect(document.querySelectorAll('[data-variant-chip]').length).toBe(0)
  })

  it('does not render variant chips on per-stage rows whose stage model has no variants', async () => {
    arrangeLoaded({
      stageModels: { build: 'openai/gpt-4' },
      modelVariants: { 'openai/gpt-4': [], 'anthropic/claude-3': ['low', 'high'] },
    })
    renderSection()

    const overridesButton = await screen.findByRole('button', { name: /Stage Model Overrides/i })
    fireEvent.click(overridesButton)
    fireEvent.click(document.getElementById('settings-stage-model-build')!)

    await waitFor(() => {
      expect(
        document.querySelector('[data-model-id="openai/gpt-4"]'),
      ).toBeInTheDocument()
    })
    expect(
      document.querySelector('[data-testid="settings-stage-model-build-row-openai/gpt-4-variant-low"]'),
    ).toBeNull()
    expect(screen.queryByTestId('settings-stage-model-build-variant-trigger')).not.toBeInTheDocument()
  })

  it('renders inline compact variant chips on a per-stage row whose model has variants', async () => {
    arrangeLoaded({
      stageModels: { build: 'anthropic/claude-3' },
      stageModelVariants: { build: 'medium' },
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    renderSection()

    const overridesButton = await screen.findByRole('button', { name: /Stage Model Overrides/i })
    fireEvent.click(overridesButton)
    fireEvent.click(document.getElementById('settings-stage-model-build')!)

    const mediumChip = await waitFor(() =>
      screen.getByTestId('settings-stage-model-build-row-anthropic/claude-3-variant-medium'),
    )
    expect(mediumChip).toBeInTheDocument()
    expect(mediumChip.getAttribute('data-variant-active')).toBe('true')
  })

  it('persists only model and variant through the stage-model mutation when a per-stage chip is clicked', async () => {
    arrangeLoaded({
      stageModels: { build: 'anthropic/claude-3' },
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    const user = userEvent.setup()
    renderSection()

    const overridesButton = await screen.findByRole('button', { name: /Stage Model Overrides/i })
    await user.click(overridesButton)
    await user.click(document.getElementById('settings-stage-model-build')!)

    const highChip = await screen.findByTestId(
      'settings-stage-model-build-row-anthropic/claude-3-variant-high',
    )
    await user.click(highChip)

    await waitFor(() => {
      expect(patchCaptures.length).toBe(1)
    })
    const body = patchCaptures[0]
    const buildStage = (body.stages as Record<string, unknown>)?.build as Record<string, unknown>
    const agent = buildStage?.vars as Record<string, unknown>
    expect(agent?.agent).toEqual({ type: 'opencode', model: 'anthropic/claude-3', variant: 'high' })
  })

  it('persists model and variant when a chip is clicked on an unset stage row', async () => {
    arrangeLoaded({
      stageModels: null,
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    const user = userEvent.setup()
    renderSection()

    const overridesButton = await screen.findByRole('button', { name: /Stage Model Overrides/i })
    await user.click(overridesButton)
    await user.click(document.getElementById('settings-stage-model-build')!)

    const highChip = await screen.findByTestId(
      'settings-stage-model-build-row-anthropic/claude-3-variant-high',
    )
    await user.click(highChip)

    await waitFor(() => {
      expect(patchCaptures.length).toBe(1)
    })
    const body = patchCaptures[0]
    const buildStage = (body.stages as Record<string, unknown>)?.build as Record<string, unknown>
    const agent = buildStage?.vars as Record<string, unknown>
    expect(agent?.agent).toEqual({ type: 'opencode', model: 'anthropic/claude-3', variant: 'high' })
  })
})

describe('AiSettingsSection default-model row click', () => {
  afterEach(() => {
    vi.clearAllMocks()
    patchCaptures.length = 0
    _availableModels = { models: ['openai/gpt-4', 'anthropic/claude-3', 'google/gemini-2'], modelVariants: {} }
    _opencodeRuntime = { mode: 'local', command: 'opencode', model: 'openai/gpt-4', note: '' }
    _workflowVariables = { vars: null, stages: null }
  })

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
    fireEvent.click(claudeRow)

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
    fireEvent.click(claudeRow)

    await waitFor(() => {
      expect(patchCaptures.length).toBe(1)
    })
    const body = patchCaptures[0]
    const agent = (body.vars as Record<string, unknown>)?.agent as Record<string, unknown>
    expect(agent?.model).toBe('anthropic/claude-3')
    expect(agent?.variant).toBeNull()
    expect(Object.prototype.hasOwnProperty.call(agent ?? {}, 'variant')).toBe(true)
  })

  it('clicking a default model row fires the mutation with variant: null even when no prior variant was stored (idempotent delete)', async () => {
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
    fireEvent.click(claudeRow)

    await waitFor(() => {
      expect(patchCaptures.length).toBe(1)
    })
    const body = patchCaptures[0]
    const agent = (body.vars as Record<string, unknown>)?.agent as Record<string, unknown>
    expect(agent?.model).toBe('anthropic/claude-3')
    expect(agent?.variant).toBeNull()
  })
})
