/**
 * `options.model` first-slash parsing for the OpenCode runtime.
 *
 * The Mohist contract (see `specs/opencode-model-catalog/spec.md`):
 *   - non-empty `provider/modelID` form,
 *   - provider is the substring before the first `/`,
 *   - the model ID is the entire remainder (additional `/` preserved),
 *   - `options.variant` is a sibling and never appended or parsed from
 *     the model identifier.
 *
 * The runtime constructs the SDK model DTO from the parsed parts
 * inside the module boundary; the parser is exposed so callers can
 * validate input without importing SDK types.
 */

export interface ParsedModelIdentifier {
  readonly providerID: string
  readonly modelID: string
}

export type ParseModelResult =
  | { kind: "ok"; value: ParsedModelIdentifier }
  | { kind: "failure"; message: string }

export function parseModelIdentifier(raw: string): ParseModelResult {
  if (typeof raw !== "string") {
    return { kind: "failure", message: "model must be a string" }
  }
  const trimmed = raw.trim()
  if (!trimmed) {
    return { kind: "failure", message: "model must be a non-empty 'provider/model' string" }
  }
  const split = trimmed.match(/^([^/\s]+)\/(\S+)$/)
  if (!split) {
    return { kind: "failure", message: "model must be 'provider/model' (provider and model-id required)" }
  }
  return {
    kind: "ok",
    value: { providerID: split[1], modelID: split[2] },
  }
}
