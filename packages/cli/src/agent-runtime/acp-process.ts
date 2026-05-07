import { spawn, type ChildProcess } from 'child_process';
import { Writable, Readable } from 'stream';
import { ndJsonStream } from '@agentclientprotocol/sdk';
import { Log } from '../util/log';
import { resolveOpencodeBinPath } from '../config/config-loader';

const log = Log.create({ service: 'acp-process' });

export interface AcpProcessOptions {
  cwd: string;
  opencodeBinPath?: string;
  onError?: (err: Error) => void;
  onExit?: (info: { exitCode: number | null; phase: 'init' | 'running' }) => void;
}

export class AcpProcess {
  private proc: ChildProcess;
  private _exited = false;
  private _initialized = false;
  private _rejectOnSpawn: ((err: Error) => void) | undefined;
  private _rejectOnExit: ((err: Error) => void) | undefined;

  readonly spawnFailure: Promise<never>;
  readonly exitFailure: Promise<never>;
  readonly stream: ReturnType<typeof ndJsonStream>;

  constructor(options: AcpProcessOptions) {
    const resolvedBinPath = options.opencodeBinPath || resolveOpencodeBinPath() || 'opencode';

    this.proc = spawn(resolvedBinPath, ['acp'], {
      cwd: options.cwd,
      stdio: ['pipe', 'pipe', 'inherit'],
      env: Object.fromEntries(
        Object.entries(process.env).filter(
          ([key]) =>
            key !== 'OPENCODE_SERVER_PASSWORD' &&
            key !== 'OPENCODE_SERVER_USERNAME'
        )
      ),
    });

    this.spawnFailure = new Promise<never>((_, reject) => {
      this._rejectOnSpawn = reject;
    });

    this.exitFailure = new Promise<never>((_, reject) => {
      this._rejectOnExit = reject;
    });

    this.proc.on('error', (err) => {
      log.error('opencode acp subprocess error', { error: err.message });
      options.onError?.(err);
      if (!this._initialized && this._rejectOnSpawn) {
        this._rejectOnSpawn(new Error(`[SPAWN_FAILED] ${err.message}`));
      }
    });

    this.proc.on('exit', () => {
      try { this.proc.stdin!.destroy(); } catch (err) {
        log.warn('stdin destroy failed', { error: err instanceof Error ? err.message : String(err) });
      }
      try { this.proc.stdout!.destroy(); } catch (err) {
        log.warn('stdout destroy failed', { error: err instanceof Error ? err.message : String(err) });
      }
    });

    this.proc.on('exit', (code) => {
      const phase = this._initialized ? 'running' as const : 'init' as const;
      options.onExit?.({ exitCode: code, phase });
      if (!this._initialized && code !== 0) {
        log.error('opencode acp subprocess exited before initialize', { exitCode: code });
        if (this._rejectOnSpawn) {
          this._rejectOnSpawn(new Error(`[SPAWN_FAILED] opencode process exited before initialize (exit code: ${code ?? 'signal'})`));
        }
      }
      if (this._initialized && code !== 0 && this._rejectOnExit) {
        log.error('opencode acp subprocess exited unexpectedly during session', { exitCode: code });
        this._rejectOnExit(new Error(`[PROCESS_EXIT] opencode process exited unexpectedly (exit code: ${code ?? 'killed by signal'})`));
        this._rejectOnExit = undefined;
      }
    });

    this.proc.stdin!.on('error', () => {});
    this.proc.stdout!.on('error', () => {});

    const input = Writable.toWeb(this.proc.stdin!) as WritableStream<Uint8Array>;
    const output = Readable.toWeb(this.proc.stdout!) as ReadableStream<Uint8Array>;
    this.stream = ndJsonStream(input, output);
  }

  get process(): ChildProcess {
    return this.proc;
  }

  markInitialized(): void {
    this._initialized = true;
    this._rejectOnSpawn = undefined;
  }

  ensureKill(): void {
    if (!this._exited) {
      this._exited = true;
      try { this.proc.kill('SIGTERM'); } catch (err) {
        log.warn('SIGTERM failed', { error: err instanceof Error ? err.message : String(err) });
      }
      setTimeout(() => {
        try { this.proc.kill('SIGKILL'); } catch (err) {
          log.warn('SIGKILL failed', { error: err instanceof Error ? err.message : String(err) });
        }
      }, 5000);
    }
  }

  async cleanup(): Promise<void> {
    const results = await Promise.allSettled([
      this.stream.readable.cancel().catch(() => {}),
      this.stream.writable.abort().catch(() => {}),
    ]);
    results.forEach((result, index) => {
      if (result.status === 'rejected') {
        log.error('Cleanup failed', { index, reason: String(result.reason) });
      }
    });
    this.ensureKill();
  }
}
