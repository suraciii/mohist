import { describe, expect, it } from "vitest"
import {
  parseAheadBehind,
  parseCommits,
  parseDiffFiles,
  parseNumstatTotal,
  splitDiffByFile,
} from "../src/server/git-parsers.js"

// Direct unit tests for the pure git-output parsers. Behaviour must remain
// byte-identical to the former inline implementations: numstat parsers
// tolerate binary entries and malformed lines; ahead/behind and commit
// parsers tolerate malformed output; and per-file diffs use the b/ path key
// required by `GetDiff`.

describe("git-parsers", () => {
  describe("parseDiffFiles", () => {
    it("binary file line yields zero additions, zero deletions, isBinary true", () => {
      // Spec scenario "Binary file line yields zero additions and deletions":
      //   WHEN a numstat line is `-\t-\tbin/logo.png`
      //   THEN the parsed file has { additions: 0, deletions: 0, isBinary: true }
      const result = parseDiffFiles("-\t-\tbin/logo.png", "")
      expect(result).toEqual([
        { file: "bin/logo.png", additions: 0, deletions: 0, diff: "", isBinary: true },
      ])
    })

    it("skips blank lines and lines with fewer than three tab-separated fields", () => {
      // Spec scenario "Malformed numstat lines are skipped":
      //   WHEN the numstat output contains blank lines or lines with fewer than
      //   three tab fields THEN those lines contribute no file and do not
      //   affect the totals.
      const numstat = [
        "3\t1\ta.txt",
        "",
        "   ",
        "two\tfields",
        "one",
        "-\t-\tb.bin",
      ].join("\n")
      const result = parseDiffFiles(numstat, "")
      expect(result.map(f => f.file)).toEqual(["a.txt", "b.bin"])
      expect(result.find(f => f.file === "a.txt")).toMatchObject({
        additions: 3,
        deletions: 1,
        isBinary: false,
      })
      expect(result.find(f => f.file === "b.bin")).toMatchObject({
        additions: 0,
        deletions: 0,
        isBinary: true,
      })
    })

    it("joins per-file patch under the b/ path key when the diff contains `diff --git`", () => {
      // Spec scenario "Per-file diff is keyed by the b/ path":
      //   WHEN the full diff contains `diff --git a/foo.txt b/foo.txt`
      //   followed by patch lines THEN the matching files entry has
      //   file: "foo.txt" and diff containing the joined patch for that file.
      const fullDiff = [
        "diff --git a/foo.txt b/foo.txt",
        "index 111..222 100644",
        "--- a/foo.txt",
        "+++ b/foo.txt",
        "@@ -1 +1 @@",
        "-old",
        "+new",
        "diff --git a/sub/bar.md b/sub/bar.md",
        "index 333..444 100644",
        "--- a/sub/bar.md",
        "+++ b/sub/bar.md",
        "@@ -1 +1 @@",
        "-prev",
        "+next",
      ].join("\n")
      const numstat = "3\t1\tfoo.txt\n5\t0\tsub/bar.md"
      const files = parseDiffFiles(numstat, fullDiff)

      const foo = files.find(f => f.file === "foo.txt")
      const bar = files.find(f => f.file === "sub/bar.md")
      expect(foo).toBeDefined()
      expect(bar).toBeDefined()
      expect(foo!.diff).toContain("diff --git a/foo.txt b/foo.txt")
      expect(foo!.diff).toContain("+new")
      expect(foo!.diff).not.toContain("diff --git a/sub/bar.md b/sub/bar.md")
      expect(bar!.diff).toContain("diff --git a/sub/bar.md b/sub/bar.md")
      expect(bar!.diff).toContain("+next")
      expect(bar!.diff).not.toContain("diff --git a/foo.txt b/foo.txt")
    })

    it("falls back to empty diff when numstat references a file absent from the full diff", () => {
      const result = parseDiffFiles("1\t0\tmissing.txt", "")
      expect(result).toHaveLength(1)
      expect(result[0]).toMatchObject({
        file: "missing.txt",
        additions: 1,
        deletions: 0,
        diff: "",
        isBinary: false,
      })
    })

    it("treats non-integer additions/deletions as zero (parseInt fallback)", () => {
      const result = parseDiffFiles("abc\txyz\ta.txt", "")
      expect(result).toEqual([
        { file: "a.txt", additions: 0, deletions: 0, diff: "", isBinary: false },
      ])
    })

    it("returns an empty array for empty input", () => {
      expect(parseDiffFiles("", "")).toEqual([])
    })
  })

  describe("splitDiffByFile", () => {
    it("keys each file's patch by the b/ path and concatenates lines verbatim", () => {
      const diff = [
        "diff --git a/x.txt b/x.txt",
        "index 1..2 100644",
        "--- a/x.txt",
        "+++ b/x.txt",
        "@@ -1 +1 @@",
        "-a",
        "+b",
        "diff --git a/dir/y.md b/dir/y.md",
        "index 3..4 100644",
        "--- a/dir/y.md",
        "+++ b/dir/y.md",
        "@@ -1 +1 @@",
        "-prev",
        "+next",
      ].join("\n")
      const patches = splitDiffByFile(diff)
      expect(Object.keys(patches).sort()).toEqual(["dir/y.md", "x.txt"])
      expect(patches["x.txt"]).toContain("diff --git a/x.txt b/x.txt")
      expect(patches["x.txt"]).toContain("+b")
      expect(patches["x.txt"]).not.toContain("diff --git a/dir/y.md b/dir/y.md")
      expect(patches["dir/y.md"]).toContain("diff --git a/dir/y.md b/dir/y.md")
      expect(patches["dir/y.md"]).toContain("+next")
    })

    it("returns an empty object for empty or whitespace-only diff input", () => {
      expect(splitDiffByFile("")).toEqual({})
      expect(splitDiffByFile("   \n\t\n")).toEqual({})
    })

    it("drops lines that come before any `diff --git ` header (no currentPath yet)", () => {
      const diff = [
        "some preamble line",
        "diff --git a/q.txt b/q.txt",
        "@@ -1 +1 @@",
        "-x",
        "+y",
      ].join("\n")
      const patches = splitDiffByFile(diff)
      expect(patches["q.txt"]).toBeDefined()
      expect(patches["q.txt"]).toContain("@@ -1 +1 @@")
      // The preamble line is accumulated before the first `diff --git` header
      // arrives, so it is dropped by `flush()` (currentPath is null at that
      // point). The per-file block starts cleanly at `diff --git a/q.txt b/q.txt`.
      expect(patches["q.txt"]).not.toMatch(/^some preamble line/)
    })

    it("ignores the b/ token when it is missing or the diff header is malformed", () => {
      // No "diff --git a/... b/..." header at all — the buffer accumulates
      // but never flushes because currentPath stays null.
      const patches = splitDiffByFile("@@ -1 +1 @@\n-x\n+y\n")
      expect(patches).toEqual({})
    })
  })

  describe("parseCommits", () => {
    it("parses tab-separated log lines in field order with empty files array", () => {
      // Spec scenario "Commits are parsed from the tab-separated log":
      //   WHEN the log emits <hash>\t<shortHash>\t<subject>\t<author>\t<date>
      //   THEN commits contains one entry per non-empty line in the same
      //   field order with files: []
      const log = [
        "abc1234567\tabc1234\tfirst commit\tAlice\t2026-01-01T00:00:00+00:00",
        "def8901234\tdef8901\tsecond commit\tBob\t2026-01-02T00:00:00+00:00",
      ].join("\n")
      expect(parseCommits(log)).toEqual([
        { hash: "abc1234567", shortHash: "abc1234", message: "first commit", author: "Alice", date: "2026-01-01T00:00:00+00:00", files: [] },
        { hash: "def8901234", shortHash: "def8901", message: "second commit", author: "Bob", date: "2026-01-02T00:00:00+00:00", files: [] },
      ])
    })

    it("drops log lines with fewer than five tab-separated fields", () => {
      // Spec scenario "parseCommits drops short lines":
      //   WHEN the log output contains a line with fewer than five tab fields
      //   THEN that line is excluded from the parsed commits array
      const log = [
        "abc\tdef\tghi",
        "abc\tdef\tghi\tjkl",
        "h\ts\tsuba\tauthor\t2026-01-01",
      ].join("\n")
      const commits = parseCommits(log)
      expect(commits).toHaveLength(1)
      expect(commits[0]).toMatchObject({
        hash: "h",
        shortHash: "s",
        message: "suba",
        author: "author",
        date: "2026-01-01",
      })
    })

    it("returns an empty array on empty input", () => {
      expect(parseCommits("")).toEqual([])
    })

    it("returns an empty array on whitespace-only input", () => {
      expect(parseCommits("   \n\t\n")).toEqual([])
    })
  })

  describe("parseAheadBehind", () => {
    it("returns [ahead, behind] in that order for well-formed output", () => {
      // Spec scenario "parseAheadBehind on well-formed output":
      //   WHEN the rev-list output is `3\t2\n`
      //   THEN parseAheadBehind returns [2, 3] (ahead=2, behind=3)
      expect(parseAheadBehind("3\t2\n")).toEqual([2, 3])
    })

    it("treats non-integer fields as zero on well-formed two-field input", () => {
      expect(parseAheadBehind("foo\tbar")).toEqual([0, 0])
    })

    it("returns [0, 0] for empty output", () => {
      // Spec scenario "parseAheadBehind on malformed output":
      //   WHEN the rev-list output is empty or a single field
      //   THEN parseAheadBehind returns [0, 0]
      expect(parseAheadBehind("")).toEqual([0, 0])
      expect(parseAheadBehind("\n")).toEqual([0, 0])
    })

    it("returns [0, 0] for single-field output", () => {
      expect(parseAheadBehind("42")).toEqual([0, 0])
    })

    it("returns [0, 0] for output with more than two tab fields", () => {
      expect(parseAheadBehind("3\t2\textra")).toEqual([0, 0])
    })
  })

  describe("parseNumstatTotal", () => {
    it("aggregates filesChanged, additions, and deletions across valid lines", () => {
      // Spec scenario "parseNumstatTotal aggregates across files":
      //   WHEN numstat contains `3\t1\ta.txt` and `-\t-\tb.bin`
      //   THEN parseNumstatTotal returns { filesChanged: 2, additions: 3, deletions: 1 }
      const result = parseNumstatTotal("3\t1\ta.txt\n-\t-\tb.bin")
      expect(result).toEqual({ filesChanged: 2, additions: 3, deletions: 1 })
    })

    it("treats binary entries as zero additions and zero deletions but still counts the file", () => {
      const result = parseNumstatTotal("-\t-\ta.bin\n-\t-\tb.bin")
      expect(result).toEqual({ filesChanged: 2, additions: 0, deletions: 0 })
    })

    it("skips blank lines and lines with fewer than three tab-separated fields", () => {
      const numstat = [
        "2\t0\ta.txt",
        "",
        "   ",
        "one\ttwo",
        "single",
        "10\t2\tb.txt",
      ].join("\n")
      expect(parseNumstatTotal(numstat)).toEqual({ filesChanged: 2, additions: 12, deletions: 2 })
    })

    it("treats non-integer additions/deletions as zero via parseInt fallback", () => {
      const result = parseNumstatTotal("abc\txyz\ta.txt\n5\t0\tb.txt")
      expect(result).toEqual({ filesChanged: 2, additions: 5, deletions: 0 })
    })

    it("returns zeros for empty or whitespace-only input", () => {
      expect(parseNumstatTotal("")).toEqual({ filesChanged: 0, additions: 0, deletions: 0 })
      expect(parseNumstatTotal("   \n\t\n")).toEqual({ filesChanged: 0, additions: 0, deletions: 0 })
    })
  })
})
