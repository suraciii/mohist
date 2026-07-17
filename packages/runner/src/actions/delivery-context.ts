import type { ActionContext } from "../core/types.js"
import { stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"

export function resolveDeliveryBaseBranch(context: ActionContext, inputName = "target"): string | null {
  const explicit = stringInput(context.with, inputName) ?? (inputName === "target" ? stringInput(context.with, "baseBranch") : null)
  if (explicit) return explicit
  const authoritative = stringAt(context.variables, ["repository", "baseBranch"])
  if (authoritative) return authoritative
  if (isIssueBacked(context)) return null
  return stringAt(context.variables, ["project", "defaultBranch"])
    ?? stringAt(context.variables, ["project", "baseBranch"])
    ?? "main"
}

export function isIssueBacked(context: ActionContext): boolean {
  return context.issueNumber !== undefined || numberAt(context.variables, ["issue", "number"]) !== undefined
}

function numberAt(value: Record<string, unknown> | null | undefined, path: string[]): number | undefined {
  let current: unknown = value
  for (const segment of path) {
    if (!current || typeof current !== "object") return undefined
    current = (current as Record<string, unknown>)[segment]
  }
  return typeof current === "number" ? current : undefined
}
