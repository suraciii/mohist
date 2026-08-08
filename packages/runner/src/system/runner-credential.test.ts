import { afterEach, describe, expect, it, vi } from "vitest"

// In-memory node:fs so the unit never touches the real filesystem
// (design/testing.md hard constraint 1).
const files = vi.hoisted(() => new Map<string, { content: string; mode?: number }>())

vi.mock("node:fs", () => ({
  mkdirSync: vi.fn((directory: string) => {
    files.set(directory, { content: "" })
  }),
  readFileSync: vi.fn((path: string) => {
    const entry = files.get(path)
    if (!entry) {
      const error = new Error(`ENOENT: no such file or directory, open '${path}'`) as NodeJS.ErrnoException
      error.code = "ENOENT"
      throw error
    }
    return entry.content
  }),
  writeFileSync: vi.fn((path: string, content: string, options?: { mode?: number }) => {
    files.set(path, { content, mode: typeof options === "object" ? options.mode : undefined })
  }),
}))

import {
  loadRunnerCredential,
  registerWithEnrollmentToken,
  resolveRunnerCredential,
  runnerCredentialPath,
  writeRunnerCredential,
} from "./runner-credential.js"

const serverUrl = "http://server"
const signal = new AbortController().signal

afterEach(() => {
  files.clear()
  vi.restoreAllMocks()
})

describe("runner credential file", () => {
  it("loads the persisted credential", () => {
    files.set(runnerCredentialPath("/runner"), { content: "moh_runner_abc\n" })

    expect(loadRunnerCredential("/runner")).toBe("moh_runner_abc")
  })

  it("returns null when the file does not exist", () => {
    expect(loadRunnerCredential("/runner")).toBeNull()
  })

  it("writes owner-only (0600) with a trailing newline", () => {
    writeRunnerCredential("/runner", "moh_runner_abc")

    expect(files.has("/runner")).toBe(true)
    expect(files.get(runnerCredentialPath("/runner"))).toEqual({ content: "moh_runner_abc\n", mode: 0o600 })
  })
})

describe("registerWithEnrollmentToken", () => {
  it("posts the enrollment token and returns the machine credential", async () => {
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(JSON.stringify({ success: true, data: { token: "moh_runner_abc", runnerId: "runner-1" } }), {
        status: 201,
        headers: { "content-type": "application/json" },
      }),
    )

    const credential = await registerWithEnrollmentToken(serverUrl, "runner-1", "host-1", "moh_enroll_xyz", signal)

    expect(credential).toBe("moh_runner_abc")
    const [url, init] = fetchSpy.mock.calls[0]!
    expect(url).toBe("http://server/api/runners/register")
    expect(init?.method).toBe("POST")
    expect(JSON.parse(init?.body as string)).toEqual({
      token: "moh_enroll_xyz",
      runnerId: "runner-1",
      hostname: "host-1",
    })
  })

  it("throws when the server rejects the token", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("expired", { status: 401 }))

    await expect(
      registerWithEnrollmentToken(serverUrl, "runner-1", "host-1", "moh_enroll_xyz", signal),
    ).rejects.toThrow(/registration with enrollment token failed: 401/)
  })

  it("throws on a malformed response", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify({ success: true }), { status: 201 }))

    await expect(
      registerWithEnrollmentToken(serverUrl, "runner-1", "host-1", "moh_enroll_xyz", signal),
    ).rejects.toThrow(/malformed/)
  })
})

describe("resolveRunnerCredential", () => {
  it("uses the persisted credential without any registration call", async () => {
    files.set(runnerCredentialPath("/runner"), { content: "moh_runner_abc\n" })
    const fetchSpy = vi.spyOn(globalThis, "fetch")

    const credential = await resolveRunnerCredential({
      serverUrl,
      runnerId: "runner-1",
      runnerRoot: "/runner",
      hostname: "host-1",
      enrollmentToken: "moh_enroll_xyz",
      signal,
    })

    expect(credential).toBe("moh_runner_abc")
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it("registers through the enrollment token and persists the credential", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(JSON.stringify({ success: true, data: { token: "moh_runner_new" } }), { status: 201 }),
    )

    const credential = await resolveRunnerCredential({
      serverUrl,
      runnerId: "runner-1",
      runnerRoot: "/runner",
      hostname: "host-1",
      enrollmentToken: "moh_enroll_xyz",
      signal,
    })

    expect(credential).toBe("moh_runner_new")
    expect(files.get(runnerCredentialPath("/runner"))).toEqual({ content: "moh_runner_new\n", mode: 0o600 })
  })

  it("returns null when there is no credential and no enrollment token", async () => {
    const fetchSpy = vi.spyOn(globalThis, "fetch")

    const credential = await resolveRunnerCredential({
      serverUrl,
      runnerId: "runner-1",
      runnerRoot: "/runner",
      hostname: "host-1",
      signal,
    })

    expect(credential).toBeNull()
    expect(fetchSpy).not.toHaveBeenCalled()
  })
})
