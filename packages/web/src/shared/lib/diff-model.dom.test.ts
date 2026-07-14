import { describe, expect, it } from 'vitest'
import { parseDiff, isLargeDiff, getDiffStats, DEFAULT_LARGE_DIFF_THRESHOLD } from './diff-model'

const SAMPLE_DIFF = `diff --git a/src/foo.ts b/src/foo.ts
index 1234567..abcdefg 100644
--- a/src/foo.ts
+++ b/src/foo.ts
@@ -1,3 +1,5 @@
+import React from 'react'
+
 const foo = 'hello'
-const bar = 'world'
+const bar = 'world changed'
+const baz = 'new line'
@@ -10,3 +12,5 @@ export { foo, bar }
// comment 1
// comment 2
// comment 3
+const newCode = 'added'
+export { newCode }
diff --git a/src/bar.ts b/src/bar.ts
new file mode 100644
--- /dev/null
+++ b/src/bar.ts
@@ -0,0 +1,3 @@
+export const bar = 'new file'
+export const baz = 'another'
diff --git a/src/utils/helper.ts b/src/utils/helper.ts
deleted file mode 100644
--- a/src/utils/helper.ts
+++ /dev/null
@@ -1,10 +0,0 @@
-// old code
-export const old = true`

describe('parseDiff', () => {
  it('parses issue diff format into file blocks', () => {
    const blocks = parseDiff(SAMPLE_DIFF)
    expect(blocks.length).toBe(3)
  })

  it('extracts file paths and status', () => {
    const blocks = parseDiff(SAMPLE_DIFF)
    const fooBlock = blocks.find(b => b.newPath === 'src/foo.ts')!
    expect(fooBlock.oldPath).toBe('src/foo.ts')
    expect(fooBlock.status).toBe('modified')
  })

  it('extracts additions and deletions counts', () => {
    const blocks = parseDiff(SAMPLE_DIFF)
    const fooBlock = blocks.find(b => b.newPath === 'src/foo.ts')!
    expect(fooBlock.additions).toBeGreaterThan(0)
    expect(fooBlock.deletions).toBeGreaterThan(0)
  })

  it('parses new file status', () => {
    const blocks = parseDiff(SAMPLE_DIFF)
    const barBlock = blocks.find(b => b.newPath === 'src/bar.ts')!
    expect(barBlock.status).toBe('added')
  })

  it('parses deleted file status', () => {
    const deletedDiff = `diff --git a/src/utils/helper.ts b/src/utils/helper.ts
deleted file mode 100644
--- a/src/utils/helper.ts
+++ /dev/null
@@ -1,10 +0,0 @@
-// old code
-export const old = true`
    const blocks = parseDiff(deletedDiff)
    expect(blocks.length).toBe(1)
    expect(blocks[0].oldPath).toBe('src/utils/helper.ts')
    expect(blocks[0].newPath).toBe('src/utils/helper.ts')
    expect(blocks[0].status).toBe('deleted')
  })

  it('extracts hunk headers and lines', () => {
    const simpleDiff = `diff --git a/test.ts b/test.ts
index 1234567..abcdefg 100644
--- a/test.ts
+++ b/test.ts
@@ -1,3 +1,4 @@
+new line
 old line
-old
+new
 another`
    const blocks = parseDiff(simpleDiff)
    expect(blocks.length).toBe(1)
    expect(blocks[0].hunks.length).toBeGreaterThan(0)
    expect(blocks[0].hunkCount).toBe(blocks[0].hunks.length)
  })

  it('calculates changedLineCount', () => {
    const simpleDiff = `diff --git a/test.ts b/test.ts
index 1234567..abcdefg 100644
--- a/test.ts
+++ b/test.ts
@@ -1,3 +1,4 @@
+new line
 old line
-old
+new
 another`
    const blocks = parseDiff(simpleDiff)
    const block = blocks[0]
    expect(block.changedLineCount).toBe(block.additions + block.deletions)
  })

  it('populates rawPatch for each block', () => {
    const blocks = parseDiff(SAMPLE_DIFF)
    const fooBlock = blocks.find(b => b.newPath === 'src/foo.ts')!
    expect(fooBlock.rawPatch).toContain('diff --git a/src/foo.ts b/src/foo.ts')
  })

  it('returns empty array for empty diff', () => {
    expect(parseDiff('')).toEqual([])
    expect(parseDiff('   ')).toEqual([])
  })
})

describe('isLargeDiff', () => {
  it('returns false when changedLineCount is below threshold', () => {
    const blocks = parseDiff(SAMPLE_DIFF)
    const fooBlock = blocks.find(b => b.newPath === 'src/foo.ts')!
    expect(isLargeDiff(fooBlock)).toBe(false)
  })

  it('returns true when changedLineCount exceeds default threshold', () => {
    const largeDiff = `diff --git a/large.txt b/large.txt
index 1234567..abcdefg 100644
--- a/large.txt
+++ b/large.txt
@@ -1,350 +1,350 @@
${Array.from({ length: 350 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`
    const blocks = parseDiff(largeDiff)
    const largeBlock = blocks[0]
    expect(largeBlock.changedLineCount).toBeGreaterThan(DEFAULT_LARGE_DIFF_THRESHOLD)
    expect(isLargeDiff(largeBlock)).toBe(true)
  })

  it('respects custom threshold', () => {
    const largeDiff = `diff --git a/large.txt b/large.txt
index 1234567..abcdefg 100644
--- a/large.txt
+++ b/large.txt
@@ -1,10 +1,10 @@
-line1
-line2
-line3
-line4
-line5
+line1new
+line2new
+line3new
+line4new
+line5new`
    const blocks = parseDiff(largeDiff)
    const largeBlock = blocks[0]
    expect(largeBlock.changedLineCount).toBeGreaterThan(0)
    expect(isLargeDiff(largeBlock, 3)).toBe(true)
    expect(isLargeDiff(largeBlock, 1000)).toBe(false)
  })
})

describe('getDiffStats', () => {
  it('aggregates stats across all blocks', () => {
    const blocks = parseDiff(SAMPLE_DIFF)
    const stats = getDiffStats(blocks)
    expect(stats.filesChanged).toBe(3)
    expect(stats.additions).toBeGreaterThan(0)
    expect(stats.deletions).toBeGreaterThan(0)
  })

  it('returns zeros for empty blocks', () => {
    const stats = getDiffStats([])
    expect(stats.filesChanged).toBe(0)
    expect(stats.additions).toBe(0)
    expect(stats.deletions).toBe(0)
  })
})
