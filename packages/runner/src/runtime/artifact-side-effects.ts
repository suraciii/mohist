import { renderTemplate, unresolvedReferences } from "../core/template.js"
import type { ActionResult, JsonObject, DispatchWorkItem, WorkItemResult } from "../core/types.js"
import {
  actionProducedArtifacts,
  captureArtifacts,
  summarizeCaptureFailures,
  uploadCapturedArtifacts,
  type ArtifactCaptureFailure,
  type ArtifactUploader,
  type CapturedArtifact,
  type UploadCapturedArtifactsResult,
} from "./artifact-capture.js"

export type RenderedArtifactDeclarations =
  | { kind: "ok"; artifacts: JsonObject | null }
  | { kind: "failure"; status: WorkItemResult["status"]; message: string }

export type UploadedCaptures =
  | { kind: "ok"; uploadIds: string[]; failures: ArtifactCaptureFailure[] }
  | { kind: "failure"; status: WorkItemResult["status"]; message: string }

export type ArtifactCaptureBatch =
  | { kind: "ok"; captures: CapturedArtifact[]; failures: ArtifactCaptureFailure[] }
  | { kind: "failure"; message: string }

const MAX_MESSAGE_LEN = 4000

export function renderArtifactDeclarations(
  work: DispatchWorkItem,
  variables: JsonObject,
): RenderedArtifactDeclarations {
  if (!work.artifacts) return { kind: "ok", artifacts: null }
  try {
    const unresolved = unresolvedReferences(work.artifacts, variables)
    if (unresolved.length > 0) {
      const refs = unresolved.map((p) => "'${{ " + p + " }}'").join(", ")
      return {
        kind: "failure",
        status: "failed",
        message: `artifact declaration references undefined variable(s): ${refs}. ` +
          `Add the variable to workflow.variables or a parent stage.`,
      }
    }
    return { kind: "ok", artifacts: renderTemplate(work.artifacts, variables) as JsonObject | null }
  } catch (error) {
    return {
      kind: "failure",
      status: "failed",
      message: `artifact template render failed: ${error instanceof Error ? error.message : String(error)}`,
    }
  }
}

export async function captureAndUploadArtifactsForWork(
  connection: ArtifactUploader,
  work: DispatchWorkItem,
  workspaceRoot: string,
  workDir: string,
  result: WorkItemResult,
  actionResult: ActionResult,
  variables: JsonObject,
  signal: AbortSignal,
): Promise<WorkItemResult> {
  const rendered = renderArtifactDeclarations(work, variables)
  if (rendered.kind === "failure") return withArtifactFailure(result, rendered.status, rendered.message)

  const captured = await captureArtifactsForWork(work, workspaceRoot, workDir, rendered.artifacts, actionResult)
  if (captured.kind === "failure") return withArtifactFailure(result, "failed", captured.message)
  if (captured.captures.length === 0) {
    return appendArtifactWarning(result, captured.failures, "artifact capture warnings")
  }
  const uploads = await uploadCapturesForWork(connection, work, captured.captures, signal)
  if (uploads.kind === "failure") return withArtifactFailure(result, uploads.status, uploads.message)
  const allFailures = [...captured.failures, ...uploads.failures]
  return { ...appendArtifactWarning(result, allFailures, "artifact warnings"), artifactUploadIds: uploads.uploadIds }
}

export async function captureArtifactsForWork(
  work: DispatchWorkItem,
  workspaceRoot: string,
  workDir: string,
  renderedArtifacts: JsonObject | null,
  actionResult: ActionResult,
): Promise<ArtifactCaptureBatch> {
  try {
    const declaredOutcome = await captureArtifacts({ work, workDir: workspaceRoot, renderedArtifacts })
    const dynamicInputs = actionProducedArtifacts(actionResult)
    const dynamicOutcome = dynamicInputs.length === 0
      ? { captures: [], failures: [] }
      : await captureArtifacts({ work: { ...work, artifacts: null }, workDir, dynamicArtifacts: dynamicInputs })
    return {
      kind: "ok",
      captures: [...declaredOutcome.captures, ...dynamicOutcome.captures],
      failures: [...declaredOutcome.failures, ...dynamicOutcome.failures],
    }
  } catch (error) {
    return { kind: "failure", message: `artifact capture failed: ${error instanceof Error ? error.message : String(error)}` }
  }
}

export async function uploadCapturesForWork(
  connection: ArtifactUploader,
  work: DispatchWorkItem,
  captures: ReadonlyArray<CapturedArtifact>,
  signal: AbortSignal,
): Promise<UploadedCaptures> {
  const ownerKind = work.ownerKind === "agent-job" ? "agent-job" : "workflow"
  const ownerId = ownerKind === "agent-job" ? work.agentJobId : work.workflowRunId
  if (!ownerId) {
    const ownerLabel = ownerKind === "agent-job" ? "agentJobId" : "workflowRunId"
    return { kind: "failure", status: "failed", message: `artifact upload failed: missing ${ownerLabel}` }
  }
  let outcome: UploadCapturedArtifactsResult
  try {
    outcome = await uploadCapturedArtifacts(connection, ownerId, work.workId, captures, signal, ownerKind)
  } catch (error) {
    return {
      kind: "failure",
      status: "failed",
      message: `artifact upload failed: ${error instanceof Error ? error.message : String(error)}`,
    }
  }
  return {
    kind: "ok",
    uploadIds: outcome.uploads.map((upload) => upload.uploadId),
    failures: outcome.failures,
  }
}

export function appendArtifactWarning(
  result: WorkItemResult,
  failures: ReadonlyArray<ArtifactCaptureFailure>,
  prefix: string,
): WorkItemResult {
  if (failures.length === 0) return result
  return appendMessage(result, `${prefix}: ${summarizeCaptureFailures(failures)}`)
}

export function withArtifactFailure(
  result: WorkItemResult,
  status: WorkItemResult["status"],
  message: string,
): WorkItemResult {
  return appendMessage({ ...result, status }, message)
}

function appendMessage(result: WorkItemResult, addition: string): WorkItemResult {
  const prefix = result.message ? result.message + "; " : ""
  return { ...result, message: (prefix + addition).slice(0, MAX_MESSAGE_LEN) }
}
