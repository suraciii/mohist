/**
 * Single sink for ops command output. The runner exposes ONE capture
 * funnel (`ActionContext.log.write(source, text)`) so every ops output
 * — workspace preparation, branch-stability checks, action body, and
 * clean-worktree enforcement — flows through one chokepoint that
 * masks credentials, assigns a monotonic `seq`, and buffers the entry.
 *
 * Two collaborating pieces live here:
 *
 *   - {@link TaskLogger}: the public sink. Calls `maskCredential`
 *     BEFORE assigning `seq` and BEFORE handing off to the buffer so
 *     no unmasked text ever leaves the sink. The sink is producer-only
 *     for entries; the {@link TaskLogCollector} owns the buffer.
 *
 *   - {@link TaskLogCollector}: the per-work buffer. Drops oldest head
 *     lines when the capacity ceiling is exceeded, keeps the most
 *     recent tail (error context), sets `truncated`, and never reuses
 *     a discarded `seq` so cursor pagination stays stable.
 *
 * The Phase 1 masker is intentionally minimal: a small set of
 * credential patterns (git remote URLs with embedded credentials,
 * common token prefixes). A hardened masker with encoding-variant
 * defense is future security work (design D5 / issue body Non-Goals).
 */

/**
 * Maximum number of buffered entries kept per work item. Beyond this
 * the oldest head lines are dropped; the most recent tail (which holds
 * the error context) is retained. Single named constant so the cap is
 * tunable from one place.
 */
export const MAX_TASK_LOG_LINES = 5_000

/**
 * One captured line ready for upload. The `seq` is the canonical
 * ordering key — monotonic within a work item, never reused after a
 * head-drop truncation. `timestamp` is taken from the injected clock
 * at write time; never from wall-clock inside production code.
 */
export interface TaskLogEntry {
  seq: number
  timestamp: Date
  source: string
  text: string
}

/**
 * Terminal-batch result of {@link TaskLogCollector.flush}.
 * `entries` is ordered by `seq` ascending and excludes any line the
 * collector dropped during head-drop truncation.
 */
export interface TaskLogBatch {
  entries: ReadonlyArray<TaskLogEntry>
  truncated: boolean
}

/**
 * Minimal Phase 1 credential masker. Replaces known credential
 * patterns with a redacted placeholder. The set is intentionally
 * small (git remote URLs with embedded credentials, common token
 * prefixes); the goal is to plug the most common leak paths while
 * leaving the future hardened masker free to expand the catalog.
 *
 * The masker is exposed so the executor wiring (T-003) can prime it
 * with additional runtime secrets (e.g. agent API keys) at host
 * startup; defaults cover the common case.
 */
export class CredentialMasker {
  private readonly additional: string[] = []

  /**
   * Add a literal secret to the runtime-known list. Substrings match
   * case-sensitively; the secret itself is never written to logs.
   */
  registerSecret(secret: string) {
    if (typeof secret !== "string" || secret.length < 6) return
    if (!this.additional.includes(secret)) this.additional.push(secret)
  }

  mask(text: string): string {
    if (typeof text !== "string" || text.length === 0) return text
    let result = maskKnownPatterns(text)
    for (const secret of this.additional) {
      // Replace each literal secret occurrence with the redaction
      // placeholder. We avoid the `String.replace` form with a string
      // needle because the secret may include characters that have
      // special meaning in the replacement string (`$`, `&`); a
      // callback sidesteps the issue and matches the line-oriented
      // emit model the rest of the file uses.
      let cursor = 0
      while (cursor <= result.length) {
        const found = result.indexOf(secret, cursor)
        if (found < 0) break
        result = result.slice(0, found) + REDACTED + result.slice(found + secret.length)
        cursor = found + REDACTED.length
      }
    }
    return result
  }
}

const REDACTED = "***"
const SECRET_ENV_NAME = /(?:TOKEN|PASSWORD|SECRET|API_KEY|ACCESS_KEY|AUTH)$/i

export function createCredentialMaskerFromEnvironment(env: NodeJS.ProcessEnv = process.env): CredentialMasker {
  const masker = new CredentialMasker()
  for (const [name, value] of Object.entries(env)) {
    if (!SECRET_ENV_NAME.test(name)) continue
    if (typeof value === "string") masker.registerSecret(value)
  }
  return masker
}

/**
 * Ordered list of `RegExp` patterns covering the known credential
 * shapes for Phase 1. Each pattern must include a capture of the
 * secret material so the replacement preserves the URL structure
 * (e.g. scheme/host stay visible, the credential slot is redacted).
 *
 * Order matters when patterns can overlap; the first match wins.
 */
const KNOWN_PATTERNS: ReadonlyArray<{ pattern: RegExp; replace: (match: string, ...groups: string[]) => string }> = [
  // https://user:pass@host/...  Redact the full user-info segment: token
  // remotes often put the token in the username slot (`token:x-oauth-basic`).
  {
    pattern: /\b([a-z][a-z0-9+.\-]*:\/\/)([^:\s/]+):([^@\s/]+)@/gi,
    replace: (_match, scheme: string) => `${scheme}${REDACTED}@`,
  },
  // https://token:@host/...  (empty password still means the username is secret)
  {
    pattern: /\b([a-z][a-z0-9+.\-]*:\/\/)([^:\s/]{12,}):@/gi,
    replace: (_match, scheme: string) => `${scheme}${REDACTED}@`,
  },
  // https://token@host/...  (personal access tokens embedded in git remotes)
  {
    pattern: /\b([a-z][a-z0-9+.\-]*:\/\/)([A-Za-z0-9._\-]{12,})@/g,
    replace: (_match, scheme: string) => `${scheme}${REDACTED}@`,
  },
  // Bearer tokens in HTTP responses / Authorization headers.
  {
    pattern: /\bBearer\s+[A-Za-z0-9._\-]+/g,
    replace: () => `Bearer ${REDACTED}`,
  },
  // GitHub PAT prefixes (ghp_, gho_, ghu_, ghs_, ghr_)
  {
    pattern: /\b(gh[pousr])_[A-Za-z0-9]{20,}/g,
    replace: (_match, prefix: string) => `${prefix}_${REDACTED}`,
  },
  // OpenAI / Anthropic / generic key shapes
  {
    pattern: /\bsk-[A-Za-z0-9_\-]{16,}/g,
    replace: () => `sk-${REDACTED}`,
  },
  // Basic auth header line: `Authorization: Basic <base64>`
  {
    pattern: /\b(Basic\s+)[A-Za-z0-9+/=]{8,}/g,
    replace: (_match, prefix: string) => `${prefix}${REDACTED}`,
  },
]

function maskKnownPatterns(text: string): string {
  let current = text
  for (const { pattern, replace } of KNOWN_PATTERNS) {
    current = current.replace(pattern, replace as (substring: string, ...args: string[]) => string)
  }
  return current
}

export interface TaskLoggerOptions {
  collector: TaskLogCollector
  masker?: CredentialMasker
}

export class TaskLogger {
  private readonly collector: TaskLogCollector
  private readonly masker: CredentialMasker

  constructor(options: TaskLoggerOptions) {
    this.collector = options.collector
    this.masker = options.masker ?? new CredentialMasker()
  }

  /**
   * Record one captured line. Masking happens BEFORE the seq is
   * assigned and BEFORE the entry is appended to the collector's
   * buffer so a raw credential can never land in the upload batch.
   * Returns the assigned `seq`.
   */
  write(source: string, text: string): number {
    const masked = this.masker.mask(text)
    return this.collector.append(source, masked)
  }

  flush(): TaskLogBatch {
    return this.collector.flush()
  }
}

/**
 * Per-work item collector. Single producer-side `append` (the sink
 * owns the only write path), `flush` returns the terminal batch.
 * The collector is **not** safe for concurrent producers — the only
 * caller is the sink which serialises writes through the executor
 * lifecycle.
 */
export class TaskLogCollector {
  private readonly entries: TaskLogEntry[] = []
  private readonly maxLines: number
  private readonly now: () => Date
  private nextSeq = 1
  private truncated = false
  private discardedCount = 0

  constructor(options: TaskLogCollectorOptions = {}) {
    this.maxLines = positiveInt(options.maxLines ?? MAX_TASK_LOG_LINES, MAX_TASK_LOG_LINES)
    this.now = options.now ?? (() => new Date())
  }

  /**
   * Append a (already masked) entry. Assigns the next `seq` value;
   * capacity overflow drops the oldest head line, marks `truncated`,
   * and **never** reuses the discarded seq.
   */
  append(source: string, text: string): number {
    const seq = this.nextSeq
    this.nextSeq += 1
    this.entries.push({ seq, timestamp: this.now(), source, text })
    if (this.entries.length > this.maxLines) {
      const overflow = this.entries.length - this.maxLines
      this.entries.splice(0, overflow)
      this.discardedCount += overflow
      this.truncated = true
    }
    return seq
  }

  /**
   * Number of head lines dropped since construction. Exposed for
   * tests so the assertion does not have to reconstruct the
   * truncation from the buffered array alone.
   */
  getDiscardedCount(): number {
    return this.discardedCount
  }

  /**
   * Current buffered size, excluding discarded head lines.
   */
  size(): number {
    return this.entries.length
  }

  /**
   * Whether the head was dropped at any point during this collector's
   * lifetime. Once set, it stays set: a later truncation does not
   * "undo" earlier head loss — the persisted flag reflects "any head
   * was dropped", which is what the web client surfaces.
   */
  isTruncated(): boolean {
    return this.truncated
  }

  /**
   * Returns the lowest `seq` currently buffered, or `null` when the
   * buffer is empty. After head-drop the value is strictly greater
   * than every discarded seq, so pagination remains stable.
   */
  firstSeq(): number | null {
    return this.entries.length === 0 ? null : this.entries[0]!.seq
  }

  /**
   * Terminal-batch snapshot. The collector is NOT cleared after a
   * flush — design D6 makes this a one-shot terminal batch per work
   * item, so the buffer is discarded by the host once the upload
   * completes. The returned array is a defensive copy.
   */
  flush(): TaskLogBatch {
    return {
      entries: this.entries.map((entry) => ({ ...entry })),
      truncated: this.truncated,
    }
  }
}

export interface TaskLogCollectorOptions {
  maxLines?: number
  now?: () => Date
}

function positiveInt(value: number, fallback: number): number {
  return Number.isFinite(value) && value > 0 ? Math.floor(value) : fallback
}
