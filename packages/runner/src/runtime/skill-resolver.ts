import { readFile, realpath } from "node:fs/promises"
import { homedir } from "node:os"
import { isAbsolute, join, relative, resolve } from "node:path"

export interface ResolvedSkill {
  readonly name: string
  readonly instructions: string
}

export interface SkillResolverOptions {
  readonly homeDir?: string
  readonly environment?: Record<string, string | undefined>
}

export type SkillResolution =
  | { readonly ok: true; readonly skills: readonly ResolvedSkill[] }
  | { readonly ok: false; readonly code: "skill_not_found"; readonly name: string; readonly message: string }

const SAFE_SKILL_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]*$/

export class SkillResolver {
  private readonly homeDir: string
  private readonly environment: Record<string, string | undefined>

  constructor(options: SkillResolverOptions = {}) {
    this.homeDir = options.homeDir ?? homedir()
    this.environment = options.environment ?? process.env
  }

  async resolve(names: readonly string[] | null | undefined, workDir: string): Promise<SkillResolution> {
    if (!names || names.length === 0) return { ok: true, skills: [] }
    const roots = this.roots(workDir)
    const skills: ResolvedSkill[] = []
    for (const name of names) {
      if (typeof name !== "string" || !SAFE_SKILL_NAME.test(name) || name === "." || name === "..") {
        return this.failure(String(name), "Skill name must be a safe single path segment")
      }
      const loaded = await this.load(name, roots)
      if (!loaded.ok) return loaded
      if (!("skill" in loaded)) return this.failure(name, "Malformed Skill resolver result")
      skills.push(loaded.skill)
    }
    return { ok: true, skills }
  }

  private roots(workDir: string): readonly string[] {
    const additions = (this.environment.MOHIST_SKILL_ROOTS ?? "")
      .split(":")
      .map((root) => root.trim())
      .filter((root) => root.length > 0)
    return [join(workDir, ".agents", "skills"), join(this.homeDir, ".agents", "skills"), ...additions]
  }

  private async load(name: string, roots: readonly string[]): Promise<{ readonly ok: true; readonly skill: ResolvedSkill } | SkillResolution> {
    for (const root of roots) {
      let rootReal: string
      try {
        rootReal = await realpath(resolve(root))
      } catch {
        continue
      }
      let fileReal: string
      try {
        fileReal = await realpath(join(rootReal, name, "SKILL.md"))
      } catch {
        continue
      }
      const rel = relative(rootReal, fileReal)
      if (rel.startsWith("..") || isAbsolute(rel)) return this.failure(name, "SKILL.md resolves outside its configured root")
      let body: string
      try {
        body = new TextDecoder("utf-8", { fatal: true }).decode(await readFile(fileReal))
      } catch {
        return this.failure(name, "SKILL.md is unreadable or is not valid UTF-8")
      }
      if (!body.trim()) return this.failure(name, "SKILL.md is empty")
      return { ok: true, skill: { name, instructions: body } }
    }
    return this.failure(name, "SKILL.md was not found or could not be read")
  }

  private failure(name: string, reason: string): SkillResolution {
    return { ok: false, code: "skill_not_found", name, message: `Skill '${name}' could not be resolved: ${reason}` }
  }
}
