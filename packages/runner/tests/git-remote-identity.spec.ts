import { describe, expect, it } from "vitest"
import { fingerprintGitRemote, normalizeGitRemote, REMOTE_IDENTITY_VERSION } from "../src/runtime/git-remote-identity.js"

describe("git remote identity", () => {
  it.each([
    ["git@example.com:owner/repo.git", "ssh://example.com/owner/repo"],
    ["ssh:git@example.com:owner/repo", "ssh://example.com/owner/repo"],
    ["ssh://git@example.com/owner/repo.git", "ssh://example.com/owner/repo"],
    ["deploy@github.com:owner/repo.git", "ssh://github.com/owner/repo"],
    ["https://user:pw@example.com/owner/repo.git", "https://example.com/owner/repo"],
    ["https://example.com:443/owner/repo", "https://example.com/owner/repo"],
    ["HTTPS://Example.COM/Owner/Repo.git", "https://example.com/Owner/Repo"],
    ["https://example.com/owner/repo?ref=main", "https://example.com/owner/repo"],
    ["ssh://git@example.com:22/owner/repo.git", "ssh://example.com/owner/repo"],
  ])("normalizes %s", (input, expected) => {
    expect(normalizeGitRemote(input)).toBe(expected)
  })

  it("uses the versioned lowercase sha256 fingerprint", () => {
    const identity = fingerprintGitRemote("https://example.com/owner/repo.git")
    expect(identity).toMatchObject({ remoteIdentityVersion: REMOTE_IDENTITY_VERSION })
    expect(identity?.remoteFingerprint).toMatch(/^[0-9a-f]{64}$/)
  })

  it("rejects unparseable remotes", () => {
    expect(fingerprintGitRemote("not a remote")).toBeNull()
  })
})
