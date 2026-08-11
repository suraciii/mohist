import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  CredentialMasker,
  MAX_TASK_LOG_LINES,
  TaskLogCollector,
  TaskLogger,
} from "../src/runtime/task-log.js"

describe("CredentialMasker", () => {
  let masker: CredentialMasker
  beforeEach(() => {
    masker = new CredentialMasker()
  })

  it("MasksUserInfoPasswordInHttpsRemoteUrl", () => {
    const masked = masker.mask("fatal: unable to access 'https://alice:hunter2@github.com/x/y.git'")
    expect(masked).not.toContain("hunter2")
    expect(masked).not.toContain("alice")
    expect(masked).toContain("***")
  })

  it("MasksUserInfoPasswordInHttpRemoteUrl", () => {
    const masked = masker.mask("error: failed to fetch http://ci:supersecret@host.example/repo")
    expect(masked).not.toContain("supersecret")
    expect(masked).not.toContain("ci")
    expect(masked).toContain("***")
  })

  it("MasksTokenUsernameInOauthRemoteUrl", () => {
    const token = "ghp_abcdefghijklmnopqrstuvwxyz1234"
    const masked = masker.mask(`fatal: https://${token}:x-oauth-basic@github.com/org/repo.git failed`)
    expect(masked).not.toContain(token)
    expect(masked).not.toContain("x-oauth-basic")
    expect(masked).toContain("https://***@github.com/org/repo.git")
  })

  it("MasksTokenUsernameWithEmptyPasswordInRemoteUrl", () => {
    const token = "ghp_abcdefghijklmnopqrstuvwxyz1234"
    const masked = masker.mask(`fatal: https://${token}:@github.com/org/repo.git failed`)
    expect(masked).not.toContain(token)
    expect(masked).toContain("https://***@github.com/org/repo.git")
  })

  it("MasksTokenStyleUrlWithoutUser", () => {
    const masked = masker.mask("remote error: https://ghp_aaaaaaaaaaaaaaaaaaaa@github.com/x/y")
    expect(masked).not.toContain("ghp_aaaaaaaaaaaaaaaaaaaa")
    expect(masked).toContain("***")
  })

  it("MasksBearerAuthorizationHeader", () => {
    const masked = masker.mask("response: Authorization: Bearer abcdef0123456789abcdef0123456789")
    expect(masked).not.toContain("abcdef0123456789abcdef0123456789")
    expect(masked).toContain("Bearer ***")
  })

  it("MasksGitHubPatPrefix", () => {
    const masked = masker.mask("token ghp_abcdefghijklmnopqrstuvwxyz1234 leaked")
    expect(masked).not.toContain("abcdefghijklmnopqrstuvwxyz1234")
    expect(masked).toContain("ghp_***")
  })

  it("MasksGenericApiKeyShape", () => {
    const masked = masker.mask("authorization: sk-projabcdefghijklmnopqrstuv")
    expect(masked).not.toContain("sk-projabcdefghijklmnopqrstuv")
    expect(masked).toContain("sk-***")
  })

  it("MasksBasicAuthBlob", () => {
    const masked = masker.mask("Authorization: Basic Y2lfdXNlcjpwYXNz")
    expect(masked).not.toContain("Y2lfdXNlcjpwYXNz")
    expect(masked).toContain("Basic ***")
  })

  it("LeavesPlainTextUntouched", () => {
    expect(masker.mask("build succeeded in 3.2s")).toBe("build succeeded in 3.2s")
  })

  it("RegisterSecret_MasksRuntimeKnownToken", () => {
    masker.registerSecret("custom-secret-ABCDEF-1234567890")
    const masked = masker.mask("server said: custom-secret-ABCDEF-1234567890 was rejected")
    expect(masked).not.toContain("custom-secret-ABCDEF-1234567890")
    expect(masked).toContain("***")
  })

  it("RegisterSecret_IgnoresShortStrings", () => {
    masker.registerSecret("abc")
    expect(masker.mask("abc is fine")).toBe("abc is fine")
  })

  it("EmptyOrNonStringInputPassesThrough", () => {
    expect(masker.mask("")).toBe("")
    // Cast to any to verify the defensive guard without breaking TS.
    expect(masker.mask(undefined as unknown as string)).toBe(undefined as unknown as string)
  })
})

describe("TaskLogCollector", () => {
  let now: Date
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date("2026-07-01T00:00:00.000Z"))
    now = new Date()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it("AssignsMonotonicSeqOnAppend", () => {
    const collector = new TaskLogCollector()
    const first = collector.append("workspace-prep", "first")
    const second = collector.append("branch-check", "second")
    const third = collector.append("action:rebase", "third")

    expect(first).toBe(1)
    expect(second).toBe(2)
    expect(third).toBe(3)
    const flushed = collector.flush()
    expect(flushed.entries.map((e) => e.seq)).toEqual([1, 2, 3])
    expect(flushed.entries.map((e) => e.source)).toEqual(["workspace-prep", "branch-check", "action:rebase"])
  })

  it("StoresTimestampFromInjectedClock", () => {
    const collector = new TaskLogCollector({ now: () => new Date("2026-06-01T01:02:03.000Z") })
    collector.append("action", "hello")
    const flushed = collector.flush()
    expect(flushed.entries[0]!.timestamp.toISOString()).toBe("2026-06-01T01:02:03.000Z")
  })

  it("FlushReturnsAscendingSeq", () => {
    const collector = new TaskLogCollector({ maxLines: 10 })
    for (let i = 0; i < 5; i++) collector.append("action", `line ${i}`)
    const flushed = collector.flush()
    expect(flushed.entries.map((e) => e.seq)).toEqual([1, 2, 3, 4, 5])
    expect(flushed.truncated).toBe(false)
  })

  it("DropsOldestHeadOnOverflowAndKeepsTail", () => {
    const collector = new TaskLogCollector({ maxLines: 3 })
    for (let i = 0; i < 5; i++) collector.append("action", `line ${i}`)
    const flushed = collector.flush()

    expect(flushed.entries.map((e) => e.text)).toEqual(["line 2", "line 3", "line 4"])
    expect(flushed.entries.map((e) => e.seq)).toEqual([3, 4, 5])
    expect(flushed.truncated).toBe(true)
    expect(collector.getDiscardedCount()).toBe(2)
  })

  it("DoesNotReuseDiscardedSeq", () => {
    const collector = new TaskLogCollector({ maxLines: 2 })
    const first = collector.append("a", "x")
    const second = collector.append("a", "y")
    expect(first).toBe(1)
    expect(second).toBe(2)

    // Two more writes overflow the buffer. Discarded seqs are 1 and 2.
    const third = collector.append("a", "z")
    const fourth = collector.append("a", "w")
    expect(third).toBe(3)
    expect(fourth).toBe(4)

    // The retained buffer's seqs must be strictly greater than every
    // discarded seq — pagination remains stable.
    const firstSeq = collector.firstSeq()
    expect(firstSeq).toBe(3)

    const flushed = collector.flush()
    expect(flushed.entries.map((e) => e.seq)).toEqual([3, 4])
  })

  it("FirstSeqReturnsNullWhenEmpty", () => {
    const collector = new TaskLogCollector()
    expect(collector.firstSeq()).toBeNull()
  })

  it("TruncatedFlagStaysTrueOnceSet", () => {
    const collector = new TaskLogCollector({ maxLines: 2 })
    collector.append("a", "1")
    collector.append("a", "2")
    collector.append("a", "3")
    expect(collector.isTruncated()).toBe(true)

    // The collector is one-shot per work item; flush does not reset
    // the truncation flag because the persisted batch value matters
    // even if a future write is well within capacity.
    collector.flush()
    expect(collector.isTruncated()).toBe(true)
  })

  it("UsesDefaultMaxLinesFromNamedConstant", () => {
    expect(MAX_TASK_LOG_LINES).toBe(5_000)
    const collector = new TaskLogCollector()
    // Smoke: capacity should be the documented constant.
    for (let i = 0; i < MAX_TASK_LOG_LINES; i++) collector.append("a", `l${i}`)
    expect(collector.isTruncated()).toBe(false)
    collector.append("a", "overflow")
    expect(collector.isTruncated()).toBe(true)
    expect(collector.size()).toBe(MAX_TASK_LOG_LINES)
  })

  it("FallsBackToMaxWhenCustomValueIsInvalid", () => {
    const collector = new TaskLogCollector({ maxLines: 0 })
    expect(collector.size()).toBe(0)
    // The fallback value is MAX_TASK_LOG_LINES so a write at the
    // boundary should not overflow. We do not assert the exact size
    // (depends on constant) — only that a single write fits.
    collector.append("a", "fits")
    expect(collector.size()).toBe(1)
    expect(collector.isTruncated()).toBe(false)
  })
})

describe("TaskLogger single sink", () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date("2026-07-01T00:00:00.000Z"))
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it("MasksBeforeBuffering_SoRawCredentialNeverReachesTheBuffer", () => {
    const collector = new TaskLogCollector()
    const logger = new TaskLogger({ collector })
    logger.write("workspace-prep", "https://alice:hunter2@github.com/x.git")

    const flushed = collector.flush()
    expect(flushed.entries).toHaveLength(1)
    const text = flushed.entries[0]!.text
    expect(text).not.toContain("hunter2")
    expect(text).not.toContain("alice")
  })

  it("ReturnsAssignedSeqAndKeepsMonotonicOrderAcrossWrites", () => {
    const collector = new TaskLogCollector()
    const logger = new TaskLogger({ collector })
    const seq1 = logger.write("workspace-prep", "first")
    const seq2 = logger.write("branch-check", "second")
    const seq3 = logger.write("action:rebase", "third")

    expect([seq1, seq2, seq3]).toEqual([1, 2, 3])
    const flushed = collector.flush()
    expect(flushed.entries.map((e) => e.source)).toEqual([
      "workspace-prep",
      "branch-check",
      "action:rebase",
    ])
  })

  it("HeadDropDoesNotReuseSeqOnTheSinkEither", () => {
    const collector = new TaskLogCollector({ maxLines: 2 })
    const logger = new TaskLogger({ collector })
    const seq1 = logger.write("a", "first")
    const seq2 = logger.write("a", "second")
    const seq3 = logger.write("a", "third")
    expect([seq1, seq2, seq3]).toEqual([1, 2, 3])
    expect(collector.isTruncated()).toBe(true)
    const flushed = collector.flush()
    expect(flushed.entries.map((e) => e.text)).toEqual(["second", "third"])
  })

  it("FlushDelegatesToCollector", () => {
    const collector = new TaskLogCollector()
    const logger = new TaskLogger({ collector })
    logger.write("a", "hello")
    const batch = logger.flush()
    expect(batch.entries.map((e) => e.text)).toEqual(["hello"])
    expect(batch.truncated).toBe(false)
  })

  it("CarriesTimestampFromInjectedClock", () => {
    const collector = new TaskLogCollector({ now: () => new Date("2026-06-15T12:00:00.000Z") })
    const logger = new TaskLogger({ collector })
    logger.write("a", "hi")
    const flushed = logger.flush()
    expect(flushed.entries[0]!.timestamp.toISOString()).toBe("2026-06-15T12:00:00.000Z")
  })

  it("HonorsMaskerRegisterSecret", () => {
    const collector = new TaskLogCollector()
    const masker = new CredentialMasker()
    masker.registerSecret("runner-token-ABCDEF-1234567890")
    const logger = new TaskLogger({ collector, masker })
    logger.write("a", "logged in with runner-token-ABCDEF-1234567890")
    const flushed = logger.flush()
    expect(flushed.entries[0]!.text).toContain("***")
    expect(flushed.entries[0]!.text).not.toContain("runner-token-ABCDEF-1234567890")
  })
})

describe("TaskLogCollector incremental drain and sent-sequence watermark", () => {
  let now: Date
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date("2026-07-01T00:00:00.000Z"))
    now = new Date()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it("DrainReturnsNothingBeforeAnyAppend", () => {
    const collector = new TaskLogCollector()
    expect(collector.drain()).toBeNull()
    expect(collector.pendingSinceWatermark()).toBe(0)
  })

  it("DrainReturnsAllEntriesOnFirstCallAndAdvancesWatermark", () => {
    const collector = new TaskLogCollector()
    collector.append("a", "1")
    collector.append("a", "2")
    collector.append("a", "3")
    const batch = collector.drain()
    expect(batch).not.toBeNull()
    expect(batch!.entries.map((e) => e.seq)).toEqual([1, 2, 3])
    expect(collector.pendingSinceWatermark()).toBe(0)
  })

  it("SnapshotDoesNotAdvanceWatermark", () => {
    const collector = new TaskLogCollector()
    collector.append("a", "1")

    expect(collector.snapshot().entries.map((entry) => entry.seq)).toEqual([1])
    expect(collector.drain()!.entries.map((entry) => entry.seq)).toEqual([1])
  })

  it("SecondDrainExcludesLinesAlreadySent", () => {
    const collector = new TaskLogCollector()
    collector.append("a", "1")
    collector.append("a", "2")
    collector.append("a", "3")
    const first = collector.drain()
    expect(first!.entries.map((e) => e.seq)).toEqual([1, 2, 3])

    collector.append("a", "4")
    collector.append("a", "5")
    const second = collector.drain()
    expect(second!.entries.map((e) => e.seq)).toEqual([4, 5])
    expect(second!.entries.map((e) => e.text)).toEqual(["4", "5"])
  })

  it("DrainReturnsNullWhenNoNewLinesSinceLastDrain", () => {
    const collector = new TaskLogCollector()
    collector.append("a", "1")
    collector.append("a", "2")
    const first = collector.drain()
    expect(first!.entries).toHaveLength(2)

    // No new lines appended between drains.
    expect(collector.drain()).toBeNull()
    expect(collector.pendingSinceWatermark()).toBe(0)
  })

  it("DrainReturnsDefensiveCopy_LaterAppendsDoNotMutateReturnedBatch", () => {
    const collector = new TaskLogCollector()
    collector.append("a", "1")
    const first = collector.drain()
    expect(first!.entries).toHaveLength(1)
    collector.append("a", "2")
    expect(first!.entries).toHaveLength(1)
    expect(first!.entries[0]!.text).toBe("1")
  })

  it("WatermarkAdvancesPastHeadDroppedSeqs_AndDrainOnlyReturnsLiveEntries", () => {
    const collector = new TaskLogCollector({ maxLines: 3 })
    for (let i = 0; i < 5; i++) collector.append("a", `line ${i}`)
    // Buffer keeps tail (seq 3, 4, 5); discarded seqs 1, 2 are gone.
    const drained = collector.drain()
    expect(drained!.entries.map((e) => e.seq)).toEqual([3, 4, 5])
    expect(drained!.truncated).toBe(true)
    // Subsequent appends must still respect the watermark: no seq
    // 1 or 2 reappears.
    collector.append("a", "line 5")
    const second = collector.drain()
    expect(second!.entries.map((e) => e.seq)).toEqual([6])
  })

  it("FlushAdvancesWatermarkSoFollowupDrainIsNull", () => {
    const collector = new TaskLogCollector()
    collector.append("a", "1")
    collector.append("a", "2")
    const terminal = collector.flush()
    expect(terminal.entries.map((e) => e.seq)).toEqual([1, 2])
    // After flush the watermark is past the tail — drain must be null.
    expect(collector.drain()).toBeNull()
    expect(collector.pendingSinceWatermark()).toBe(0)
  })

  it("FlushStillReturnsFullSnapshot_NotJustUnsentLines", () => {
    // flush() is the terminal reconciliation batch and must return
    // the COMPLETE snapshot, including lines already drained. This
    // is the design D1 reconciliation contract.
    const collector = new TaskLogCollector()
    collector.append("a", "1")
    collector.append("a", "2")
    collector.append("a", "3")
    const drained = collector.drain()
    expect(drained!.entries.map((e) => e.seq)).toEqual([1, 2, 3])
    collector.append("a", "4")
    const terminal = collector.flush()
    expect(terminal.entries.map((e) => e.seq)).toEqual([1, 2, 3, 4])
  })

  it("SetAppendListenerFiresSynchronouslyOnEachAppend_AndCanBeCleared", () => {
    const collector = new TaskLogCollector()
    const seen: number[] = []
    const listener = (entry: { seq: number }) => {
      seen.push(entry.seq)
    }
    collector.setAppendListener(listener)
    collector.append("a", "1")
    collector.append("a", "2")
    expect(seen).toEqual([1, 2])
    collector.setAppendListener(null)
    collector.append("a", "3")
    expect(seen).toEqual([1, 2])
  })

  it("PendingSinceWatermarkReflectsUnflushedLines", () => {
    const collector = new TaskLogCollector()
    expect(collector.pendingSinceWatermark()).toBe(0)
    collector.append("a", "1")
    collector.append("a", "2")
    expect(collector.pendingSinceWatermark()).toBe(2)
    collector.drain()
    expect(collector.pendingSinceWatermark()).toBe(0)
    collector.append("a", "3")
    expect(collector.pendingSinceWatermark()).toBe(1)
  })

  it("AppendListenerSeesTheAssignedSeq_BeforeDrainObservedIt", () => {
    const collector = new TaskLogCollector()
    let seqAtFire: number | null = null
    let drainCount = 0
    collector.setAppendListener((entry) => {
      seqAtFire = entry.seq
      // At the moment of the append, the watermark is one less than
      // the new entry's seq (this is the FIRST time pendingSinceWatermark
      // would include the entry).
      drainCount = collector.pendingSinceWatermark()
    })
    collector.append("a", "hello")
    expect(seqAtFire).toBe(1)
    expect(drainCount).toBe(1)
  })
})
