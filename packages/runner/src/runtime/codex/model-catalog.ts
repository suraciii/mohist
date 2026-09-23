/**
 * Codex model catalog discovery and execution-configuration mapping.
 *
 * Codex exposes model discovery as a cursor-paged `model/list` method. This
 * module consumes every page, adapts the native reasoning-effort vocabulary
 * to Mohist's canonical vocabulary, and keeps the last complete snapshot when
 * a later refresh cannot produce a complete catalog.
 *
 * The transport is deliberately small so the catalog can be exercised with a
 * fake app-server in unit tests. It never retries a page: a lost response
 * leaves the refresh failed rather than risking an ambiguous request.
 */

import { createHash as createSha256 } from 'node:crypto'
import { redactCodexCredentialString } from './credential.js'
import type {
  CodexCanonicalReasoningEffort,
  CodexCatalog,
  CodexDiagnostic,
  CodexModelDescriptor,
  CodexNativeReasoningEffort,
  CodexResult,
} from './types.js'
import { CODEX_CANONICAL_REASONING_EFFORTS, CODEX_NATIVE_REASONING_EFFORTS } from './types.js'
import { normalizeInvalidInputCodex, normalizeUnsupportedExecutionConfigurationCodex } from './errors.js'
import {
  isCodexModelListResult,
  type CodexJsonRpcSuccess,
  type CodexModelListParams,
  type CodexModelListResult,
} from './protocol-types.js'

const DEFAULT_CODEX_MODEL_PAGE_SIZE = 256
const DEFAULT_CODEX_MAX_MODEL_PAGES = 128

export interface CodexModelListTransport {
  send<P, R>(request: { readonly method: 'model/list'; readonly params?: P; readonly id: number }): Promise<R>
}

export interface CodexModelCatalogOptions {
  readonly pageSize?: number
  readonly maxPages?: number
  /** Starts after the initialize request id when no shared allocator is supplied. */
  readonly firstRequestId?: number
  /** Shared app-server request-id allocator owned by the runtime generation. */
  readonly nextRequestId?: () => number
}

export interface CodexCatalogRefreshResult {
  /** True only when this refresh assembled a complete, non-empty snapshot. */
  readonly ok: boolean
  /** The new snapshot on success, or the retained last snapshot on failure. */
  readonly catalog: CodexCatalog | null
  /** True only when the complete snapshot content changed. */
  readonly changed: boolean
  readonly diagnostics: readonly CodexDiagnostic[]
}

interface CodexCatalogRefreshStore {
  loadCatalog(): Promise<CodexCatalog | null>
  refreshCatalog(): Promise<CodexCatalogRefreshResult>
  catalog(): CodexCatalog | null
  diagnostic(): CodexDiagnostic | null
}

export interface CodexTurnConfigurationInput {
  readonly model?: unknown
  readonly reasoningEffort?: unknown
  readonly variant?: unknown
  readonly unknownKeys?: readonly string[]
}

export interface CodexResolvedTurnConfiguration {
  readonly model: string | null
  readonly reasoningEffort: CodexCanonicalReasoningEffort | null
  readonly nativeReasoningEffort: CodexNativeReasoningEffort | null
}

/**
 * Adapt one native Codex reasoning effort. Unknown values are intentionally
 * omitted from the published catalog; catalog discovery records the
 * omitted value as a diagnostic.
 */
export function mapCodexNativeReasoningEffort(value: unknown): CodexCanonicalReasoningEffort | null {
  if (value === 'none') return 'off'
  if ((CODEX_CANONICAL_REASONING_EFFORTS as readonly string[]).includes(value as string)) {
    return value as CodexCanonicalReasoningEffort
  }
  return null
}

/** Map a canonical Mohist effort to the exact native Codex spelling. */
export function mapCodexCanonicalReasoningEffort(value: unknown): CodexNativeReasoningEffort | null {
  if (value === 'off') return 'none'
  if ((CODEX_NATIVE_REASONING_EFFORTS as readonly string[]).includes(value as string)) {
    return value as CodexNativeReasoningEffort
  }
  return null
}

/** Compatibility aliases that read naturally at call sites. */
export const canonicalReasoningEffortForCodex = mapCodexNativeReasoningEffort
export const nativeReasoningEffortForCodex = mapCodexCanonicalReasoningEffort

/**
 * Cursor-paged catalog manager. Its snapshot is replaced only by a complete,
 * non-empty refresh. A failed refresh leaves the old snapshot available while
 * exposing the current failure through `diagnostic()` and the refresh result.
 */
export class CodexCatalogManager implements CodexCatalogRefreshStore {
  private readonly pageSize: number
  private readonly maxPages: number
  private readonly requestIdAllocator: (() => number) | null
  private nextRequestId: number
  private snapshot: CodexCatalog | null = null
  private currentDiagnostic: CodexDiagnostic | null = null

  constructor(
    private readonly transport: CodexModelListTransport,
    options: CodexModelCatalogOptions = {},
  ) {
    this.pageSize = positiveBoundedNumber(options.pageSize, DEFAULT_CODEX_MODEL_PAGE_SIZE)
    this.maxPages = positiveBoundedNumber(options.maxPages, DEFAULT_CODEX_MAX_MODEL_PAGES)
    this.requestIdAllocator = options.nextRequestId ?? null
    this.nextRequestId = positiveRequestId(options.firstRequestId ?? 2)
  }

  catalog(): CodexCatalog | null {
    return this.snapshot
  }

  diagnostic(): CodexDiagnostic | null {
    return this.currentDiagnostic
  }

  async loadCatalog(): Promise<CodexCatalog | null> {
    const result = await this.refreshCatalog()
    return result.ok ? result.catalog : null
  }

  async refreshCatalog(): Promise<CodexCatalogRefreshResult> {
    const loaded = await this.loadCompleteCatalog()
    if (!loaded.ok) {
      this.currentDiagnostic = loaded.diagnostic
      return {
        ok: false,
        catalog: this.snapshot,
        changed: false,
        diagnostics: [loaded.diagnostic],
      }
    }

    const changed = !catalogsEqual(this.snapshot, loaded.catalog)
    this.snapshot = loaded.catalog
    this.currentDiagnostic = loaded.diagnostics[0] ?? null
    return {
      ok: true,
      catalog: this.snapshot,
      changed,
      diagnostics: loaded.diagnostics,
    }
  }

  private async loadCompleteCatalog(): Promise<
    | { readonly ok: true; readonly catalog: CodexCatalog; readonly diagnostics: readonly CodexDiagnostic[] }
    | { readonly ok: false; readonly diagnostic: CodexDiagnostic }
  > {
    const nativeModels: NativeModel[] = []
    const seenCursors = new Set<string>()
    const diagnostics: CodexDiagnostic[] = []
    let cursor: string | null = null

    for (let page = 0; page < this.maxPages; page += 1) {
      const params: CodexModelListParams = {
        limit: this.pageSize,
        includeHidden: false,
        ...(cursor === null ? {} : { cursor }),
      }
      let response: unknown
      try {
        response = await this.transport.send<CodexModelListParams, unknown>({
          id: this.takeRequestId(),
          method: 'model/list',
          params,
        })
      } catch (cause) {
        return {
          ok: false,
          diagnostic: catalogDiagnostic(
            'catalog-refresh-failed',
            `Codex model catalog refresh failed: ${errorMessage(cause)}`,
          ),
        }
      }

      const result = modelListResult(response)
      if (result === null) {
        return {
          ok: false,
          diagnostic: catalogDiagnostic(
            'catalog-protocol-invalid',
            'Codex model/list response did not match the locked catalog shape',
          ),
        }
      }
      for (const model of result.models) {
        const parsed = nativeModel(model)
        if (parsed === null) {
          diagnostics.push({
            severity: 'warning',
            code: 'catalog-model-invalid',
            message: 'Codex model/list contained a model without a valid string id; the entry was omitted',
          })
          continue
        }
        nativeModels.push(parsed)
      }

      if (result.complete) {
        const adapted = adaptModels(nativeModels, diagnostics)
        if (adapted.length === 0) {
          return {
            ok: false,
            diagnostic: catalogDiagnostic(
              'catalog-empty',
              'Codex model catalog refresh completed without a valid model',
            ),
          }
        }
        return {
          ok: true,
          catalog: {
            models: adapted,
            complete: true,
            capabilityRevision: capabilityRevisionForCodexCatalog(adapted),
          },
          diagnostics,
        }
      }

      const nextCursor = result.nextCursor
      if (typeof nextCursor !== 'string' || nextCursor.length === 0) {
        return {
          ok: false,
          diagnostic: catalogDiagnostic(
            'catalog-incomplete',
            'Codex model catalog page was not complete and did not provide a next cursor',
          ),
        }
      }
      if (seenCursors.has(nextCursor)) {
        return {
          ok: false,
          diagnostic: catalogDiagnostic(
            'catalog-pagination-loop',
            'Codex model catalog returned a cursor more than once; refusing an unbounded refresh',
          ),
        }
      }
      seenCursors.add(nextCursor)
      cursor = nextCursor
    }

    return {
      ok: false,
      diagnostic: catalogDiagnostic(
        'catalog-page-limit',
        `Codex model catalog exceeded the ${this.maxPages}-page refresh bound`,
      ),
    }
  }

  private takeRequestId(): number {
    if (this.requestIdAllocator) return this.requestIdAllocator()
    const id = this.nextRequestId
    this.nextRequestId = positiveRequestId(id + 1)
    return id
  }
}

/** Create a paged catalog manager over an app-server handle. */
export function createCodexModelCatalogLoader(
  transport: CodexModelListTransport,
  options?: CodexModelCatalogOptions,
): CodexCatalogManager {
  return new CodexCatalogManager(transport, options)
}

/** Alias emphasizing that the object retains a last-known-good snapshot. */
export const createCodexCatalogManager = createCodexModelCatalogLoader

/**
 * Validate execution configuration again immediately before `turn/start`.
 * The catalog is configuration evidence, not a guarantee that the provider
 * will accept the request, so this check belongs at the effect boundary too.
 */
export function validateCodexTurnConfiguration(
  catalog: CodexCatalog | null,
  input: CodexTurnConfigurationInput | null | undefined,
): CodexResult<CodexResolvedTurnConfiguration> {
  const options = input ?? {}
  const diagnostics: CodexDiagnostic[] = []
  if (options.variant !== undefined && options.variant !== null) {
    const error = normalizeUnsupportedExecutionConfigurationCodex(
      'Codex does not support execution variants; remove options.variant before starting the turn',
    )
    return { ok: false, error, diagnostics: error.diagnostics }
  }

  const unknownKeys = options.unknownKeys ?? []
  if (unknownKeys.length > 0) {
    diagnostics.push({
      severity: 'warning',
      code: 'unknown-execution-option',
      message: `Unknown Codex execution option(s) ignored: ${unknownKeys.join(', ')}`,
    })
  }

  if (catalog === null || catalog.models.length === 0) {
    const error = normalizeInvalidInputCodex(
      'Codex model catalog is not available; model and reasoning effort cannot be validated at turn/start',
    )
    return { ok: false, error, diagnostics: error.diagnostics }
  }

  const model = readOptionalString(options.model, 'options.model')
  if (!model.ok) return model
  const effort = readOptionalCanonicalEffort(options.reasoningEffort)
  if (!effort.ok) return effort

  const selectedModel = model.value === null ? null : catalog.models.find((entry) => entry.id === model.value)
  if (model.value !== null && selectedModel === undefined) {
    const error = normalizeInvalidInputCodex(`options.model '${model.value}' is not published by the Codex catalog`)
    return { ok: false, error, diagnostics: error.diagnostics }
  }

  if (effort.value === null) {
    return {
      ok: true,
      value: { model: model.value, reasoningEffort: null, nativeReasoningEffort: null },
      diagnostics,
    }
  }

  // An effort is meaningful only alongside the model whose capabilities were
  // published. This mirrors the frozen model/effort execution tuple and
  // prevents validating an effort against an arbitrary provider default.
  if (!selectedModel) {
    const error = normalizeInvalidInputCodex(
      'options.reasoningEffort requires an explicit model published by the Codex catalog',
    )
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  if (!selectedModel.reasoningEfforts.includes(effort.value)) {
    const error = normalizeInvalidInputCodex(
      `options.reasoningEffort '${effort.value}' is not published for Codex model '${selectedModel.id}'`,
    )
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  const nativeReasoningEffort = mapCodexCanonicalReasoningEffort(effort.value)
  if (nativeReasoningEffort === null) {
    const error = normalizeInvalidInputCodex(
      `options.reasoningEffort '${effort.value}' is not a canonical Codex reasoning effort`,
    )
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  return {
    ok: true,
    value: {
      model: model.value,
      reasoningEffort: effort.value,
      nativeReasoningEffort,
    },
    diagnostics,
  }
}

interface NativeModel {
  readonly id: string
  readonly displayName: string | null
  readonly reasoningEfforts: readonly string[]
  readonly defaultReasoningEffort: string | null
}

function nativeModel(value: unknown): NativeModel | null {
  if (!value || typeof value !== 'object') return null
  const candidate = value as {
    id?: unknown
    displayName?: unknown
    reasoningEfforts?: unknown
    supportedReasoningEfforts?: unknown
    defaultReasoningEffort?: unknown
  }
  if (typeof candidate.id !== 'string' || candidate.id.length === 0) return null
  const nativeEfforts = candidate.reasoningEfforts ?? candidate.supportedReasoningEfforts
  const reasoningEfforts = Array.isArray(nativeEfforts)
    ? nativeEfforts.flatMap((effort): string[] => {
        if (typeof effort === 'string') return [effort]
        if (
          effort &&
          typeof effort === 'object' &&
          typeof (effort as { reasoningEffort?: unknown }).reasoningEffort === 'string'
        ) {
          return [(effort as { reasoningEffort: string }).reasoningEffort]
        }
        return []
      })
    : []
  return {
    id: candidate.id,
    displayName: typeof candidate.displayName === 'string' ? candidate.displayName : null,
    reasoningEfforts,
    defaultReasoningEffort:
      typeof candidate.defaultReasoningEffort === 'string' ? candidate.defaultReasoningEffort : null,
  }
}

function adaptModels(nativeModels: readonly NativeModel[], diagnostics: CodexDiagnostic[]): CodexModelDescriptor[] {
  const merged = new Map<
    string,
    {
      displayName: string | null
      efforts: Set<CodexCanonicalReasoningEffort>
      defaultEffort: CodexCanonicalReasoningEffort | null
    }
  >()
  for (const model of nativeModels) {
    let entry = merged.get(model.id)
    if (!entry) {
      entry = { displayName: model.displayName, efforts: new Set(), defaultEffort: null }
      merged.set(model.id, entry)
    } else if (entry.displayName === null && model.displayName !== null) {
      entry.displayName = model.displayName
    }

    for (const nativeEffort of model.reasoningEfforts) {
      const canonical = mapCodexNativeReasoningEffort(nativeEffort)
      if (canonical === null) {
        diagnostics.push({
          severity: 'warning',
          code: 'catalog-unknown-reasoning-effort',
          message: `Codex model '${model.id}' advertised an unknown reasoning effort '${nativeEffort}'; it was not published`,
          details: { modelId: model.id, nativeEffort },
        })
        continue
      }
      entry.efforts.add(canonical)
    }

    if (model.defaultReasoningEffort !== null) {
      const canonical = mapCodexNativeReasoningEffort(model.defaultReasoningEffort)
      if (canonical === null) {
        diagnostics.push({
          severity: 'warning',
          code: 'catalog-unknown-default-reasoning-effort',
          message: `Codex model '${model.id}' advertised an unknown default reasoning effort '${model.defaultReasoningEffort}'; it was not published`,
          details: { modelId: model.id, nativeEffort: model.defaultReasoningEffort },
        })
      } else {
        entry.defaultEffort = canonical
        entry.efforts.add(canonical)
      }
    }
  }

  return [...merged.entries()]
    .sort(([left], [right]) => compareStrings(left, right))
    .map(([id, model]) => ({
      id,
      displayName: model.displayName,
      reasoningEfforts: orderCanonicalEfforts(model.efforts),
      defaultReasoningEffort: model.defaultEffort,
      supportsReasoningEffort: true,
    }))
}

function compareStrings(left: string, right: string): number {
  if (left < right) return -1
  if (left > right) return 1
  return 0
}

function orderCanonicalEfforts(values: Set<CodexCanonicalReasoningEffort>): CodexCanonicalReasoningEffort[] {
  return CODEX_CANONICAL_REASONING_EFFORTS.filter((effort) => values.has(effort))
}

function catalogsEqual(left: CodexCatalog | null, right: CodexCatalog): boolean {
  if (left === null) return false
  if (left.capabilityRevision !== right.capabilityRevision) return false
  if (left.models.length !== right.models.length) return false
  return left.models.every((model, index) => modelEqual(model, right.models[index] ?? null))
}

function modelEqual(left: CodexModelDescriptor, right: CodexModelDescriptor | null): boolean {
  if (right === null) return false
  return (
    left.id === right.id &&
    left.displayName === right.displayName &&
    left.defaultReasoningEffort === right.defaultReasoningEffort &&
    left.reasoningEfforts.length === right.reasoningEfforts.length &&
    left.reasoningEfforts.every((effort, index) => effort === right.reasoningEfforts[index])
  )
}

function capabilityRevisionForCodexCatalog(models: readonly CodexModelDescriptor[]): string {
  // Keep this payload aligned with the Runner registration catalog: display
  // names are presentation metadata, while model IDs and reasoning efforts
  // are the execution capabilities that invalidate a frozen dispatch.
  const modelList = models.map((model) => model.id)
  const reasoningEfforts = Object.fromEntries(models.map((model) => [model.id, [...model.reasoningEfforts]]))
  return sha256(JSON.stringify({ models: modelList, reasoningEfforts }))
}

function sha256(value: string): string {
  return createSha256('sha256').update(value).digest('hex')
}

function readOptionalString(value: unknown, field: string): CodexResult<string | null> {
  if (value === undefined || value === null) {
    return { ok: true, value: null, diagnostics: [] }
  }
  if (typeof value !== 'string' || value.length === 0) {
    const error = normalizeInvalidInputCodex(`${field} must be a non-empty string when present`)
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  return { ok: true, value, diagnostics: [] }
}

function readOptionalCanonicalEffort(value: unknown): CodexResult<CodexCanonicalReasoningEffort | null> {
  if (value === undefined || value === null) {
    return { ok: true, value: null, diagnostics: [] }
  }
  if (typeof value !== 'string' || !(CODEX_CANONICAL_REASONING_EFFORTS as readonly string[]).includes(value)) {
    const error = normalizeInvalidInputCodex(
      `options.reasoningEffort '${String(value)}' is not a canonical Codex reasoning effort`,
    )
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  return { ok: true, value: value as CodexCanonicalReasoningEffort, diagnostics: [] }
}

function modelListResult(value: unknown): CodexModelListResult | null {
  if (isCodexModelListResult(value)) {
    const result = value.result
    if (Array.isArray(result.data) && !Array.isArray(result.models)) {
      return { ...result, models: result.data, complete: result.complete ?? result.nextCursor == null }
    }
    return result
  }
  if (!value || typeof value !== 'object') return null
  const raw = value as { models?: unknown; data?: unknown; nextCursor?: unknown; complete?: unknown }
  const models = Array.isArray(raw.models) ? raw.models : Array.isArray(raw.data) ? raw.data : null
  if (!models) return null
  return {
    models: models as CodexModelListResult['models'],
    nextCursor: typeof raw.nextCursor === 'string' ? raw.nextCursor : null,
    complete: typeof raw.complete === 'boolean' ? raw.complete : raw.nextCursor == null,
  }
}

function catalogDiagnostic(code: string, message: string): CodexDiagnostic {
  return { severity: 'error', code, message: redactCodexCredentialString(message) }
}

function errorMessage(cause: unknown): string {
  if (cause instanceof Error) return redactCodexCredentialString(cause.message)
  if (typeof cause === 'string') return redactCodexCredentialString(cause)
  if (cause && typeof cause === 'object') {
    const message = (cause as { readonly message?: unknown }).message
    if (typeof message === 'string' && message.length > 0) return redactCodexCredentialString(message)
  }
  return 'unknown transport failure'
}

function positiveBoundedNumber(value: number | undefined, fallback: number): number {
  if (!Number.isFinite(value) || value === undefined || value <= 0) return fallback
  return Math.max(1, Math.floor(value))
}

function positiveRequestId(value: number): number {
  if (!Number.isSafeInteger(value) || value <= 0) return 1
  return value
}
