import { AsyncLocalStorage } from "node:async_hooks"
import { join } from "node:path"
import { describe, expect, it as vitestIt } from "vitest"
import {
  materializeIssueWorkspace,
  namedWorkspaceMarkerPath,
  namedWorkspacePath,
  readNamedWorkspaceMarker,
} from "../src/runtime/workspace-entity.js"
import { NamedWorkspaceRegistry } from "../src/runtime/workspace-registry.js"
import type { RunnerResourceContext } from "../src/system/filesystem.js"
import type { CommandLineOptions } from "../src/system/process.js"
import { withTestRunnerResources } from "./support/test-resources.js"

describe("materializeIssueWorkspace", () => {
  type FakeCommandResult = {
    success: boolean
    stdout: string
    stderr: string
    exitCode: number
    combinedOutput: string
  }

  interface MaterializeTestState {
    readonly root: string
    readonly registry: NamedWorkspaceRegistry
    readonly signal: AbortSignal
    readonly gitCalls: Array<{ command: string; args: string[]; cwd: string }>
    readonly fakeGitRefs: Map<string, string[]>
    commandBehavior: (command: string, args: string[], cwd: string, options?: CommandLineOptions) => Promise<FakeCommandResult>
  }

  const materializeTestStorage = new AsyncLocalStorage<MaterializeTestState>()

  function currentState(): MaterializeTestState {
    const state = materializeTestStorage.getStore()
    if (!state) throw new Error("materialize test resource context is not active")
    return state
  }

  function testRoot(): string {
    return currentState().root
  }

  function testRegistry(): NamedWorkspaceRegistry {
    return currentState().registry
  }

  function testSignal(): AbortSignal {
    return currentState().signal
  }

  function testGitCalls(): Array<{ command: string; args: string[]; cwd: string }> {
    return currentState().gitCalls
  }

  function testGitRefs(): Map<string, string[]> {
    return currentState().fakeGitRefs
  }
  const PROJECT_ID = "test-project"
  const WORKSPACE_NAME = "issue-42"
  const GIT_URL = "https://example.test/repo.git"
  const BASE_BRANCH = "main"
  const RUN_BRANCH = `mohist/ws-${WORKSPACE_NAME}`

  function it(name: string, body: () => Promise<void>): void {
    vitestIt(name, async () => {
      const resources: RunnerResourceContext = {
        commandRunner: {
          run: async (command, args, cwd, _signal, _env, options) => {
            const state = currentState()
            return await state.commandBehavior(command, args, cwd, options as CommandLineOptions | undefined)
          },
        },
      }
      await withTestRunnerResources(async (fileSystem) => {
        const state = {
          root: "/virtual/issue-workspace-materialize",
          registry: new NamedWorkspaceRegistry("/virtual/issue-workspace-materialize"),
          signal: new AbortController().signal,
          gitCalls: [],
          fakeGitRefs: new Map<string, string[]>(),
          commandBehavior: async (): Promise<FakeCommandResult> => ({ success: true, stdout: "", stderr: "", exitCode: 0, combinedOutput: "" }),
        }
        await materializeTestStorage.run(state, async () => {
          await state.registry.load()
          try {
            await body()
          } finally {
            await fileSystem.deleteDirectory(state.root)
            if (fileSystem.exists(state.root)) throw new Error(`materialize test root was not cleaned: ${state.root}`)
          }
        })
      }, resources)
    })
  }

  function installFakeGit() {
    currentState().commandBehavior = async (command, args, cwd) => {
      testGitCalls().push({ command, args, cwd })
      const gitArgs = args

      if (command === "git" && gitArgs[0] === "-C" && gitArgs[2] === "remote" && gitArgs[3] === "get-url" && gitArgs[4] === "origin") {
        return { success: true, stdout: `${GIT_URL}\n`, stderr: "", exitCode: 0, combinedOutput: `${GIT_URL}\n` }
      }

      if (command === "git" && gitArgs[0] === "-C" && gitArgs[2] === "rev-parse" && gitArgs[3] === "--verify") {
        const ref = gitArgs[4]
        const refs = testGitRefs().get(gitArgs[1]!)
        if (refs?.includes(ref)) {
          return { success: true, stdout: "fake-sha\n", stderr: "", exitCode: 0, combinedOutput: "fake-sha\n" }
        }
        return { success: false, stdout: "", stderr: "fatal: needed a single revision", exitCode: 128, combinedOutput: "fatal: needed a single revision" }
      }

      return { success: true, stdout: "", stderr: "", exitCode: 0, combinedOutput: "" }
    }
  }

  it("clones the repository when directory does not exist", async () => {
    installFakeGit()
    const result = await materializeIssueWorkspace({
      runnerRoot: testRoot(),
      projectId: PROJECT_ID,
      workspaceName: WORKSPACE_NAME,
      gitUrl: GIT_URL,
      baseBranch: BASE_BRANCH,
      runBranch: RUN_BRANCH,
      registry: testRegistry(),
      signal: testSignal(),
    })

    expect(result.created).toBe(true)
    expect(result.path).toBe(namedWorkspacePath(testRoot(), PROJECT_ID, WORKSPACE_NAME))

    const cloneCalls = testGitCalls().filter((c) => c.args.length >= 2 && c.args[0] === "clone")
    expect(cloneCalls.length).toBe(1)

    const checkoutCalls = testGitCalls().filter((c) => c.args.includes("checkout"))
    expect(checkoutCalls.length).toBe(1)

    const marker = await readNamedWorkspaceMarker(result.path)
    expect(marker).not.toBeNull()
    expect(marker!.projectId).toBe(PROJECT_ID)
    expect(marker!.workspaceName).toBe(WORKSPACE_NAME)
    expect(marker!.repositories).toHaveLength(1)

    const entry = testRegistry().get(PROJECT_ID, WORKSPACE_NAME)
    expect(entry).not.toBeNull()
    expect(entry!.workspacePath).toBe(result.path)
  })

  it("skips clone when directory already exists with valid marker", async () => {
    installFakeGit()
    const first = await materializeIssueWorkspace({
      runnerRoot: testRoot(),
      projectId: PROJECT_ID,
      workspaceName: WORKSPACE_NAME,
      gitUrl: GIT_URL,
      baseBranch: BASE_BRANCH,
      runBranch: RUN_BRANCH,
      registry: testRegistry(),
      signal: testSignal(),
    })
    expect(first.created).toBe(true)
    const cloneCount = testGitCalls().filter((c) => c.args.includes("clone")).length

    const second = await materializeIssueWorkspace({
      runnerRoot: testRoot(),
      projectId: PROJECT_ID,
      workspaceName: WORKSPACE_NAME,
      gitUrl: GIT_URL,
      baseBranch: BASE_BRANCH,
      runBranch: RUN_BRANCH,
      registry: testRegistry(),
      signal: testSignal(),
    })
    expect(second.created).toBe(false)
    expect(second.path).toBe(first.path)
    // No additional clone
    expect(testGitCalls().filter((c) => c.args.includes("clone")).length).toBe(cloneCount)
  })

  it("restores run branch from remote when remote ref exists", async () => {
    // Set up remote ref to exist for the run branch
    const wsPath = namedWorkspacePath(testRoot(), PROJECT_ID, WORKSPACE_NAME)
    // We need to mock the git operations more carefully
    // The function clones to <path>.preparing first
    const prepPath = `${wsPath}.preparing`
    testGitRefs().set(prepPath, [`refs/remotes/origin/${RUN_BRANCH}`])

    installFakeGit()
    const result = await materializeIssueWorkspace({
      runnerRoot: testRoot(),
      projectId: PROJECT_ID,
      workspaceName: WORKSPACE_NAME,
      gitUrl: GIT_URL,
      baseBranch: BASE_BRANCH,
      runBranch: RUN_BRANCH,
      registry: testRegistry(),
      signal: testSignal(),
    })

    expect(result.created).toBe(true)
    const checkoutCalls = testGitCalls().filter((c) => c.args.includes("checkout") && c.args.includes("-B"))
    expect(checkoutCalls.length).toBe(1)
  })

  it("registers workspace in NamedWorkspaceRegistry only", async () => {
    installFakeGit()
    await materializeIssueWorkspace({
      runnerRoot: testRoot(),
      projectId: PROJECT_ID,
      workspaceName: WORKSPACE_NAME,
      gitUrl: GIT_URL,
      baseBranch: BASE_BRANCH,
      runBranch: RUN_BRANCH,
      registry: testRegistry(),
      signal: testSignal(),
    })

    const namedEntry = testRegistry().get(PROJECT_ID, WORKSPACE_NAME)
    expect(namedEntry).not.toBeNull()
    expect(namedEntry!.phase).toBe("active")
  })

  it("removes incomplete clone on failure", async () => {
    currentState().commandBehavior = async (_command, args) => {
      const gitArgs = args
      if (gitArgs[0] === "clone") {
        return { success: false, stdout: "", stderr: "connection refused", exitCode: 128, combinedOutput: "connection refused" }
      }
      if (gitArgs[0] === "-C" && gitArgs[2] === "remote" && gitArgs[3] === "get-url") {
        return { success: true, stdout: `${GIT_URL}\n`, stderr: "", exitCode: 0, combinedOutput: `${GIT_URL}\n` }
      }
      return { success: true, stdout: "", stderr: "", exitCode: 0, combinedOutput: "" }
    }

    await expect(
      materializeIssueWorkspace({
        runnerRoot: testRoot(),
        projectId: PROJECT_ID,
        workspaceName: WORKSPACE_NAME,
        gitUrl: GIT_URL,
        baseBranch: BASE_BRANCH,
        runBranch: RUN_BRANCH,
        registry: testRegistry(),
        signal: testSignal(),
      }),
    ).rejects.toThrow(/git clone failed/)

    // Verify no registry entry was left
    const entry = testRegistry().get(PROJECT_ID, WORKSPACE_NAME)
    expect(entry).toBeNull()
  })

  it("parallel issues have disjoint directories with independent clones", async () => {
    const cloneLog: Array<{ command: string; args: string[]; cwd: string }> = []
    currentState().commandBehavior = async (command, args, cwd) => {
      cloneLog.push({ command, args, cwd })
      const gitArgs = args
      if (command === "git" && gitArgs[0] === "-C" && gitArgs[2] === "remote" && gitArgs[3] === "get-url" && gitArgs[4] === "origin") {
        return { success: true, stdout: `${GIT_URL}\n`, stderr: "", exitCode: 0, combinedOutput: `${GIT_URL}\n` }
      }
      if (command === "git" && gitArgs[0] === "-C" && gitArgs[2] === "rev-parse" && gitArgs[3] === "--verify") {
        return { success: false, stdout: "", stderr: "fatal: needed a single revision", exitCode: 128, combinedOutput: "" }
      }
      return { success: true, stdout: "", stderr: "", exitCode: 0, combinedOutput: "" }
    }

    const ws42 = await materializeIssueWorkspace({
      runnerRoot: testRoot(), projectId: PROJECT_ID, workspaceName: "issue-42",
      gitUrl: GIT_URL, baseBranch: BASE_BRANCH, runBranch: "mohist/ws-issue-42",
      registry: testRegistry(), signal: testSignal(),
    })
    const ws99 = await materializeIssueWorkspace({
      runnerRoot: testRoot(), projectId: PROJECT_ID, workspaceName: "issue-99",
      gitUrl: GIT_URL, baseBranch: BASE_BRANCH, runBranch: "mohist/ws-issue-99",
      registry: testRegistry(), signal: testSignal(),
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

    expect(testRegistry().get(PROJECT_ID, "issue-42")).not.toBeNull()
    expect(testRegistry().get(PROJECT_ID, "issue-99")).not.toBeNull()
  })
})
