import { join } from "node:path"
import { describe, expect, it as vitestIt } from "vitest"
import { buildExecutionEnvelope } from "./execution-envelope.js"
import { SkillResolver } from "./skill-resolver.js"
import { inlineSlackCollaborationSkill } from "./slack-execution-context.js"
import { MemoryFileSystem } from "../../tests/support/memory-filesystem.js"
import { withTestRunnerResources } from "../../tests/support/test-resources.js"

async function skill(fileSystem: MemoryFileSystem, root: string, name: string, body: string): Promise<void> {
  await fileSystem.ensureDir(join(root, name))
  await fileSystem.writeText(join(root, name, "SKILL.md"), body)
}

function it(name: string, body: (fileSystem: MemoryFileSystem) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const fileSystem = new MemoryFileSystem()
    try {
      await withTestRunnerResources(async () => await body(fileSystem), { fileSystem })
    } finally {
      await fileSystem.deleteDirectory("/")
      if (fileSystem.exists("/")) throw new Error("skill resolver test filesystem was not cleaned up")
    }
  })
}

describe("SkillResolver", () => {
  it("uses workdir, home, then configured roots in order", async (fileSystem) => {
    const base = "/virtual/mohist-skills"
    const work = join(base, "work")
    const home = join(base, "home")
    const extra = join(base, "extra")
    await skill(fileSystem, join(work, ".agents", "skills"), "first", "work")
    await skill(fileSystem, join(home, ".agents", "skills"), "second", "home")
    await skill(fileSystem, extra, "third", "extra")
    await skill(fileSystem, join(work, ".agents", "skills"), "same", "work-wins")
    await skill(fileSystem, join(home, ".agents", "skills"), "same", "home-loses")
    await skill(fileSystem, extra, "same", "extra-loses")

    const result = await new SkillResolver({ homeDir: home, environment: { MOHIST_SKILL_ROOTS: extra } }).resolve(["same", "second", "third"], work)
    expect(result).toEqual({ ok: true, skills: [
      { name: "same", instructions: "work-wins" },
      { name: "second", instructions: "home" },
      { name: "third", instructions: "extra" },
    ] })
  })

  it("rejects unsafe, missing, and empty skills", async (fileSystem) => {
    const base = "/virtual/mohist-skills-unsafe"
    const work = join(base, "work")
    await skill(fileSystem, join(work, ".agents", "skills"), "empty", "   ")
    for (const name of ["../escape", "a/b", "empty", "missing"]) {
      const result = await new SkillResolver({ homeDir: join(base, "home"), environment: {} }).resolve([name], work)
      expect(result.ok).toBe(false)
      expect(result).toMatchObject({ code: "skill_not_found", name })
    }
  })

  it("does not resolve an empty list and serializes skill data", async () => {
    const empty = await new SkillResolver({ environment: { MOHIST_SKILL_ROOTS: "/does/not/exist" } }).resolve([], "/does/not/exist")
    expect(empty).toEqual({ ok: true, skills: [] })
    const envelope = buildExecutionEnvelope("goal", "agent instructions", [{ name: "demo", instructions: "body with ${not-a-template}" }])
    expect(envelope).toContain(JSON.stringify({ instructions: "agent instructions", skills: [{ name: "demo", instructions: "body with ${not-a-template}" }] }))
    expect(envelope).toContain("goal")
  })

  it("preserves a normal dispatch exactly and serializes managed Slack facts separately", () => {
    expect(buildExecutionEnvelope("goal", "agent instructions")).toBe("agent instructions\n\ngoal")

    const instructions = "Use the reply anchor supplied by the Server."
    const context = {
      version: 1,
      replyAnchor: {
        workspaceId: "T1",
        conversationId: "D1",
        threadRootMessageId: "100.0",
        triggeringMessageId: "101.0",
        initiatingMemberId: "U1",
        connectionId: "connection_1",
        sessionId: "session_1",
        dispatchRef: "dispatch_1",
      },
      collaborationSkill: {
        name: "mohist-slack-collaboration",
        version: "1.0.0",
        instructions,
        contentHash: "test-hash",
      },
    } as const

    const envelope = buildExecutionEnvelope(
      "goal",
      null,
      [inlineSlackCollaborationSkill(context)],
      context,
    )
    expect(envelope).toContain("[mohist-system-facts]")
    expect(envelope).toContain('"dispatchRef":"dispatch_1"')
    expect(envelope).toContain('"name":"mohist-slack-collaboration"')
    expect(envelope).toContain(instructions)
  })
})
