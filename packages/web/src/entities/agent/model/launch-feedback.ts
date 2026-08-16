import type { AgentExecutabilityResult } from "../api/client";

export type AgentLaunchFeedbackKind =
  | "back-pressure"
  | "runner-offline"
  | "not-configured"
  | "not-executable"
  | "execution-unavailable"
  | "launch-conflict"
  | "launch-pending"
  | "execution-config-unresolvable";

export interface AgentLaunchFeedback {
  kind: AgentLaunchFeedbackKind;
  title: string;
  message: string;
  nextAction: string;
}

export function getAgentAvailabilityFeedback(
  waitingReason: string | null | undefined,
): AgentLaunchFeedback {
  switch (waitingReason) {
    case "no-online-runner":
      return {
        kind: "runner-offline",
        title: "Runner offline",
        message: "No available runner is online for this Agent.",
        nextAction: "Connect a runner, then retry the launch.",
      };
    case "capacity-full":
      return {
        kind: "back-pressure",
        title: "Launch waiting for capacity",
        message:
          "Runner capacity is full; this is an Availability wait, not a configuration gap.",
        nextAction: "Wait for a runner slot to free up, then retry the launch.",
      };
    case "concurrency-limit":
      return {
        kind: "back-pressure",
        title: "Launch waiting for capacity",
        message:
          "This Agent is at its concurrency limit; active work must finish before another run starts.",
        nextAction: "Wait for an active run to finish, then retry the launch.",
      };
    case "dispatch-pending":
    default:
      return {
        kind: "back-pressure",
        title: "Launch waiting for dispatch",
        message:
          "The launch is waiting for dispatch and is not a configuration gap.",
        nextAction:
          "Wait for dispatch to complete, then retry with the same launch intent if needed.",
      };
  }
}

type LaunchErrorLike = {
  code?: unknown;
  message?: unknown;
  status?: unknown;
  details?: unknown;
  data?: unknown;
};

export function getAgentLaunchErrorFeedback(
  error: unknown,
  executability?: AgentExecutabilityResult | null,
): AgentLaunchFeedback | null {
  const candidate = error as LaunchErrorLike | null;
  const code =
    typeof candidate?.code === "string" ? candidate.code.toLowerCase() : "";
  const message =
    typeof candidate?.message === "string"
      ? candidate.message.toLowerCase()
      : "";
  const status = typeof candidate?.status === "number" ? candidate.status : 0;

  if (code === "execution_config_unresolvable") {
    return {
      kind: "execution-config-unresolvable",
      title: "Execution configuration is required",
      message: "This task does not have a resolvable execution configuration.",
      nextAction:
        "Choose a Runtime and Model here, or configure the Project default execution configuration, then retry.",
    };
  }

  if (code === "launch_setup_pending") {
    return {
      kind: "launch-pending",
      title: "Launch is still converging",
      message: "The server is still determining the launch outcome.",
      nextAction:
        "Retry with the same Idempotency-Key so the original outcome can be recovered.",
    };
  }

  if (
    code === "launch_idempotency_conflict" ||
    (status === 409 && message.includes("idempotency"))
  ) {
    return {
      kind: "launch-conflict",
      title: "Launch request conflicts with an earlier attempt",
      message:
        "This Idempotency-Key is already associated with different task details.",
      nextAction:
        "Keep the original task details or start a new launch with a new key.",
    };
  }

  if (
    code === "agent_not_configured" ||
    executability?.state === "not-configured"
  ) {
    return {
      kind: "not-configured",
      title: "Agent is not configured",
      message: "The server has not accepted this Agent's execution definition.",
      nextAction:
        "Fix the listed gaps in Agent settings, then retry the launch.",
    };
  }

  if (
    code === "agent_not_executable" ||
    executability?.state === "not-executable"
  ) {
    return {
      kind: "not-executable",
      title: "Agent is not executable",
      message:
        "The current execution configuration was rejected by the runtime.",
      nextAction: "Update the Agent execution settings, then retry the launch.",
    };
  }

  if (
    code === "no_available_runner" ||
    code === "no-online-runner" ||
    message.includes("no available runner") ||
    message.includes("no runner is online")
  ) {
    return getAgentAvailabilityFeedback("no-online-runner");
  }

  if (
    code === "capacity-full" ||
    code === "concurrency-limit" ||
    code === "dispatch-pending" ||
    message.includes("capacity full") ||
    message.includes("concurrency limit") ||
    message.includes("dispatch pending")
  ) {
    return getAgentAvailabilityFeedback(
      code === "concurrency-limit"
        ? "concurrency-limit"
        : code === "dispatch-pending"
          ? "dispatch-pending"
          : "capacity-full",
    );
  }

  if (
    code === "external_agent_unavailable" ||
    code === "runtime-unavailable" ||
    code === "execution-unavailable" ||
    message.includes("external agent unavailable") ||
    message.includes("external agent is unavailable") ||
    message.includes("runtime unavailable") ||
    message.includes("execution unavailable") ||
    (message.includes("backend") && message.includes("unavailable"))
  ) {
    return {
      kind: "execution-unavailable",
      title: "Execution backend unavailable",
      message:
        "The configured execution backend cannot run right now; the external agent is unavailable.",
      nextAction:
        "Wait for the runtime or provider to recover, then retry the launch.",
    };
  }

  return null;
}
