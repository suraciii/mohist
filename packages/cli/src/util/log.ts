import path from "path"
import fs from "fs/promises"
import { createWriteStream, readdirSync, statSync } from "fs"
import os from "os"

export namespace Log {
  export type Level = "DEBUG" | "INFO" | "WARN" | "ERROR"

  const levelPriority: Record<Level, number> = {
    DEBUG: 0,
    INFO: 1,
    WARN: 2,
    ERROR: 3,
  }

  let level: Level = "INFO"

  function shouldLog(input: Level): boolean {
    return levelPriority[input] >= levelPriority[level]
  }

  export type Logger = {
    debug(message?: any, extra?: Record<string, any>): void
    info(message?: any, extra?: Record<string, any>): void
    error(message?: any, extra?: Record<string, any>): void
    warn(message?: any, extra?: Record<string, any>): void
    tag(key: string, value: string): Logger
    clone(): Logger
    time(
      message: string,
      extra?: Record<string, any>,
    ): {
      stop(): void
      [Symbol.dispose](): void
    }
  }

  const loggers = new Map<string, Logger>()

  export const Default = create({ service: "default" })

  export interface Options {
    print: boolean
    dev?: boolean
    level?: Level
  }

  let logpath = ""
  export function file() {
    return logpath
  }

  let write = (msg: any) => {
    process.stderr.write(msg)
    return msg.length
  }

  let printMode = true

  const LOG_DIR = path.join(os.homedir(), ".mohist", "logs")
  const LOG_PREFIX = "mohist"
  const LOG_SUFFIX = ".log"
  const ROLLING_PATTERN = /^mohist-\d{4}-\d{2}-\d{2}\.log$/

  function formatLocalDate(): string {
    const d = new Date()
    const yyyy = d.getFullYear()
    const mm = String(d.getMonth() + 1).padStart(2, "0")
    const dd = String(d.getDate()).padStart(2, "0")
    return `${yyyy}-${mm}-${dd}`
  }

  export async function init(options: Options) {
    if (options.level) level = options.level
    await cleanup()
    printMode = options.print

    logpath = path.join(
      LOG_DIR,
      options.dev ? "dev.log" : `${LOG_PREFIX}-${formatLocalDate()}${LOG_SUFFIX}`,
    )
    await fs.mkdir(LOG_DIR, { recursive: true })
    if (options.dev) {
      await fs.truncate(logpath).catch(() => {})
    }
    const stream = createWriteStream(logpath, { flags: "a" })

    if (options.print) {
      write = (msg: any) => {
        process.stderr.write(msg)
        stream.write(msg)
        return msg.length
      }
      return
    }

    write = async (msg: any) => {
      return new Promise((resolve, reject) => {
        stream.write(msg, (err) => {
          if (err) reject(err)
          else resolve(msg.length)
        })
      })
    }
  }

  async function cleanup() {
    try {
      await fs.mkdir(LOG_DIR, { recursive: true })
    } catch {
      return
    }
    let files: string[]
    try {
      files = readdirSync(LOG_DIR).filter((f) => ROLLING_PATTERN.test(f))
    } catch {
      return
    }
    const now = Date.now()
    const ms24h = 24 * 60 * 60 * 1000
    await Promise.all(
      files
        .filter((f) => {
          try {
            return now - statSync(path.join(LOG_DIR, f)).mtimeMs > ms24h
          } catch {
            return false
          }
        })
        .map((f) => fs.unlink(path.join(LOG_DIR, f)).catch(() => {})),
    )
  }

  function formatError(error: Error, depth = 0): string {
    const result = error.message
    return error.cause instanceof Error && depth < 10
      ? result + " Caused by: " + formatError(error.cause, depth + 1)
      : result
  }

  let last = Date.now()

  function createLogger(tags: Record<string, any>): Logger {
    function buildText(message: any, extra?: Record<string, any>): string {
      const prefix = Object.entries({
        ...tags,
        ...extra,
      })
        .filter(([_, value]) => value !== undefined && value !== null)
        .map(([key, value]) => {
          const p = `${key}=`
          if (value instanceof Error) return p + formatError(value)
          if (typeof value === "object") return p + JSON.stringify(value)
          return p + value
        })
        .join(" ")
      const next = new Date()
      const diff = next.getTime() - last
      last = next.getTime()
      return (
        [next.toISOString().split(".")[0], "+" + diff + "ms", prefix, message]
          .filter(Boolean)
          .join(" ") + "\n"
      )
    }

    function buildJson(
      lvl: Level,
      message: any,
      extra?: Record<string, any>,
    ): string {
      const next = new Date()
      const diffMs = next.getTime() - last
      last = next.getTime()
      const { service: _, ...restTags } = tags
      const merged = { ...restTags, ...extra }
      const entry: Record<string, any> = {
        level: lvl,
        time: next.toISOString(),
        diffMs,
        service: tags["service"] ?? "default",
        message: String(message ?? ""),
      }
      for (const [k, v] of Object.entries(merged)) {
        if (v !== undefined && v !== null) {
          entry[k] = v instanceof Error ? formatError(v) : v
        }
      }
      return JSON.stringify(entry) + "\n"
    }

    function emit(lvl: Level, message: any, extra?: Record<string, any>) {
      if (!shouldLog(lvl)) return
      if (printMode) {
        write(lvl.padEnd(5) + " " + buildText(message, extra))
      } else {
        write(buildJson(lvl, message, extra))
      }
    }

    const result: Logger = {
      debug(message?: any, extra?: Record<string, any>) {
        emit("DEBUG", message, extra)
      },
      info(message?: any, extra?: Record<string, any>) {
        emit("INFO", message, extra)
      },
      error(message?: any, extra?: Record<string, any>) {
        emit("ERROR", message, extra)
      },
      warn(message?: any, extra?: Record<string, any>) {
        emit("WARN", message, extra)
      },
      tag(key: string, value: string) {
        if (tags) tags[key] = value
        return result
      },
      clone() {
        return createLogger({ ...tags })
      },
      time(message: string, extra?: Record<string, any>) {
        const now = Date.now()
        result.info(message, { status: "started", ...extra })
        function stop() {
          result.info(message, {
            status: "completed",
            duration: Date.now() - now,
            ...extra,
          })
        }
        return {
          stop,
          [Symbol.dispose]() {
            stop()
          },
        }
      },
    }

    return result
  }

  export function create(tags?: Record<string, any>) {
    tags = tags || {}

    const service = tags["service"]
    if (service && typeof service === "string") {
      const cached = loggers.get(service)
      if (cached) {
        return cached
      }
    }

    const result = createLogger(tags)

    if (service && typeof service === "string") {
      loggers.set(service, result)
    }

    return result
  }
}
