export type ConsoleLevel = "error" | "warn"

export class UnexpectedConsoleRecorder {
  private readonly calls = new Map<string, number>()

  record(level: ConsoleLevel, values: unknown[]) {
    const message = `${level}: ${values.map(formatConsoleValue).join(" ")}`
    this.calls.set(message, (this.calls.get(message) ?? 0) + 1)
  }

  clear() {
    this.calls.clear()
  }

  takeError(): Error | null {
    if (this.calls.size === 0) return null

    const messages = [...this.calls.entries()]
      .map(([message, count]) => `  - ${message}${count === 1 ? "" : ` (${count}x)`}`)
      .join("\n")
    this.clear()
    return new Error(`Unexpected console output:\n${messages}`)
  }
}

function formatConsoleValue(value: unknown): string {
  if (value instanceof Error) return `${value.name}: ${value.message}`
  if (typeof value === "string") return value

  try {
    return JSON.stringify(value) ?? String(value)
  } catch {
    return String(value)
  }
}
