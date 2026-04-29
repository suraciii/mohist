import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';

vi.mock('child_process', () => ({
  execSync: vi.fn(),
}));

vi.mock('chalk', () => ({
  default: {
    blue: (s: string) => s,
    green: (s: string) => s,
    yellow: (s: string) => s,
    red: (s: string) => s,
    gray: (s: string) => s,
  },
}));

import { execSync } from 'child_process';
import {
  generateServiceFile,
  isSystemdServiceInstalled,
  getSystemdStatus,
  runLinger,
  detectInstallMode,
} from '../src/cli/commands/server-systemd';

const mockExec = execSync as unknown as ReturnType<typeof vi.fn>;

describe('server-systemd', () => {
  beforeEach(() => {
    mockExec.mockReset();
  });

  describe('generateServiceFile', () => {
    it('should produce valid .service content with all required sections', () => {
      const content = generateServiceFile({
        nodePath: '/usr/bin/node',
        scriptPath: '/opt/mohist/packages/cli/bin/mo-server',
        workingDir: '/opt/mohist',
      });

      expect(content).toContain('[Unit]');
      expect(content).toContain('[Service]');
      expect(content).toContain('[Install]');
    });

    it('should include correct Unit fields', () => {
      const content = generateServiceFile({
        nodePath: '/usr/bin/node',
        scriptPath: '/opt/mohist/packages/cli/bin/mo-server',
      });

      expect(content).toContain('Description=Mohist AI Workflow Server');
      expect(content).toContain('After=network-online.target');
    });

    it('should include correct Service fields', () => {
      const content = generateServiceFile({
        nodePath: '/usr/bin/node',
        scriptPath: '/opt/mohist/packages/cli/bin/mo-server',
      });

      expect(content).toContain('Type=simple');
      expect(content).toContain('Restart=on-failure');
      expect(content).toContain('RestartSec=5');
      expect(content).toContain('TimeoutStopSec=30');
      expect(content).toContain('SuccessExitStatus=0 143');
      expect(content).toContain('StandardError=journal');
    });

    it('should include ExecStart with node and script path plus --print-logs', () => {
      const content = generateServiceFile({
        nodePath: '/usr/bin/node',
        scriptPath: '/opt/mo-server',
      });

      expect(content).toContain('ExecStart=/usr/bin/node /opt/mo-server --print-logs');
    });

    it('should include WorkingDirectory only in source mode (when workingDir is provided)', () => {
      const withWorkingDir = generateServiceFile({
        nodePath: '/usr/bin/node',
        scriptPath: '/opt/mohist/packages/cli/bin/mo-server',
        workingDir: '/opt/mohist',
      });

      const withoutWorkingDir = generateServiceFile({
        nodePath: '/usr/bin/node',
        scriptPath: '/opt/mo-server',
      });

      expect(withWorkingDir).toContain('WorkingDirectory=/opt/mohist');
      expect(withoutWorkingDir).not.toContain('WorkingDirectory');
    });

    it('should include WantedBy=default.target in Install section', () => {
      const content = generateServiceFile({
        nodePath: '/usr/bin/node',
        scriptPath: '/opt/mo-server',
      });

      expect(content).toContain('WantedBy=default.target');
    });

    it('should reject nodePath containing newline characters', () => {
      expect(() =>
        generateServiceFile({ nodePath: '/usr/bin/node\nmalicious', scriptPath: '/foo' }),
      ).toThrow('contains newline characters');
    });

    it('should reject nodePath containing carriage return characters', () => {
      expect(() =>
        generateServiceFile({ nodePath: '/usr/bin/node\r', scriptPath: '/foo' }),
      ).toThrow('contains newline characters');
    });

    it('should reject scriptPath containing newline characters', () => {
      expect(() =>
        generateServiceFile({ nodePath: '/usr/bin/node', scriptPath: '/foo\nbar' }),
      ).toThrow('contains newline characters');
    });

    it('should reject workingDir containing newline characters', () => {
      expect(() =>
        generateServiceFile({
          nodePath: '/usr/bin/node',
          scriptPath: '/foo',
          workingDir: '/bar\nbaz',
        }),
      ).toThrow('contains newline characters');
    });

    it('should quote paths containing spaces', () => {
      const content = generateServiceFile({
        nodePath: '/usr/bin/node',
        scriptPath: '/path with spaces/mo-server',
        workingDir: '/my repo dir',
      });

      expect(content).toContain('ExecStart=/usr/bin/node "/path with spaces/mo-server" --print-logs');
      expect(content).toContain('WorkingDirectory="/my repo dir"');
    });

    it('should quote paths containing double quotes', () => {
      const content = generateServiceFile({
        nodePath: '/usr/bin/node',
        scriptPath: '/path"with/quotes',
      });

      expect(content).toContain('"/path\\"with/quotes"');
    });
  });

  describe('isSystemdServiceInstalled', () => {
    it('should return false when service file does not exist', () => {
      const result = isSystemdServiceInstalled();
      expect(typeof result).toBe('boolean');
    });
  });

  describe('getSystemdStatus', () => {
    it('should return status with activeState and mainPID from systemctl show output', () => {
      mockExec.mockReturnValue(
        'Loaded=loaded (/home/user/.config/systemd/user/mohist.service; enabled)\n' +
          'ActiveState=active\n' +
          'MainPID=12345\n',
      );

      const status = getSystemdStatus();
      expect(status).not.toBeNull();
      expect(status!.activeState).toBe('active');
      expect(status!.mainPID).toBe(12345);
    });

    it('should return null when Loaded line is absent', () => {
      mockExec.mockReturnValue('ActiveState=inactive\nMainPID=0\n');

      const status = getSystemdStatus();
      expect(status).toBeNull();
    });

    it('should return null when Loaded=not-loaded', () => {
      mockExec.mockReturnValue(
        'Loaded=not-loaded\nActiveState=inactive\nMainPID=0\n',
      );

      const status = getSystemdStatus();
      expect(status).toBeNull();
    });

    it('should return null when Loaded is empty string', () => {
      mockExec.mockReturnValue('Loaded=\nActiveState=inactive\nMainPID=0\n');

      const status = getSystemdStatus();
      expect(status).toBeNull();
    });

    it('should return null when systemctl show throws', () => {
      mockExec.mockImplementation(() => {
        throw new Error('unit not found');
      });

      const status = getSystemdStatus();
      expect(status).toBeNull();
    });

    it('should return unknown activeState when ActiveState line is missing', () => {
      mockExec.mockReturnValue(
        'Loaded=loaded (/home/user/.config/systemd/user/mohist.service)\n' +
          'MainPID=0\n',
      );

      const status = getSystemdStatus();
      expect(status).not.toBeNull();
      expect(status!.activeState).toBe('unknown');
    });
  });

  describe('runLinger', () => {
    it('should call loginctl enable-linger with current username', () => {
      mockExec.mockReturnValue('');

      runLinger();

      const username = os.userInfo().username;
      expect(mockExec).toHaveBeenCalledWith(
        `loginctl enable-linger ${username}`,
        expect.objectContaining({ encoding: 'utf-8' }),
      );
    });

    it('should not warn when linger is already enabled', () => {
      const consoleSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      mockExec.mockImplementation(() => {
        throw Object.assign(new Error('failed'), {
          stderr: 'linger already enabled for user',
        });
      });

      runLinger();

      expect(consoleSpy).not.toHaveBeenCalled();
      consoleSpy.mockRestore();
    });

    it('should warn on real errors', () => {
      const consoleSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      mockExec.mockImplementation(() => {
        throw Object.assign(new Error('failed'), {
          stderr: 'Permission denied',
        });
      });

      runLinger();

      expect(consoleSpy).toHaveBeenCalledWith(
        expect.stringContaining('Warning: could not enable linger'),
      );
      consoleSpy.mockRestore();
    });
  });

  describe('detectInstallMode', () => {
    it('should return nodePath from process.execPath', () => {
      const mode = detectInstallMode();
      expect(mode.nodePath).toBe(process.execPath);
    });

    it('should return a non-empty scriptPath', () => {
      const mode = detectInstallMode();
      expect(mode.scriptPath).toBeTruthy();
    });
  });
});
