import { stringInput } from '../core/json.js'
import type { JsonObject } from '../core/types.js'

/**
 * Publication gate shared by every action that performs an external write
 * (`git push`, `gh pr create/edit`, `gh pr merge --auto`). The gate has two
 * parts:
 * 1. Strategy selection is explicit at the action boundary. Each action
 *    declares the publication strategies it implements; requesting any other
 *    strategy fails with an actionable result instead of silently falling back
 *    to another strategy. The push action's `full-clone` strategy explicitly
 *    materializes complete objects before it validates or pushes.
 * 2. Commit trailers and identities are validated before the external write.
 *    A payload containing a literal backslash-n escape (the #1076 dogfood
 *    failure mode, where an API-composed commit stored `\n` as two
 *    characters) is always rejected. Required trailers and author/committer
 *    identity are enforced when the action input configures them.
 */

export type PublicationStrategy = 'partial-git' | 'full-clone' | 'github-api'

export const PUBLICATION_STRATEGIES: readonly PublicationStrategy[] = ['partial-git', 'full-clone', 'github-api']

export type PublicationStrategyResolution =
  | { kind: 'ok'; strategy: PublicationStrategy }
  | { kind: 'unsupported'; requested: string; supported: readonly PublicationStrategy[] }

export function resolvePublicationStrategy(
  inputs: JsonObject,
  supported: readonly PublicationStrategy[],
  fallback: PublicationStrategy,
): PublicationStrategyResolution {
  const raw = stringInput(inputs, 'strategy')
  if (raw === undefined) return { kind: 'ok', strategy: fallback }
  const requested = raw.trim().toLowerCase()
  if (
    (PUBLICATION_STRATEGIES as readonly string[]).includes(requested) &&
    supported.includes(requested as PublicationStrategy)
  ) {
    return { kind: 'ok', strategy: requested as PublicationStrategy }
  }
  return { kind: 'unsupported', requested: raw, supported }
}

export function unsupportedStrategyMessage(
  action: string,
  requested: string,
  supported: readonly PublicationStrategy[],
): string {
  return (
    `Publication strategy '${requested}' is not supported by ${action}. ` +
    `Supported strategies: ${supported.join(', ')}. ` +
    `Choose one of the supported strategies explicitly; no external write was performed.`
  )
}

export interface PublicationPolicy {
  readonly requiredTrailers: readonly string[]
  readonly author?: string
  readonly committer?: string
}

/**
 * Reads the optional publication policy inputs. `requiredTrailers` accepts an
 * array of tokens or a comma-separated string; `author` / `committer` accept
 * a full `Name <email>` identity or a bare email address.
 */
export function publicationPolicyFromInputs(inputs: JsonObject): PublicationPolicy {
  return {
    requiredTrailers: requiredTrailersInput(inputs['requiredTrailers']),
    author: stringInput(inputs, 'author'),
    committer: stringInput(inputs, 'committer'),
  }
}

function requiredTrailersInput(value: unknown): string[] {
  if (Array.isArray(value)) {
    return value
      .filter((entry): entry is string => typeof entry === 'string')
      .map((entry) => entry.trim())
      .filter(Boolean)
  }
  if (typeof value === 'string') {
    return value
      .split(',')
      .map((entry) => entry.trim())
      .filter(Boolean)
  }
  return []
}

export type PublicationValidationErrorCode =
  | 'literal-escaped-newline'
  | 'missing-trailer'
  | 'malformed-trailer'
  | 'author-mismatch'
  | 'committer-mismatch'

export interface PublicationValidationError {
  readonly code: PublicationValidationErrorCode
  readonly message: string
}

const LITERAL_ESCAPED_NEWLINE = /\\[nr]/
const TRAILER_LINE = /^([A-Za-z][A-Za-z0-9-]*):[ \t]+(\S.*)$/

/**
 * Validates one commit-message-shaped payload. A literal backslash-n (or
 * backslash-r) escape is always rejected: it means the payload was
 * double-encoded instead of carrying real newline characters. When
 * `requiredTrailers` is non-empty, the final paragraph of the message must
 * consist entirely of well-formed `Token: value` trailer lines and must
 * contain every required token.
 */
export function validateMessageTrailers(
  message: string,
  requiredTrailers: readonly string[] = [],
): PublicationValidationError[] {
  if (LITERAL_ESCAPED_NEWLINE.test(message)) {
    return [
      {
        code: 'literal-escaped-newline',
        message: `Message contains a literal escaped newline ('\\n' or '\\r' as two characters). Rebuild the payload with real newline characters instead of escape sequences.`,
      },
    ]
  }
  if (requiredTrailers.length === 0) return []
  const errors: PublicationValidationError[] = []
  const present = new Set<string>()
  for (const line of trailerBlock(message)) {
    const match = TRAILER_LINE.exec(line)
    if (!match) {
      errors.push({
        code: 'malformed-trailer',
        message: `Malformed trailer line '${line}': the trailing paragraph must consist of 'Token: value' lines.`,
      })
      continue
    }
    present.add(match[1]!.toLowerCase())
  }
  for (const required of requiredTrailers) {
    if (!present.has(required.toLowerCase())) {
      errors.push({ code: 'missing-trailer', message: `Required trailer '${required}' is missing.` })
    }
  }
  return errors
}

function trailerBlock(message: string): string[] {
  const lines = message.replace(/\r\n/g, '\n').split('\n')
  let end = lines.length
  while (end > 0 && lines[end - 1]!.trim() === '') end--
  let start = end
  while (start > 0 && lines[start - 1]!.trim() !== '') start--
  return lines.slice(start, end)
}

export interface PublicationIdentity {
  readonly name: string
  readonly email: string
}

/**
 * Matches a configured identity against an actual commit identity. A
 * `Name <email>` expectation requires an exact name match and a
 * case-insensitive email match; a bare expectation is treated as an email.
 */
export function validateIdentity(
  expected: string,
  actual: PublicationIdentity,
  role: 'author' | 'committer',
): PublicationValidationError | null {
  const mismatch = (): PublicationValidationError => ({
    code: role === 'author' ? 'author-mismatch' : 'committer-mismatch',
    message: `Commit ${role} '${actual.name} <${actual.email}>' does not match the configured ${role} identity '${expected}'.`,
  })
  const parsed = /^([^<>]*)<([^<>]+)>$/.exec(expected.trim())
  if (parsed) {
    const name = parsed[1]!.trim()
    const email = parsed[2]!.trim()
    if (name === actual.name && email.toLowerCase() === actual.email.toLowerCase()) return null
    return mismatch()
  }
  if (expected.trim().toLowerCase() === actual.email.toLowerCase()) return null
  return mismatch()
}

export interface PublicationCommit {
  readonly sha: string
  readonly author: PublicationIdentity
  readonly committer: PublicationIdentity
  readonly message: string
}

/** NUL is excluded from Git commit metadata, including commit messages. */
export const PUBLICATION_LOG_FORMAT = '%H%x00%an%x00%ae%x00%cn%x00%ce%x00%B%x00'

export function parsePublicationCommits(logOutput: string): PublicationCommit[] {
  const fields = logOutput.split('\x00')
  if (fields.at(-1) !== '' && fields.at(-1) !== '\n') throw new Error('Incomplete publication commit log')
  fields.pop()
  const commits: PublicationCommit[] = []
  for (let index = 0; index < fields.length; index += 6) {
    if (fields.length - index < 6) throw new Error('Incomplete publication commit log')
    const [rawSha, authorName, authorEmail, committerName, committerEmail, message] = fields.slice(index, index + 6)
    const sha = rawSha!.trim()
    if (!/^[0-9a-f]{40,64}$/.test(sha) || !authorName || !authorEmail || !committerName || !committerEmail) {
      throw new Error('Malformed publication commit log')
    }
    commits.push({
      sha,
      author: { name: authorName!, email: authorEmail! },
      committer: { name: committerName!, email: committerEmail! },
      message: message!.replace(/\n+$/, ''),
    })
  }
  return commits
}

export function validatePublicationCommits(
  commits: readonly PublicationCommit[],
  policy: PublicationPolicy,
): PublicationValidationError[] {
  const errors: PublicationValidationError[] = []
  for (const commit of commits) {
    const label = commit.sha ? `commit ${commit.sha.slice(0, 12)}` : 'commit'
    for (const error of validateMessageTrailers(commit.message, policy.requiredTrailers)) {
      errors.push({ ...error, message: `${label}: ${error.message}` })
    }
    if (policy.author !== undefined) {
      const error = validateIdentity(policy.author, commit.author, 'author')
      if (error) errors.push({ ...error, message: `${label}: ${error.message}` })
    }
    if (policy.committer !== undefined) {
      const error = validateIdentity(policy.committer, commit.committer, 'committer')
      if (error) errors.push({ ...error, message: `${label}: ${error.message}` })
    }
  }
  return errors
}

export function formatPublicationValidationErrors(errors: readonly PublicationValidationError[]): string {
  return errors.map((error) => `- ${error.message}`).join('\n')
}
