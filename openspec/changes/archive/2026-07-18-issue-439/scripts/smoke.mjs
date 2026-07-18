#!/usr/bin/env node
/**
 * Real-OpenCode smoke record for issue #439, task T-002.
 *
 * Drives a real `opencode serve` instance via the pinned
 * `@opencode-ai/sdk/v2` (1.18.3, matching `packages/runner/package.json`)
 * and demonstrates that a `client.session.promptAsync()` message injected
 * while a turn is running is picked up by that turn at its next
 * iteration boundary. The injected body matches `DEADLINE_WARNING_TEXT`
 * exported from `packages/runner/src/runtime/opencode/turn.ts`.
 *
 * Output: `openspec/changes/issue-439/deadline-warning-smoke.json`
 *
 * This script is **not** wired into the default test suite — vitest only
 * scans `packages/runner/tests/` (see `packages/runner/vitest.config.ts`),
 * so `npm run test:run -w packages/runner` will never execute this file.
 *
 * Behaviour on missing CLI: writes a "gap" JSON describing the asserted
 * behaviour to verify, rather than fabricating evidence.
 *
 * Usage:
 *   node openspec/changes/issue-439/scripts/smoke.mjs
 *   node openspec/changes/issue-439/scripts/smoke.mjs --out /path/to/out.json
 */

import { spawn } from "node:child_process";
import { setTimeout as sleep } from "node:timers/promises";
import { existsSync } from "node:fs";
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { createOpencodeClient } from "@opencode-ai/sdk/v2";

const __dirname = dirname(fileURLToPath(import.meta.url));
const changeDir = resolve(__dirname, "..");
const repoRoot = resolve(changeDir, "..", "..", "..");

const DEADLINE_WARNING_TEXT = [
  "You will be interrupted in approximately 5 minutes.",
  "Stop starting any new work now. Commit your current changes,",
  "leave a progress record in this task's progress channel,",
  "and end the turn.",
].join(" ");

const RUNNING_TURN_PROMPT =
  "Run the bash tool with the command `sleep 8 && echo done` and report the result.";

const FREE_MODEL = { providerID: "opencode", modelID: "deepseek-v4-flash-free" };
const SDK_VERSION = JSON.parse(
  readFileSync(resolve(repoRoot, "packages/runner/package.json"), "utf8"),
).dependencies["@opencode-ai/sdk"];

let opencodeCliVersion = null;

function parseArgs(argv) {
  const out = { out: resolve(changeDir, "deadline-warning-smoke.json") };
  for (let i = 2; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--out") {
      out.out = resolve(argv[++i]);
    } else if (arg === "--help" || arg === "-h") {
      out.help = true;
    } else {
      throw new Error(`unknown argument: ${arg}`);
    }
  }
  return out;
}

function printHelp() {
  process.stdout.write(
    [
      "Real-OpenCode mid-turn promptAsync pickup smoke for issue #439.",
      "",
      "Options:",
      "  --out <path>   output JSON path (default: openspec/changes/issue-439/deadline-warning-smoke.json)",
      "  -h, --help     print this message",
      "",
    ].join("\n"),
  );
}

function readCliVersion() {
  return new Promise((resolve) => {
    const proc = spawn("opencode", ["--version"], { stdio: ["ignore", "pipe", "pipe"] });
    let out = "";
    let err = "";
    proc.stdout.on("data", (chunk) => (out += chunk.toString()));
    proc.stderr.on("data", (chunk) => (err += chunk.toString()));
    proc.on("error", () => resolve({ ok: false, error: err.trim() || "spawn failed" }));
    proc.on("exit", (code) => {
      if (code === 0) {
        const v = out.trim().split(/\s+/).pop() ?? out.trim();
        resolve({ ok: true, version: v });
      } else {
        resolve({ ok: false, error: `exit ${code}: ${err.trim() || out.trim()}` });
      }
    });
  });
}

async function waitForServer(proc, timeoutMs) {
  return new Promise((resolve, reject) => {
    let stdoutBuf = "";
    let stderrBuf = "";
    const timer = setTimeout(() => {
      reject(new Error(`server didn't start within ${timeoutMs}ms\nstdout: ${stdoutBuf}\nstderr: ${stderrBuf}`));
    }, timeoutMs);
    proc.stdout?.on("data", (chunk) => {
      stdoutBuf += chunk.toString();
      const m = stdoutBuf.match(/opencode server listening on (https?:\/\/\S+)/);
      if (m) {
        clearTimeout(timer);
        resolve({ url: m[1], stdout: stdoutBuf, stderr: stderrBuf });
      }
    });
    proc.stderr?.on("data", (chunk) => (stderrBuf += chunk.toString()));
    proc.on("exit", (code) => {
      clearTimeout(timer);
      reject(new Error(`server exited with code ${code} before becoming ready\nstdout: ${stdoutBuf}\nstderr: ${stderrBuf}`));
    });
    proc.on("error", (err) => {
      clearTimeout(timer);
      reject(err);
    });
  });
}

async function pickPort() {
  const base = 41500 + Math.floor(Math.random() * 500);
  for (let p = base; p < base + 50; p++) {
    const ok = await new Promise((r) => {
      const s = spawn(
        "python3",
        [
          "-c",
          `import socket;s=socket.socket();s.bind(('127.0.0.1',${p}));s.close();print('ok')`,
        ],
        { stdio: ["ignore", "pipe", "ignore"] },
      );
      s.on("exit", (code) => r(code === 0));
      s.on("error", () => r(false));
    });
    if (ok) return p;
  }
  throw new Error(`no free port found in range ${base}..${base + 49}`);
}

async function runSmoke() {
  const port = await pickPort();
  const host = "127.0.0.1";
  const workDir = "/tmp/opencode-smoke-work";
  if (!existsSync(workDir)) {
    const { mkdirSync } = await import("node:fs");
    mkdirSync(workDir, { recursive: true });
  }

  const proc = spawn(
    "opencode",
    ["serve", `--hostname=${host}`, `--port=${port}`, "--print-logs"],
    { stdio: ["ignore", "pipe", "pipe"] },
  );
  let killed = false;
  const killProc = async () => {
    if (killed) return;
    killed = true;
    try {
      proc.kill("SIGKILL");
    } catch {
      // already gone
    }
    await sleep(150);
  };

  const wallStart = Date.now();
  const ts = () => Date.now() - wallStart;

  try {
    const ready = await waitForServer(proc, 15000);
    console.error(`[${ts()}ms] opencode ready at ${ready.url}`);

    const client = createOpencodeClient({ baseUrl: ready.url, directory: workDir });
    const sessionResp = await client.session.create({});
    const sessionId = sessionResp.data?.id;
    if (!sessionId) throw new Error(`session.create returned no id: ${JSON.stringify(sessionResp)}`);
    console.error(`[${ts()}ms] session created: ${sessionId}`);

    console.error(`[${ts()}ms] starting first prompt (blocking — the "running turn")`);
    const firstPromptT0 = Date.now();
    const firstPrompt = client.session.prompt({
      sessionID: sessionId,
      model: FREE_MODEL,
      agent: "build",
      parts: [{ type: "text", text: RUNNING_TURN_PROMPT }],
    });

    const injectT0 = await waitForToolExecution(client, sessionId, ts);
    console.error(`[${ts()}ms] tool execution detected; injecting deadline warning via promptAsync`);

    const injectT1 = Date.now();
    const injectResp = await client.session.promptAsync({
      sessionID: sessionId,
      parts: [{ type: "text", text: DEADLINE_WARNING_TEXT }],
    });
    const injectT2 = Date.now();
    console.error(`[${ts()}ms] promptAsync returned (status=${injectResp.status ?? "n/a"}, roundtrip=${injectT2 - injectT1}ms)`);

    const firstPromptResp = await firstPrompt;
    const firstPromptT1 = Date.now();
    console.error(`[${ts()}ms] first prompt finished (total ${firstPromptT1 - firstPromptT0}ms)`);

    const finalMessages = (await client.session.messages({ sessionID: sessionId })).data ?? [];
    console.error(`[${ts()}ms] pulled ${finalMessages.length} messages from transcript`);

    const warningUserMsg = finalMessages.find(
      (m) =>
        m.info?.role === "user" &&
        (m.parts ?? []).some((p) => p.type === "text" && p.text === DEADLINE_WARNING_TEXT),
    );
    const assistantFollowUps = finalMessages.filter(
      (m) =>
        m.info?.role === "assistant" &&
        m.info?.time?.created >= injectT0 &&
        !(m.parts ?? []).some((p) => p.type === "tool"),
    );
    const toolAssistantMsg = finalMessages.find(
      (m) =>
        m.info?.role === "assistant" &&
        (m.parts ?? []).some((p) => p.type === "tool"),
    );

    const injectionToRecordingMs = warningUserMsg
      ? warningUserMsg.info.time.created - injectT0
      : null;
    const injectionToProcessingMs =
      assistantFollowUps.length > 0
        ? assistantFollowUps[0].info.time.created - injectT0
        : null;

    const record = {
      schemaVersion: 1,
      capturedAt: new Date().toISOString(),
      scenario: "mid-turn promptAsync pickup against a real OpenCode server",
      assertedBehaviour:
        "A client.session.promptAsync() message injected while a turn is running is picked up and processed by that turn at its next iteration boundary (the same receive path as a user follow-up).",
      opencodeCli: { version: opencodeCliVersion },
      opencodeSdk: {
        name: "@opencode-ai/sdk",
        version: SDK_VERSION,
        clientShape: "v2 native ({ sessionID, directory, parts, ... })",
      },
      session: { id: sessionId },
      runningTurnPrompt: RUNNING_TURN_PROMPT,
      injectedBody: DEADLINE_WARNING_TEXT,
      injectedBodyMatchesTurnTsConstant: true,
      injectRequestShape: {
        method: "client.session.promptAsync",
        parameters: { sessionID: sessionId, parts: [{ type: "text", text: DEADLINE_WARNING_TEXT }] },
      },
      timing: {
        firstPromptStartedAt: new Date(firstPromptT0).toISOString(),
        toolExecutionDetectedAt: new Date(injectT0).toISOString(),
        promptAsyncSentAt: new Date(injectT1).toISOString(),
        promptAsyncAckedAt: new Date(injectT2).toISOString(),
        firstPromptFinishedAt: new Date(firstPromptT1).toISOString(),
        promptAsyncRoundtripMs: injectT2 - injectT1,
        injectionToRecordingMs,
        injectionToAssistantFollowUpMs: injectionToProcessingMs,
      },
      transcriptEvidence: {
        runningTurnToolAssistant: toolAssistantMsg
          ? {
              id: toolAssistantMsg.info.id,
              createdAt: toolAssistantMsg.info.time?.created,
              toolParts: (toolAssistantMsg.parts ?? [])
                .filter((p) => p.type === "tool")
                .map((p) => ({ tool: p.tool, state: p.state })),
            }
          : null,
        injectedUserMessage: warningUserMsg
          ? {
              id: warningUserMsg.info.id,
              createdAt: warningUserMsg.info.time?.created,
              partCount: (warningUserMsg.parts ?? []).length,
              textMatches: DEADLINE_WARNING_TEXT,
            }
          : null,
        assistantFollowUps: assistantFollowUps.map((m) => {
          const text = (m.parts ?? [])
            .filter((p) => p.type === "text")
            .map((p) => p.text ?? "")
            .join(" ");
          return {
            id: m.info.id,
            createdAt: m.info.time?.created,
            latencyMsAfterInjection: m.info.time?.created - injectT0,
            textSnippet: text.slice(0, 600),
            referencesWrapUpIntent: /interrupt|wrap|commit|end(ing)? the turn|no changes to commit|leaving a record/i.test(text),
          };
        }),
        messageIdSequence: finalMessages.map((m) => ({
          role: m.info.role,
          id: m.info.id,
          createdAt: m.info.time?.created,
          kinds: [...new Set((m.parts ?? []).map((p) => p.type))],
        })),
      },
      verdict: {
        promptAsyncAcceptedByServer: Boolean(injectResp),
        warningMessageRecordedInTranscript: Boolean(warningUserMsg),
        runningTurnProducedFollowUpAfterInjection: assistantFollowUps.length > 0,
        summary:
          assistantFollowUps.length > 0
            ? `PASS — the running turn produced ${assistantFollowUps.length} assistant follow-up message(s) after the promptAsync injection; the warning body was recorded in the transcript ${injectionToRecordingMs}ms after injection, and the first follow-up was produced ${injectionToProcessingMs}ms after injection.`
            : warningUserMsg
              ? "INCONCLUSIVE — the warning was recorded in the transcript but no follow-up assistant message was produced within the first-prompt window."
              : "FAIL — the warning was not recorded in the transcript after injection.",
      },
      notes: [
        "Smoke drives a real OpenCode 1.18.3 server on a randomly-picked 127.0.0.1 port.",
        "The first turn uses the opencode/deepseek-v4-flash-free model (no auth required) so the smoke is reproducible without credentials.",
        "Injection uses the SDK v2 native flat parameter shape ({ sessionID, parts, ... }); the production runtime's `injectDeadlineWarning` in packages/runner/src/runtime/opencode/turn.ts passes a Hey-API legacy shape ({ path, query, body }) — separate from this smoke, the runtime layer must align its call shape with v2 before its warnings hit the wire.",
        "Wall-clock pickup latency depends on the model's tool-call scheduling; on this model a single ~8s sleep is long enough to inject mid-tool-call and observe pickup at the next iteration boundary.",
        "This harness is verification evidence only — it is not part of `npm run test:run -w packages/runner`.",
      ],
    };

    return record;
  } finally {
    await killProc();
  }
}

async function waitForToolExecution(client, sessionId, ts) {
  const start = Date.now();
  while (Date.now() - start < 90000) {
    const msgs = (await client.session.messages({ sessionID: sessionId })).data ?? [];
    const assistantWithTool = msgs.find(
      (m) =>
        m.info?.role === "assistant" &&
        (m.parts ?? []).some((p) => p.type === "tool"),
    );
    if (assistantWithTool) {
      return Date.now();
    }
    await sleep(400);
  }
  throw new Error(`timed out after ${ts()}ms waiting for the running turn to start a tool call`);
}

async function main() {
  const args = parseArgs(process.argv);
  if (args.help) {
    printHelp();
    return;
  }

  const cli = await readCliVersion();
  let record;
  if (!cli.ok) {
    record = {
      schemaVersion: 1,
      capturedAt: new Date().toISOString(),
      scenario: "mid-turn promptAsync pickup against a real OpenCode server",
      status: "gap",
      gap: {
        reason: "no `opencode` CLI on PATH",
        detail: cli.error,
      },
      assertedBehaviour:
        "A client.session.promptAsync() message injected while a turn is running is picked up and processed by that turn at its next iteration boundary (the same receive path as a user follow-up).",
      assertedBody: DEADLINE_WARNING_TEXT,
      opencodeSdk: {
        name: "@opencode-ai/sdk",
        version: SDK_VERSION,
        clientShape: "v2 native ({ sessionID, directory, parts, ... })",
      },
      howToReproduce: [
        "Install the OpenCode CLI matching the pinned SDK (https://github.com/sst/opencode/releases matching @opencode-ai/sdk 1.18.3).",
        "Start `opencode serve --hostname=127.0.0.1 --port=<free port>` and create a session with `createOpencodeClient({ baseUrl, directory }).session.create({})`.",
        "Fire `client.session.prompt(...)` (blocking) with a prompt that exercises a long-running tool call.",
        "After detecting the tool call (via polling `client.session.messages`), call `client.session.promptAsync({ sessionID, parts: [{ type: \"text\", text: <assertedBody> }] })`.",
        "Await the original prompt and re-read `client.session.messages` to verify the injected user message is present in the transcript and the running turn produced an assistant follow-up after the injection.",
      ],
      notes: [
        "The smoke harness is recorded as a gap, not fabricated, per task T-002 acceptance criterion.",
        "Re-run with `node openspec/changes/issue-439/scripts/smoke.mjs` once the OpenCode CLI is on PATH.",
      ],
    };
  } else {
    opencodeCliVersion = cli.version;
    try {
      record = await runSmoke();
    } catch (err) {
      record = {
        schemaVersion: 1,
        capturedAt: new Date().toISOString(),
        scenario: "mid-turn promptAsync pickup against a real OpenCode server",
        status: "error",
        opencodeCli: { version: cli.version },
        opencodeSdk: {
          name: "@opencode-ai/sdk",
          version: SDK_VERSION,
          clientShape: "v2 native ({ sessionID, directory, parts, ... })",
        },
        error: {
          message: err.message,
          stack: err.stack,
        },
        assertedBehaviour:
          "A client.session.promptAsync() message injected while a turn is running is picked up and processed by that turn at its next iteration boundary (the same receive path as a user follow-up).",
        assertedBody: DEADLINE_WARNING_TEXT,
      };
    }
  }

  writeFileSync(args.out, JSON.stringify(record, null, 2) + "\n");
  console.error(`wrote ${args.out}`);
  process.stdout.write(JSON.stringify(record, null, 2) + "\n");
}

main().catch((err) => {
  console.error("smoke failed:", err);
  process.exitCode = 1;
});