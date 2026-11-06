import { spawn, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import type { ResourceIdentity } from './realm-client';
import type { BuildResult } from './testserver';

/**
 * The Asgard instance in ../negativeserver, and the rest of what the negative suite sets up once: its realms and the
 * resource certificates. Its setup project starts the server and its teardown project stops it, so the state between
 * them lives in SUITE_DIR - outside the repo, like every key and certificate the suite makes.
 */

export const NEGATIVESERVER_DIR = path.join(__dirname, '..', 'negativeserver');

const DLL = path.join(NEGATIVESERVER_DIR, 'bin', 'Debug', 'net10.0', 'Asgard.NegativeServer.dll');

export const SUITE_DIR = path.join(os.tmpdir(), 'asgard-negative-suite');

const STATE_FILE = path.join(SUITE_DIR, 'state.json');

export interface NegativeSuite {
  /** Every realm the setup created - its own two and the framework's precondition realms - for the teardown. */
  createdRealms: string[];
  realm?: string;
  otherRealm?: string;
  server?: NegativeServer;
  resourceA?: ResourceIdentity;
  resourceB?: ResourceIdentity;
}

export const saveSuite = (suite: NegativeSuite): void => fs.writeFileSync(STATE_FILE, JSON.stringify(suite, null, 2));

export const loadSuite = (): NegativeSuite => JSON.parse(fs.readFileSync(STATE_FILE, 'utf8'));

export function buildNegativeServer(timeoutMs = 8 * 60_000): BuildResult {
  const res = spawnSync('dotnet', ['build', '-v', 'q', '--nologo'], { cwd: NEGATIVESERVER_DIR, encoding: 'utf8', timeout: timeoutMs });
  return { ok: res.status === 0, status: res.status, output: `${res.stdout ?? ''}\n${res.stderr ?? ''}${res.error ? `\n${res.error.message}` : ''}` };
}

export interface NegativeServer {
  pid: number;
  /** Takes DPoP-bound tokens. */
  httpUrl: string;
  /** Takes certificate-bound tokens, from callers with a realm client certificate. */
  httpsUrl: string;
  logFile: string;
  /** The certificate the realm signed for the server's resource key, once it is enrolled. */
  certificatePath: string;
}

export const serverLog = (server: NegativeServer): string => fs.readFileSync(server.logFile, 'utf8');

function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const probe = net.createServer().listen(0, 'localhost', () => {
      const { port } = probe.address() as net.AddressInfo;
      probe.close(() => resolve(port));
    });
    probe.on('error', reject);
  });
}

/**
 * Starts the server with a realm's adaptor and waits until it answers. Startup sends its certificate request, so by
 * then the change requests for it are filed. The server outlives this process; stopNegativeServer ends it.
 */
export async function startNegativeServer(adaptor: object, timeoutMs = 90_000): Promise<NegativeServer> {
  if (!fs.existsSync(DLL)) throw new Error(`the negative server is not built: ${DLL} is missing (run buildNegativeServer)`);

  const file = (name: string) => path.join(SUITE_DIR, name);
  const httpUrl = `http://localhost:${await freePort()}`;
  const httpsUrl = `https://localhost:${await freePort()}`;
  const logFile = file('asgard.log');
  const log = fs.openSync(logFile, 'w');

  const child = spawn('dotnet', [DLL], {
    cwd: NEGATIVESERVER_DIR,
    env: {
      ...process.env,
      ASPNETCORE_URLS: `${httpUrl};${httpsUrl}`,
      TIDECLOAK_JSON: JSON.stringify({ ...adaptor, enrollment_poll_interval_seconds: 1 }),
      Asgard__identity_key_path: file('resource.key'),
      Asgard__tide_key_path: file('tide.key'),
      Asgard__certificate_request_path: file('resource.csr'),
      Asgard__certificate_path: file('resource.crt'),
      Asgard__root_ca_path: file('root.crt'),
    },
    detached: true,
    stdio: ['ignore', log, log],
  });
  child.unref();
  fs.closeSync(log);

  const server = { pid: child.pid!, httpUrl, httpsUrl, logFile, certificatePath: file('resource.crt') };
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`the negative server exited with code ${child.exitCode}:\n${serverLog(server)}`);
    try {
      await fetch(`${httpUrl}/bound`, { signal: AbortSignal.timeout(2000) });
      return server;
    } catch {
      await new Promise((r) => setTimeout(r, 500));
    }
  }

  stopNegativeServer(server);
  throw new Error(`the negative server did not answer at ${httpUrl} within ${timeoutMs}ms:\n${serverLog(server)}`);
}

export function stopNegativeServer(server: NegativeServer): void {
  try {
    process.kill(server.pid, 'SIGTERM');
  } catch {
    // already gone
  }
}
