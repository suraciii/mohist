import { Hono } from 'hono';
import { ApiResponse } from '../types';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { execFile } from 'child_process';
import { promisify } from 'util';

const execFileAsync = promisify(execFile);

interface DirEntry {
  name: string;
  absolute: string;
}

export function createFsRoutes(): Hono {
  const app = new Hono();

  app.get('/home', async (c) => {
    const response: ApiResponse<string> = {
      success: true,
      data: os.homedir()
    };
    return c.json(response);
  });

  app.get('/list', async (c) => {
    try {
      const rawPath = c.req.query('path');
      if (!rawPath) {
        const response: ApiResponse = {
          success: false,
          error: 'path query parameter is required'
        };
        return c.json(response, 400);
      }

      const home = os.homedir();
      const resolved = path.resolve(rawPath.replace(/^~/, home));

      if (!resolved.startsWith(home)) {
        const response: ApiResponse = {
          success: false,
          error: 'Path resolves outside HOME directory'
        };
        return c.json(response, 400);
      }

      if (!fs.existsSync(resolved)) {
        const response: ApiResponse = {
          success: false,
          error: 'Path does not exist'
        };
        return c.json(response, 400);
      }

      const stat = fs.statSync(resolved);
      if (!stat.isDirectory()) {
        const response: ApiResponse = {
          success: false,
          error: 'Path is not a directory'
        };
        return c.json(response, 400);
      }

      const entries = fs.readdirSync(resolved, { withFileTypes: true });
      const dirs: DirEntry[] = entries
        .filter(entry => entry.isDirectory() && !entry.name.startsWith('.'))
        .map(entry => ({
          name: entry.name,
          absolute: path.join(resolved, entry.name)
        }))
        .sort((a, b) => a.name.localeCompare(b.name));

      const response: ApiResponse<DirEntry[]> = {
        success: true,
        data: dirs
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/search', async (c) => {
    try {
      const query = c.req.query('query');
      if (!query) {
        const response: ApiResponse = {
          success: false,
          error: 'query query parameter is required'
        };
        return c.json(response, 400);
      }

      const limitParam = c.req.query('limit');
      const limit = limitParam ? parseInt(limitParam, 10) : 50;

      const home = os.homedir();

      const escapedQuery = query.replace(/[\\*?[\]]/g, (ch) => '\\' + ch);

      const { stdout } = await execFileAsync('find', [
        home,
        '-type', 'd',
        '-maxdepth', '4',
        '-iname', `*${escapedQuery}*`
      ], { maxBuffer: 1024 * 1024 });

      const lines = stdout.trim().split('\n').filter(Boolean);
      const results: DirEntry[] = lines
        .filter(dirPath => dirPath !== home)
        .slice(0, limit)
        .map(dirPath => ({
          name: path.basename(dirPath),
          absolute: dirPath
        }));

      const response: ApiResponse<DirEntry[]> = {
        success: true,
        data: results
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  return app;
}
