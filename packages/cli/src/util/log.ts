import path from "path"
import fs from "fs/promises"
import { createWriteStream, readdirSync } from "fs"
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

  const LOG_DIR = path.join(os.homedir(), ".mohist", "logs")
  const LOG_FILE_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{6}\.log$/
  const MAX_LOG_FILES = 10

  export async function init(options: Options) {
    if (options.level) level = options.level
    await cleanup()
    if (options.print) {
      const defaultWrite = (msg: any) => {
        process.stderr.write(msg)
        return msg.length
      }
      write = defaultWrite
      return
    }
    logpath = path.join(
      LOG_DIR,
      options.dev
        ? "dev.log"
        : new Date().toISOString().split(".")[0].replace(/:/g, "") + ".log",
    )
    await fs.mkdir(LOG_DIR, { recursive: true })
    if (options.dev) {
      await fs.truncate(logpath).catch(() => {})
    }
    const stream = createWriteStream(logpath, { flags: "a" })
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
      files = readdirSync(LOG_DIR)
        .filter((f) => LOG_FILE_PATTERN.test(f))
        .sort()
    } catch {
      return
    }
    if (files.length <= MAX_LOG_FILES) return
    const filesToDelete = files.slice(0, files.length - MAX_LOG_FILES)
    await Promise.all(
      filesToDelete.map((f) => fs.unlink(path.join(LOG_DIR, f)).catch(() => {})),
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
    function build(message: any, extra?: Record<string, any>) {
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

    const result: Logger = {
      debug(message?: any, extra?: Record<string, any>) {
        if (shouldLog("DEBUG")) {
          write("DEBUG " + build(message, extra))
        }
      },
      info(message?: any, extra?: Record<string, any>) {
        if (shouldLog("INFO")) {
          write("INFO  " + build(message, extra))
        }
      },
      error(message?: any, extra?: Record<string, any>) {
        if (shouldLog("ERROR")) {
          write("ERROR " + build(message, extra))
        }
      },
      warn(message?: any, extra?: Record<string, any>) {
        if (shouldLog("WARN")) {
          write("WARN  " + build(message, extra))
        }
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
