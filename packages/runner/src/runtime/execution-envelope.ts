import type { ResolvedSkill } from "./skill-resolver.js"

export function buildExecutionEnvelope(prompt: string, instructions?: string | null, skills: readonly ResolvedSkill[] = []): string {
  const normalizedInstructions = instructions?.trim() || null
  if (skills.length === 0) return normalizedInstructions ? `${normalizedInstructions}\n\n${prompt}` : prompt
  const data = JSON.stringify({
    instructions: normalizedInstructions,
    skills: skills.map((skill) => ({ name: skill.name, instructions: skill.instructions })),
  })
  return `[mohist-execution-definition]\n${data}\n[/mohist-execution-definition]\n\n${prompt}`
}
