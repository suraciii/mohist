import { describe, expect, it } from "vitest"
import { formatDirectoryReclaimSummary } from "../src/runtime/opencode/reclaim-summary.js"

describe("formatDirectoryReclaimSummary", () => {
  it("keeps the fixed fields and reports no diagnostics without extra noise", () => {
    expect(formatDirectoryReclaimSummary({
      tracked: 3,
      candidates: 0,
      disposed: 0,
      busy: 0,
      failed: 0,
      blockedDirectories: [],
      diagnostics: [],
    })).toBe("workspace reclaim: tracked=3 candidates=0 disposed=0 busy=0 failed=0 diagnostics=none:0 omitted=0")
  })

  it("aggregates codes deterministically and omits categories after four", () => {
    expect(formatDirectoryReclaimSummary({
      tracked: 8,
      candidates: 5,
      disposed: 2,
      busy: 1,
      failed: 2,
      blockedDirectories: [],
      diagnostics: [
        { severity: "warning", code: "zeta", message: "hidden" },
        { severity: "info", code: "alpha", message: "hidden" },
        { severity: "warning", code: "alpha", message: "hidden" },
        { severity: "warning", code: "delta", message: "hidden" },
        { severity: "warning", code: "beta", message: "hidden" },
        { severity: "warning", code: "omega", message: "hidden" },
      ],
    })).toBe("workspace reclaim: tracked=8 candidates=5 disposed=2 busy=1 failed=2 diagnostics=alpha:2,beta:1,delta:1,omega:1 omitted=1")
  })
})
