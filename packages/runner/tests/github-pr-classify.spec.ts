import { describe, expect, it } from "vitest"
import type { GitHubPrErrorCode } from "../src/actions/github-pr-types.js"
import {
  classifyGhFailure,
  classifyPushFailure,
  looksLikeAuthFailure,
  looksLikeBaseMoved,
  looksLikePrStateConflict,
  looksLikeProtectionConflict,
  looksLikeRetrySafe,
} from "../src/actions/github-pr-classify.js"

describe("looksLikeBaseMoved", () => {
  it("matches each documented phrase (lowercase)", () => {
    const phrases = [
      "merge conflict",
      "not mergeable",
      "can't be merged",
      "can not be merged",
      "base branch head",
      "base branch has been updated",
      "branch is out-of-date",
      "is out of date",
      "diverged",
      "non-fast-forward",
      "stale info",
    ]
    for (const phrase of phrases) {
      expect(looksLikeBaseMoved(`error: ${phrase}`)).toBe(true)
    }
  })

  it("matches each documented phrase case-insensitively", () => {
    const phrases = [
      "MERGE CONFLICT",
      "Not Mergeable",
      "Base Branch Head",
      "BASE BRANCH HAS BEEN UPDATED",
      "Branch Is Out-Of-Date",
      "Diverged",
      "Non-Fast-Forward",
      "STALE INFO",
    ]
    for (const phrase of phrases) {
      expect(looksLikeBaseMoved(`gh: ${phrase}`)).toBe(true)
    }
  })

  it("returns false for unrelated error text", () => {
    const negatives = [
      "pull request is in a closed state",
      "rate limit exceeded",
      "gh: authentication required",
      "protected branch",
      "already merged",
      "fatal: repository not found",
    ]
    for (const phrase of negatives) {
      expect(looksLikeBaseMoved(phrase)).toBe(false)
    }
  })

  it("returns false for empty input", () => {
    expect(looksLikeBaseMoved("")).toBe(false)
  })
})

describe("looksLikeProtectionConflict", () => {
  it("matches each documented phrase (lowercase)", () => {
    const phrases = [
      "protected branch",
      "required status check",
      "status check",
      "required review",
      "review required",
      "approving review",
      "branch protection",
      "branch policy",
    ]
    for (const phrase of phrases) {
      expect(looksLikeProtectionConflict(`error: ${phrase}`)).toBe(true)
    }
  })

  it("matches each documented phrase case-insensitively", () => {
    const phrases = [
      "PROTECTED BRANCH",
      "Required Status Check",
      "STATUS CHECK",
      "Required Review",
      "REVIEW REQUIRED",
      "Approving Review",
      "Branch Protection",
      "BRANCH POLICY",
    ]
    for (const phrase of phrases) {
      expect(looksLikeProtectionConflict(`gh: ${phrase}`)).toBe(true)
    }
  })

  it("returns false for unrelated error text", () => {
    const negatives = [
      "merge conflict",
      "rate limit exceeded",
      "gh: authentication required",
      "pull request is in a closed state",
      "non-fast-forward",
    ]
    for (const phrase of negatives) {
      expect(looksLikeProtectionConflict(phrase)).toBe(false)
    }
  })

  it("returns false for empty input", () => {
    expect(looksLikeProtectionConflict("")).toBe(false)
  })
})

describe("looksLikePrStateConflict", () => {
  it("matches each documented phrase (lowercase)", () => {
    const phrases = [
      "pull request is in a closed state",
      "closed pull request",
      "pull request is in a merged state",
      "already merged",
      "state was changed",
      "state has changed",
    ]
    for (const phrase of phrases) {
      expect(looksLikePrStateConflict(`error: ${phrase}`)).toBe(true)
    }
  })

  it("matches each documented phrase case-insensitively", () => {
    const phrases = [
      "PULL REQUEST IS IN A CLOSED STATE",
      "Closed Pull Request",
      "Pull Request Is In A Merged State",
      "ALREADY MERGED",
      "State Was Changed",
      "STATE HAS CHANGED",
    ]
    for (const phrase of phrases) {
      expect(looksLikePrStateConflict(`gh: ${phrase}`)).toBe(true)
    }
  })

  it("returns false for unrelated error text", () => {
    const negatives = [
      "merge conflict",
      "rate limit exceeded",
      "gh: authentication required",
      "protected branch",
      "non-fast-forward",
    ]
    for (const phrase of negatives) {
      expect(looksLikePrStateConflict(phrase)).toBe(false)
    }
  })

  it("returns false for empty input", () => {
    expect(looksLikePrStateConflict("")).toBe(false)
  })
})

describe("looksLikeAuthFailure", () => {
  it("matches each documented phrase (lowercase)", () => {
    const phrases = [
      "not logged into",
      "not logged in",
      "authentication required",
      "bad credentials",
      "github token",
      "gh: authentication",
      "login required",
      "must be logged in",
    ]
    for (const phrase of phrases) {
      expect(looksLikeAuthFailure(`error: ${phrase}`)).toBe(true)
    }
  })

  it("matches each documented phrase case-insensitively", () => {
    const phrases = [
      "NOT LOGGED INTO",
      "Not Logged In",
      "AUTHENTICATION REQUIRED",
      "Bad Credentials",
      "GITHUB TOKEN",
      "GH: AUTHENTICATION",
      "Login Required",
      "MUST BE LOGGED IN",
    ]
    for (const phrase of phrases) {
      expect(looksLikeAuthFailure(`gh: ${phrase}`)).toBe(true)
    }
  })

  it("returns false for unrelated error text", () => {
    const negatives = [
      "merge conflict",
      "rate limit exceeded",
      "pull request is in a closed state",
      "protected branch",
      "non-fast-forward",
    ]
    for (const phrase of negatives) {
      expect(looksLikeAuthFailure(phrase)).toBe(false)
    }
  })

  it("returns false for empty input", () => {
    expect(looksLikeAuthFailure("")).toBe(false)
  })
})

describe("looksLikeRetrySafe", () => {
  it("matches each documented phrase (lowercase)", () => {
    const phrases = [
      "rate limit",
      "api rate limit",
      "could not resolve host",
      "network",
      "timeout",
      "timed out",
      "connection reset",
      "temporarily unavailable",
      "try again",
      "502",
      "503",
      "504",
      "unexpected eof",
      "connection refused",
      "broken pipe",
      "dial tcp",
      "no such host",
      "tls handshake",
      "context deadline exceeded",
      "i/o timeout",
    ]
    for (const phrase of phrases) {
      expect(looksLikeRetrySafe(`error: ${phrase}`)).toBe(true)
    }
  })

  it("matches each documented phrase case-insensitively", () => {
    const phrases = [
      "RATE LIMIT",
      "API Rate Limit",
      "Could Not Resolve Host",
      "Network",
      "TIMEOUT",
      "Timed Out",
      "CONNECTION RESET",
      "Temporarily Unavailable",
      "Try Again",
      "Unexpected EOF",
      "Connection Refused",
      "Broken Pipe",
      "Dial TCP",
      "No Such Host",
      "TLS Handshake",
      "Context Deadline Exceeded",
      "I/O Timeout",
    ]
    for (const phrase of phrases) {
      expect(looksLikeRetrySafe(`gh: ${phrase}`)).toBe(true)
    }
  })

  it("returns false for unrelated error text", () => {
    const negatives = [
      "merge conflict",
      "gh: authentication required",
      "pull request is in a closed state",
      "protected branch",
      "non-fast-forward",
    ]
    for (const phrase of negatives) {
      expect(looksLikeRetrySafe(phrase)).toBe(false)
    }
  })

  it("returns false for empty input", () => {
    expect(looksLikeRetrySafe("")).toBe(false)
  })
})

describe("classifyGhFailure", () => {
  it("classifies auth-failure text as config-error", () => {
    expect(classifyGhFailure("gh: not logged in", "")).toBe<GitHubPrErrorCode>("config-error")
    expect(classifyGhFailure("", "bad credentials")).toBe<GitHubPrErrorCode>("config-error")
  })

  it("classifies protection-conflict text as protection-conflict", () => {
    expect(classifyGhFailure("protected branch", "")).toBe<GitHubPrErrorCode>("protection-conflict")
    expect(classifyGhFailure("", "branch protection rule violated")).toBe<GitHubPrErrorCode>("protection-conflict")
  })

  it("classifies base-moved text as base-moved", () => {
    expect(classifyGhFailure("non-fast-forward", "")).toBe<GitHubPrErrorCode>("base-moved")
    expect(classifyGhFailure("", "Pull request is not mergeable")).toBe<GitHubPrErrorCode>("base-moved")
  })

  it("classifies pr-state-conflict text as pr-state-conflict", () => {
    expect(classifyGhFailure("pull request is in a closed state", "")).toBe<GitHubPrErrorCode>("pr-state-conflict")
    expect(classifyGhFailure("", "already merged")).toBe<GitHubPrErrorCode>("pr-state-conflict")
  })

  it("classifies retry-safe text as retry-safe", () => {
    expect(classifyGhFailure("rate limit exceeded", "")).toBe<GitHubPrErrorCode>("retry-safe")
    expect(classifyGhFailure("", "connection reset by peer")).toBe<GitHubPrErrorCode>("retry-safe")
  })

  it("falls back to retry-safe for unclassified text", () => {
    expect(classifyGhFailure("some unrelated gh failure", "")).toBe<GitHubPrErrorCode>("retry-safe")
    expect(classifyGhFailure("", "weird error we don't recognize")).toBe<GitHubPrErrorCode>("retry-safe")
  })

  it("falls back to retry-safe for empty stdout/stderr", () => {
    expect(classifyGhFailure("", "")).toBe<GitHubPrErrorCode>("retry-safe")
  })

  it("falls back to retry-safe for whitespace-only stdout/stderr", () => {
    expect(classifyGhFailure("   \n\t  ", "")).toBe<GitHubPrErrorCode>("retry-safe")
    expect(classifyGhFailure("", "\n  \t")).toBe<GitHubPrErrorCode>("retry-safe")
    expect(classifyGhFailure("  ", "\t")).toBe<GitHubPrErrorCode>("retry-safe")
  })

  it("pins precedence: auth-failure beats protection beats base-moved beats pr-state beats retry-safe", () => {
    const all: GitHubPrErrorCode[] = ["config-error", "protection-conflict", "base-moved", "pr-state-conflict", "retry-safe"]
    for (const winning of all) {
      const phrase = (() => {
        switch (winning) {
          case "config-error": return "gh: not logged in"
          case "protection-conflict": return "protected branch"
          case "base-moved": return "merge conflict"
          case "pr-state-conflict": return "already merged"
          case "retry-safe": return "rate limit"
        }
      })()
      const loser = (() => {
        switch (winning) {
          case "config-error": return "merge conflict and already merged and rate limit"
          case "protection-conflict": return "merge conflict and already merged and rate limit"
          case "base-moved": return "already merged and rate limit"
          case "pr-state-conflict": return "rate limit"
          case "retry-safe": return ""
        }
      })()
      expect(classifyGhFailure(phrase as string, loser as string)).toBe<GitHubPrErrorCode>(winning)
    }
  })

  it("precedence: auth beats protection when both present in same text", () => {
    expect(classifyGhFailure("not logged in", "protected branch")).toBe<GitHubPrErrorCode>("config-error")
  })

  it("precedence: protection beats base-moved when both present in same text", () => {
    expect(classifyGhFailure("protected branch", "merge conflict")).toBe<GitHubPrErrorCode>("protection-conflict")
  })

  it("precedence: base-moved beats pr-state when both present in same text", () => {
    expect(classifyGhFailure("merge conflict", "already merged")).toBe<GitHubPrErrorCode>("base-moved")
  })

  it("precedence: pr-state beats retry-safe when both present in same text", () => {
    expect(classifyGhFailure("already merged", "rate limit exceeded")).toBe<GitHubPrErrorCode>("pr-state-conflict")
  })

  it("joins stdout and stderr with a newline before lowercasing", () => {
    expect(classifyGhFailure("", "NOT LOGGED IN")).toBe<GitHubPrErrorCode>("config-error")
    expect(classifyGhFailure("RATE LIMIT", "")).toBe<GitHubPrErrorCode>("retry-safe")
  })
})

describe("classifyPushFailure", () => {
  it("delegates to classifyGhFailure (same return for same input)", () => {
    expect(classifyPushFailure("non-fast-forward", "")).toBe(classifyGhFailure("non-fast-forward", ""))
    expect(classifyPushFailure("", "rate limit exceeded")).toBe(classifyGhFailure("", "rate limit exceeded"))
    expect(classifyPushFailure("merge conflict", "protected branch")).toBe(classifyGhFailure("merge conflict", "protected branch"))
  })

  it("returns retry-safe for empty input", () => {
    expect(classifyPushFailure("", "")).toBe<GitHubPrErrorCode>("retry-safe")
  })

  it("returns config-error for auth-style git push output", () => {
    expect(classifyPushFailure("not logged in", "")).toBe<GitHubPrErrorCode>("config-error")
  })

  it("returns base-moved for non-fast-forward git push rejection", () => {
    expect(classifyPushFailure(
      "To https://example.com/repo.git\n ! [rejected]        master -> master (non-fast-forward)\nerror: failed to push some refs to 'https://example.com/repo.git'\nhint: Updates were rejected because the tip of your current branch is behind",
      "",
    )).toBe<GitHubPrErrorCode>("base-moved")
  })
})