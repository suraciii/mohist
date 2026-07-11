import { afterEach, beforeEach } from "vitest"
import { setExternalProcessPolicyForTest, type ExternalProcessPolicy } from "../src/system/process-policy.js"

const denyExternalProcess: ExternalProcessPolicy = {
  assertAllowed(label) {
    throw new Error(`external process forbidden in default test: ${label}`)
  },
  register() {},
}

setExternalProcessPolicyForTest(denyExternalProcess)
beforeEach(() => setExternalProcessPolicyForTest(denyExternalProcess))
afterEach(() => setExternalProcessPolicyForTest(denyExternalProcess))
