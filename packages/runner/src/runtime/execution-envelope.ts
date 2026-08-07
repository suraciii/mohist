import type { ResolvedSkill } from "./skill-resolver.js"
import type { SlackExecutionContext } from "./slack-execution-context.js"
import type { AgentSessionStartup } from "../core/types.js"

export function buildExecutionEnvelope(
  prompt: string,
  instructions?: string | null,
  skills: readonly ResolvedSkill[] = [],
  slackExecutionContext?: SlackExecutionContext | null,
  agentSessionStartup?: AgentSessionStartup | null,
  workspaceAnchor?: string | null,
): string {
  const normalizedInstructions = instructions?.trim() || null
  const startup = agentSessionStartup
    ? `[mohist-agent-session-startup]\n${JSON.stringify(agentSessionStartup)}\n[/mohist-agent-session-startup]`
    : null
  const anchor = workspaceAnchor?.trim()
    ? `[mohist-workspace-anchor]\n${workspaceAnchor.trim()}\n[/mohist-workspace-anchor]`
    : null
  if (skills.length === 0 && !slackExecutionContext) {
    const task = normalizedInstructions ? `${normalizedInstructions}\n\n${prompt}` : prompt
    return [anchor, startup, task].filter((section): section is string => section !== null).join("\n\n")
  }
  const data = JSON.stringify({
    instructions: normalizedInstructions,
    skills: skills.map((skill) => ({ name: skill.name, instructions: skill.instructions })),
  })
  const definition = `[mohist-execution-definition]\n${data}\n[/mohist-execution-definition]`
  if (!slackExecutionContext) {
    const sections = [anchor, startup, definition, prompt].filter((section): section is string => section !== null)
    return sections.join("\n\n")
  }
  const facts = JSON.stringify({
    source: "slack",
    version: slackExecutionContext.version,
    replyAnchor: slackExecutionContext.replyAnchor,
  })
  const envelope = `[mohist-system-facts]\n${facts}\n[/mohist-system-facts]\n\n${definition}\n\n${prompt}`
  return [anchor, startup, envelope].filter((section): section is string => section !== null).join("\n\n")
}
