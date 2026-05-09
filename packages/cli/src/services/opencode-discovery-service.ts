import { execFile } from 'child_process';
import { resolveOpencodeBinPath } from '../config/config-loader';
import { isValidModelId } from '../config/model-resolution';
import { Log } from '../util/log';

const log = Log.create({ service: 'opencode-discovery' });

const DISCOVERY_TTL_MS = 30 * 60 * 1000;

interface DiscoveryCache {
  models: string[];
  timestamp: number;
}

let cache: DiscoveryCache | null = null;

function probeModels(): Promise<string[]> {
  const binPath = resolveOpencodeBinPath() || 'opencode';

  return new Promise<string[]>((resolve, reject) => {
    const timeout = setTimeout(() => {
      log.error('model discovery probe timed out');
      reject(new Error('model discovery probe timed out'));
    }, 30000);

    execFile(
      binPath,
      ['models'],
      {
        cwd: process.cwd(),
        env: Object.fromEntries(
          Object.entries(process.env).filter(
            ([key]) =>
              key !== 'OPENCODE_SERVER_PASSWORD' &&
              key !== 'OPENCODE_SERVER_USERNAME'
          )
        ),
      },
      (err, stdout, stderr) => {
        clearTimeout(timeout);

        if (err) {
          log.error('model discovery probe failed', {
            error: err.message,
            stderr: stderr?.slice(0, 500),
          });
          reject(new Error(`model discovery failed: ${err.message}`));
          return;
        }

        const lines = stdout.split(/\r?\n/);
        const models = lines
          .map((line) => line.trim())
          .filter((line) => isValidModelId(line));

        if (models.length === 0) {
          log.error('model discovery returned no parseable models', {
            stdout: stdout.slice(0, 500),
          });
          reject(new Error('model discovery returned no parseable models'));
          return;
        }

        resolve(models);
      }
    );
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
      log.error('model discovery probe failed', {
        error: err instanceof Error ? err.message : String(err),
      });
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
