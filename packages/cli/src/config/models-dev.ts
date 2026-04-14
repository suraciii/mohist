import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import * as z from 'zod';

export const ModelsDevModel = z.object({
  id: z.string(),
  name: z.string(),
  family: z.string().optional(),
  release_date: z.string(),
  attachment: z.boolean(),
  reasoning: z.boolean(),
  temperature: z.boolean(),
  tool_call: z.boolean(),
  interleaved: z
    .union([
      z.literal(true),
      z
        .object({
          field: z.enum(["reasoning_content", "reasoning_details"]),
        })
        .strict(),
    ])
    .optional(),
  cost: z
    .object({
      input: z.number(),
      output: z.number(),
      cache_read: z.number().optional(),
      cache_write: z.number().optional(),
      context_over_200k: z
        .object({
          input: z.number(),
          output: z.number(),
          cache_read: z.number().optional(),
          cache_write: z.number().optional(),
        })
        .optional(),
    })
    .optional(),
  limit: z.object({
    context: z.number(),
    input: z.number().optional(),
    output: z.number(),
  }),
  modalities: z
    .object({
      input: z.array(z.enum(["text", "audio", "image", "video", "pdf"])),
      output: z.array(z.enum(["text", "audio", "image", "video", "pdf"])),
    })
    .optional(),
  experimental: z.boolean().optional(),
  status: z.enum(["alpha", "beta", "deprecated"]).optional(),
  provider: z.object({ npm: z.string().optional(), api: z.string().optional() }).optional(),
});
export type ModelsDevModel = z.infer<typeof ModelsDevModel>;

export const ModelsDevProvider = z.object({
  api: z.string().optional(),
  name: z.string(),
  env: z.array(z.string()),
  id: z.string(),
  npm: z.string().optional(),
  models: z.record(z.string(), ModelsDevModel),
});
export type ModelsDevProvider = z.infer<typeof ModelsDevProvider>;

const CACHE_DIR = () => path.join(os.homedir(), '.mohist', 'cache');
const CACHE_FILE = () => path.join(CACHE_DIR(), 'models.json');
const SNAPSHOT_FILE = () => path.join(__dirname, 'models-snapshot.js');
const TTL = 60 * 60 * 1000;
const SOURCE_URL = 'https://models.dev';

let dataCache: Record<string, ModelsDevProvider> | undefined;
let dataCacheTime = 0;

function fresh(): boolean {
  const cachePath = CACHE_FILE();
  if (!fs.existsSync(cachePath)) return false;
  const mtime = fs.statSync(cachePath).mtimeMs;
  return Date.now() - mtime < TTL;
}

function ensureCacheDir(): void {
  const dir = CACHE_DIR();
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
  }
}

async function fetchApi(): Promise<{ ok: boolean; text: string }> {
  try {
    const result = await fetch(`${SOURCE_URL}/api.json`, {
      signal: AbortSignal.timeout(10000),
    });
    return { ok: result.ok, text: await result.text() };
  } catch {
    return { ok: false, text: '' };
  }
}

async function loadFromCache(): Promise<Record<string, ModelsDevProvider> | undefined> {
  const cachePath = CACHE_FILE();
  if (!fs.existsSync(cachePath)) return undefined;
  try {
    const content = fs.readFileSync(cachePath, 'utf-8');
    return JSON.parse(content) as Record<string, ModelsDevProvider>;
  } catch {
    return undefined;
  }
}

async function loadFromSnapshot(): Promise<Record<string, ModelsDevProvider> | undefined> {
  const snapshotPath = SNAPSHOT_FILE();
  if (!fs.existsSync(snapshotPath)) return undefined;
  try {
    const mod = await import(snapshotPath);
    return mod.snapshot as Record<string, ModelsDevProvider>;
  } catch {
    return undefined;
  }
}

async function loadData(): Promise<Record<string, ModelsDevProvider>> {
  if (dataCache && Date.now() - dataCacheTime < TTL) {
    return dataCache;
  }

  const cached = await loadFromCache();
  if (cached) {
    dataCache = cached;
    dataCacheTime = Date.now();
    return cached;
  }

  const snapshot = await loadFromSnapshot();
  if (snapshot) {
    dataCache = snapshot;
    dataCacheTime = Date.now();
    return snapshot;
  }

  if (process.env.MODELS_DEV_DISABLE_FETCH) return {};

  const result = await fetchApi();
  if (result.ok) {
    try {
      const parsed = JSON.parse(result.text) as Record<string, ModelsDevProvider>;
      ensureCacheDir();
      fs.writeFileSync(CACHE_FILE(), result.text, { mode: 0o600 });
      dataCache = parsed;
      dataCacheTime = Date.now();
      return parsed;
    } catch {
      // fall through
    }
  }

  return snapshot ?? {};
}

export namespace ModelsDev {
  export async function get(): Promise<Record<string, ModelsDevProvider>> {
    return loadData();
  }

  export async function refresh(force = false): Promise<void> {
    if (!force && fresh()) return;

    if (process.env.MODELS_DEV_DISABLE_FETCH) return;

    const result = await fetchApi();
    if (!result.ok) return;

    try {
      const parsed = JSON.parse(result.text) as Record<string, ModelsDevProvider>;
      ensureCacheDir();
      fs.writeFileSync(CACHE_FILE(), result.text, { mode: 0o600 });
      dataCache = parsed;
      dataCacheTime = Date.now();
    } catch {
      // silently fail
    }
  }
}

if (!process.env.MODELS_DEV_DISABLE_FETCH) {
  setInterval(() => {
    ModelsDev.refresh().catch(() => {});
  }, 60 * 60 * 1000).unref();
}
