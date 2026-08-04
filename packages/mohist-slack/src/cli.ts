#!/usr/bin/env node
import { SocketModeClient } from "@slack/socket-mode"
import { WebClient, type FetchFunction } from "@slack/web-api"
import { readFile } from "node:fs/promises"
import { pathToFileURL } from "node:url"
import { SlackAdapter } from "./adapter.js"
import { HttpAdapterTransport } from "./transport.js"
import type { SocketClient } from "./types.js"

export interface SlackCliOptions {
  readonly adapterId: string
  readonly serverUrl: string
  readonly operatorToken: string
  readonly serverFetch?: typeof fetch
  readonly slackFetch?: FetchFunction
  readonly heartbeatIntervalMs?: number
  readonly deliveryPollIntervalMs?: number
  readonly discoveryIntervalMs?: number
  readonly maxInFlight?: number
}

export type OperatorCredentialFileReader = (path: string) => Promise<string>

export function createSlackAdapter(options: SlackCliOptions): SlackAdapter {
  return new SlackAdapter({
    adapterId: options.adapterId,
    transport: new HttpAdapterTransport({
      serverUrl: options.serverUrl,
      operatorToken: options.operatorToken,
      fetch: options.serverFetch,
    }),
    socketFactory: (appToken) => new SocketModeClient({ appToken }) as unknown as SocketClient,
    webFactory: (botToken) => new WebClient(botToken, options.slackFetch ? { fetch: options.slackFetch } : undefined),
    heartbeatIntervalMs: options.heartbeatIntervalMs ?? 15_000,
    deliveryPollIntervalMs: options.deliveryPollIntervalMs ?? 1_000,
    discoveryIntervalMs: options.discoveryIntervalMs ?? 15_000,
    maxInFlight: options.maxInFlight ?? 8,
  })
}

export async function resolveOperatorToken(
  environment: NodeJS.ProcessEnv = process.env,
  readCredentialFile: OperatorCredentialFileReader = (path) => readFile(path, "utf8"),
): Promise<string> {
  const directToken = firstNonBlank(environment.MOHIST_OPERATOR_TOKEN, environment.OPERATOR_TOKEN)
  if (directToken !== undefined)
    return validateOperatorToken(directToken)

  const credentialPath = environment.MOHIST_OPERATOR_TOKEN_PATH?.trim()
  if (!credentialPath)
    throw new Error("Mohist operator credential is required")

  let fileToken: string
  try {
    fileToken = await readCredentialFile(credentialPath)
  } catch {
    throw new Error("Mohist operator credential file could not be read")
  }
  return validateOperatorToken(fileToken)
}

export async function runCli() {
  const controller = new AbortController()
  process.on("SIGINT", () => controller.abort())
  process.on("SIGTERM", () => controller.abort())

  const adapter = createSlackAdapter({
    adapterId: env("ADAPTER_ID") ?? `mohist-slack-${process.pid}`,
    serverUrl: env("SERVER_URL") ?? "http://localhost:3456",
    operatorToken: await resolveOperatorToken(),
    heartbeatIntervalMs: positiveNumberEnv("HEARTBEAT_INTERVAL_MS"),
    deliveryPollIntervalMs: positiveNumberEnv("DELIVERY_POLL_INTERVAL_MS"),
    discoveryIntervalMs: positiveNumberEnv("DISCOVERY_POLL_INTERVAL_MS"),
    maxInFlight: positiveNumberEnv("MAX_IN_FLIGHT"),
  })
  await adapter.start(controller.signal)
  await new Promise<void>((resolve) => controller.signal.addEventListener("abort", () => resolve(), { once: true }))
}

function firstNonBlank(...values: Array<string | undefined>): string | undefined {
  return values.find((value) => value?.trim())?.trim()
}

function validateOperatorToken(value: string): string {
  const token = value.trim()
  if (!token)
    throw new Error("Mohist operator credential is invalid")
  return token
}

function env(name: string) {
  return process.env[name] || undefined
}

function positiveNumberEnv(name: string) {
  const value = env(name)
  const parsed = value ? Number(value) : NaN
  return Number.isFinite(parsed) && parsed > 0 ? Math.floor(parsed) : undefined
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  await runCli()
