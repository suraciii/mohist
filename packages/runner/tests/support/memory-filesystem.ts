import { dirname, join, resolve } from "node:path"
import type { RunnerDirectoryEntry, RunnerFileInfo, RunnerFileSystem } from "../../src/system/filesystem.js"

type MemoryNode =
  | { kind: "file"; content: Uint8Array; mtimeMs: number }
  | { kind: "directory"; mtimeMs: number }
  | { kind: "symlink"; target: string; mtimeMs: number }

export class MemoryFileSystem implements RunnerFileSystem {
  readonly supportsDirectoryHandles: boolean = false
  private readonly nodes = new Map<string, MemoryNode>([["/", { kind: "directory", mtimeMs: 0 }]])
  private nextMtime = 1

  exists(path: string): boolean {
    return this.nodes.has(this.normalize(path))
  }

  async ensureDir(path: string): Promise<void> {
    this.ensureDirectory(this.normalize(path))
  }

  ensureDirSync(path: string): void {
    this.ensureDirectory(this.normalize(path))
  }

  async readText(path: string): Promise<string> {
    return new TextDecoder().decode(await this.readBinary(path))
  }

  async readBinary(path: string): Promise<Uint8Array> {
    const node = this.node(path, false)
    if (node.kind !== "file") throw this.error("EISDIR", path)
    return new Uint8Array(node.content)
  }

  async writeText(path: string, content: string): Promise<void> {
    await this.writeBinary(path, new TextEncoder().encode(content))
  }

  async writeBinary(path: string, content: Uint8Array): Promise<void> {
    const normalized = this.normalize(path)
    this.ensureDirectory(dirname(normalized))
    this.nodes.set(normalized, { kind: "file", content: new Uint8Array(content), mtimeMs: this.nextMtime++ })
  }

  async appendText(path: string, content: string): Promise<void> {
    const previous = this.exists(path) ? await this.readText(path) : ""
    await this.writeText(path, previous + content)
  }

  async deleteFile(path: string): Promise<void> {
    const normalized = this.normalize(path)
    const node = this.nodes.get(normalized)
    if (node?.kind === "directory") throw this.error("EISDIR", path)
    this.nodes.delete(normalized)
  }

  async deleteDirectory(path: string): Promise<void> {
    const normalized = this.normalize(path)
    for (const candidate of [...this.nodes.keys()]) {
      if (candidate === normalized || candidate.startsWith(`${normalized}/`)) this.nodes.delete(candidate)
    }
  }

  async rename(source: string, destination: string): Promise<void> {
    const sourcePath = this.normalize(source)
    const destinationPath = this.normalize(destination)
    const sourceNode = this.nodes.get(sourcePath)
    if (!sourceNode) throw this.error("ENOENT", source)
    this.ensureDirectory(dirname(destinationPath))
    await this.deleteDirectory(destinationPath)
    const moved = [...this.nodes.entries()]
      .filter(([path]) => path === sourcePath || path.startsWith(`${sourcePath}/`))
      .map(([path, node]) => [path === sourcePath ? destinationPath : join(destinationPath, path.slice(sourcePath.length + 1)), node] as const)
    await this.deleteDirectory(sourcePath)
    for (const [path, node] of moved) this.nodes.set(path, node)
  }

  async lstat(path: string): Promise<RunnerFileInfo> {
    return this.info(this.node(path, false))
  }

  async stat(path: string): Promise<RunnerFileInfo> {
    return this.info(this.node(path, true))
  }

  async readdir(path: string): Promise<RunnerDirectoryEntry[]> {
    const normalized = this.normalize(path)
    const node = this.node(normalized, true)
    if (node.kind !== "directory") throw this.error("ENOTDIR", path)
    const prefix = normalized === "/" ? "/" : `${normalized}/`
    const names = new Set<string>()
    for (const candidate of this.nodes.keys()) {
      if (!candidate.startsWith(prefix) || candidate === normalized) continue
      const rest = candidate.slice(prefix.length)
      if (!rest || rest.includes("/")) continue
      names.add(rest)
    }
    return [...names].sort().map((name) => {
      const child = this.nodes.get(join(normalized, name))!
      return {
        name,
        isFile: () => child.kind === "file",
        isDirectory: () => child.kind === "directory",
        isSymbolicLink: () => child.kind === "symlink",
      }
    })
  }

  async realpath(path: string): Promise<string> {
    return this.resolveNode(path, new Set())
  }

  async readTail(path: string, start: number, length: number): Promise<string> {
    const content = await this.readBinary(path)
    return new TextDecoder().decode(content.slice(start, start + length))
  }

  async copyDirectory(source: string, destination: string): Promise<void> {
    const sourcePath = this.normalize(source)
    const destinationPath = this.normalize(destination)
    const sourceNode = this.node(sourcePath, true)
    if (sourceNode.kind !== "directory") throw this.error("ENOTDIR", source)
    this.ensureDirectory(destinationPath)
    for (const [path, node] of this.nodes.entries()) {
      if (!path.startsWith(`${sourcePath}/`)) continue
      const copiedPath = join(destinationPath, path.slice(sourcePath.length + 1))
      if (node.kind === "file") await this.writeBinary(copiedPath, node.content)
      else if (node.kind === "directory") await this.ensureDir(copiedPath)
      else await this.symlink(node.target, copiedPath)
    }
  }

  async symlink(target: string, path: string): Promise<void> {
    const normalized = this.normalize(path)
    this.ensureDirectory(dirname(normalized))
    this.nodes.set(normalized, { kind: "symlink", target, mtimeMs: this.nextMtime++ })
  }

  protected normalize(path: string): string {
    return resolve("/", path)
  }

  private node(path: string, followSymlink: boolean): MemoryNode {
    const normalized = this.normalize(path)
    const node = this.nodes.get(normalized)
    if (!node) throw this.error("ENOENT", path)
    if (!followSymlink || node.kind !== "symlink") return node
    return this.nodes.get(this.resolveNode(normalized, new Set())) ?? (() => { throw this.error("ENOENT", path) })()
  }

  private resolveNode(path: string, seen: Set<string>): string {
    const normalized = this.normalize(path)
    const node = this.nodes.get(normalized)
    if (!node) throw this.error("ENOENT", path)
    if (node.kind !== "symlink") return normalized
    if (seen.has(normalized)) throw this.error("ELOOP", path)
    seen.add(normalized)
    return this.resolveNode(node.target.startsWith("/") ? node.target : join(dirname(normalized), node.target), seen)
  }

  private ensureDirectory(path: string): void {
    const normalized = this.normalize(path)
    if (normalized === "/") return
    const parent = dirname(normalized)
    this.ensureDirectory(parent)
    const existing = this.nodes.get(normalized)
    if (existing && existing.kind !== "directory") throw this.error("ENOTDIR", path)
    if (!existing) this.nodes.set(normalized, { kind: "directory", mtimeMs: this.nextMtime++ })
  }

  private info(node: MemoryNode): RunnerFileInfo {
    return {
      kind: node.kind,
      size: node.kind === "file" ? node.content.byteLength : 0,
      mtimeMs: node.mtimeMs,
      isFile: () => node.kind === "file",
      isDirectory: () => node.kind === "directory",
      isSymbolicLink: () => node.kind === "symlink",
    }
  }

  private error(code: string, path: string): NodeJS.ErrnoException {
    const error = new Error(`${code}: ${path}`) as NodeJS.ErrnoException
    error.code = code
    return error
  }
}

export class MemoryDirectoryHandleFileSystem extends MemoryFileSystem {
  readonly supportsDirectoryHandles = true
  private readonly aliases = new Map<string, string>()
  private nextHandle = 1

  async openDirectory(path: string): Promise<{ path: string; close(): Promise<void> }> {
    const info = await this.stat(path)
    if (!info.isDirectory()) {
      const error = new Error(`ENOTDIR: ${path}`) as NodeJS.ErrnoException
      error.code = "ENOTDIR"
      throw error
    }
    const alias = `/memory-handle-${this.nextHandle++}`
    this.aliases.set(alias, this.normalize(path))
    return { path: alias, close: async () => { this.aliases.delete(alias) } }
  }

  protected normalize(path: string): string {
    const normalized = resolve("/", path)
    for (const [alias, target] of this.aliases) {
      if (normalized === alias) return target
      if (normalized.startsWith(`${alias}/`)) return join(target, normalized.slice(alias.length + 1))
    }
    return normalized
  }

  async rename(source: string, destination: string): Promise<void> {
    const sourcePath = this.normalize(source)
    const destinationPath = this.normalize(destination)
    await super.rename(source, destination)
    for (const [alias, target] of this.aliases) {
      if (target === sourcePath) this.aliases.set(alias, destinationPath)
      else if (target.startsWith(`${sourcePath}/`)) this.aliases.set(alias, join(destinationPath, target.slice(sourcePath.length + 1)))
    }
  }
}
