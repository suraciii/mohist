import type { ResolvedSkill } from "./skill-resolver.js"
import type { SlackExecutionContext } from "./slack-execution-context.js"

export function buildExecutionEnvelope(
  prompt: string,
  instructions?: string | null,
  skills: readonly ResolvedSkill[] = [],
  slackExecutionContext?: SlackExecutionContext | null,
): string {
  const normalizedInstructions = instructions?.trim() || null
  if (skills.length === 0 && !slackExecutionContext) return normalizedInstructions ? `${normalizedInstructions}\n\n${prompt}` : prompt
  const data = JSON.stringify({
    instructions: normalizedInstructions,
    skills: skills.map((skill) => ({ name: skill.name, instructions: skill.instructions })),
  })
  const definition = `[mohist-execution-definition]\n${data}\n[/mohist-execution-definition]`
  if (!slackExecutionContext) return `${definition}\n\n${prompt}`
  const facts = JSON.stringify({
    source: "slack",
    version: slackExecutionContext.version,
    replyAnchor: slackExecutionContext.replyAnchor,
  })
  return `[mohist-system-facts]\n${facts}\n[/mohist-system-facts]\n\n${definition}\n\n${prompt}`
}
