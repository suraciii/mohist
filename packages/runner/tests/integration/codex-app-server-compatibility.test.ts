import { execFileSync } from 'node:child_process'
import { mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { createSpawnedCodexServer, type CodexServerHandle } from '../../src/runtime/codex/server-process.js'
import { CODEX_INITIALIZE_PARAMS } from '../../src/runtime/codex/protocol-types.js'
import { CODEX_SUPPORTED_VERSION_RANGE } from '../../src/runtime/codex/types.js'
import {
  performCodexInitialization,
  codexInitializationTransportFromHandle,
} from '../../src/runtime/codex/initialization.js'
import { isCodexVersionSupported } from '../../src/runtime/codex/readiness.js'
import { normalizeCodexNotification } from '../../src/runtime/codex/turn-events.js'

const FIXED_CLIENT_USER_MESSAGE_ID = 'mohist-codex-compatibility-smoke-v1'
const FIXED_SENTINEL = 'codex-compat-ok'

interface SmokeAvailability {
  readonly available: boolean
  readonly reason: string
  readonly codexHome: string | null
}

function detectSmokeAvailability(): SmokeAvailability {
  const configuredHome = process.env.MOHIST_CODEX_SMOKE_HOME
  if (!configuredHome) {
    return {
      available: false,
      reason: 'MOHIST_CODEX_SMOKE_HOME is not configured with a dedicated managed Codex home',
      codexHome: null,
    }
  }
  const codexHome = resolve(configuredHome)
  if (resolve(process.env.HOME ?? '', '.codex') === codexHome) {
    return {
      available: false,
      reason: 'MOHIST_CODEX_SMOKE_HOME points at the personal default Codex home',
      codexHome: null,
    }
  }
  try {
    const output = execFileSync('codex', ['--version'], {
      encoding: 'utf8',
      timeout: 5_000,
      env: { ...process.env, CODEX_HOME: codexHome },
    })
    const version = output.match(/\b\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?\b/)?.[0]
    if (!version) {
      return { available: false, reason: 'codex --version did not return a version', codexHome: null }
    }
    if (!isCodexVersionSupported(version, CODEX_SUPPORTED_VERSION_RANGE.min, CODEX_SUPPORTED_VERSION_RANGE.max)) {
      return {
        available: false,
        reason: `installed Codex ${version} is outside ${CODEX_SUPPORTED_VERSION_RANGE.min} <= version < ${CODEX_SUPPORTED_VERSION_RANGE.max}`,
        codexHome: null,
      }
    }
  } catch (error) {
    return {
      available: false,
      reason: `codex CLI is unavailable: ${error instanceof Error ? error.message : String(error)}`,
      codexHome: null,
    }
  }
  return { available: true, reason: '', codexHome }
}

function objectValue(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : {}
}

function nestedObject(value: unknown, key: string): Record<string, unknown> {
  return objectValue(objectValue(value)[key])
}

function stringValue(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null
}

function modelIdFromResponse(value: unknown): string | null {
  const root = objectValue(value)
  const data = Array.isArray(root.data) ? root.data : Array.isArray(root.models) ? root.models : []
  const first = objectValue(data[0])
  return stringValue(first.id) ?? stringValue(first.modelId)
}

function threadIdFromResponse(value: unknown): string | null {
  const thread = nestedObject(value, 'thread')
  const root = objectValue(value)
  return stringValue(thread.id) ?? stringValue(thread.threadId) ?? stringValue(root.threadId)
}

function turnIdFromResponse(value: unknown): string | null {
  const turn = nestedObject(value, 'turn')
  const root = objectValue(value)
  return stringValue(turn.id) ?? stringValue(turn.turnId) ?? stringValue(root.turnId)
}

function assistantTextFromNotifications(notifications: readonly unknown[]): string {
  let text = ''
  for (const message of notifications) {
    const event = normalizeCodexNotification(message)
    if (!event || typeof event !== 'object') continue
    const candidate = event as { type?: unknown; text?: unknown; delta?: unknown }
    if (candidate.type !== 'agentMessage') continue
    if (typeof candidate.text !== 'string') continue
    // Deltas arrive as fragments; completed items arrive whole. Concatenating
    // both is safe because the completed item re-states the full text.
    text += candidate.text
  }
  return text
}

function waitForCompletion(handle: CodexServerHandle, threadId: string, turnId: string): Promise<unknown> {
  return new Promise((resolveCompletion, reject) => {
    const timeout = setTimeout(() => {
      unsubscribe()
      reject(new Error(`timed out waiting for turn/completed for ${threadId}/${turnId}`))
    }, 120_000)
    const unsubscribe = handle.subscribe((message) => {
      const envelope = objectValue(message)
      if (envelope.method === 'protocol-failure') {
        clearTimeout(timeout)
        unsubscribe()
        reject(new Error('Codex app-server reported a protocol failure during compatibility smoke'))
        return
      }
      if (envelope.method !== 'turn/completed') return
      const params = objectValue(envelope.params)
      const eventTurn = nestedObject(params, 'turn')
      const eventThreadId = stringValue(params.threadId) ?? stringValue(eventTurn.threadId)
      const eventTurnId = stringValue(params.turnId) ?? stringValue(eventTurn.id) ?? stringValue(eventTurn.turnId)
      if (eventThreadId !== threadId || eventTurnId !== turnId) return
      clearTimeout(timeout)
      unsubscribe()
      resolveCompletion(message)
    })
  })
}

const smoke = detectSmokeAvailability()
describe.skipIf(!smoke.available)(
  `Codex app-server compatibility smoke${smoke.reason ? ` (skipped: ${smoke.reason})` : ''}`,
  () => {
    it('initializes, discovers a model, and completes one fixed-key no-tool turn without mutating the workspace', async () => {
      const workspace = await mkdtemp(join(tmpdir(), 'mohist-codex-smoke-workspace-'))
      const sentinel = join(workspace, 'sentinel.txt')
      await writeFile(sentinel, 'unchanged\n', 'utf8')
      const beforeEntries = await readdir(workspace)
      const handle = await createSpawnedCodexServer({
        codexHome: smoke.codexHome!,
        cwd: workspace,
        environment: { ...process.env, CODEX_HOME: smoke.codexHome! },
      })
      const notifications: unknown[] = []
      const unsubscribe = handle.subscribe((message) => notifications.push(message))
      try {
        const initialization = await performCodexInitialization(codexInitializationTransportFromHandle(handle), {
          managedCodexHome: smoke.codexHome!,
          startupTimeoutMs: 10_000,
        })
        expect(initialization.ok).toBe(true)
        expect(CODEX_INITIALIZE_PARAMS.capabilities).toBeNull()

        const models = await handle.send({ id: 2, method: 'model/list', params: { limit: 1, includeHidden: false } })
        const modelId = modelIdFromResponse(models)
        expect(modelId).toBeTruthy()

        const thread = await handle.send({
          id: 3,
          method: 'thread/start',
          params: {
            cwd: workspace,
            approvalPolicy: 'never',
            sandbox: 'danger-full-access',
            model: modelId,
            ephemeral: false,
          },
        })
        const threadId = threadIdFromResponse(thread)
        expect(threadId).toBeTruthy()

        const turn = await handle.send({
          id: 4,
          method: 'turn/start',
          params: {
            threadId,
            input: [
              {
                type: 'text',
                text: `Respond exactly with ${FIXED_SENTINEL}. Do not call tools or modify files.`,
                text_elements: [],
              },
            ],
            clientUserMessageId: FIXED_CLIENT_USER_MESSAGE_ID,
            model: modelId,
          },
        })
        const turnId = turnIdFromResponse(turn)
        expect(turnId).toBeTruthy()

        const completed = await waitForCompletion(handle, threadId!, turnId!)
        const completedParams = objectValue(objectValue(completed).params)
        const completedTurn = nestedObject(completedParams, 'turn')
        expect(completedParams.status ?? completedTurn.status).toBe('completed')
        expect(assistantTextFromNotifications(notifications)).toContain(FIXED_SENTINEL)
        expect(
          notifications.some((message) => {
            const envelope = objectValue(message)
            return typeof envelope.id === 'number' && typeof envelope.method === 'string'
          }),
        ).toBe(false)
        expect(
          notifications.some((message) => {
            const envelope = objectValue(message)
            if (envelope.method !== 'item/started' && envelope.method !== 'item/completed') return false
            const item = nestedObject(envelope.params, 'item')
            return ['commandExecution', 'mcpToolCall', 'dynamicToolCall'].includes(String(item.type))
          }),
        ).toBe(false)
        expect(await readFile(sentinel, 'utf8')).toBe('unchanged\n')
        expect(await readdir(workspace)).toEqual(beforeEntries)
      } finally {
        unsubscribe()
        await handle.close()
        await rm(workspace, { recursive: true, force: true })
      }
    })
  },
)
