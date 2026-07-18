import type { ActionContext } from "../core/types.js"
import { stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"

export function resolveDeliveryBaseBranch(context: ActionContext, inputName = "target"): string | null {
  const explicit = stringInput(context.with, inputName) ?? (inputName === "target" ? stringInput(context.with, "baseBranch") : null)
  const authoritative = stringAt(context.variables, ["repository", "baseBranch"])
  if (isIssueBacked(context)) {
    if (hasAuthoritativeIssueRepository(context)) {
      if (!authoritative || (explicit && explicit !== authoritative)) return null
      return authoritative
    }
    return explicit ?? authoritative ?? null
  }
  if (explicit) return explicit
  if (authoritative) return authoritative
  return stringAt(context.variables, ["project", "defaultBranch"])
    ?? stringAt(context.variables, ["project", "baseBranch"])
    ?? "main"
}

export function isIssueBacked(context: ActionContext): boolean {
  return context.issueNumber !== undefined || numberAt(context.variables, ["issue", "number"]) !== undefined
}

function hasAuthoritativeIssueRepository(context: ActionContext): boolean {
  return isIssueBacked(context)
    && !!stringAt(context.variables, ["repository", "gitUrl"])
    && !!stringAt(context.variables, ["repository", "baseBranch"])
}

export function resolveDeliveryRemote(context: ActionContext, genericDefault: string | null = "origin"): string | null {
  const explicit = stringInput(context.with, "remote")
  if (hasAuthoritativeIssueRepository(context)) return explicit && explicit !== "origin" ? null : "origin"
  return explicit ?? genericDefault
}

export function resolveDeliverySource(context: ActionContext): string | null {
  const explicit = stringInput(context.with, "source")
  const authoritative = stringAt(context.variables, ["workspace", "branch"])
  if (hasAuthoritativeIssueRepository(context)) {
    if (!authoritative || (explicit && explicit !== authoritative)) return null
    return authoritative
  }
  return explicit ?? authoritative ?? "HEAD"
}

export function resolveGitHubRepository(context: ActionContext): string | null | undefined {
  if (!hasAuthoritativeIssueRepository(context)) return undefined
  const gitUrl = stringAt(context.variables, ["repository", "gitUrl"])
  if (!gitUrl) return null
  const trimmed = gitUrl.trim()
  const scpBody = trimmed.toLowerCase().startsWith("ssh:") ? trimmed.slice("ssh:".length) : trimmed
  if (!scpBody.includes("://")) {
    const scp = /^(?:[^@]+@)?([^:/]+):(.+)$/.exec(scpBody)
    if (scp) return toGitHubRepository(scp[1]!, scp[2]!)
  }
  try {
    const url = new URL(trimmed)
    return toGitHubRepository(url.hostname, url.pathname)
  } catch {
    return null
  }
}

function toGitHubRepository(host: string, rawPath: string): string | null {
  const parts = rawPath.replace(/^\/+|\/+$/g, "").replace(/\.git$/i, "").split("/")
  if (parts.length !== 2 || parts.some((part) => !part)) return null
  return `${host.toLowerCase()}/${parts.join("/")}`
}

function numberAt(value: Record<string, unknown> | null | undefined, path: string[]): number | undefined {
  let current: unknown = value
  for (const segment of path) {
    if (!current || typeof current !== "object") return undefined
    current = (current as Record<string, unknown>)[segment]
  }
  return typeof current === "number" ? current : undefined
}
