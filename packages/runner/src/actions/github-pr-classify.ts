import type { GitHubPrErrorCode } from "./github-pr-types.js"

export function classifyGhFailure(stdout: string, stderr: string, status?: "timeout"): GitHubPrErrorCode {
  if (status === "timeout") return "timeout"
  const text = `${stdout}\n${stderr}`.toLowerCase()
  if (!text.trim()) return "retry-safe"
  if (looksLikeAuthFailure(text)) return "config-error"
  if (looksLikeProtectionConflict(text)) return "protection-conflict"
  if (looksLikeBaseMoved(text)) return "base-moved"
  if (looksLikePrStateConflict(text)) return "pr-state-conflict"
  if (looksLikeRetrySafe(text)) return "retry-safe"
  return "retry-safe"
}

export function classifyPushFailure(stdout: string, stderr: string, status?: "timeout"): GitHubPrErrorCode {
  return classifyGhFailure(stdout, stderr, status)
}

export function looksLikeBaseMoved(text: string): boolean {
  const lower = text.toLowerCase()
  if (lower.includes("merge conflict") || lower.includes("not mergeable") || lower.includes("can't be merged") || lower.includes("can not be merged")) {
    return true
  }
  if (lower.includes("base branch head") || lower.includes("base branch has been updated") || lower.includes("branch is out-of-date") || lower.includes("is out of date") || lower.includes("diverged") || lower.includes("non-fast-forward") || lower.includes("stale info")) {
    return true
  }
  return false
}

export function looksLikeProtectionConflict(text: string): boolean {
  const lower = text.toLowerCase()
  if (lower.includes("protected branch")) return true
  if (lower.includes("required status check") || lower.includes("status check")) return true
  if (lower.includes("required review") || lower.includes("review required") || lower.includes("approving review")) return true
  if (lower.includes("branch protection") || lower.includes("branch policy")) return true
  return false
}

export function looksLikePrStateConflict(text: string): boolean {
  const lower = text.toLowerCase()
  if (lower.includes("pull request is in a closed state") || lower.includes("closed pull request")) return true
  if (lower.includes("pull request is in a merged state") || lower.includes("already merged")) return true
  if (lower.includes("state was changed") || lower.includes("state has changed")) return true
  return false
}

export function looksLikeAuthFailure(text: string): boolean {
  const lower = text.toLowerCase()
  if (lower.includes("not logged into") || lower.includes("not logged in")) return true
  if (lower.includes("authentication required") || lower.includes("bad credentials")) return true
  if (lower.includes("github token") || lower.includes("gh: authentication")) return true
  if (lower.includes("login required") || lower.includes("must be logged in")) return true
  return false
}

export function looksLikeRetrySafe(text: string): boolean {
  const lower = text.toLowerCase()
  if (lower.includes("rate limit") || lower.includes("api rate limit")) return true
  if (lower.includes("could not resolve host") || lower.includes("network") || lower.includes("timeout") || lower.includes("timed out")) return true
  if (lower.includes("connection reset") || lower.includes("temporarily unavailable") || lower.includes("try again")) return true
  if (lower.includes("502") || lower.includes("503") || lower.includes("504")) return true
  // Go net/http transport errors emitted by gh (e.g. when the TLS stream to
  // api.github.com is cut mid-response through an unstable proxy path).
  if (
    lower.includes("unexpected eof") ||
    lower.includes("connection refused") ||
    lower.includes("broken pipe") ||
    lower.includes("dial tcp") ||
    lower.includes("no such host") ||
    lower.includes("tls handshake") ||
    lower.includes("context deadline exceeded") ||
    lower.includes("i/o timeout")
  ) {
    return true
  }
  return false
}
