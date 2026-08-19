import { randomUUID } from 'node:crypto'
import WebSocket, { type ClientOptions } from 'ws'

export const RUNNER_CONTROL_MAX_MESSAGE_BYTES = 4 * 1024 * 1024
export const RUNNER_CONTROL_HANDSHAKE_TIMEOUT_MS = 15_000

export interface RunnerControlSocket extends WebSocket {}

export interface RunnerControlSocketAttempt {
  readonly socket: RunnerControlSocket
  readonly connectionId: string
}

export type RunnerControlSocketFactory = (url: string, credential: string | null) => RunnerControlSocketAttempt

export function createRunnerControlSocket(url: string, credential: string | null): RunnerControlSocketAttempt {
  const connectionId = randomUUID().toLowerCase()
  return {
    connectionId,
    socket: new WebSocket(url, runnerControlSocketOptions(connectionId, credential)),
  }
}

export function runnerControlSocketOptions(connectionId: string, credential: string | null): ClientOptions {
  const headers: ClientOptions['headers'] = { 'X-Runner-Connection-Id': connectionId }
  if (credential) headers.Authorization = `Bearer ${credential}`
  return {
    headers,
    maxPayload: RUNNER_CONTROL_MAX_MESSAGE_BYTES,
    handshakeTimeout: RUNNER_CONTROL_HANDSHAKE_TIMEOUT_MS,
    perMessageDeflate: false,
  }
}
