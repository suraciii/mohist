import type { PromptKind } from './session-transcript-display'

export const PROMPT_KIND_LABELS: Record<PromptKind, string> = {
  initial: 'Initial Task',
  task: 'Task',
  retry: 'Retry',
  followup: 'Follow-up',
  recovery: 'Recovery',
  'legacy-missing': 'Missing Prompt',
}

export function promptKindLabel(kind: PromptKind): string {
  return PROMPT_KIND_LABELS[kind] ?? kind
}