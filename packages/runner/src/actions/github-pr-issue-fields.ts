import { stringInput } from "../core/json.js"
import type { JsonObject } from "../core/types.js"
import type { ActionHost } from "./host.js"
import { errorMessage } from "./github-pr-parse.js"
import { isIssueFieldSource, type IssueFields } from "./issue-fields.js"

export async function resolveCreatePrText(inputs: JsonObject, host: ActionHost): Promise<
  | { kind: "ok"; title: string; body: string }
  | { kind: "failure"; message: string }
> {
  const titleLiteral = stringInput(inputs, "title") ?? stringInput(inputs, "message")
  const bodyLiteral = stringInput(inputs, "body")
  const titleSource = titleLiteral === undefined ? stringInput(inputs, "titleFrom") ?? "issue.title" : undefined
  const bodySource = bodyLiteral === undefined ? stringInput(inputs, "bodyFrom") ?? "issue.body" : undefined

  const sourceError = validateIssueFieldSource("titleFrom", titleSource) ?? validateIssueFieldSource("bodyFrom", bodySource)
  if (sourceError) return { kind: "failure", message: sourceError }

  let issueFields: IssueFields | null = null
  if (titleSource || bodySource) {
    if (!host.issue) return { kind: "failure", message: "Issue field resolution requires the issue-fields capability" }
    try {
      issueFields = await host.issue.fields()
    } catch (error) {
      return { kind: "failure", message: errorMessage(error) }
    }
  }

  return {
    kind: "ok",
    title: titleLiteral ?? resolveIssueFieldValue(requiredIssueFields(issueFields), titleSource),
    body: bodyLiteral ?? resolveIssueFieldValue(requiredIssueFields(issueFields), bodySource),
  }
}

export async function resolveMergeSubject(inputs: JsonObject, host: ActionHost): Promise<
  | { kind: "ok"; subject: string }
  | { kind: "failure"; message: string }
> {
  const literal = stringInput(inputs, "subject")
  if (literal !== undefined) return { kind: "ok", subject: literal }

  const source = stringInput(inputs, "subjectFrom") ?? "issue.title"
  const sourceError = validateIssueFieldSource("subjectFrom", source)
  if (sourceError) return { kind: "failure", message: sourceError }

  if (!host.issue) return { kind: "failure", message: "Issue field resolution requires the issue-fields capability" }
  try {
    const issueFields = await host.issue.fields()
    return { kind: "ok", subject: resolveIssueFieldValue(issueFields, source) }
  } catch (error) {
    return { kind: "failure", message: errorMessage(error) }
  }
}

export function validateIssueFieldSource(name: string, source: string | undefined): string | null {
  if (source === undefined || isIssueFieldSource(source)) return null
  return `Unsupported ${name} source '${source}'. Supported sources: issue.title, issue.body.`
}

export function resolveIssueFieldValue(issueFields: IssueFields, source: string | undefined): string {
  if (source === "issue.body") return issueFields.body
  return issueFields.title
}

export function requiredIssueFields(issueFields: IssueFields | null): IssueFields {
  if (issueFields) return issueFields
  throw new Error("issue fields were not loaded")
}
