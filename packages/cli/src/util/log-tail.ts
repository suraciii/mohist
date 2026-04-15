import fs from "fs/promises"
import path from "path"
import { Log } from "./log.js"

const DEFAULT_LIMIT = 500
const DEFAULT_MAX_BYTES = 250_000
const MAX_LIMIT = 5000
const MAX_BYTES = 1_000_000
const ROLLING_LOG_RE = /^mohist-\d{4}-\d{2}-\d{2}\.log$/

function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value))
}

export type LogTailResult = {
  file: string
  cursor: number
  lines: string[]
  truncated: boolean
}

function isRollingLogFile(file: string): boolean {
  return ROLLING_LOG_RE.test(path.basename(file))
}

async function resolveLogFile(file: string): Promise<string> {
  const stat = await fs.stat(file).catch(() => null)
  if (stat) return file
  if (!isRollingLogFile(file)) return file

  const dir = path.dirname(file)
  const entries = await fs.readdir(dir, { withFileTypes: true }).catch(() => null)
  if (!entries) return file

  const candidates = await Promise.all(
    entries
      .filter((entry) => entry.isFile() && ROLLING_LOG_RE.test(entry.name))
      .map(async (entry) => {
        const fullPath = path.join(dir, entry.name)
        const fileStat = await fs.stat(fullPath).catch(() => null)
        return fileStat ? { path: fullPath, mtimeMs: fileStat.mtimeMs } : null
      }),
  )
  const sorted = candidates
    .filter((entry): entry is NonNullable<typeof entry> => Boolean(entry))
    .sort((a, b) => b.mtimeMs - a.mtimeMs)
  return sorted[0]?.path ?? file
}

async function readLogSlice(params: {
  file: string
  cursor?: number
  limit: number
  maxBytes: number
}): Promise<Omit<LogTailResult, "file">> {
  const stat = await fs.stat(params.file).catch(() => null)
  if (!stat) {
    return { cursor: 0, lines: [], truncated: false }
  }

  const size = stat.size
  const maxBytes = clamp(params.maxBytes, 1, MAX_BYTES)
  const limit = clamp(params.limit, 1, MAX_LIMIT)
  const cursor =
    typeof params.cursor === "number" && Number.isFinite(params.cursor)
      ? Math.max(0, Math.floor(params.cursor))
      : undefined

  let truncated = false
  let start: number

  if (cursor != null) {
    if (cursor > size) {
      start = Math.max(0, size - maxBytes)
      truncated = start > 0
    } else {
      start = cursor
      if (size - start > maxBytes) {
        start = Math.max(0, size - maxBytes)
        truncated = true
      }
    }
  } else {
    start = Math.max(0, size - maxBytes)
    truncated = start > 0
  }

  if (size === 0 || size <= start) {
    return { cursor: size, lines: [], truncated }
  }

  const handle = await fs.open(params.file, "r")
  try {
    let prefix = ""
    if (start > 0) {
      const prefixBuf = Buffer.alloc(1)
      const prefixRead = await handle.read(prefixBuf, 0, 1, start - 1)
      prefix = prefixBuf.toString("utf8", 0, prefixRead.bytesRead)
    }

    const length = Math.max(0, size - start)
    const buffer = Buffer.alloc(length)
    const readResult = await handle.read(buffer, 0, length, start)
    const text = buffer.toString("utf8", 0, readResult.bytesRead)
    let lines = text.split("\n")
    if (start > 0 && prefix !== "\n") {
      lines = lines.slice(1)
    }
    if (lines.length > 0 && lines[lines.length - 1] === "") {
      lines = lines.slice(0, -1)
    }
    if (lines.length > limit) {
      lines = lines.slice(lines.length - limit)
    }

    return { cursor: size, lines, truncated }
  } finally {
    await handle.close()
  }
}

export async function readLogTail(params?: {
  cursor?: number
  limit?: number
  maxBytes?: number
}): Promise<LogTailResult> {
  const primaryFile = Log.file()
  const file = await resolveLogFile(primaryFile)
  const result = await readLogSlice({
    file,
    cursor: params?.cursor,
    limit: params?.limit ?? DEFAULT_LIMIT,
    maxBytes: params?.maxBytes ?? DEFAULT_MAX_BYTES,
  })
  return { file, ...result }
}
