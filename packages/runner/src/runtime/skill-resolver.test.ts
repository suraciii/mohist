import { mkdtemp, mkdir, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { describe, expect, it } from "vitest"
import { buildExecutionEnvelope } from "./execution-envelope.js"
import { SkillResolver } from "./skill-resolver.js"

async function skill(root: string, name: string, body: string): Promise<void> {
  await mkdir(join(root, name), { recursive: true })
  await writeFile(join(root, name, "SKILL.md"), body, "utf8")
}

describe("SkillResolver", () => {
  it("uses workdir, home, then configured roots in order", async () => {
    const base = await mkdtemp(join(tmpdir(), "mohist-skills-"))
    const work = join(base, "work")
    const home = join(base, "home")
    const extra = join(base, "extra")
    await skill(join(work, ".agents", "skills"), "first", "work")
    await skill(join(home, ".agents", "skills"), "second", "home")
    await skill(extra, "third", "extra")
    await skill(join(work, ".agents", "skills"), "same", "work-wins")
    await skill(join(home, ".agents", "skills"), "same", "home-loses")
    await skill(extra, "same", "extra-loses")

    const result = await new SkillResolver({ homeDir: home, environment: { MOHIST_SKILL_ROOTS: extra } }).resolve(["same", "second", "third"], work)
    expect(result).toEqual({ ok: true, skills: [
      { name: "same", instructions: "work-wins" },
      { name: "second", instructions: "home" },
      { name: "third", instructions: "extra" },
    ] })
  })

  it("rejects unsafe, missing, and empty skills", async () => {
    const base = await mkdtemp(join(tmpdir(), "mohist-skills-"))
    const work = join(base, "work")
    await skill(join(work, ".agents", "skills"), "empty", "   ")
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
})
