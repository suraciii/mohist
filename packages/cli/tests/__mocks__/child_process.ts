import { vi } from 'vitest';
import { EventEmitter } from 'events';

export interface MockChildProcess extends EventEmitter {
  pid?: number;
  stdin: EventEmitter | null;
  stdout: EventEmitter | null;
  stderr: EventEmitter | null;
  kill: ReturnType<typeof vi.fn>;
  disconnected: boolean;
}

export function createMockChildProcess(): MockChildProcess {
  const process = new EventEmitter() as MockChildProcess;
  process.pid = 12345;
  process.stdin = new EventEmitter();
  process.stdout = new EventEmitter();
  process.stderr = new EventEmitter();
  process.kill = vi.fn(() => true);
  process.disconnected = false;
  return process;
}

export function createMockSpawn() {
  const mockSpawn = vi.fn();
  const processes: MockChildProcess[] = [];

  mockSpawn.mockImplementation(() => {
    const childProcess = createMockChildProcess();
    processes.push(childProcess);
    return childProcess;
  });

  return {
    mockSpawn,
    processes,
    getLastProcess: () => processes[processes.length - 1],
    getAllProcesses: () => processes,
    simulateExit: (code: number = 0, processIndex: number = processes.length - 1) => {
      const proc = processes[processIndex];
      if (proc) {
        proc.emit('close', code);
      }
    },
    simulateError: (error: Error, processIndex: number = processes.length - 1) => {
      const proc = processes[processIndex];
      if (proc) {
        proc.emit('error', error);
      }
    },
    simulateStdout: (data: string, processIndex: number = processes.length - 1) => {
      const proc = processes[processIndex];
      if (proc?.stdout) {
        proc.stdout.emit('data', Buffer.from(data));
      }
    },
    simulateStderr: (data: string, processIndex: number = processes.length - 1) => {
      const proc = processes[processIndex];
      if (proc?.stderr) {
        proc.stderr.emit('data', Buffer.from(data));
      }
    }
  };
}
