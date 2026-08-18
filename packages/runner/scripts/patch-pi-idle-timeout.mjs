#!/usr/bin/env node
/**
 * Patch @earendil-works/pi-ai's Anthropic SSE stream with a no-event idle timeout.
 *
 * Why: pi's `iterateSseMessages` awaits `reader.read()` with no timeout. When an
 * Anthropic-compatible endpoint (e.g. minimax) finishes delivering the full SSE
 * payload including message_stop but then lingers without producing any further
 * events, the turn hangs forever and the runner task is stuck "running"
 * (community issue earendil-works/pi#7954).
 *
 * Two linger shapes are covered by a single "no event" timeout:
 *  - no data at all: `reader.read()` never resolves
 *  - keep-alive comments (`:` lines): `reader.read()` keeps returning data but
 *    `decodeSseLine` yields nothing, so the `for await` loop never advances
 *
 * The timer is armed per read and anchored to the last produced event; when it
 * fires it rejects the in-flight read, the stream ends, and the turn settles
 * through the normal error path instead of hanging.
 *
 * Timer state lives at function scope (before `try`) so the `finally` block can
 * clear it. The timer is NOT unref'd: an unref'd timer does not fire when the
 * event loop has no other handles (a stalled read keeps the loop empty in a
 * minimal harness), which would defeat the timeout exactly when it is needed.
 *
 * Apply after `npm install` (postinstall) so it ships inside the runner bundle.
 * Override the default timeout with PI_SSE_IDLE_TIMEOUT_MS.
 */
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const IDLE_TIMEOUT_MS = Number(process.env.PI_SSE_IDLE_TIMEOUT_MS || 90_000);

const here = fileURLToPath(new URL(".", import.meta.url));
// packages/runner/scripts -> packages/runner
const runnerRoot = resolve(here, "..");
const DEFAULT_TARGET = resolve(
  runnerRoot,
  "node_modules/@earendil-works/pi-coding-agent/node_modules/@earendil-works/pi-ai/dist/api/anthropic-messages.js",
);

/**
 * Apply the no-event idle timeout to the given anthropic-messages.js file.
 * Returns { applied: true } on success, { applied: false, reason } if the file
 * is already patched or the expected pattern is missing.
 */
export function applyIdleTimeoutPatch(targetPath, idleTimeoutMs = IDLE_TIMEOUT_MS) {
  if (!existsSync(targetPath)) {
    return { applied: false, reason: "target-not-found" };
  }
  let source = readFileSync(targetPath, "utf8");

  if (source.includes("lastEventAt")) {
    return { applied: false, reason: "already-applied" };
  }

  const oldHead = `    let buffer = "";
    try {
        while (true) {
            if (signal?.aborted) {
                throw new Error("Request was aborted");
            }
            const { value, done } = await reader.read();
            if (done) {
                break;
            }`;

  const newHead = `    let buffer = "";
    const IDLE_TIMEOUT_MS = Number(process.env.PI_SSE_IDLE_TIMEOUT_MS || ${idleTimeoutMs});
    let lastEventAt = Date.now();
    let idleTimer = null;
    let currentReadReject = null;
    const clearIdleTimer = () => {
        if (idleTimer !== null) {
            clearTimeout(idleTimer);
            idleTimer = null;
        }
    };
    const armIdleTimer = () => {
        clearIdleTimer();
        const elapsed = Date.now() - lastEventAt;
        const delay = Math.max(0, IDLE_TIMEOUT_MS - elapsed);
        idleTimer = setTimeout(() => {
            idleTimer = null;
            if (currentReadReject !== null) {
                const reject = currentReadReject;
                currentReadReject = null;
                reject(new Error(\`SSE stream idle timeout after \${IDLE_TIMEOUT_MS}ms\`));
            }
        }, delay);
    };
    const readWithIdleTimeout = () => new Promise((resolve, reject) => {
        currentReadReject = reject;
        armIdleTimer();
        reader.read().then(
            (result) => {
                currentReadReject = null;
                resolve(result);
            },
            (error) => {
                clearIdleTimer();
                currentReadReject = null;
                reject(error);
            },
        );
    });
    try {
        while (true) {
            if (signal?.aborted) {
                throw new Error("Request was aborted");
            }
            const { value, done } = await readWithIdleTimeout();
            if (done) {
                break;
            }`;

  if (!source.includes(oldHead)) {
    return { applied: false, reason: "loop-pattern-not-found" };
  }
  source = source.replace(oldHead, newHead);

  const oldYield = `                const event = decodeSseLine(consumed.line, state);
                if (event) {
                    yield event;
                }`;

  const newYield = `                const event = decodeSseLine(consumed.line, state);
                if (event) {
                    lastEventAt = Date.now();
                    clearIdleTimer();
                    yield event;
                }`;

  if (!source.includes(oldYield)) {
    return { applied: false, reason: "yield-pattern-not-found" };
  }
  source = source.replace(oldYield, newYield);

  const oldFinally = `    finally {
        reader.releaseLock();
    }`;

  const newFinally = `    finally {
        clearIdleTimer();
        currentReadReject = null;
        reader.releaseLock();
    }`;

  if (source.includes(oldFinally)) {
    source = source.replace(oldFinally, newFinally);
  }

  writeFileSync(targetPath, source);
  return { applied: true };
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  const result = applyIdleTimeoutPatch(DEFAULT_TARGET, IDLE_TIMEOUT_MS);
  if (result.applied) {
    console.log(`[patch-pi-idle-timeout] applied no-event idle timeout of ${IDLE_TIMEOUT_MS}ms`);
  } else if (result.reason === "already-applied") {
    console.log("[patch-pi-idle-timeout] already applied, skipping");
  } else {
    console.error(`[patch-pi-idle-timeout] failed: ${result.reason}`);
    process.exit(1);
  }
}
