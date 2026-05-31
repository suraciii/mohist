#!/usr/bin/env node
import { RunnerHost } from "./runtime/host.js"
import { defaultRunnerRoot } from "./runtime/workspace.js"

const controller = new AbortController()
process.on("SIGINT", () => controller.abort())
process.on("SIGTERM", () => controller.abort())

await new RunnerHost({
  serverUrl: env("SERVER_URL") ?? env("ServerUrl") ?? "http://localhost:3456",
  runnerId: env("RUNNER_ID") ?? env("RunnerId") ?? `runner-${process.env.COMPUTERNAME ?? process.env.HOSTNAME ?? process.pid}`,
  projectId: env("PROJECT_ID") ?? env("ProjectId"),
  runnerRoot: env("RUNNER_ROOT") ?? env("RunnerRoot") ?? defaultRunnerRoot(),
  pollIntervalMs: numberEnv("POLL_INTERVAL_MS") ?? 1000,
  heartbeatIntervalMs: numberEnv("HEARTBEAT_INTERVAL_MS") ?? 15_000,
}).run(controller.signal)

function env(name: string) {
  return process.env[name] || undefined
}

function numberEnv(name: string) {
  const value = env(name)
  if (!value) return undefined
  const parsed = Number(value)
  return Number.isFinite(parsed) ? parsed : undefined
}
