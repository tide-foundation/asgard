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

/** `dotnet build`. Slow on a cold obj/, hence the generous timeout. */
export function buildTestServer(timeoutMs = 8 * 60_000): BuildResult {
  const res = spawnSync('dotnet', ['build', '-v', 'q', '--nologo'], {
    cwd: TESTSERVER_DIR,
    encoding: 'utf8',
    timeout: timeoutMs,
  });
  const output = `${res.stdout ?? ''}\n${res.stderr ?? ''}`;
  return { ok: res.status === 0, status: res.status, output };
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
  if (!fs.existsSync(dll)) throw new Error(`test server is not built: ${dll} is missing`);

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
  // directory resolved, since a missing file throws during startup.
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (exited !== null) {
      throw new Error(`test server exited with code ${exited} before becoming ready:\n${out}`);
    }
    try {
      const res = await fetch(`${url}/keycloak.json`, { signal: AbortSignal.timeout(2000) });
      if (res.ok) return { url, log: () => out, stop };
    } catch {
      // not up yet
    }
    await new Promise((r) => setTimeout(r, 500));
  }

  await stop();
  throw new Error(`test server did not become ready at ${url} within ${timeoutMs}ms:\n${out}`);
}
