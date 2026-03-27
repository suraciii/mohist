export const CodeAgentDef = {
  name: 'code',

  buildPrompt(task: string): string {
    return task;
  },
} as const;
