import { spawn } from 'child_process';
import { Writable, Readable } from 'stream';
import {
  ClientSideConnection,
  ndJsonStream,
  PROTOCOL_VERSION,
} from '@agentclientprotocol/sdk';
import { resolveOpencodeBinPath } from '../config/config-loader';
import { Log } from '../util/log';

const log = Log.create({ service: 'opencode-discovery' });

const DISCOVERY_TTL_MS = 5 * 60 * 1000;

interface DiscoveryCache {
  models: string[];
  timestamp: number;
}

let cache: DiscoveryCache | null = null;

async function probeModels(): Promise<string[]> {
  const binPath = resolveOpencodeBinPath() || 'opencode';
  const cwd = process.cwd();

  return new Promise<string[]>((resolve, reject) => {
    const proc = spawn(binPath, ['acp'], {
      cwd,
      stdio: ['pipe', 'pipe', 'inherit'],
      env: Object.fromEntries(
        Object.entries(process.env).filter(
          ([key]) =>
            key !== 'OPENCODE_SERVER_PASSWORD' &&
            key !== 'OPENCODE_SERVER_USERNAME'
        )
      ),
    });

    let procExited = false;
    let sessionId = '';

    const ensureKill = () => {
      if (!procExited) {
        procExited = true;
        try {
          proc.kill('SIGTERM');
        } catch {
          // already exited
        }
        setTimeout(() => {
          try {
            proc.kill('SIGKILL');
          } catch {
            // already exited
          }
        }, 5000);
      }
    };

    const input = Writable.toWeb(proc.stdin) as WritableStream<Uint8Array>;
    const output = Readable.toWeb(proc.stdout) as ReadableStream<Uint8Array>;
    const stream = ndJsonStream(input, output);

    const cleanup = async () => {
      await Promise.allSettled([
        stream.readable.cancel().catch(() => {}),
        stream.writable.abort().catch(() => {}),
      ]);
      ensureKill();
    };

    const connection = new ClientSideConnection(
      (_agent) => ({
        sessionUpdate: async () => {},
        requestPermission: async () => ({ outcome: { outcome: 'cancelled' } }),
      }),
      stream
    );

    const timeout = setTimeout(async () => {
      log.warn('model discovery probe timed out');
      await cleanup();
      reject(new Error('model discovery probe timed out'));
    }, 30000);

    proc.on('error', (err) => {
      clearTimeout(timeout);
      log.error('model discovery probe spawn error', { error: err.message });
      reject(new Error(`probe spawn failed: ${err.message}`));
    });

    proc.on('exit', (code) => {
      if (!sessionId) {
        clearTimeout(timeout);
        log.error('model discovery probe exited before session created', { exitCode: code });
      }
    });

    (async () => {
      try {
        await connection.initialize({
          protocolVersion: PROTOCOL_VERSION,
          clientInfo: { name: 'mohist-discovery', version: '0.1.0' },
        });

        const sessionResult = await connection.newSession({ cwd, mcpServers: [] });
        sessionId = sessionResult.sessionId;

        const models = (sessionResult as { models?: { availableModels?: string[] } }).models?.availableModels ?? [];

        clearTimeout(timeout);
        await cleanup();
        resolve(models);
      } catch (err) {
        clearTimeout(timeout);
        await cleanup();
        reject(err instanceof Error ? err : new Error(String(err)));
      }
    })();
  });
}

export class OpencodeDiscoveryService {
  async getAvailableModels(): Promise<string[]> {
    if (cache && Date.now() - cache.timestamp < DISCOVERY_TTL_MS) {
      return cache.models;
    }

    try {
      const models = await probeModels();
      cache = { models, timestamp: Date.now() };
      log.info('model discovery completed', { count: models.length });
      return models;
    } catch (err) {
      log.error('model discovery probe failed', { error: err instanceof Error ? err.message : String(err) });
      throw err;
    }
  }

  async refresh(): Promise<string[]> {
    cache = null;
    return this.getAvailableModels();
  }

  isCached(): boolean {
    return cache !== null && Date.now() - cache.timestamp < DISCOVERY_TTL_MS;
  }
}

let _instance: OpencodeDiscoveryService | null = null;

export function getOpencodeDiscoveryService(): OpencodeDiscoveryService {
  if (!_instance) {
    _instance = new OpencodeDiscoveryService();
  }
  return _instance;
}