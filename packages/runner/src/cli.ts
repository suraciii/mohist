#!/usr/bin/env node
import { hostname } from "node:os"
import { RunnerHost } from "./runtime/host.js"
import { defaultRunnerRoot } from "./runtime/workspace.js"
import { configureRunnerLogger } from "./system/logger.js"
import { resolveRunnerCredential } from "./system/runner-credential.js"

const controller = new AbortController()
process.on("SIGINT", () => controller.abort())
process.on("SIGTERM", () => controller.abort())

const logger = configureRunnerLogger()
try {
  const serverUrl = env("SERVER_URL") ?? env("ServerUrl") ?? "http://localhost:3456"
  const runnerId = env("RUNNER_ID") ?? env("RunnerId") ?? `runner-${hostname()}`
  const runnerRoot = env("RUNNER_ROOT") ?? env("RunnerRoot") ?? defaultRunnerRoot()
  const hostnameValue = hostname()
  // Install registration: a fresh runner exchanges the one-time
  // enrollment token (injected by `mo install runner`) for its own
  // machine credential; afterwards the persisted credential is used.
  // A failed registration is fatal so systemd restarts the runner and
  // the error stays visible in the journal.
  const credential = await resolveRunnerCredential({
    serverUrl,
    runnerId,
    runnerRoot,
    hostname: hostnameValue,
    enrollmentToken: env("MOHIST_ENROLLMENT_TOKEN"),
    signal: controller.signal,
  })
  if (!credential) {
    logger.warn("no runner credential and no MOHIST_ENROLLMENT_TOKEN; server requests will be unauthenticated")
  }
  await new RunnerHost({
    serverUrl,
    runnerId,
    projectId: env("PROJECT_ID") ?? env("ProjectId"),
    runnerRoot,
    pollIntervalMs: numberEnv("POLL_INTERVAL_MS") ?? 1000,
    heartbeatIntervalMs: numberEnv("HEARTBEAT_INTERVAL_MS") ?? 15_000,
    dispatchLivenessProbeIntervalMs: numberEnv("DISPATCH_LIVENESS_PROBE_INTERVAL_MS") ?? 10_000,
    cleanupConvergenceIntervalMs: positiveNumberEnv("CLEANUP_CONVERGENCE_INTERVAL_MS") ?? 5 * 60_000,
    cleanupLoopIntervalMs: positiveNumberEnv("CLEANUP_LOOP_INTERVAL_MS") ?? 2 * 60_000,
    credential: credential ?? undefined,
  }).run(controller.signal)
} finally {
  await logger.flush()
}

function env(name: string) {
  return process.env[name] || undefined
}

function numberEnv(name: string) {
  const value = env(name)
  if (!value) return undefined
  const parsed = Number(value)
  return Number.isFinite(parsed) ? parsed : undefined
}

function positiveNumberEnv(name: string) {
  const parsed = numberEnv(name)
  return parsed !== undefined && parsed > 0 ? Math.floor(parsed) : undefined
}
