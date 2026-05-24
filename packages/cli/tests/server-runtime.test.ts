import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { findMohistRepoRoot, getPidFileStatus, resolveRuntimeCommands } from '../src/cli/commands/server';

describe('server runtime commands', () => {
  const roots: string[] = [];

  afterEach(() => {
    vi.unstubAllEnvs();
    for (const root of roots.splice(0)) {
      fs.rmSync(root, { recursive: true, force: true });
    }
  });

  function makeRepo(): string {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-runtime-'));
    roots.push(root);
    fs.mkdirSync(path.join(root, 'packages', 'server', 'src', 'Mohist.Server'), { recursive: true });
    fs.mkdirSync(path.join(root, 'packages', 'runner', 'src', 'Mohist.Runner'), { recursive: true });
    fs.writeFileSync(path.join(root, 'packages', 'server', 'src', 'Mohist.Server', 'Mohist.Server.csproj'), '<Project />');
    fs.writeFileSync(path.join(root, 'packages', 'runner', 'src', 'Mohist.Runner', 'Mohist.Runner.csproj'), '<Project />');
    return root;
  }

  it('finds the source repo from nested paths', () => {
    const root = makeRepo();
    const nested = path.join(root, 'packages', 'cli', 'src');
    fs.mkdirSync(nested, { recursive: true });

    expect(findMohistRepoRoot(nested)).toBe(root);
  });

  it('builds dotnet commands for server and runner', () => {
    const root = makeRepo();
    const nested = path.join(root, 'packages', 'cli', 'src', 'cli', 'commands');
    fs.mkdirSync(nested, { recursive: true });

    vi.stubEnv('USER', 'tester');
    const commands = resolveRuntimeCommands(nested);

    expect(commands.server.command).toBe('dotnet');
    expect(commands.server.args).toContain('run');
    expect(commands.server.args).toContain(path.join(root, 'packages', 'server', 'src', 'Mohist.Server', 'Mohist.Server.csproj'));
    expect(commands.runner.args).toContain(path.join(root, 'packages', 'runner', 'src', 'Mohist.Runner', 'Mohist.Runner.csproj'));
    expect(commands.runner.args).toContain('--ServerUrl=http://localhost:3456');
    expect(commands.runner.args).toContain('--RunnerId=mohist-local-tester');
  });

  it('cleans stale pid files', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-pid-'));
    roots.push(root);
    const pidFile = path.join(root, 'missing.pid');
    fs.writeFileSync(pidFile, '99999999');

    const status = getPidFileStatus(pidFile);

    expect(status.running).toBe(false);
    expect(fs.existsSync(pidFile)).toBe(false);
  });
});
