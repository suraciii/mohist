#!/usr/bin/env node
import { SocketModeClient } from "@slack/socket-mode"
import { WebClient } from "@slack/web-api"
import { SlackAdapter } from "./adapter.js"
import { HttpAdapterTransport } from "./transport.js"
import type { SocketClient } from "./types.js"

const controller = new AbortController()
process.on("SIGINT", () => controller.abort())
process.on("SIGTERM", () => controller.abort())

const adapter = new SlackAdapter({
  adapterId: env("ADAPTER_ID") ?? `mohist-slack-${process.pid}`,
  transport: new HttpAdapterTransport({
    serverUrl: env("SERVER_URL") ?? "http://localhost:3456",
    operatorToken: requiredEnv("MOHIST_OPERATOR_TOKEN", "OPERATOR_TOKEN"),
  }),
  socketFactory: (appToken) => new SocketModeClient({ appToken }) as unknown as SocketClient,
  webFactory: (botToken) => new WebClient(botToken),
  heartbeatIntervalMs: positiveNumberEnv("HEARTBEAT_INTERVAL_MS") ?? 15_000,
  deliveryPollIntervalMs: positiveNumberEnv("DELIVERY_POLL_INTERVAL_MS") ?? 1_000,
  discoveryIntervalMs: positiveNumberEnv("DISCOVERY_POLL_INTERVAL_MS") ?? 15_000,
  maxInFlight: positiveNumberEnv("MAX_IN_FLIGHT") ?? 8,
})
await adapter.start(controller.signal)
await new Promise<void>((resolve) => controller.signal.addEventListener("abort", () => resolve(), { once: true }))

function requiredEnv(name: string, fallback?: string): string {
  const value = env(name) ?? (fallback ? env(fallback) : undefined)
  if (!value) throw new Error(`${name} is required`)
  return value
}

function env(name: string) {
  return process.env[name] || undefined
}

function positiveNumberEnv(name: string) {
  const value = env(name)
  const parsed = value ? Number(value) : NaN
  return Number.isFinite(parsed) && parsed > 0 ? Math.floor(parsed) : undefined
}
