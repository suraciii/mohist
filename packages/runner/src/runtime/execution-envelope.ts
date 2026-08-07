import type { ResolvedSkill } from "./skill-resolver.js"
import type { SlackExecutionContext } from "./slack-execution-context.js"
import type { AgentSessionStartup } from "../core/types.js"

export function buildExecutionEnvelope(
  prompt: string,
  instructions?: string | null,
  skills: readonly ResolvedSkill[] = [],
  slackExecutionContext?: SlackExecutionContext | null,
  agentSessionStartup?: AgentSessionStartup | null,
): string {
  const normalizedInstructions = instructions?.trim() || null
  const startup = agentSessionStartup
    ? `[mohist-agent-session-startup]\n${JSON.stringify(agentSessionStartup)}\n[/mohist-agent-session-startup]`
    : null
  if (skills.length === 0 && !slackExecutionContext) {
    const task = normalizedInstructions ? `${normalizedInstructions}\n\n${prompt}` : prompt
    return startup ? `${startup}\n\n${task}` : task
  }
  const data = JSON.stringify({
    instructions: normalizedInstructions,
    skills: skills.map((skill) => ({ name: skill.name, instructions: skill.instructions })),
  })
  const definition = `[mohist-execution-definition]\n${data}\n[/mohist-execution-definition]`
  if (!slackExecutionContext) return startup ? `${startup}\n\n${definition}\n\n${prompt}` : `${definition}\n\n${prompt}`
  const facts = JSON.stringify({
    source: "slack",
    version: slackExecutionContext.version,
    replyAnchor: slackExecutionContext.replyAnchor,
  })
  const envelope = `[mohist-system-facts]\n${facts}\n[/mohist-system-facts]\n\n${definition}\n\n${prompt}`
  return startup ? `${startup}\n\n${envelope}` : envelope
}
