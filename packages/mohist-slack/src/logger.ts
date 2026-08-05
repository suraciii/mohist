export type SlackLogFields = Record<string, unknown>

export interface SlackLogTerminal {
  write(line: string): void
}

export interface SlackLogger {
  info(message: string, fields?: SlackLogFields): void
  error(message: string, fields?: SlackLogFields): void
  child(component: string): SlackLogger
  flush(): Promise<void>
}

export interface SlackLoggerOptions {
  clock?: () => Date
  terminal?: SlackLogTerminal
}

const processTerminal: SlackLogTerminal = {
  write(line) {
    process.stderr.write(line)
  },
}

const RESERVED_FIELD_KEYS = new Set(["time", "level", "msg", "service", "component"])

export function createSlackLogger(options: SlackLoggerOptions = {}): SlackLogger {
  const clock = options.clock ?? (() => new Date())
  const terminal = options.terminal ?? processTerminal

  const emit = (level: "INFO" | "ERROR", component: string, message: string, fields?: SlackLogFields): void => {
    try {
      terminal.write(formatLogLine(clock(), level, message, component, fields))
    } catch {
    }
  }

  const child = (component: string): SlackLogger => ({
    info: (message, fields) => emit("INFO", component, message, fields),
    error: (message, fields) => emit("ERROR", component, message, fields),
    child,
    flush: async () => {},
  })

  return child("slack")
}

function formatLogLine(
  date: Date,
  level: "INFO" | "ERROR",
  message: string,
  component: string,
  fields?: SlackLogFields,
): string {
  const values: Array<[string, unknown]> = [
    ["time", date.toISOString()],
    ["level", level],
    ["msg", message],
    ["service", "slack"],
    ["component", component],
  ]
  for (const [key, value] of Object.entries(fields ?? {})) {
    if (!RESERVED_FIELD_KEYS.has(key)) values.push([key, value])
  }
  return values
    .flatMap(([key, value]) => {
      const formatted = formatFieldValue(value)
      return formatted === null ? [] : [`${key}=${formatted}`]
    })
    .join(" ") + "\n"
}

function formatFieldValue(value: unknown): string | null {
  if (value === undefined || value === null) return null
  if (typeof value === "string") return formatString(value)
  if (typeof value === "number" || typeof value === "boolean" || typeof value === "bigint") return String(value)
  if (typeof value === "object") {
    try {
      const serialized = JSON.stringify(value)
      return serialized === undefined ? null : formatString(serialized)
    } catch {
      return null
    }
  }
  return formatString(String(value))
}

function formatString(value: string): string {
  return needsQuoting(value) ? quoteLogfmt(value) : value
}

function quoteLogfmt(value: string): string {
  let result = '"'
  for (const character of value) {
    const codePoint = character.codePointAt(0)!
    switch (character) {
      case "\\": result += "\\\\"; break
      case '"': result += '\\"'; break
      case "\n": result += "\\n"; break
      case "\r": result += "\\r"; break
      case "\t": result += "\\t"; break
      case "\b": result += "\\b"; break
      case "\f": result += "\\f"; break
      case "\u000b": result += "\\v"; break
      case "\u0007": result += "\\a"; break
      default:
        if (codePoint < 0x20 || (codePoint >= 0x7f && codePoint <= 0x9f))
          result += `\\x${codePoint.toString(16).padStart(2, "0")}`
        else
          result += character
    }
  }
  return `${result}"`
}

function needsQuoting(value: string): boolean {
  if (value.length === 0 || /[\s=\\"]/.test(value)) return true
  for (const character of value) {
    const codePoint = character.codePointAt(0)!
    if (codePoint < 0x20 || (codePoint >= 0x7f && codePoint <= 0x9f)) return true
  }
  return false
}

const noopLogger: SlackLogger = {
  info() {},
  error() {},
  child() { return noopLogger },
  flush: async () => {},
}

let activeLogger: SlackLogger = noopLogger

export function configureSlackLogger(options: SlackLoggerOptions = {}): SlackLogger {
  activeLogger = createSlackLogger(options)
  return activeLogger
}

export function setSlackLoggerForTest(logger: SlackLogger): () => void {
  const previous = activeLogger
  activeLogger = logger
  return () => {
    activeLogger = previous
  }
}

export const slackLogger: SlackLogger = {
  info(message, fields) { activeLogger.info(message, fields) },
  error(message, fields) { activeLogger.error(message, fields) },
  child(component) {
    return {
      info(message, fields) { activeLogger.child(component).info(message, fields) },
      error(message, fields) { activeLogger.child(component).error(message, fields) },
      child(nestedComponent) { return slackLogger.child(nestedComponent) },
      flush() { return activeLogger.flush() },
    }
  },
  flush() { return activeLogger.flush() },
}
