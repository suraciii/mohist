import { http, HttpResponse } from 'msw'
import { vi } from 'vitest'
import { render } from '../../../../tests/test-utils'
import { AiSettingsSection } from './AiSettingsSection'

let opencodeRuntime: { mode: string; command: string; model: string | null; note: string } = {
  mode: 'local',
  command: 'opencode',
  model: 'openai/gpt-4',
  note: '',
}
let availableModels: { models: string[]; modelVariants: Record<string, string[]> } = {
  models: ['openai/gpt-4', 'anthropic/claude-3', 'google/gemini-2'],
  modelVariants: {},
}
let workflowVariables: Record<string, unknown> = { vars: null, stages: null }
export const patchCaptures: Array<Record<string, unknown>> = []

export const aiSettingsSectionHandlers = [
  http.get('/api/opencode/runtime', () =>
    HttpResponse.json({ success: true, data: opencodeRuntime }),
  ),
  http.get('/api/projects/:projectId/opencode/models', () =>
    HttpResponse.json({ success: true, data: availableModels }),
  ),
  http.get('/api/projects/:projectId/variables', () =>
    HttpResponse.json({ success: true, data: workflowVariables }),
  ),
  http.patch('/api/projects/:projectId/variables', async ({ request }) => {
    const body = await request.json()
    patchCaptures.push(body as Record<string, unknown>)
    return HttpResponse.json({ success: true, data: body })
  }),
]

export function renderSection() {
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

export function arrangeLoaded(options: ArrangeOptions = {}) {
  const models = options.models ?? ['openai/gpt-4', 'anthropic/claude-3', 'google/gemini-2']
  const modelVariants = options.modelVariants ?? {}

  availableModels = { models, modelVariants }
  opencodeRuntime = { mode: 'local', command: 'opencode', model: 'openai/gpt-4', note: '' }

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

  workflowVariables = {
    vars: Object.keys(vars).length > 0 ? vars : null,
    stages: Object.keys(stages).length > 0 ? stages : null,
  }
}

export function resetAiSettingsSectionTestState() {
  vi.clearAllMocks()
  patchCaptures.length = 0
  availableModels = { models: ['openai/gpt-4', 'anthropic/claude-3', 'google/gemini-2'], modelVariants: {} }
  opencodeRuntime = { mode: 'local', command: 'opencode', model: 'openai/gpt-4', note: '' }
  workflowVariables = { vars: null, stages: null }
}
