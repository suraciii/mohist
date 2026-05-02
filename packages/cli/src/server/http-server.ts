import { Hono } from 'hono';
import { serve } from '@hono/node-server';
import { serveStatic } from '@hono/node-server/serve-static';
import * as fs from 'fs';
import * as path from 'path';
import { Config, ServerState } from '../types';
import type { RateLimiter } from '../utils/rate-limiter';
import { Log } from '../util/log';

const log = Log.create({ service: 'server' });

export class HttpServer {
  private app: Hono;
  private server: ReturnType<typeof serve> | null;
  private config: Config;
  private state: ServerState;
  private rateLimiter: RateLimiter | null;

  constructor(config: Config, rateLimiter?: RateLimiter) {
    this.config = config;
    this.app = new Hono();
    this.server = null;
    this.state = {
      isRunning: false,
      port: config.serverPort
    };
    this.rateLimiter = rateLimiter || null;
    this.setupMiddleware();
    this.setupRoutes();
  }

  private setupMiddleware(): void {
    this.app.use('*', async (c, next) => {
      const start = Date.now();
      await next();
      const duration = Date.now() - start;
      log.info('HTTP request', {
        method: c.req.method,
        path: c.req.path,
        status: c.res.status,
        duration,
      });
    });
  }

  private setupRoutes(): void {
    this.app.get('/api/health', (c) => {
      return c.json({ status: 'ok', timestamp: new Date().toISOString() });
    });
  }

  public addRouter(path: string, router: Hono): void {
    this.app.route(path, router);
  }

  public serveStaticFiles(webDistDir: string): void {
    const resolvedDir = path.resolve(webDistDir);

    if (!fs.existsSync(resolvedDir)) {
      log.info(`Web dist directory not found, skipping static file serving`, { path: resolvedDir });
      return;
    }

    this.app.use('/assets/*', serveStatic({ root: resolvedDir }));

    this.app.get('*', async (c) => {
      const indexPath = path.join(resolvedDir, 'index.html');
      if (!fs.existsSync(indexPath)) {
        return c.notFound();
      }
      const content = fs.readFileSync(indexPath, 'utf-8');
      c.header('Cache-Control', 'no-store, no-cache, must-revalidate, proxy-revalidate');
      c.header('Pragma', 'no-cache');
      c.header('Expires', '0');
      return c.html(content);
    });
  }

  public getApp(): Hono {
    return this.app;
  }

  public getState(): ServerState {
    return this.state;
  }

  public start(): Promise<void> {
    return new Promise((resolve) => {
      this.server = serve(
        { fetch: this.app.fetch, port: this.config.serverPort, hostname: this.config.serverHost },
        (info) => {
          this.state.isRunning = true;
          this.state.startedAt = new Date().toISOString();
          log.info('Server listening', { host: this.config.serverHost, port: info.port });
          resolve();
        }
      );
    });
  }

  public stop(): Promise<void> {
    return new Promise((resolve, reject) => {
      if (!this.server) {
        resolve();
        return;
      }

      if (this.rateLimiter) {
        this.rateLimiter.dispose();
      }

      this.server.close((err?: Error) => {
        if (err) {
          reject(err);
        } else {
          this.state.isRunning = false;
          log.info('Server stopped');
          resolve();
        }
      });
    });
  }
}
