import { isAbsolute, join, relative, resolve } from "node:path"
import type { ActionContext, JsonObject, WorkItem, WorkItemResult } from "../core/types.js"
import { stringInput } from "../core/json.js"
import { renderTemplate, unresolvedReferences, wholeStringUnresolvedReferences } from "../core/template.js"
import { ensureDir } from "../system/process.js"
import { runnerVariables, WorkspaceManager } from "./workspace.js"
import type { ActionRegistry } from "../actions/registry.js"
import type { ServerConnection } from "../server/connection.js"
import type { AcpSessionManager, SharedAcpConnection } from "./acp-connection.js"
import {
  actionProducedArtifacts,
  captureArtifacts,
  captureRequiresFailures,
  summarizeCaptureFailures,
  uploadCapturedArtifacts,
} from "./artifact-capture.js"

export class WorkExecutor {
  constructor(
    private readonly actions: ActionRegistry,
    private readonly workspaceManager: WorkspaceManager,
    private readonly connection: ServerConnection,
    private readonly sessionManager: AcpSessionManager,
    private acpConnection: SharedAcpConnection | null,
    private readonly fallbackWorkDir = process.cwd(),
  ) {}

  updateAcpConnection(acp: SharedAcpConnection | null) {
    this.acpConnection = acp
  }

  async execute(work: WorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    if (work.workType === "checks") return await this.executeChecks(work, signal)
    return await this.executeOne(work, signal)
  }

  private async executeOne(work: WorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    const action = this.actions.resolve(work.uses)
    if (!action) return failure(work, `No action found for '${work.uses}'`)

    try {
      const variables = await this.variables(work, signal)
      const unresolved = wholeStringUnresolvedReferences(work.with, variables)
      if (unresolved.length > 0) {
        return failure(work, formatUnresolvedError(work, unresolved))
      }
      const renderedWith = renderTemplate(work.with, variables)
      const workspaceRoot = this.workspaceRoot(variables)
      const workDir = await this.resolveWorkDir(renderedWith, workspaceRoot)
      const result = await action({ ...baseContext(work, variables, signal, this.sessionManager, this.acpConnection, this.connection), with: renderedWith, workDir })
      const normalized = normalize(work, result)
      if (normalized.status !== "completed") {
        return normalized
      }
      return await this.captureAndUploadArtifacts(work, workspaceRoot, workDir, normalized, result, variables, signal)
    } catch (error) {
      return failure(work, error instanceof Error ? error.message : String(error))
    }
  }

  private async executeChecks(work: WorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    const variables = await this.variables(work, signal)
    const checks = Array.isArray(work.with?.checks) ? work.with.checks.filter(isCheck) : []
    if (checks.length === 0) return failure(work, "No checks found in dispatch")

    const results = await Promise.all(checks.map(async (check) => {
      const action = this.actions.resolve(check.uses)
      if (!action) return { name: check.name, status: "fail", message: `No action found for '${check.uses}'` }
      try {
        const unresolved = wholeStringUnresolvedReferences(check.with ?? null, variables)
        if (unresolved.length > 0) {
          return { name: check.name, status: "fail", message: formatCheckUnresolvedError(unresolved) }
        }
        const renderedWith = renderTemplate(check.with ?? null, variables)
        const workDir = await this.resolveWorkDir(renderedWith, this.workspaceRoot(variables))
        const result = await action({ ...baseContext(work, variables, signal, this.sessionManager, this.acpConnection, this.connection), workType: "check", title: check.title, uses: check.uses, with: renderedWith, workDir })
        return { name: check.name, status: toCheckStatus(result.status), message: result.message, output: result.output }
      } catch (error) {
        return { name: check.name, status: "fail", message: error instanceof Error ? error.message : String(error) }
      }
    }))

    const verdict = results.every((result) => result.status === "pass") ? "pass" : "fail"
    const output = JSON.stringify(results)
    if (verdict === "fail") {
      const failedChecks = results.filter((r) => r.status === "fail")
      const checkDetails = failedChecks.map((c) => {
        const isMarkerCheck = checks.find((ch) => ch.name === c.name)?.uses === "core/marker"
        if (isMarkerCheck) {
          const checkConfig = checks.find((ch) => ch.name === c.name)
          const expectedMarker = checkConfig?.with?.expect ?? checkConfig?.with?.contains ?? "PASS"
          return `${c.name}: expected verdict marker '${expectedMarker}' but it was not found in the artifact`
        }
        return `${c.name}: ${c.message}`
      }).join("; ")
      return { status: "fail", message: `Check verdict failure: ${checkDetails}`, output }
    }
    return { status: "pass", output }
  }

  private async variables(work: WorkItem, signal: AbortSignal): Promise<JsonObject> {
    const workspace = await this.workspaceManager.ensure(work, signal)
    return { ...(work.variables ?? {}), runner: runnerVariables(), workspace: { path: workspace.path, branch: workspace.branch ?? null, changeDir: workspace.changeDir ?? null } }
  }

  private workspaceRoot(variables: JsonObject) {
    return stringAt(variables, ["workspace", "path"]) ?? join(this.fallbackWorkDir, "default")
  }

  private async resolveWorkDir(withInput: JsonObject | null, workspaceRoot: string) {
    const requested = stringInput(withInput, "working-directory")
    const root = resolve(workspaceRoot)
    const workDir = requested ? resolveWorkspacePath(root, requested) : root
    await ensureDir(workDir)
    return workDir
  }

  /**
   * Capture the task's declared `artifacts.files` plus any
   * action-produced dynamic artifacts from the runner workspace, upload
   * each to the server, and attach the resulting upload ids to the task
   * result. A failure to capture or upload any declared artifact fails
   * the task through the normal task failure path; dynamic artifact
   * failures are reported on the message but do not fail the task.
   */
  private async captureAndUploadArtifacts(
    work: WorkItem,
    workspaceRoot: string,
    workDir: string,
    result: WorkItemResult,
    actionResult: import("../core/types.js").ActionResult,
    variables: JsonObject,
    signal: AbortSignal,
  ): Promise<WorkItemResult> {
    // Render the declared artifacts object so template variables
    // (e.g. `${{ openspecChangeDir }}/review.md` from the default
    // workflow) resolve to workspace-relative paths before the
    // capture layer hands them to the filesystem. Without this
    // substitution the runner would read from a literal
    // `${{ openspecChangeDir }}` directory and fail every declared
    // artifact capture with ENOENT.
    //
    // Artifact `path` strings must resolve every embedded reference;
    // unlike `with.prompt` they are real workspace paths, not
    // documentation, so an embedded `${{ unknown }}` left in place
    // is a bug rather than a tolerated literal. We use
    // `unresolvedReferences` (which catches both whole-string and
    // embedded) to surface the failure before the capture layer
    // would otherwise encounter an ENOENT.
    let renderedArtifacts: JsonObject | null = null
    if (work.artifacts) {
      try {
        const unresolved = unresolvedReferences(work.artifacts, variables)
        if (unresolved.length > 0) {
          return {
            ...result,
            status: "failed",
            message: `${result.message ? result.message + "; " : ""}artifact declaration references undefined variable(s): ${unresolved.map((p) => "'${{ " + p + " }}'").join(", ")}. Add the variable to workflow.variables or a parent stage.`.slice(0, 4000),
          }
        }
        renderedArtifacts = renderTemplate(work.artifacts, variables) as JsonObject | null
      } catch (error) {
        return {
          ...result,
          status: "failed",
          message: `${result.message ? result.message + "; " : ""}artifact template render failed: ${error instanceof Error ? error.message : String(error)}`.slice(0, 4000),
        }
      }
    }

    const dynamicInputs = actionProducedArtifacts(actionResult)
    let captureOutcome
    try {
      const declaredOutcome = await captureArtifacts({ work, workDir: workspaceRoot, renderedArtifacts })
      const dynamicOutcome = dynamicInputs.length === 0
        ? { captures: [], failures: [] }
        : await captureArtifacts({ work: { ...work, artifacts: null }, workDir, dynamicArtifacts: dynamicInputs })
      captureOutcome = {
        captures: [...declaredOutcome.captures, ...dynamicOutcome.captures],
        failures: [...declaredOutcome.failures, ...dynamicOutcome.failures],
      }
    } catch (error) {
      return {
        ...result,
        status: "failed",
        message: `${result.message ? result.message + "; " : ""}artifact capture failed: ${error instanceof Error ? error.message : String(error)}`.slice(0, 4000),
      }
    }
    const declaredFailures = captureRequiresFailures(captureOutcome)
    if (declaredFailures.length > 0) {
      return {
        ...result,
        status: "failed",
        message: `${result.message ? result.message + "; " : ""}required declared artifacts could not be captured: ${summarizeCaptureFailures(declaredFailures)}`.slice(0, 4000),
      }
    }
    if (captureOutcome.captures.length === 0) {
      return result
    }
    let uploads
    try {
      uploads = await uploadCapturedArtifacts(this.connection, work.workflowRunId, work.workId, captureOutcome.captures, signal)
    } catch (error) {
      return {
        ...result,
        status: "failed",
        message: `${result.message ? result.message + "; " : ""}artifact upload failed: ${error instanceof Error ? error.message : String(error)}`.slice(0, 4000),
      }
    }
    const uploadFailures = uploads.failures
    const requiredUploadFailures = uploadFailures.filter((failure) => failure.source === "declared")
    if (requiredUploadFailures.length > 0) {
      return {
        ...result,
        status: "failed",
        message: `${result.message ? result.message + "; " : ""}required declared artifacts could not be uploaded: ${summarizeCaptureFailures(requiredUploadFailures)}`.slice(0, 4000),
      }
    }
    const message = uploadFailures.length > 0
      ? `${result.message ? result.message + "; " : ""}some dynamic artifacts failed to upload: ${summarizeCaptureFailures(uploadFailures)}`
      : result.message
    return {
      ...result,
      message: message ?? result.message,
      artifactUploadIds: uploads.uploads.map((upload) => upload.uploadId),
    }
  }
}

function baseContext(work: WorkItem, variables: JsonObject, signal: AbortSignal, sessionManager: AcpSessionManager, acpConnection: SharedAcpConnection | null, connection: ServerConnection): Omit<ActionContext, "with" | "workDir"> {
  return { workflowRunId: work.workflowRunId, workId: work.workId, workType: work.workType, stage: work.stage, title: work.title, uses: work.uses, variables, signal, projectId: work.projectId, issueNumber: work.issueNumber, acpSessionManager: sessionManager, acpConnection, serverConnection: connection }
}

function normalize(work: WorkItem, result: WorkItemResult): WorkItemResult {
  const status = result.status.toLowerCase()
  if (work.workType === "check") {
    if (["pass", "passed", "success", "succeeded", "completed"].includes(status)) return { ...result, status: "pass" }
    if (status === "pending") return { ...result, status: "pending" }
    return { ...result, status: "fail" }
  }
  if (["completed", "success", "succeeded", "pass", "passed"].includes(status)) return { ...result, status: "completed" }
  return { ...result, status: "failed" }
}

function failure(work: WorkItem, message: string): WorkItemResult {
  return { status: work.workType === "check" || work.workType === "checks" ? "fail" : "failed", message }
}

function toCheckStatus(status: string) {
  const normalized = status.toLowerCase()
  if (["pass", "passed", "success", "succeeded", "completed"].includes(normalized)) return "pass"
  if (normalized === "pending") return "pending"
  return "fail"
}

function isCheck(value: unknown): value is { name?: string; title?: string; uses: string; with?: JsonObject | null } {
  return typeof value === "object" && value !== null && "uses" in value && typeof (value as { uses?: unknown }).uses === "string"
}

function stringAt(value: JsonObject, path: string[]) {
  const found = path.reduce<unknown>((current, part) => {
    if (typeof current !== "object" || current === null || Array.isArray(current)) return undefined
    return (current as JsonObject)[part]
  }, value)
  return typeof found === "string" ? found : undefined
}

function resolveWorkspacePath(workspaceRoot: string, requested: string) {
  const resolved = isAbsolute(requested) ? resolve(requested) : resolve(workspaceRoot, requested)
  const rel = relative(workspaceRoot, resolved)
  if (rel.startsWith("..") || isAbsolute(rel)) {
    throw new Error(`working-directory '${requested}' escapes workspace.path`)
  }
  return resolved
}

function formatUnresolvedError(work: WorkItem, unresolved: string[]): string {
  const label = work.title?.trim() || work.uses || work.workId
  const refs = unresolved.map((p) => "'${{ " + p + " }}'").join(", ")
  return "Task " + work.workId + " (" + label + ") references undefined variable(s): " + refs + ". " +
    "Add the variable to workflow.variables, define it in a parent stage, or escape the literal with \\${{ ... }}."
}

function formatCheckUnresolvedError(unresolved: string[]): string {
  const refs = unresolved.map((p) => "'${{ " + p + " }}'").join(", ")
  return "check references undefined variable(s): " + refs + ". " +
    "Add the variable to workflow.variables, define it in a parent stage, or escape the literal with \\${{ ... }}."
}
