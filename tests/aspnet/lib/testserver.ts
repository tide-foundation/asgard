import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * Lifecycle for the minimal ASP.NET app in ../testserver.
 *
 * The app deliberately holds no TideCloak settings of its own — it reads a
 * directory containing frontend.keycloak.json + backend.keycloak.json, which is
 * exactly what the adaptor-download stage produces. Installing adaptors is
 * therefore a file copy, not a config edit.
 */

export const TESTSERVER_DIR = path.join(__dirname, '..', 'testserver');

/** Where the app looks for adaptors by default. */
const INSTALL_DIR = path.join(TESTSERVER_DIR, 'adaptors');

const ADAPTOR_FILES = ['frontend.keycloak.json', 'backend.keycloak.json'];

/**
 * mTLS material AddAsgard(..., AutoMTLSEnrollment) writes beside the app at
 * startup. It is bound to the realm that issued it, and FileResourceKeyProvider
 * REUSES an existing key rather than enrolling again — so leaving it in place
 * would silently carry one run's identity into the next run's realm.
 */
const ENROLLMENT_FILES = ['resource.key', 'resource.csr', 'resource.crt', 'root.crt'];

/** Default listen URL — must match the frontend client's registered origin. */
export const TESTSERVER_URL = process.env.ASGARD_EXAMPLE_URL ?? 'http://localhost:3000';

/**
 * Copy a realm's adaptors into the server's own adaptors/ directory, so the app
 * picks them up with no environment wiring. Returns the install directory.
 */
export function installAdaptors(fromDir: string): string {
  fs.mkdirSync(INSTALL_DIR, { recursive: true });
  for (const name of ADAPTOR_FILES) {
    const src = path.join(fromDir, name);
    if (!fs.existsSync(src)) throw new Error(`adaptor not found: ${src}`);
    fs.copyFileSync(src, path.join(INSTALL_DIR, name));
  }

  // New adaptors mean a new realm, which invalidates any enrolled identity.
  clearEnrollment();

  return INSTALL_DIR;
}

/** Remove enrolled mTLS material so the next startup enrolls from scratch. */
export function clearEnrollment(): string[] {
  const removed: string[] = [];
  for (const name of ENROLLMENT_FILES) {
    const file = path.join(TESTSERVER_DIR, name);
    if (fs.existsSync(file)) {
      fs.rmSync(file);
      removed.push(name);
    }
  }
  return removed;
}

export interface BuildResult {
  ok: boolean;
  status: number | null;
  output: string;
}

const CLIENTAPP_DIR = path.join(TESTSERVER_DIR, 'ClientApp');

/** Where vite writes the SPA (ClientApp/vite.config.js) and the app serves it from. */
const SPA_INDEX = path.join(TESTSERVER_DIR, 'wwwroot', 'index.html');

function run(cmd: string, args: string[], cwd: string, timeoutMs: number): BuildResult {
  const res = spawnSync(cmd, args, { cwd, encoding: 'utf8', timeout: timeoutMs });
  let output = `${res.stdout ?? ''}\n${res.stderr ?? ''}`;
  if (res.error) output += `\n${res.error.message}`;
  return { ok: res.status === 0, status: res.status, output };
}

/**
 * Build the SPA into wwwroot: `npm install --ignore-scripts` then `vite build`.
 *
 * Without this the server starts and serves /keycloak.json happily, but `/`
 * is a 404 because wwwroot does not exist — the login test then times out
 * waiting for #auth-status, which is a confusing way to learn the SPA was
 * never built.
 *
 * `--ignore-scripts` is required: @tidecloak/js is linked from source and its
 * `prepare` script rebuilds the package, which does not compile on every
 * machine. The package ships a prebuilt dist/, so skipping scripts is enough.
 * The install is a no-op when node_modules is already current, so it runs
 * every time rather than trying to guess staleness. `--package-lock=false`
 * because the linked package's own dependencies vary by machine and npm would
 * otherwise rewrite the committed package-lock.json on every run.
 */
export function buildClientApp(timeoutMs = 8 * 60_000): BuildResult {
  const install = run('npm', ['install', '--ignore-scripts', '--package-lock=false', '--no-audit', '--no-fund'], CLIENTAPP_DIR, timeoutMs);
  if (!install.ok) return { ...install, output: `npm install failed:\n${install.output}` };

  const build = run('npm', ['run', 'build'], CLIENTAPP_DIR, timeoutMs);
  const output = `${install.output}\n${build.output}`;
  if (!build.ok) return { ok: false, status: build.status, output: `vite build failed:\n${output}` };

  if (!fs.existsSync(SPA_INDEX)) {
    return { ok: false, status: build.status, output: `vite build succeeded but ${SPA_INDEX} is missing:\n${output}` };
  }
  return { ok: true, status: build.status, output };
}

/** `dotnet build`. Slow on a cold obj/, hence the generous timeout. */
export function buildTestServer(timeoutMs = 8 * 60_000): BuildResult {
  return run('dotnet', ['build', '-v', 'q', '--nologo'], TESTSERVER_DIR, timeoutMs);
}

/**
 * Everything startTestServer needs: the SPA in wwwroot and the compiled dll.
 * Returns the first failure, so the assertion message names the stage.
 */
export function buildAll(timeoutMs?: number): BuildResult {
  const spa = buildClientApp(timeoutMs);
  if (!spa.ok) return spa;
  const server = buildTestServer(timeoutMs);
  if (!server.ok) return server;
  return { ok: true, status: 0, output: `${spa.output}\n${server.output}` };
}

export interface RunningServer {
  url: string;
  /** Everything the app wrote to stdout+stderr, for assertion messages. */
  log(): string;
  stop(): Promise<void>;
}

/**
 * Start the built app and wait until it serves the frontend adaptor.
 *
 * Runs the compiled dll rather than `dotnet run`: `dotnet run` is a wrapper that
 * spawns the app as a CHILD, so killing it leaves the real server holding the
 * port. One process means one clean kill.
 */
export async function startTestServer(
  opts: { adaptorDir?: string; url?: string; timeoutMs?: number } = {},
): Promise<RunningServer> {
  const url = opts.url ?? TESTSERVER_URL;
  const timeoutMs = opts.timeoutMs ?? 90_000;

  const dll = path.join(TESTSERVER_DIR, 'bin', 'Debug', 'net10.0', 'Asgard.TestServer.dll');
  if (!fs.existsSync(dll)) throw new Error(`test server is not built: ${dll} is missing (run buildTestServer)`);
  if (!fs.existsSync(SPA_INDEX)) throw new Error(`SPA is not built: ${SPA_INDEX} is missing (run buildClientApp)`);

  let out = '';
  const child: ChildProcess = spawn('dotnet', [dll], {
    cwd: TESTSERVER_DIR,
    env: {
      ...process.env,
      ASPNETCORE_URLS: url,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ...(opts.adaptorDir ? { ASGARD_ADAPTORS_DIR: opts.adaptorDir } : {}),
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  child.stdout?.on('data', (d) => (out += d));
  child.stderr?.on('data', (d) => (out += d));

  let exited: number | null = null;
  child.on('exit', (code) => (exited = code));

  const stop = async () => {
    if (child.exitCode === null && child.signalCode === null) {
      child.kill('SIGTERM');
      await new Promise<void>((resolve) => {
        const t = setTimeout(() => {
          child.kill('SIGKILL');
          resolve();
        }, 10_000);
        child.on('exit', () => {
          clearTimeout(t);
          resolve();
        });
      });
    }
  };

  // Ready when it serves the frontend adaptor — which also proves the adaptor
  // directory resolved, since a missing file throws during startup — AND the
  // SPA itself, so a missing/empty wwwroot fails here rather than as a
  // timeout in the browser.
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (exited !== null) {
      throw new Error(`test server exited with code ${exited} before becoming ready:\n${out}`);
    }
    try {
      const adaptor = await fetch(`${url}/keycloak.json`, { signal: AbortSignal.timeout(2000) });
      if (adaptor.ok) {
        const index = await fetch(`${url}/`, { signal: AbortSignal.timeout(2000) });
        if (index.ok) return { url, log: () => out, stop };
        await stop();
        throw new Error(`test server is up but GET ${url}/ returned ${index.status} — is the SPA built into wwwroot?\n${out}`);
      }
    } catch (err) {
      if (err instanceof Error && err.message.includes('is the SPA built')) throw err;
      // not up yet
    }
    await new Promise((r) => setTimeout(r, 500));
  }

  await stop();
  throw new Error(`test server did not become ready at ${url} within ${timeoutMs}ms:\n${out}`);
}
