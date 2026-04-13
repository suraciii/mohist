import http from 'http';
import { load, getServerConfig } from '../config/config-loader';

function getApiBase(): string {
  try {
    const config = load();
    const serverConfig = getServerConfig(config);
    return `http://${serverConfig.host}:${serverConfig.port}/api`;
  } catch {
    return 'http://127.0.0.1:3456/api';
  }
}

export const API_BASE = getApiBase();

export function apiClient<T = any>(
  method: string,
  path: string,
  body?: any
): Promise<T> {
  return new Promise((resolve, reject) => {
    const data = body ? JSON.stringify(body) : undefined;

    const req = http.request(
      `${getApiBase()}${path}`,
      {
        method,
        headers: {
          'Content-Type': 'application/json',
          'Content-Length': data ? Buffer.byteLength(data) : 0
        }
      },
      (res) => {
        let responseData = '';

        res.on('data', (chunk) => {
          responseData += chunk;
        });

        res.on('end', () => {
          try {
            const parsed = JSON.parse(responseData);
            resolve(parsed);
          } catch (error) {
            reject(new Error('Invalid JSON response'));
          }
        });
      }
    );

    req.on('error', reject);

    if (data) {
      req.write(data);
    }

    req.end();
  });
}
