#!/usr/bin/env node
import { SocketModeClient } from "@slack/socket-mode"
import { WebClient, type FetchFunction } from "@slack/web-api"
import { fetch as undiciFetch, ProxyAgent, type RequestInit as UndiciRequestInit } from "undici"
import { readFile } from "node:fs/promises"
import { pathToFileURL } from "node:url"
import { SlackAdapter } from "./adapter.js"
import { HttpAdapterTransport } from "./transport.js"
import { configureSlackLogger } from "./logger.js"
import type { SocketClient, SocketEvent, SocketHello } from "./types.js"

export interface SlackCliOptions {
  readonly adapterId: string
  readonly serverUrl: string
  readonly operatorToken: string
  readonly operatorId: string
  readonly serverFetch?: typeof fetch
  readonly slackFetch?: FetchFunction
  readonly slackProxyUrl?: string
  readonly heartbeatIntervalMs?: number
  readonly deliveryPollIntervalMs?: number
  readonly discoveryIntervalMs?: number
  readonly maxInFlight?: number
}

export type OperatorCredentialFileReader = (path: string) => Promise<string>

export const DEFAULT_OPERATOR_ID = "mohist-slack"

const PROXIED_CLIENT_PING_TIMEOUT_MS = 24 * 60 * 60 * 1_000
const SOCKET_RECONNECT_MAX_DELAY_MS = 30_000

export function createSlackAdapter(options: SlackCliOptions): SlackAdapter {
  const proxyUrl = options.slackProxyUrl?.trim()
  const dispatcher = proxyUrl ? new ProxyAgent(proxyUrl) : undefined
  const slackFetch = options.slackFetch ?? (dispatcher
    ? (input: string | URL, init?: Parameters<FetchFunction>[1]) => undiciFetch(input, { ...(init as UndiciRequestInit | undefined), dispatcher })
    : undefined)

  return new SlackAdapter({
    adapterId: options.adapterId,
    transport: new HttpAdapterTransport({
      serverUrl: options.serverUrl,
      operatorToken: options.operatorToken,
      operatorId: options.operatorId,
      fetch: options.serverFetch,
    }),
    socketFactory: (appToken) => socketClient(appToken, dispatcher),
    webFactory: (botToken) => new WebClient(botToken, slackFetch ? { fetch: slackFetch } : undefined),
    heartbeatIntervalMs: options.heartbeatIntervalMs ?? 15_000,
    deliveryPollIntervalMs: options.deliveryPollIntervalMs ?? 1_000,
    discoveryIntervalMs: options.discoveryIntervalMs ?? 15_000,
    maxInFlight: options.maxInFlight ?? 8,
    dispose: dispatcher ? () => dispatcher.close() : undefined,
  })
}

function socketClient(appToken: string, dispatcher?: ProxyAgent): SocketClient {
  return new AdapterSocketClient(appToken, dispatcher)
}

class AdapterSocketClient implements SocketClient {
  private readonly client: SocketModeClient
  private reconnectTimer?: ReturnType<typeof setTimeout>
  private reconnectAttempts = 0
  private startPromise?: Promise<SocketHello>
  private hello?: { resolve: (appId: string) => void; reject: (error: Error) => void }
  private stopped = true

  constructor(appToken: string, dispatcher?: ProxyAgent) {
    this.client = new SocketModeClient({
      appToken,
      dispatcher,
      autoReconnectEnabled: false,
      ...(dispatcher ? { clientPingTimeout: PROXIED_CLIENT_PING_TIMEOUT_MS } : {}),
    })
    this.client.on("connected", () => { this.reconnectAttempts = 0 })
    this.client.on("disconnected", () => this.scheduleReconnect())
    this.client.on("ws_message", (message: string | ArrayBuffer, isBinary: boolean) => {
      if (isBinary || !this.hello) return
      const appId = helloAppId(message)
      if (appId) this.hello.resolve(appId)
      else this.hello.reject(new Error("Slack Socket hello did not contain app_id"))
    })
  }

  on(event: "slack_event", handler: (event: SocketEvent) => Promise<void>): void {
    this.client.on(event, handler)
  }

  onState(
    event: "connecting" | "connected" | "reconnecting" | "disconnected" | "error",
    handler: (error?: unknown) => void,
  ): void {
    this.client.on(event, handler)
  }

  async start(): Promise<SocketHello> {
    this.stopped = false
    return await this.startNow()
  }

  async disconnect(): Promise<void> {
    this.stopped = true
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer)
    this.reconnectTimer = undefined
    await this.client.disconnect()
  }

  private async startNow(): Promise<SocketHello> {
    if (this.startPromise) return await this.startPromise
    const hello = new Promise<string>((resolve, reject) => {
      this.hello = { resolve, reject }
    })
    const pending = this.client.start().then(async () => ({ appId: await hello }))
    this.startPromise = pending
    try {
      return await pending
    } finally {
      if (this.startPromise === pending) this.startPromise = undefined
      this.hello = undefined
    }
  }

  private scheduleReconnect(): void {
    if (this.stopped || this.reconnectTimer) return
    const delay = Math.min(1_000 * 2 ** this.reconnectAttempts, SOCKET_RECONNECT_MAX_DELAY_MS)
    this.reconnectAttempts += 1
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = undefined
      if (this.stopped) return
      void this.startNow().catch(() => this.scheduleReconnect())
    }, delay)
  }
}

function helloAppId(value: string | ArrayBuffer): string | undefined {
  try {
    const payload: unknown = JSON.parse(typeof value === "string" ? value : new TextDecoder().decode(value))
    if (!isRecord(payload) || payload.type !== "hello") return undefined
    if (typeof payload.app_id === "string") return payload.app_id
    const connectionInfo = payload.connection_info
    return isRecord(connectionInfo) && typeof connectionInfo.app_id === "string"
      ? connectionInfo.app_id
      : undefined
  } catch {
    return undefined
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value)
}

export function resolveOperatorId(environment: NodeJS.ProcessEnv = process.env): string {
  const configured = environment.MOHIST_OPERATOR_ID?.trim()
  return configured ? configured : DEFAULT_OPERATOR_ID
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

  const logger = configureSlackLogger()
  let adapter: SlackAdapter | undefined
  try {
    adapter = createSlackAdapter({
      adapterId: env("ADAPTER_ID") ?? `mohist-slack-${process.pid}`,
      serverUrl: env("SERVER_URL") ?? "http://localhost:3456",
      operatorToken: await resolveOperatorToken(),
      operatorId: resolveOperatorId(),
      slackProxyUrl: env("SLACK_PROXY_URL"),
      heartbeatIntervalMs: positiveNumberEnv("HEARTBEAT_INTERVAL_MS"),
      deliveryPollIntervalMs: positiveNumberEnv("DELIVERY_POLL_INTERVAL_MS"),
      discoveryIntervalMs: positiveNumberEnv("DISCOVERY_POLL_INTERVAL_MS"),
      maxInFlight: positiveNumberEnv("MAX_IN_FLIGHT"),
    })
    await adapter.start(controller.signal)
    await new Promise<void>((resolve) => controller.signal.addEventListener("abort", () => resolve(), { once: true }))
  } finally {
    await adapter?.stop()
    await logger.flush()
  }
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
