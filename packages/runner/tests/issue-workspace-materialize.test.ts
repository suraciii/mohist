import { mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  materializeIssueWorkspace,
  namedWorkspaceMarkerPath,
  namedWorkspacePath,
  readNamedWorkspaceMarker,
} from "../src/runtime/workspace-entity.js"
import { NamedWorkspaceRegistry } from "../src/runtime/workspace-registry.js"
import * as processModule from "../src/system/process.js"

async function makeRunnerRoot() {
  return await mkdtemp(join(tmpdir(), "mohist-issue-ws-"))
}

const signal = new AbortController().signal

describe("materializeIssueWorkspace", () => {
  let root: string
  let registry: NamedWorkspaceRegistry
  let gitCalls: Array<{ command: string; args: string[]; cwd: string }> = []
  let fakeGitRefs: Map<string, string[]> = new Map()
  const PROJECT_ID = "test-project"
  const WORKSPACE_NAME = "issue-42"
  const GIT_URL = "https://example.test/repo.git"
  const BASE_BRANCH = "main"
  const RUN_BRANCH = `mohist/ws-${WORKSPACE_NAME}`

  beforeEach(async () => {
    root = await makeRunnerRoot()
    registry = new NamedWorkspaceRegistry(root)
    await registry.load()
    gitCalls = []
    fakeGitRefs = new Map()
  })

  afterEach(async () => {
    vi.restoreAllMocks()
    await rm(root, { recursive: true, force: true })
  })

  function installFakeGit() {
    vi.spyOn(processModule, "runCommand").mockImplementation(async (command, args, cwd, _signal, _env, _options) => {
      gitCalls.push({ command: command as string, args: args as string[], cwd: cwd as string })
      const gitArgs = args as string[]

      if (command === "git" && gitArgs[0] === "-C" && gitArgs[2] === "remote" && gitArgs[3] === "get-url" && gitArgs[4] === "origin") {
        return { success: true, stdout: `${GIT_URL}\n`, stderr: "", exitCode: 0, combinedOutput: `${GIT_URL}\n` }
      }

      if (command === "git" && gitArgs[0] === "-C" && gitArgs[2] === "rev-parse" && gitArgs[3] === "--verify") {
        const ref = gitArgs[4]
        const refs = fakeGitRefs.get(gitArgs[1])
        if (refs?.includes(ref)) {
          return { success: true, stdout: "fake-sha\n", stderr: "", exitCode: 0, combinedOutput: "fake-sha\n" }
        }
        return { success: false, stdout: "", stderr: "fatal: needed a single revision", exitCode: 128, combinedOutput: "fatal: needed a single revision" }
      }

      return { success: true, stdout: "", stderr: "", exitCode: 0, combinedOutput: "" }
    })
  }

  it("clones the repository when directory does not exist", async () => {
    installFakeGit()
    const result = await materializeIssueWorkspace({
      runnerRoot: root,
      projectId: PROJECT_ID,
      workspaceName: WORKSPACE_NAME,
      gitUrl: GIT_URL,
      baseBranch: BASE_BRANCH,
      runBranch: RUN_BRANCH,
      registry,
      signal,
    })

    expect(result.created).toBe(true)
    expect(result.path).toBe(namedWorkspacePath(root, PROJECT_ID, WORKSPACE_NAME))

    const cloneCalls = gitCalls.filter((c) => c.args.length >= 2 && c.args[0] === "clone")
    expect(cloneCalls.length).toBe(1)

    const checkoutCalls = gitCalls.filter((c) => c.args.includes("checkout"))
    expect(checkoutCalls.length).toBe(1)

    const marker = await readNamedWorkspaceMarker(result.path)
    expect(marker).not.toBeNull()
    expect(marker!.projectId).toBe(PROJECT_ID)
    expect(marker!.workspaceName).toBe(WORKSPACE_NAME)
    expect(marker!.repositories).toHaveLength(1)

    const entry = registry.get(PROJECT_ID, WORKSPACE_NAME)
    expect(entry).not.toBeNull()
    expect(entry!.workspacePath).toBe(result.path)
  })

  it("skips clone when directory already exists with valid marker", async () => {
    installFakeGit()
    const first = await materializeIssueWorkspace({
      runnerRoot: root,
      projectId: PROJECT_ID,
      workspaceName: WORKSPACE_NAME,
      gitUrl: GIT_URL,
      baseBranch: BASE_BRANCH,
      runBranch: RUN_BRANCH,
      registry,
      signal,
    })
    expect(first.created).toBe(true)
    const cloneCount = gitCalls.filter((c) => c.args.includes("clone")).length

    const second = await materializeIssueWorkspace({
      runnerRoot: root,
      projectId: PROJECT_ID,
      workspaceName: WORKSPACE_NAME,
      gitUrl: GIT_URL,
      baseBranch: BASE_BRANCH,
      runBranch: RUN_BRANCH,
      registry,
      signal,
    })
    expect(second.created).toBe(false)
    expect(second.path).toBe(first.path)
    // No additional clone
    expect(gitCalls.filter((c) => c.args.includes("clone")).length).toBe(cloneCount)
  })

  it("restores run branch from remote when remote ref exists", async () => {
    // Set up remote ref to exist for the run branch
    const wsPath = namedWorkspacePath(root, PROJECT_ID, WORKSPACE_NAME)
    // We need to mock the git operations more carefully
    // The function clones to <path>.preparing first
    const prepPath = `${wsPath}.preparing`
    fakeGitRefs.set(prepPath, [`refs/remotes/origin/${RUN_BRANCH}`])

    installFakeGit()
    const result = await materializeIssueWorkspace({
      runnerRoot: root,
      projectId: PROJECT_ID,
      workspaceName: WORKSPACE_NAME,
      gitUrl: GIT_URL,
      baseBranch: BASE_BRANCH,
      runBranch: RUN_BRANCH,
      registry,
      signal,
    })

    expect(result.created).toBe(true)
    const checkoutCalls = gitCalls.filter((c) => c.args.includes("checkout") && c.args.includes("-B"))
    expect(checkoutCalls.length).toBe(1)
  })

  it("registers workspace in NamedWorkspaceRegistry only", async () => {
    installFakeGit()
    await materializeIssueWorkspace({
      runnerRoot: root,
      projectId: PROJECT_ID,
      workspaceName: WORKSPACE_NAME,
      gitUrl: GIT_URL,
      baseBranch: BASE_BRANCH,
      runBranch: RUN_BRANCH,
      registry,
      signal,
    })

    const namedEntry = registry.get(PROJECT_ID, WORKSPACE_NAME)
    expect(namedEntry).not.toBeNull()
    expect(namedEntry!.phase).toBe("active")
  })

  it("removes incomplete clone on failure", async () => {
    vi.spyOn(processModule, "runCommand").mockImplementation(async (command, args, cwd, _signal, _env, _options) => {
      const gitArgs = args as string[]
      if (gitArgs[0] === "clone") {
        return { success: false, stdout: "", stderr: "connection refused", exitCode: 128, combinedOutput: "connection refused" }
      }
      if (gitArgs[0] === "-C" && gitArgs[2] === "remote" && gitArgs[3] === "get-url") {
        return { success: true, stdout: `${GIT_URL}\n`, stderr: "", exitCode: 0, combinedOutput: `${GIT_URL}\n` }
      }
      return { success: true, stdout: "", stderr: "", exitCode: 0, combinedOutput: "" }
    })

    await expect(
      materializeIssueWorkspace({
        runnerRoot: root,
        projectId: PROJECT_ID,
        workspaceName: WORKSPACE_NAME,
        gitUrl: GIT_URL,
        baseBranch: BASE_BRANCH,
        runBranch: RUN_BRANCH,
        registry,
        signal,
      }),
    ).rejects.toThrow(/git clone failed/)

    // Verify no registry entry was left
    const entry = registry.get(PROJECT_ID, WORKSPACE_NAME)
    expect(entry).toBeNull()
  })

  it("parallel issues have disjoint directories with independent clones", async () => {
    const cloneLog: Array<{ command: string; args: string[]; cwd: string }> = []
    vi.spyOn(processModule, "runCommand").mockImplementation(async (command, args, cwd, _signal, _env, _options) => {
      cloneLog.push({ command: command as string, args: args as string[], cwd: cwd as string })
      const gitArgs = args as string[]
      if (command === "git" && gitArgs[0] === "-C" && gitArgs[2] === "remote" && gitArgs[3] === "get-url" && gitArgs[4] === "origin") {
        return { success: true, stdout: `${GIT_URL}\n`, stderr: "", exitCode: 0, combinedOutput: `${GIT_URL}\n` }
      }
      if (command === "git" && gitArgs[0] === "-C" && gitArgs[2] === "rev-parse" && gitArgs[3] === "--verify") {
        return { success: false, stdout: "", stderr: "fatal: needed a single revision", exitCode: 128, combinedOutput: "" }
      }
      return { success: true, stdout: "", stderr: "", exitCode: 0, combinedOutput: "" }
    })

    const ws42 = await materializeIssueWorkspace({
      runnerRoot: root, projectId: PROJECT_ID, workspaceName: "issue-42",
      gitUrl: GIT_URL, baseBranch: BASE_BRANCH, runBranch: "mohist/ws-issue-42",
      registry, signal,
    })
    const ws99 = await materializeIssueWorkspace({
      runnerRoot: root, projectId: PROJECT_ID, workspaceName: "issue-99",
      gitUrl: GIT_URL, baseBranch: BASE_BRANCH, runBranch: "mohist/ws-issue-99",
      registry, signal,
    })

    expect(ws42.path).not.toBe(ws99.path)
    expect(ws42.created).toBe(true)
    expect(ws99.created).toBe(true)

    const cloneCalls = cloneLog.filter(c => c.args[0] === "clone")
    expect(cloneCalls).toHaveLength(2)
    expect(cloneCalls[0]!.args[2]).toContain("issue-42")
    expect(cloneCalls[1]!.args[2]).toContain("issue-99")
    expect(cloneCalls[0]!.args[2]).not.toBe(cloneCalls[1]!.args[2])

    const marker42 = await readNamedWorkspaceMarker(ws42.path)
    expect(marker42).not.toBeNull()
    expect(marker42!.workspaceName).toBe("issue-42")
    const marker99 = await readNamedWorkspaceMarker(ws99.path)
    expect(marker99).not.toBeNull()
    expect(marker99!.workspaceName).toBe("issue-99")

    expect(registry.get(PROJECT_ID, "issue-42")).not.toBeNull()
    expect(registry.get(PROJECT_ID, "issue-99")).not.toBeNull()
  })
})

describe("isRunnerOwnedWorkspacePath with NamedWorkspaceRegistry", () => {
  let root: string
  let namedRegistry: NamedWorkspaceRegistry
  const PROJECT_ID = "test-project"
  const WORKSPACE_NAME = "issue-42"

  beforeEach(async () => {
    root = await makeRunnerRoot()
    namedRegistry = new NamedWorkspaceRegistry(root)
    await namedRegistry.load()
  })

  afterEach(async () => {
    await rm(root, { recursive: true, force: true })
  })

  it("recognizes a named workspace path as runner-owned", async () => {
    const { isRunnerOwnedWorkspacePath } = await import("../src/runtime/agent-workspace.js")
    const { AgentWorkspaceRegistry } = await import("../src/runtime/agent-workspace-registry.js")
    const agentReg = new AgentWorkspaceRegistry(root)
    await agentReg.load()

    const workspacePath = namedWorkspacePath(root, PROJECT_ID, WORKSPACE_NAME)
    // Register in named registry
    await namedRegistry.register({
      projectId: PROJECT_ID,
      workspaceName: WORKSPACE_NAME,
      workspacePath,
    })

    const owned = await isRunnerOwnedWorkspacePath(workspacePath, {
      registry: agentReg,
      workflowRegistry: null,
      namedWorkspaceRegistry: namedRegistry,
      defaultWorkspacePaths: [],
    })
    expect(owned).toBe(true)
  })

  it("returns false for unregistered path", async () => {
    const { isRunnerOwnedWorkspacePath } = await import("../src/runtime/agent-workspace.js")
    const { AgentWorkspaceRegistry } = await import("../src/runtime/agent-workspace-registry.js")
    const agentReg = new AgentWorkspaceRegistry(root)
    await agentReg.load()

    const workspacePath = namedWorkspacePath(root, PROJECT_ID, WORKSPACE_NAME)
    const owned = await isRunnerOwnedWorkspacePath(workspacePath, {
      registry: agentReg,
      workflowRegistry: null,
      namedWorkspaceRegistry: namedRegistry,
      defaultWorkspacePaths: [],
    })
    expect(owned).toBe(false)
  })
})
