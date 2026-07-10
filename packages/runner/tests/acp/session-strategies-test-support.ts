import { expect, vi } from "vitest"
import { acpAgentAction as executeAcpAgentAction } from "../../src/actions/acp-agent.js"
import { stringInput } from "../../src/core/json.js"

type AcpActionResult = Awaited<ReturnType<typeof executeAcpAgentAction>>

export async function runWithProviderDefaultModelWarning(
  context: Parameters<typeof executeAcpAgentAction>[0],
  drive?: (action: ReturnType<typeof executeAcpAgentAction>) => Promise<AcpActionResult>,
) {
  const warningSpy = vi.spyOn(console, "warn").mockClear().mockImplementation(() => undefined)
  try {
    const action = executeAcpAgentAction(context)
    const result = drive === undefined ? await action : await drive(action)

    expect(warningSpy).toHaveBeenCalledTimes(1)
    expect(warningSpy).toHaveBeenNthCalledWith(
      1,
      "mohist acp model not configured; using provider default",
      providerDefaultModelWarningContext(context),
    )
    return result
  } finally {
    warningSpy.mockRestore()
  }
}

export async function runWithRejectedRequestedModel(
  context: Parameters<typeof executeAcpAgentAction>[0],
  requestedModel: string,
  expected: { requestedModelSource: "agent.model"; requestedVariant?: string },
) {
  const errorSpy = vi.spyOn(console, "error").mockClear().mockImplementation(() => undefined)
  const warningSpy = vi.spyOn(console, "warn").mockClear().mockImplementation(() => undefined)
  try {
    const result = await executeAcpAgentAction(context)

    expect(errorSpy).toHaveBeenCalledTimes(1)
    expect(errorSpy).toHaveBeenNthCalledWith(
      1,
      "Error handling request",
      expect.objectContaining({ method: "session/set_model", params: expect.objectContaining({ modelId: requestedModel }) }),
      expect.objectContaining({ code: -32603, message: "Internal error" }),
    )
    expect(warningSpy).toHaveBeenCalledTimes(1)
    expect(warningSpy).toHaveBeenNthCalledWith(
      1,
      "mohist acp set requested model failed; provider default may be used",
      {
        ...providerDefaultModelWarningContext(context),
        requestedModel,
        requestedModelSource: expected.requestedModelSource,
        ...(expected.requestedVariant === undefined ? {} : { requestedVariant: expected.requestedVariant }),
        variantDelivered: false,
        error: "Internal error",
      },
    )
    return result
  } finally {
    warningSpy.mockRestore()
    errorSpy.mockRestore()
  }
}

function providerDefaultModelWarningContext(context: Parameters<typeof executeAcpAgentAction>[0]) {
  return {
    workflowRunId: context.workflowRunId,
    workId: context.workId,
    stage: context.stage,
    sessionName: stringInput(context.with, "session") ?? context.workId,
    requestedModel: null,
    requestedModelSource: "none",
  }
}
