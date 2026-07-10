import { mkdir, mkdtemp, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { describe, expect, it, vi } from "vitest"
import { openspecArtifactsAction } from "../src/actions/openspec.js"
import type { ActionContext } from "../src/core/types.js"
import { createDefaultRegistry } from "../src/actions/registry.js"

describe("mohist/openspec-artifacts", () => {
  it("registers openspec-artifacts in the default registry", () => {
    const registry = createDefaultRegistry()
    expect(registry.resolve("mohist/openspec-artifacts")).toBe(openspecArtifactsAction)
  })

  it("returns success and lists zero missing artifacts when all four plan artifacts exist", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(join(changeDir, "specs", "pr-first-workflow"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "pr-first-workflow", "spec.md"), "spec\n")
    await writeFile(join(changeDir, "design.md"), "design\n")
    await writeFile(join(changeDir, "tasks.json"), JSON.stringify({ tasks: [] }))

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(result.message).toMatch(/OpenSpec artifacts present under /)
    expect(output.kind).toBe("openspec-artifacts")
    expect(output.changeDir).toBe(changeDir)
    expect(output.present).toBe(true)
    expect(output.missing).toEqual([])
  })

  it("returns failure listing only proposal.md when proposal.md is missing", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(join(changeDir, "specs", "pr-first-workflow"), { recursive: true })
    await writeFile(join(changeDir, "specs", "pr-first-workflow", "spec.md"), "spec\n")
    await writeFile(join(changeDir, "design.md"), "design\n")
    await writeFile(join(changeDir, "tasks.json"), JSON.stringify({ tasks: [] }))

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toContain("OpenSpec artifacts missing")
    expect(result.message).toContain(join(changeDir, "proposal.md"))
    expect(output.present).toBe(false)
    expect(output.missing).toEqual([join(changeDir, "proposal.md")])
  })

  it("returns failure listing only the specs directory when specs/ is missing", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(changeDir, { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "design.md"), "design\n")
    await writeFile(join(changeDir, "tasks.json"), JSON.stringify({ tasks: [] }))

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toContain("OpenSpec artifacts missing")
    expect(result.message).toContain(join(changeDir, "specs"))
    expect(output.kind).toBe("openspec-artifacts")
    expect(output.changeDir).toBe(changeDir)
    expect(output.present).toBe(false)
    expect(output.missing).toEqual([join(changeDir, "specs")])
  })

  it("returns failure listing only design.md when design.md is missing", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(join(changeDir, "specs", "pr-first-workflow"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "pr-first-workflow", "spec.md"), "spec\n")
    await writeFile(join(changeDir, "tasks.json"), JSON.stringify({ tasks: [] }))

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toContain(join(changeDir, "design.md"))
    expect(output.present).toBe(false)
    expect(output.missing).toEqual([join(changeDir, "design.md")])
  })

  it("returns failure listing only tasks.json when tasks.json is missing", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(join(changeDir, "specs", "pr-first-workflow"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "pr-first-workflow", "spec.md"), "spec\n")
    await writeFile(join(changeDir, "design.md"), "design\n")

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toContain(join(changeDir, "tasks.json"))
    expect(output.present).toBe(false)
    expect(output.missing).toEqual([join(changeDir, "tasks.json")])
  })

  it("returns failure when a required directory is present as a file", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(changeDir, { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs"), "not a directory\n")
    await writeFile(join(changeDir, "design.md"), "design\n")
    await writeFile(join(changeDir, "tasks.json"), JSON.stringify({ tasks: [] }))

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toContain(join(changeDir, "specs"))
    expect(output.present).toBe(false)
    expect(output.missing).toContain(join(changeDir, "specs"))
  })

  it("returns failure when a required file is present as a directory", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await mkdir(join(changeDir, "proposal.md"), { recursive: true })
    await writeFile(join(changeDir, "design.md"), "design\n")
    await writeFile(join(changeDir, "tasks.json"), JSON.stringify({ tasks: [] }))

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toContain(join(changeDir, "proposal.md"))
    expect(output.present).toBe(false)
    expect(output.missing).toContain(join(changeDir, "proposal.md"))
  })

  it("returns failure listing every missing artifact when changeDir is empty", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(changeDir, { recursive: true })

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.present).toBe(false)
    expect(output.missing).toEqual([
      join(changeDir, "proposal.md"),
      join(changeDir, "specs"),
      join(changeDir, "design.md"),
      join(changeDir, "tasks.json"),
    ])
    expect(result.message).toContain(join(changeDir, "proposal.md"))
    expect(result.message).toContain(join(changeDir, "specs"))
    expect(result.message).toContain(join(changeDir, "design.md"))
    expect(result.message).toContain(join(changeDir, "tasks.json"))
  })

  it("fails with a clear message when only changeDir is supplied (the action's sole input)", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const result = await openspecArtifactsAction({
      workflowRunId: "workflow-1",
      workId: "plan-artifacts",
      workType: "task",
      stage: "plan",
      title: "Verify plan artifacts",
      uses: "mohist/openspec-artifacts",
      with: {} as never,
      variables: {} as never,
      workDir,
      signal: new AbortController().signal,
      writeVars: vi.fn(),
    })
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toMatch(/requires 'changeDir'/)
    expect(output.kind).toBeUndefined()
  })

  it("ignores other inputs beyond changeDir (only changeDir is consulted)", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "design.md"), "design\n")
    await writeFile(join(changeDir, "tasks.json"), JSON.stringify({ tasks: [] }))

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir, {
      path: "/somewhere/else/should/be/ignored",
      extra: "noise",
    }))

    expect(result.status).toBe("success")
  })
})

function artifactsContext(workDir: string, changeDir: string, extra: Record<string, unknown> = {}): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "plan-artifacts",
    workType: "task",
    stage: "plan",
    title: "Verify plan artifacts",
    uses: "mohist/openspec-artifacts",
    with: { changeDir, ...extra } as never,
    variables: {} as never,
    workDir,
    signal: new AbortController().signal,
    writeVars: vi.fn(),
  }
}
