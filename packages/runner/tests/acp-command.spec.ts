import { afterEach, describe, expect, it } from "vitest"
import { acpArgs, acpCommand } from "../src/runtime/acp-command.js"

const originalCommand = process.env.MOHIST_AGENT_COMMAND
const originalArgs = process.env.MOHIST_AGENT_ARGS

afterEach(() => {
  setEnv("MOHIST_AGENT_COMMAND", originalCommand)
  setEnv("MOHIST_AGENT_ARGS", originalArgs)
})

describe("acp command", () => {
  it("DefaultAgentCommand_RunsOpencodeAcpPure", () => {
    delete process.env.MOHIST_AGENT_COMMAND
    delete process.env.MOHIST_AGENT_ARGS

    expect(acpCommand()).toBe("opencode")
    expect(acpArgs()).toEqual(["acp", "--pure"])
  })

  it("ConfiguredAgentArgs_OverridePureDefault", () => {
    process.env.MOHIST_AGENT_COMMAND = "custom-agent"
    process.env.MOHIST_AGENT_ARGS = JSON.stringify(["acp"])

    expect(acpCommand()).toBe("custom-agent")
    expect(acpArgs()).toEqual(["acp"])
  })
})

function setEnv(key: string, value: string | undefined) {
  if (value === undefined) delete process.env[key]
  else process.env[key] = value
}
