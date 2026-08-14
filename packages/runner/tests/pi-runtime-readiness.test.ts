import { describe, expect, it } from "vitest"
import { PiRuntime, type PiSdkFactory, type PiSdkServices } from "../src/runtime/pi/index.js"

describe("PiRuntime readiness", () => {
  it("keeps the runtime unavailable after catalog failure and retries startup", async () => {
    let creates = 0
    let closes = 0
    const sdkFactory: PiSdkFactory = {
      create: async () => {
        creates += 1
        if (creates === 1) {
          return {
            catalog: async () => { throw new Error("catalog unavailable") },
            close: async () => { closes += 1 },
          } as unknown as PiSdkServices
        }
        return {
          catalog: async () => [],
          close: async () => {},
        } as unknown as PiSdkServices
      },
    }
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory })

    const failed = await runtime.start()
    expect(failed).toMatchObject({
      ok: false,
      error: {
        kind: "unavailable-runtime",
        diagnostics: [{ severity: "error", code: "pi-catalog-failed" }],
      },
    })
    expect(runtime.ready()).toBe(false)
    expect(runtime.catalog()).toBeNull()
    expect(closes).toBe(1)

    const recovered = await runtime.start()
    expect(recovered).toMatchObject({ ok: true, value: { ready: true, catalog: { models: [] }, diagnostic: null } })
    expect(creates).toBe(2)
  })
})
