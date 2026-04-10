import { Hono } from 'hono';
import { serve } from '@hono/node-server';
import { serveStatic } from '@hono/node-server/serve-static';
import * as fs from 'fs';
import * as path from 'path';
import { Config, ServerState } from '../types';
import type { RateLimiter } from '../utils/rate-limiter';

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
      console.log(`${new Date().toISOString()} ${c.req.method} ${c.req.path}`);
      await next();
    });
  }

  private setupRoutes(): void {
    this.app.get('/api/health', (c) => {
      return c.json({ status: 'ok', timestamp: new Date().toISOString() });
    });

    this.app.get('/api/status', (c) => {
      return c.json({
        success: true,
        data: this.state
      });
    });
  }

  public addRouter(path: string, router: Hono): void {
    this.app.route(path, router);
  }

  public serveStaticFiles(webDistDir: string): void {
    const resolvedDir = path.resolve(webDistDir);

    if (!fs.existsSync(resolvedDir)) {
      console.log(`Web dist directory not found: ${resolvedDir}, skipping static file serving`);
      return;
    }

    this.app.use('/assets/*', serveStatic({ root: resolvedDir }));

    this.app.get('*', async (c) => {
      const indexPath = path.join(resolvedDir, 'index.html');
      if (!fs.existsSync(indexPath)) {
        return c.notFound();
      }
      const content = fs.readFileSync(indexPath, 'utf-8');
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
          console.log(`Server listening on ${this.config.serverHost}:${info.port}`);
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
          console.log('Server stopped');
          resolve();
        }
      });
    });
  }
}
