import * as fs from 'node:fs';
import * as path from 'node:path';
import { type APIRequestContext, test as setup, expect } from '@playwright/test';
import { commitAll, listChangeRequests } from '../lib/iga-changerequests';
import { inspectIgaEngineRepo, runRecipeKeepingRealm } from '../lib/iga-engine';
import {
  type NegativeSuite,
  SUITE_DIR,
  buildNegativeServer,
  saveSuite,
  serverLog,
  startNegativeServer,
} from '../lib/negativeserver';
import { collectResourceCertificate, requestResourceCertificate } from '../lib/realm-client';
import { KC_BASE_URL, downloadAdaptorConfig, isTidecloakReachable, listRealmNames } from '../lib/tidecloak';

const RECIPES = path.join(__dirname, '..', 'recipes');

/** Provisions a realm from recipes/{name}.recipe.json and returns its name - the framework names it iga-{name}-{suffix}. */
async function provision(request: APIRequestContext, suite: NegativeSuite, name: string): Promise<string> {
  const before = new Set(await listRealmNames(request));
  const run = runRecipeKeepingRealm(path.join(RECIPES, `${name}.recipe.json`));
  const created = (await listRealmNames(request)).filter((realm) => !before.has(realm));
  suite.createdRealms.push(...created);
  saveSuite(suite);

  expect(run.ok, `recipe ${name} failed (exit ${run.status}):\n${run.output.slice(-4000)}`).toBe(true);
  const realm = created.find((r) => r.startsWith(`iga-${name}-`));
  expect(realm, `recipe ${name} left no iga-${name}-* realm; new realms were: ${created.join(', ') || '(none)'}`).toBeTruthy();
  return realm!;
}

setup('provision the realms, enroll the resource certificates and start Asgard', async ({ request }) => {
  fs.rmSync(SUITE_DIR, { recursive: true, force: true });
  fs.mkdirSync(SUITE_DIR, { recursive: true });
  const suite: NegativeSuite = { createdRealms: [] };
  saveSuite(suite);

  expect(await isTidecloakReachable(request), `TideCloak is not reachable at ${KC_BASE_URL} - bring up the local Tide stack`).toBe(true);
  const framework = inspectIgaEngineRepo();
  expect(framework.ok, `iga-engine-tests is unusable: ${framework.problems.join('; ')}`).toBe(true);

  const realm = (suite.realm = await provision(request, suite, 'asgard-negative'));
  suite.otherRealm = await provision(request, suite, 'asgard-other-realm');

  const build = buildNegativeServer();
  expect(build.ok, `dotnet build failed:\n${build.output.slice(-4000)}`).toBe(true);

  suite.server = await startNegativeServer(await downloadAdaptorConfig(request, realm, 'asgard'));
  saveSuite(suite);

  const keyA = await requestResourceCertificate(request, realm, 'resource-a', SUITE_DIR);
  const keyB = await requestResourceCertificate(request, realm, 'resource-b', SUITE_DIR);

  // Asgard's request and the two above, plus the realm CA they all depend on
  const certificateRequests = (await listChangeRequests(request, realm)).filter((cr) =>
    ['REQUEST_REALM_CERT', 'REQUEST_SERVER_CERT'].includes(cr.actionType),
  );
  expect(certificateRequests.map((cr) => cr.actionType).sort()).toEqual([
    'REQUEST_REALM_CERT',
    'REQUEST_SERVER_CERT',
    'REQUEST_SERVER_CERT',
    'REQUEST_SERVER_CERT',
  ]);
  const approval = await commitAll(request, realm, certificateRequests.map((cr) => cr.id));
  expect(approval.failures, `certificate requests not approved: ${JSON.stringify(approval.attempts, null, 2)}`).toEqual([]);

  suite.resourceA = await collectResourceCertificate(realm, 'resource-a', keyA);
  suite.resourceB = await collectResourceCertificate(realm, 'resource-b', keyB);
  saveSuite(suite);

  await expect
    .poll(() => serverLog(suite.server!), { timeout: 30_000, message: 'Asgard never collected its approved certificate' })
    .toContain('Resource identity enrolled');
});
