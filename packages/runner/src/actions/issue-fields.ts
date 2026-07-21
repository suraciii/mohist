import { runCommand } from "../system/process.js"
export interface IssueFieldLookupContext {
  readonly workDir: string
  readonly signal: AbortSignal
  readonly projectId: string | null
  readonly issueNumber: number | null
}

type CommandRunner = typeof runCommand

let commandRunner: CommandRunner = runCommand

export function setIssueFieldCommandRunnerForTest(runner: CommandRunner | null) {
  commandRunner = runner ?? runCommand
}

export type IssueFieldSource = "issue.title" | "issue.body"

export interface IssueFields {
  title: string
  body: string
}

export function isIssueFieldSource(source: string | undefined): source is IssueFieldSource {
  return source === "issue.title" || source === "issue.body"
}

export async function resolveIssueField(context: IssueFieldLookupContext, source: IssueFieldSource): Promise<string> {
  const fields = await resolveIssueFields(context)
  return source === "issue.title" ? fields.title : fields.body
}

export async function resolveIssueFields(context: IssueFieldLookupContext): Promise<IssueFields> {
  const issueNumber = resolveIssueNumber(context)
  if (issueNumber === null) {
    throw new Error("issue field source requires an issue number")
  }

  const projectId = resolveProjectId(context)
  if (!projectId) {
    throw new Error("issue field source requires a project id")
  }

  const result = await commandRunner(
    "mo",
    ["issue", "show", String(issueNumber), "--project-id", projectId, "--output", "json"],
    context.workDir,
    context.signal,
  )
  if (result.exitCode !== 0) {
    const detail = [result.stdout.trim(), result.stderr.trim()].filter(Boolean).join("\n")
    throw new Error(`mo issue show ${issueNumber} failed while resolving issue fields${detail ? `: ${detail}` : ""}`)
  }

  return parseIssueFields(result.stdout) ?? { title: "", body: "" }
}

export function parseIssueField(output: string, source: IssueFieldSource): string | null {
  const fields = parseIssueFields(output)
  if (!fields) return null
  return source === "issue.title" ? fields.title : fields.body
}

export function parseIssueFields(output: string): IssueFields | null {
  if (!output.trim()) return null
  const parsed = JSON.parse(output) as unknown
  const data = objectProperty(parsed, "data") ?? parsed
  const title = objectProperty(data, "title")
  const body = objectProperty(data, "body")
  return {
    title: typeof title === "string" ? title : "",
    body: typeof body === "string" ? body : "",
  }
}

function resolveIssueNumber(context: IssueFieldLookupContext): number | null {
  if (typeof context.issueNumber === "number" && context.issueNumber > 0) return context.issueNumber
  return null
}

function resolveProjectId(context: IssueFieldLookupContext): string | null {
  return context.projectId ?? null
}

function objectProperty(value: unknown, key: string): unknown {
  if (!value || typeof value !== "object" || Array.isArray(value)) return undefined
  return (value as Record<string, unknown>)[key]
}
