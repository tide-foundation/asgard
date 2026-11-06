import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

/**
 * The SEAM between this suite and the tidecloak-iga-engine-tests framework.
 *
 * That repo owns realm provisioning: it knows how to bootstrap a Tide+IGA realm
 * onto the ORK signing path and how to tear one down through governance (a bare
 * DELETE on an IGA realm is intercepted and silently does nothing). We do NOT
 * re-implement any of that, and we do NOT import its TypeScript across the
 * filesystem — it is `private: true` with no entry point, and its Playwright
 * version differs from ours, so a cross-repo import would load two copies of
 * @playwright/test. We shell out to its documented CLI instead.
 *
 * Everything this suite depends on in that repo is pinned by REQUIRED_PATHS /
 * REQUIRED_SCRIPTS below, so a rename over there fails loudly and specifically
 * here rather than as an opaque spawn error.
 */

/** Location of the framework. Override with IGA_ENGINE_DIR. */
export const IGA_ENGINE_DIR =
  process.env.IGA_ENGINE_DIR ?? path.join(os.homedir(), 'tidecloak-iga-engine-tests');

/** The package name we expect to find there — guards against pointing at the wrong repo. */
const EXPECTED_PACKAGE_NAME = 'tidecloak-iga-engine-tests';

/** Files this suite's integration actually relies on. */
const REQUIRED_PATHS = [
  'package.json',
  'playwright.config.ts',
  'lib/bootstrap.ts', // createTideOrkIgaRealm + the IGA-aware deleteRealm
  'lib/runner.ts', // honours KEEP_REALM=1
  'scripts/run-recipe.ts', // runs a recipe BY PATH, incl. files outside tests/
  'scripts/cleanup-realms.ts', // governed teardown of leftover realms
  'catalog/capabilities.ts', // the capability spine recipes are written against
];

/** npm scripts this suite invokes. */
const REQUIRED_SCRIPTS = ['recipe', 'cleanup'];

export interface RepoInspection {
  ok: boolean;
  dir: string;
  /** Human-readable problems, empty when ok. */
  problems: string[];
  packageName?: string;
  scripts?: Record<string, string>;
}

/**
 * Structural check of the framework repo. Pure filesystem — never touches the
 * stack, so it stays meaningful even with nothing running.
 */
export function inspectIgaEngineRepo(dir: string = IGA_ENGINE_DIR): RepoInspection {
  const problems: string[] = [];

  if (!fs.existsSync(dir) || !fs.statSync(dir).isDirectory()) {
    return {
      ok: false,
      dir,
      problems: [
        `iga-engine-tests repo not found at ${dir}. Clone it there, or set IGA_ENGINE_DIR to its location.`,
      ],
    };
  }

  let packageName: string | undefined;
  let scripts: Record<string, string> | undefined;

  const pkgPath = path.join(dir, 'package.json');
  if (fs.existsSync(pkgPath)) {
    try {
      const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
      packageName = pkg.name;
      scripts = pkg.scripts ?? {};
      if (packageName !== EXPECTED_PACKAGE_NAME) {
        problems.push(
          `${dir} is not the iga-engine-tests repo: package.json name is "${packageName}", expected "${EXPECTED_PACKAGE_NAME}".`,
        );
      }
      for (const s of REQUIRED_SCRIPTS) {
        if (!scripts?.[s]) problems.push(`missing npm script "${s}" in ${pkgPath}`);
      }
    } catch (e: any) {
      problems.push(`could not parse ${pkgPath}: ${e?.message ?? e}`);
    }
  }

  for (const rel of REQUIRED_PATHS) {
    if (!fs.existsSync(path.join(dir, rel))) problems.push(`missing required file: ${rel}`);
  }

  // The CLI is ts-node based; without an install every invocation dies opaquely.
  if (!fs.existsSync(path.join(dir, 'node_modules'))) {
    problems.push(`dependencies are not installed — run "npm install" in ${dir}`);
  }

  return { ok: problems.length === 0, dir, problems, packageName, scripts };
}

/** Environment forwarded to the framework so both suites target the same stack. */
function stackEnv(): Record<string, string> {
  const env: Record<string, string> = {};
  for (const k of ['KC_BASE_URL', 'KC_ADMIN_USER', 'KC_ADMIN_PASSWORD', 'KC_COMPOSE_FILE']) {
    const v = process.env[k];
    if (v) env[k] = v;
  }
  return env;
}

export interface RecipeRun {
  ok: boolean;
  status: number | null;
  stdout: string;
  stderr: string;
  /** stdout+stderr, for assertion messages. */
  output: string;
}

/**
 * Run one recipe file through the framework with KEEP_REALM=1 so the realm it
 * bootstraps SURVIVES the run.
 *
 * Note this always runs the framework's gated `iga-engine` project, so its
 * ORK-sign precondition executes first — and under KEEP_REALM=1 the gate's own
 * realm (iga-precond-*) is left behind too. Callers are responsible for tearing
 * down everything the run leaves.
 */
export function runRecipeKeepingRealm(
  recipeFile: string,
  opts: { dir?: string; timeoutMs?: number } = {},
): RecipeRun {
  const dir = opts.dir ?? IGA_ENGINE_DIR;
  const abs = path.resolve(recipeFile);
  if (!fs.existsSync(abs)) throw new Error(`recipe file not found: ${abs}`);

  const res = spawnSync('npm', ['run', 'recipe', '--', abs], {
    cwd: dir,
    encoding: 'utf8',
    timeout: opts.timeoutMs ?? 8 * 60_000,
    env: { ...process.env, ...stackEnv(), KEEP_REALM: '1' },
  });

  const stdout = res.stdout ?? '';
  const stderr = res.stderr ?? '';
  return { ok: res.status === 0, status: res.status, stdout, stderr, output: `${stdout}\n${stderr}` };
}

/**
 * Tear down realms through the framework's governed cleanup (toggle IGA off →
 * drain → DELETE). Names are matched by prefix on their side, so exact names
 * work and only the realms named here are touched.
 */
export function cleanupRealms(
  realms: string[],
  opts: { dir?: string; timeoutMs?: number } = {},
): RecipeRun {
  const dir = opts.dir ?? IGA_ENGINE_DIR;
  if (realms.length === 0) return { ok: true, status: 0, stdout: '', stderr: '', output: '' };

  const res = spawnSync('npm', ['run', 'cleanup', '--', ...realms], {
    cwd: dir,
    encoding: 'utf8',
    timeout: opts.timeoutMs ?? 4 * 60_000,
    env: { ...process.env, ...stackEnv() },
  });

  const stdout = res.stdout ?? '';
  const stderr = res.stderr ?? '';
  return { ok: res.status === 0, status: res.status, stdout, stderr, output: `${stdout}\n${stderr}` };
}
