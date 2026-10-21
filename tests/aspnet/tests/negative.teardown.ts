import * as fs from 'node:fs';
import { test as teardown, expect } from '@playwright/test';
import { cleanupRealms } from '../lib/iga-engine';
import { SUITE_DIR, loadSuite, stopNegativeServer } from '../lib/negativeserver';

/** Set KEEP_REALM=1 to leave the realms up after the run. */
const KEEP_REALM = !!process.env.KEEP_REALM;

teardown('stop Asgard, delete its keys and tear down the realms', async () => {
  if (!fs.existsSync(SUITE_DIR)) return;
  const suite = loadSuite();

  if (suite.server) stopNegativeServer(suite.server);
  // the keys and certificates are only good for these realms
  fs.rmSync(SUITE_DIR, { recursive: true, force: true });

  // exact names, and only the suite's own realms and the precondition realms its recipe runs left behind
  const realms = suite.createdRealms.filter((realm) =>
    ['iga-asgard-negative-', 'iga-asgard-other-realm-', 'iga-precond-'].some((prefix) => realm.startsWith(prefix)),
  );
  if (KEEP_REALM) {
    console.log(`KEEP_REALM set - leaving realms up: ${realms.join(', ')}`);
    return;
  }
  // governed teardown - a bare DELETE on an IGA realm is intercepted and leaves the realm behind
  if (!realms.length) return;
  const cleanup = cleanupRealms(realms);
  expect(cleanup.ok, `realms left behind - tear them down with npm run cleanup in iga-engine-tests:\n${cleanup.output.slice(-2000)}`).toBe(true);
});
