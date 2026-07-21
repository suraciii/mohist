import { stringInput } from "../core/json.js"
import type { ActionInvocationContext } from "./context.js"
import { errorMessage } from "./github-pr-parse.js"
import { isIssueFieldSource, resolveIssueFields, type IssueFields } from "./issue-fields.js"

export async function resolveCreatePrText(context: ActionInvocationContext): Promise<
  | { kind: "ok"; title: string; body: string }
  | { kind: "failure"; message: string }
> {
  const titleLiteral = stringInput(context.with, "title") ?? stringInput(context.with, "message")
  const bodyLiteral = stringInput(context.with, "body")
  const titleSource = titleLiteral === undefined ? stringInput(context.with, "titleFrom") ?? "issue.title" : undefined
  const bodySource = bodyLiteral === undefined ? stringInput(context.with, "bodyFrom") ?? "issue.body" : undefined

  const sourceError = validateIssueFieldSource("titleFrom", titleSource) ?? validateIssueFieldSource("bodyFrom", bodySource)
  if (sourceError) return { kind: "failure", message: sourceError }

  let issueFields: IssueFields | null = null
  if (titleSource || bodySource) {
    const loaded = await loadIssueFields(context)
    if (loaded.kind === "failure") return loaded
    issueFields = loaded.issueFields
  }

  return {
    kind: "ok",
    title: titleLiteral ?? resolveIssueFieldValue(requiredIssueFields(issueFields), titleSource),
    body: bodyLiteral ?? resolveIssueFieldValue(requiredIssueFields(issueFields), bodySource),
  }
}

export async function resolveMergeSubject(context: ActionInvocationContext): Promise<
  | { kind: "ok"; subject: string }
  | { kind: "failure"; message: string }
> {
  const literal = stringInput(context.with, "subject")
  if (literal !== undefined) return { kind: "ok", subject: literal }

  const source = stringInput(context.with, "subjectFrom") ?? "issue.title"
  const sourceError = validateIssueFieldSource("subjectFrom", source)
  if (sourceError) return { kind: "failure", message: sourceError }

  const issueFields = await loadIssueFields(context)
  if (issueFields.kind === "failure") return issueFields
  return { kind: "ok", subject: resolveIssueFieldValue(issueFields.issueFields, source) }
}

export function validateIssueFieldSource(name: string, source: string | undefined): string | null {
  if (source === undefined || isIssueFieldSource(source)) return null
  return `Unsupported ${name} source '${source}'. Supported sources: issue.title, issue.body.`
}

export async function loadIssueFields(context: ActionInvocationContext): Promise<
  | { kind: "ok"; issueFields: IssueFields }
  | { kind: "failure"; message: string }
> {
  try {
    return { kind: "ok", issueFields: await resolveIssueFields(context) }
  } catch (error) {
    return { kind: "failure", message: errorMessage(error) }
  }
}

export function resolveIssueFieldValue(issueFields: IssueFields, source: string | undefined): string {
  if (source === "issue.body") return issueFields.body
  return issueFields.title
}

export function requiredIssueFields(issueFields: IssueFields | null): IssueFields {
  if (issueFields) return issueFields
  throw new Error("issue fields were not loaded")
}
