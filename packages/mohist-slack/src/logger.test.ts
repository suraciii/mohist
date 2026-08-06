import { describe, expect, it } from "vitest"
import { createSlackLogger } from "./logger.js"

describe("mohist-slack logger", () => {
  it("writes strict logfmt with stable leading fields and escaped values", async () => {
    const lines: string[] = []
    const logger = createSlackLogger({
      clock: () => new Date("2026-08-05T05:30:00.123Z"),
      terminal: { write: (line) => lines.push(line) },
    })

    logger.child("adapter").error("ingress failed", {
      target: "connection:p 1:c=1",
      event: "app_mention",
      reason: "failed\nwithout token",
      absent: undefined,
      service: "forged",
    })
    await logger.flush()

    expect(lines).toEqual([
      "time=2026-08-05T05:30:00.123Z level=ERROR msg=\"ingress failed\" service=slack component=adapter target=\"connection:p 1:c=1\" event=app_mention reason=\"failed\\nwithout token\"\n",
    ])
  })

  it("does not let fields replace the fixed log contract", () => {
    const lines: string[] = []
    const logger = createSlackLogger({
      clock: () => new Date("2026-08-05T05:30:00.123Z"),
      terminal: { write: (line) => lines.push(line) },
    })

    logger.info("ready", { time: "forged", level: "DEBUG", msg: "forged", component: "forged" })

    expect(lines[0]).toBe(
      "time=2026-08-05T05:30:00.123Z level=INFO msg=ready service=slack component=slack\n",
    )
  })
})
