export type AgentTaskFocus = 'general' | 'coding' | 'review' | 'research'

export const AGENT_TASK_FOCUSES: Array<{ value: AgentTaskFocus; label: string }> = [
  { value: 'general', label: 'General purpose' },
  { value: 'coding', label: 'Build and debug code' },
  { value: 'review', label: 'Review and explain changes' },
  { value: 'research', label: 'Research and synthesize' },
]

const FOCUS_TERMS: Record<AgentTaskFocus, string[]> = {
  general: [],
  coding: ['code', 'coder', 'coding', 'dev', 'engineer', 'program', 'sonnet', 'gpt-4', 'o3', 'o4'],
  review: ['review', 'reason', 'think', 'opus', 'sonnet', 'o3', 'o4'],
  research: ['research', 'reason', 'think', 'gemini', 'opus', 'sonnet', 'o3', 'o4'],
}

function scoreModel(model: string, focus: AgentTaskFocus, purpose: string): number {
  const haystack = `${model} ${purpose}`.toLowerCase()
  return FOCUS_TERMS[focus].reduce((score, term) => score + (haystack.includes(term) ? 1 : 0), 0)
}

/** Rank only the models supplied by the selected Runtime catalog. */
export function recommendModels(models: string[], focus: AgentTaskFocus, purpose: string): string[] {
  return models
    .map((model, index) => ({ model, index, score: scoreModel(model, focus, purpose) }))
    .sort((left, right) => right.score - left.score || left.index - right.index)
    .map(({ model }) => model)
}
