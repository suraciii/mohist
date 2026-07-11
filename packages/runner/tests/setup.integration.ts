import { afterAll, afterEach, beforeEach } from "vitest"
import { setExternalProcessPolicyForTest, type ExternalProcessPolicy } from "../src/system/process-policy.js"
import { cleanupRegisteredChildren, registerTestChild } from "./support/child-process.js"

const allowExternalProcess: ExternalProcessPolicy = {
  assertAllowed() {},
  register: registerTestChild,
}

setExternalProcessPolicyForTest(allowExternalProcess)
beforeEach(() => setExternalProcessPolicyForTest(allowExternalProcess))
afterEach(async () => {
  try {
    await cleanupRegisteredChildren()
  } finally {
    setExternalProcessPolicyForTest(allowExternalProcess)
  }
})
afterAll(async () => {
  try {
    await cleanupRegisteredChildren()
  } finally {
    setExternalProcessPolicyForTest(null)
  }
})
