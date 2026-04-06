import http from 'http';

export const API_BASE = 'http://localhost:3456/api';

export function apiClient<T = any>(
  method: string,
  path: string,
  body?: any
): Promise<T> {
  return new Promise((resolve, reject) => {
    const data = body ? JSON.stringify(body) : undefined;

    const req = http.request(
      `${API_BASE}${path}`,
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
