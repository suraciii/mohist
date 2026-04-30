import { describe, it, expect, beforeEach, afterEach, vi } from "vitest"
import fs from "fs/promises"
import path from "path"
import os from "os"
import { Log } from "../src/util/log"

const LOG_DIR = path.join(os.homedir(), ".mohist", "logs")

describe("Log", () => {
  let stderrWrite: any

  beforeEach(() => {
    stderrWrite = process.stderr.write.bind(process.stderr)
  })

  afterEach(async () => {
    vi.restoreAllMocks()
    await Log.init({ print: true, level: "INFO" })
  })

  describe("create", () => {
    it("should return a Logger instance with service tag", () => {
      const log = Log.create({ service: "test" })
      expect(log).toBeDefined()
      expect(log.info).toBeInstanceOf(Function)
      expect(log.error).toBeInstanceOf(Function)
      expect(log.warn).toBeInstanceOf(Function)
      expect(log.debug).toBeInstanceOf(Function)
    })

    it("should return cached instance for same service name", () => {
      const log1 = Log.create({ service: "cached-test" })
      const log2 = Log.create({ service: "cached-test" })
      expect(log1).toBe(log2)
    })
  })

  describe("level filtering", () => {
    it("should not output debug when level is INFO (default)", () => {
      const writeSpy = vi.spyOn(process.stderr, "write").mockImplementation(() => true)
      const log = Log.create({ service: "level-test-info" })
      log.debug("should not appear")
      expect(writeSpy).not.toHaveBeenCalled()
    })

    it("should output info when level is INFO (default)", () => {
      const writeSpy = vi.spyOn(process.stderr, "write").mockImplementation(() => true)
      const log = Log.create({ service: "level-test-info2" })
      log.info("should appear")
      const output = writeSpy.mock.calls.map((c: any) => String(c[0])).join("")
      expect(output).toContain("INFO")
      expect(output).toContain("should appear")
    })
  })

  describe("init with print:true", () => {
    it("should write to stderr as plain text", async () => {
      const writeSpy = vi.spyOn(process.stderr, "write").mockImplementation(() => true)
      await Log.init({ print: true })
      const log = Log.create({ service: "print-test" })
      log.info("hello stderr")
      const output = writeSpy.mock.calls.map((c: any) => String(c[0])).join("")
      expect(output).toContain("hello stderr")
      expect(output).not.toMatch(/^\{.*\}$/s)
    })
  })

  describe("init with print:false (file output)", () => {
    it("should write to ~/.mohist/logs/mohist-YYYY-MM-DD.log", async () => {
      await Log.init({ print: false })
      const logfile = Log.file()
      expect(logfile).toMatch(
        /\.mohist\/logs\/mohist-\d{4}-\d{2}-\d{2}\.log$/,
      )
      const log = Log.create({ service: "file-test" })
      log.info("hello file")
      await new Promise((r) => setTimeout(r, 100))
      const content = await fs.readFile(logfile, "utf-8")
      expect(content).toContain("hello file")
      await fs.unlink(logfile).catch(() => {})
    })

    it("should write JSONL format where each line is valid JSON", async () => {
      await Log.init({ print: false })
      const logfile = Log.file()
      const log = Log.create({ service: "jsonl-test" })
      log.info("first message")
      log.warn("second message", { key: "value" })
      await new Promise((r) => setTimeout(r, 100))
      const content = await fs.readFile(logfile, "utf-8")
      const lines = content.trim().split("\n")
      for (const line of lines) {
        const parsed = JSON.parse(line)
        expect(parsed).toHaveProperty("level")
        expect(parsed).toHaveProperty("time")
        expect(parsed).toHaveProperty("diffMs")
        expect(parsed).toHaveProperty("service")
        expect(parsed).toHaveProperty("message")
      }
      await fs.unlink(logfile).catch(() => {})
    })

    it("should include extra fields as top-level JSON properties", async () => {
      await Log.init({ print: false })
      const logfile = Log.file()
      const log = Log.create({ service: "extra-test" })
      log.info("with extra", { method: "GET", path: "/api/health" })
      await new Promise((r) => setTimeout(r, 100))
      const content = await fs.readFile(logfile, "utf-8")
      const parsed = JSON.parse(content.trim())
      expect(parsed.method).toBe("GET")
      expect(parsed.path).toBe("/api/health")
      expect(parsed.service).toBe("extra-test")
      expect(parsed.message).toBe("with extra")
      await fs.unlink(logfile).catch(() => {})
    })

    it("should set level field correctly for each log level", async () => {
      await Log.init({ print: false, level: "DEBUG" })
      const logfile = Log.file()
      const log = Log.create({ service: "level-jsonl" })
      log.debug("dbg")
      log.info("inf")
      log.warn("wrn")
      log.error("err")
      await new Promise((r) => setTimeout(r, 100))
      const content = await fs.readFile(logfile, "utf-8")
      const lines = content.trim().split("\n")
      const levels = lines.map((l: string) => JSON.parse(l).level)
      expect(levels).toEqual(["DEBUG", "INFO", "WARN", "ERROR"])
      await fs.unlink(logfile).catch(() => {})
    })
  })

  describe("init with dev:true", () => {
    it("should use fixed dev.log filename", async () => {
      await Log.init({ print: false, dev: true })
      const logfile = Log.file()
      expect(logfile).toMatch(/\.mohist\/logs\/dev\.log$/)
      await fs.unlink(logfile).catch(() => {})
    })
  })

  describe("time()", () => {
    it("should output started and completed logs with duration", async () => {
      const writeSpy = vi.spyOn(process.stderr, "write").mockImplementation(() => true)
      await Log.init({ print: true })
      const log = Log.create({ service: "time-test" })
      const timer = log.time("operation", { key: "val" })
      const startedOutput = writeSpy.mock.calls.map((c: any) => String(c[0])).join("")
      expect(startedOutput).toContain("status=started")

      timer.stop()
      const allOutput = writeSpy.mock.calls.map((c: any) => String(c[0])).join("")
      expect(allOutput).toContain("status=completed")
      expect(allOutput).toMatch(/elapsedMs=\d+/)
    })
  })

  describe("clone().tag()", () => {
    it("should create independent context from original", () => {
      const writeSpy = vi.spyOn(process.stderr, "write").mockImplementation(() => true)
      const log = Log.create({ service: "clone-test" })
      const child = log.clone()
      child.tag("extra", "value")
      log.info("parent message")
      const parentOutput = writeSpy.mock.calls.map((c: any) => String(c[0])).join("")
      expect(parentOutput).not.toContain("extra=value")
    })
  })

  describe("Error formatting", () => {
    it("should recursively unfold cause chain", () => {
      const writeSpy = vi.spyOn(process.stderr, "write").mockImplementation(() => true)
      const log = Log.create({ service: "error-test" })
      const root = new Error("root cause")
      const mid = new Error("mid error", { cause: root })
      const top = new Error("top error", { cause: mid })
      log.error("failed", { err: top })
      const output = writeSpy.mock.calls.map((c: any) => String(c[0])).join("")
      expect(output).toContain("top error")
      expect(output).toContain("Caused by:")
      expect(output).toContain("mid error")
      expect(output).toContain("root cause")
    })
  })
})
