import { describe, expect, it } from "vitest"
import { sanitizedEnvironment } from "../src/system/process.js"

describe("sanitizedEnvironment", () => {
  it("RunnerSpawnedAgent_DisablesToolSelfUpdateNoise", () => {
    const env = sanitizedEnvironment({})

    expect(env.OPENCODE_DISABLE_UPDATE_CHECK).toBe("1")
    expect(env.OPENCODE_DISABLE_AUTO_UPDATE).toBe("1")
    expect(env.NO_UPDATE_NOTIFIER).toBe("1")
  })

  it("RunnerSpawnedAgent_DoesNotForwardOpencodeServerCredentials", () => {
    const env = sanitizedEnvironment({
      OPENCODE_SERVER_PASSWORD: "secret",
      OPENCODE_SERVER_USERNAME: "user",
    })

    expect(env.OPENCODE_SERVER_PASSWORD).toBeUndefined()
    expect(env.OPENCODE_SERVER_USERNAME).toBeUndefined()
  })
})
