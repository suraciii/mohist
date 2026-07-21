import { afterEach, beforeEach } from "vitest"
import { setExternalProcessPolicyForTest, type ExternalProcessPolicy } from "../src/system/process-policy.js"
import { setOpencodeModelDiscoveryForTest } from "../src/runtime/opencode-models.js"

const denyExternalProcess: ExternalProcessPolicy = {
  assertAllowed(label) {
    throw new Error(`external process forbidden in default test: ${label}`)
  },
  register() {},
}

setExternalProcessPolicyForTest(denyExternalProcess)
setOpencodeModelDiscoveryForTest(async () => ({ models: [], variants: {} }))
beforeEach(() => {
  setExternalProcessPolicyForTest(denyExternalProcess)
  setOpencodeModelDiscoveryForTest(async () => ({ models: [], variants: {} }))
})
afterEach(() => {
  setExternalProcessPolicyForTest(denyExternalProcess)
  setOpencodeModelDiscoveryForTest(async () => ({ models: [], variants: {} }))
})
