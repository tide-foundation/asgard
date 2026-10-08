import * as fs from 'node:fs';
import * as path from 'node:path';
import { type Page, test, expect } from '@playwright/test';
import { cleanupRealms, inspectIgaEngineRepo, runRecipeKeepingRealm } from '../lib/iga-engine';
import {
  commitAll,
  getChangeRequest,
  listChangeRequests,
  serverIdentityRealmCertificate,
} from '../lib/iga-changerequests';
import {
  type RunningServer,
  buildAll,
  clearEnrollment,
  installAdaptors,
  startTestServer,
} from '../lib/testserver';
import {
  KC_BASE_URL,
  downloadAdaptorConfig,
  getClient,
  getProtocolMappers,
  getUser,
  inspectRealm,
  isTidecloakReachable,
  listRealmNames,
} from '../lib/tidecloak';

/**
 * mTLS + token-exchange spec for the Asgard example app.
 *
 * Builds up in stages, each one a precondition for the next:
 *   1. TideCloak is reachable
 *   2. iga-engine-tests provisions the realm
 *   3. download the frontend + backend adaptor configs      <-- this file, so far
 *   4. install them on the web app and start it
 *   5. login
 *   6. a DPoP-delegation-required endpoint (token exchange + mTLS, which needs
 *      a root JWT to authorize the realm CA + cert change request — done MANUALLY,
 *      so the realm has to outlive the run)
 *
 * Stages 1-2 assert the STACK and the HARNESS. A failure in either is never an
 * Asgard bug — read the attached recipe-output.txt before suspecting the app.
 */

const RECIPE = path.join(__dirname, '..', 'recipes', 'mtls-exchange.recipe.json');
const recipe = JSON.parse(fs.readFileSync(RECIPE, 'utf8'));

/**
 * The realm the recipe run will create, by prefix. MIRRORS the framework's
 * lib/bootstrap.ts realmName(): `iga-${slug}-${suffix}`, where the slug is the
 * recipe's own name sanitised and TRUNCATED TO 24 CHARS — long recipe names do
 * not appear in full, so derive this rather than hand-writing it.
 */
const RECIPE_REALM_PREFIX = `iga-${String(recipe.name ?? 'r')
  .replace(/[^a-z0-9-]/gi, '-')
  .slice(0, 24)
  .toLowerCase()}-`;

/** The framework's ORK-sign precondition gate leaves one of these behind too. */
const GATE_REALM_PREFIX = 'iga-precond-';

/**
 * Set KEEP_REALM=1 to skip teardown and leave the realm up — needed for the
 * manual CA + client-certificate step, and for driving the app by hand.
 */
const KEEP_REALM = !!process.env.KEEP_REALM;

/** Realms this file created, torn down in afterAll. */
let createdRealms: string[] = [];

/** The provisioned realm, for the stages that follow. */
let provisionedRealm: string | undefined;

/**
 * Where downloaded adaptor configs land. Gitignored: they are realm-specific
 * and the backend's carries a LIVE enrollment_token.
 */
const ADAPTOR_DIR = path.join(__dirname, '..', '.adaptors');

/** Adaptor configs on disk, for the install stage. */
const adaptorFiles: Record<string, string> = {};

/** The running test server, stopped in afterAll. */
let server: RunningServer | undefined;

/** Credentials the recipe's setup[] creates. */
const ALICE = { username: 'alice', password: 'Passw0rd!' };

/**
 * Record what the browser saw, and return an attacher for it. "Failed to fetch"
 * in the SPA says nothing on its own — the enclave talks to several origins, and
 * only the browser knows which one died.
 */
function watchPage(page: Page): (label: string) => Promise<void> {
  const consoleLines: string[] = [];
  const failedRequests: string[] = [];

  page.on('console', (m) => consoleLines.push(`${m.type()}: ${m.text()}`));
  page.on('pageerror', (e) => consoleLines.push(`pageerror: ${e.message}`));
  page.on('requestfailed', (r) =>
    failedRequests.push(`${r.method()} ${r.url()} -> ${r.failure()?.errorText}`),
  );
  page.on('response', (r) => {
    if (r.status() >= 400) failedRequests.push(`${r.request().method()} ${r.url()} -> ${r.status()}`);
  });

  return async (label: string) => {
    // Also to stdout: the reporter truncates attachment previews, and these
    // lines are usually the whole diagnosis.
    console.log(
      `\n--- ${label}: browser console ---\n${consoleLines.slice(-25).join('\n') || '(none)'}\n` +
        `--- ${label}: failed requests ---\n${failedRequests.slice(-15).join('\n') || '(none)'}\n`,
    );

    await test.info().attach(`${label}-console.txt`, {
      body: consoleLines.join('\n') || '(no console output)',
      contentType: 'text/plain',
    });
    await test.info().attach(`${label}-failed-requests.txt`, {
      body: failedRequests.join('\n') || '(no failed requests)',
      contentType: 'text/plain',
    });
    await test.info().attach(`${label}-url.txt`, { body: page.url(), contentType: 'text/plain' });
    await test.info().attach(`${label}.png`, {
      body: await page.screenshot({ fullPage: true }),
      contentType: 'image/png',
    });
    await test.info().attach(`${label}-body.txt`, {
      body: await page.locator('body').innerText().catch(() => '(no body text)'),
      contentType: 'text/plain',
    });
    await test.info().attach(`${label}-server.log`, {
      body: server?.log() ?? '(server not started)',
      contentType: 'text/plain',
    });
  };
}

/**
 * Drive the SPA through a full browser login. Each test gets its own browser
 * context, so there is no session to inherit — every test that needs an
 * authenticated page logs in for itself.
 */
async function loginAsAlice(page: Page): Promise<void> {
  await page.goto(server!.url);
  await expect(page.locator('#auth-status')).toHaveText('unauthenticated', { timeout: 30_000 });
  await page.click('#btn-login');

  // Off to TideCloak's login page for this realm…
  await page.waitForURL(new RegExp(`/realms/${provisionedRealm}/`), { timeout: 60_000 });
  await page.fill('#username', ALICE.username);
  await page.fill('#password', ALICE.password);
  await page.click('#kc-login');

  // …and back to the SPA, now authenticated.
  await page.waitForURL(`${server!.url}/**`, { timeout: 60_000 });
  await expect(page.locator('#auth-status')).toHaveText('authenticated', { timeout: 60_000 });
  await expect(page.locator('#user-name')).toHaveText(ALICE.username);
}

test.describe('mTLS token exchange', () => {
  test('TideCloak is reachable', async ({ request }) => {
    test.info().annotations.push({ type: 'kc-base-url', description: KC_BASE_URL });

    expect(
      await isTidecloakReachable(request),
      `TideCloak is not reachable at ${KC_BASE_URL} — bring up the local Tide stack ` +
        `(TideCloak :8080 + ORKs :1001-1005 + postgresP) before running this suite.`,
    ).toBe(true);
  });

  test('the recipe provisions the realm the example app expects', async ({ request }) => {
    const repo = inspectIgaEngineRepo();
    expect(repo.ok, `framework unusable: ${repo.problems.join('; ')}`).toBe(true);
    expect(await isTidecloakReachable(request), `TideCloak not reachable at ${KC_BASE_URL}`).toBe(
      true,
    );

    const before = new Set(await listRealmNames(request));

    const run = runRecipeKeepingRealm(RECIPE);
    createdRealms = (await listRealmNames(request)).filter((r) => !before.has(r));

    test.info().annotations.push({ type: 'recipe-exit', description: `status=${run.status}` });
    await test.info().attach('recipe-output.txt', { body: run.output, contentType: 'text/plain' });

    expect(run.ok, `recipe run failed (exit ${run.status}):\n${run.output.slice(-4000)}`).toBe(true);

    const recipeRealms = createdRealms.filter((r) => r.startsWith(RECIPE_REALM_PREFIX));
    expect(
      recipeRealms,
      `expected exactly one surviving realm named ${RECIPE_REALM_PREFIX}* after a ` +
        `KEEP_REALM=1 run; new realms were: ${createdRealms.join(', ') || '(none)'}`,
    ).toHaveLength(1);

    const realm = recipeRealms[0];
    provisionedRealm = realm;
    test.info().annotations.push({ type: 'realm', description: realm });

    // Still a Tide realm, on the same footing the framework guarantees at bootstrap.
    const shape = await inspectRealm(request, realm);
    expect(shape.exists, `realm ${realm} vanished between listing and inspection`).toBe(true);
    expect(shape.igaEnabled, `realm ${realm} should have IGA enabled`).toBe(true);
    expect(shape.defaultSignatureAlgorithm, `realm ${realm} should be on EdDSA`).toBe('EdDSA');
    expect(
      shape.orkPath,
      `realm ${realm} must be on the ORK signing path (tide-vendor-key with no local eddsaPrivateKey)`,
    ).toBe(true);

    // Everything below was built in the recipe's PRE-IGA setup[] phase, so this
    // asserts the adopt-scan carried the whole shape across the IGA transition
    // intact — not merely that the writes landed.

    // `frontend` — the SPA client. clientId and origin must match the adaptor the
    // app ships (ClientApp/public/keycloak.json: resource=frontend, public-client).
    const frontend = await getClient(request, realm, 'frontend');
    expect(frontend, `realm ${realm} has no client "frontend"`).toBeTruthy();
    expect(frontend.publicClient, '"frontend" must be a public client').toBe(true);
    expect(frontend.standardFlowEnabled, '"frontend" needs the standard flow for browser login').toBe(
      true,
    );
    expect(frontend.redirectUris, '"frontend" must allow the example app origin').toContain(
      'http://localhost:3000/*',
    );
    expect(frontend.webOrigins, '"frontend" must allow CORS from the example app').toContain(
      'http://localhost:3000',
    );

    // `backend` — the confidential client the ASP.NET app authenticates AS
    // (appsettings.json: resource=backend, empty credentials => mTLS, not a secret).
    const backend = await getClient(request, realm, 'backend');
    expect(backend, `realm ${realm} has no client "backend"`).toBeTruthy();
    expect(backend.publicClient, '"backend" must be confidential').toBe(false);
    expect(
      backend.clientAuthenticatorType,
      '"backend" must authenticate by client certificate, not a secret',
    ).toBe('tide-mtls');
    expect(
      backend.attributes?.['standard.token.exchange.enabled'],
      '"backend" is the client that CALLS token exchange, so v2 exchange must be enabled on it',
    ).toBe('true');

    // The audience mapper is load-bearing: PolicyController exchanges the user's
    // frontend token for audience `backend`, which exchange v2 only permits when
    // the subject token already carries backend in aud.
    const mappers = await getProtocolMappers(request, realm, 'frontend');
    const audience = mappers.find((m: any) => m.name === 'backend-audience');
    expect(
      audience,
      `"frontend" is missing the backend-audience mapper; mappers present: ` +
        `${mappers.map((m: any) => m.name).join(', ') || '(none)'}`,
    ).toBeTruthy();
    expect(audience.protocolMapper).toBe('oidc-audience-mapper');
    expect(audience.config?.['included.client.audience']).toBe('backend');
    expect(
      audience.config?.['access.token.claim'],
      'the audience must land in the ACCESS token — that is the token being exchanged',
    ).toBe('true');

    // The login subject for stage 5.
    const alice = await getUser(request, realm, 'alice');
    expect(alice, `realm ${realm} has no user "alice"`).toBeTruthy();
    expect(alice.enabled).toBe(true);
  });

  test('downloads the frontend and backend adaptor configs', async ({ request }) => {
    test.skip(!provisionedRealm, 'no realm was provisioned — the previous test must pass first');
    const realm = provisionedRealm!;

    const outDir = path.join(ADAPTOR_DIR, realm);
    fs.mkdirSync(outDir, { recursive: true });

    for (const clientId of ['frontend', 'backend']) {
      const config = await downloadAdaptorConfig(request, realm, clientId);
      const file = path.join(outDir, `${clientId}.keycloak.json`);
      fs.writeFileSync(file, `${JSON.stringify(config, null, 2)}\n`);
      adaptorFiles[clientId] = file;

      // Both configs are pinned to THIS realm, which is the whole point of
      // re-downloading rather than reusing the ones committed to the app.
      expect(config.realm, `${clientId} adaptor is for the wrong realm`).toBe(realm);
      expect(config.resource, `${clientId} adaptor names the wrong client`).toBe(clientId);
      expect(config['auth-server-url'], `${clientId} adaptor has no auth-server-url`).toBeTruthy();

      // The Tide grafting. Stock Keycloak's installation endpoint returns none
      // of this; its presence is what proves we hit the vendorResources wrapper.
      expect(config.jwk?.keys?.[0]?.alg, `${clientId} adaptor is missing the EdDSA realm key`).toBe(
        'EdDSA',
      );

      // Never attach the raw config to the report — the backend's carries a live
      // credential. Key names are enough to debug a missing field.
      await test.info().attach(`${clientId}-adaptor-keys.txt`, {
        body: Object.keys(config).sort().join('\n'),
        contentType: 'text/plain',
      });
    }

    const frontend = JSON.parse(fs.readFileSync(adaptorFiles.frontend, 'utf8'));
    const backend = JSON.parse(fs.readFileSync(adaptorFiles.backend, 'utf8'));

    expect(frontend['public-client'], 'the SPA adaptor must be a public client').toBe(true);

    // The enrollment token is minted ONLY for a tide-mtls client, and only for a
    // caller that can manage the realm. AddAsgard(..., AutoMTLSEnrollment) reads
    // it to enrol the app's client certificate, so stage 6 dies without it.
    expect(
      backend.enrollment_token,
      'backend adaptor has no enrollment_token — either the client is not on tide-mtls ' +
        'or the admin token cannot manage the realm',
    ).toBeTruthy();
    expect(
      frontend.enrollment_token,
      'frontend is a public client, so it must NOT be handed an enrollment token',
    ).toBeUndefined();

    // Not asserted, because it comes from the signed-origins ceremony rather
    // than from bootstrap: without it the Tide enclave refuses the browser login
    // at stage 5 with "Client origin could not be verified".
    const originKey = 'client-origin-auth-http://localhost:3000';
    test.info().annotations.push({
      type: 'signed-origin',
      description: frontend[originKey]
        ? `${originKey} present`
        : `${originKey} MISSING — browser login will likely fail until the origin is signed`,
    });

    test.info().annotations.push({ type: 'adaptors', description: outDir });
  });

  test('alice logs in and calls /Hello with a DPoP-bound token', async ({ page }) => {
    test.skip(!adaptorFiles.frontend, 'adaptors were not downloaded — the previous test must pass');

    await test.step('install the adaptors into the test server', async () => {
      const installed = installAdaptors(path.dirname(adaptorFiles.frontend));
      test.info().annotations.push({ type: 'installed-adaptors', description: installed });
    });

    await test.step('build the SPA and the test server, then start it', async () => {
      // SPA (npm install + vite build -> wwwroot) and dotnet build. A missing
      // wwwroot makes `/` a 404, so both are part of the test, not a manual step.
      const build = buildAll();
      expect(build.ok, `build failed:\n${build.output.slice(-4000)}`).toBe(true);
      server = await startTestServer();
    });

    const captureEvidence = watchPage(page);

    try {
      await test.step(`log in as ${ALICE.username}`, async () => {
        await loginAsAlice(page);
      });

      await test.step('call /Hello with the DPoP-bound token', async () => {
        await page.click('#btn-call-api');
        // secureFetch attaches the proof; without it the server answers
        // 400 DPoP error="invalid_request", so a 200 here IS the DPoP assertion.
        await expect(page.locator('#result')).toContainText('hello 200', { timeout: 30_000 });
        await expect(page.locator('#result')).toContainText('hi!');
        await expect(page.locator('#result')).toContainText(ALICE.username);
      });
    } catch (err) {
      await captureEvidence('failure');
      throw err;
    }
  });

  test('approves the realm and resource certificate change requests', async ({ request }) => {
    test.skip(!server, 'the test server must have started first');
    const realm = provisionedRealm!;

    // Starting the server ran AddAsgard's AutoMTLSEnrollment, which FILES both
    // certificate change requests — this test does not create them, it approves
    // them. The resource CR dependsOn the realm CR, so the realm CA must land
    // first; commitAll's passes handle that ordering.
    const pending = await listChangeRequests(request, realm, 'PENDING');
    await test.info().attach('pending-change-requests.json', {
      body: JSON.stringify(pending, null, 2),
      contentType: 'application/json',
    });

    const ofType = (t: string) => pending.filter((c: any) => c.actionType === t);
    const realmCert = ofType('REQUEST_REALM_CERT');
    const resourceCert = ofType('REQUEST_SERVER_CERT');

    const summary = pending.map((c: any) => `${c.actionType}/${c.entityType}`).join(', ');
    expect(realmCert, `no pending REQUEST_REALM_CERT; pending were: ${summary || '(none)'}`)
      .toHaveLength(1);
    expect(resourceCert, `no pending REQUEST_SERVER_CERT; pending were: ${summary || '(none)'}`)
      .toHaveLength(1);

    const report = await commitAll(request, realm, [realmCert[0].id, resourceCert[0].id]);
    await test.info().attach('approval-report.json', {
      body: JSON.stringify(report, null, 2),
      contentType: 'application/json',
    });
    expect(
      report.failures,
      `change requests never committed: ${JSON.stringify(report.attempts, null, 2)}`,
    ).toEqual([]);

    for (const cr of [realmCert[0], resourceCert[0]]) {
      const after = await getChangeRequest(request, realm, cr.id);
      expect(after?.status, `${cr.actionType} did not reach APPROVED`).toBe('APPROVED');
    }

    // A committed CR is NOT proof a certificate was issued — commit is capability
    // gated, and a realm without signing capability commits to APPROVED while
    // issuing nothing. The public realmCertificate endpoint only answers once
    // the leaf AND the realm root CA both exist, so it is the real observable.
    const cert = await serverIdentityRealmCertificate(request, realm);
    await test.info().attach('realm-certificate.txt', {
      body: `HTTP ${cert.status}\n\n${cert.text}`,
      contentType: 'text/plain',
    });
    expect(cert.status, 'realm certificate was not issued despite both CRs committing').toBe(200);
    expect(cert.text).toContain('BEGIN CERTIFICATE');
  });

  test('exchanges the token over mTLS at /Hello/exchange', async ({ page }) => {
    test.skip(!server, 'the test server must have started first');

    const captureEvidence = watchPage(page);

    try {
      // The exchange authenticates to TideCloak AS `backend`, whose
      // clientAuthenticatorType is tide-mtls — so it cannot work until the
      // certificate approved in the previous test has actually been COLLECTED.
      // The background enrollment service polls every 5s (appsettings.json), so
      // wait for it to say so rather than sleeping a guessed amount.
      await expect
        .poll(() => server!.log(), {
          timeout: 90_000,
          message:
            'the resource identity was never enrolled — the app never collected the ' +
            'approved certificate, so mTLS cannot be attempted',
        })
        .toContain('Resource identity enrolled');

      await loginAsAlice(page);

      await test.step('exchange the token', async () => {
        await page.click('#btn-exchange');

        // Three things have to hold for this 200:
        //  - the DPoP delegation challenge was answered (secureFetch retries
        //    with DPoP-Resource-Delegation), else 401 delegation_required;
        //  - the app authenticated to TideCloak with its enrolled client
        //    certificate — this is the mTLS assertion;
        //  - exchange v2 accepted the subject token, which requires `backend`
        //    in its aud (the recipe's backend-audience mapper).
        await expect(page.locator('#result')).toContainText('exchange 200', { timeout: 60_000 });
        await expect(page.locator('#result')).toContainText('"exchanged":true');

        // The exchanged token must be issued TO backend, not merely obtained.
        await expect(page.locator('#result')).toContainText('"requestingClientId":"backend"');
        await expect(page.locator('#result')).toContainText('"azp":"backend"');
        // …and still carry alice as the subject: exchange delegates, not impersonates.
        await expect(page.locator('#result')).toContainText(ALICE.username);
      });

      const resultText = await page.locator('#result').innerText();
      await test.info().attach('exchange-result.txt', {
        body: resultText,
        contentType: 'text/plain',
      });

      // Print the exchanged token and its decoded claims. Safe only because the
      // realm is torn down at the end of the run — this is a live credential.
      const body = JSON.parse(resultText.match(/^exchange 200 (\{.*\})$/m)![1]);
      const claims = JSON.parse(
        Buffer.from(body.token.split('.')[1], 'base64url').toString('utf8'),
      );
      console.log(
        `\n--- exchanged token ---\n${body.token}\n` +
          `--- decoded claims ---\n${JSON.stringify(claims, null, 2)}\n`,
      );
    } catch (err) {
      await captureEvidence('exchange-failure');
      throw err;
    }
  });
});

test.afterAll(async () => {
  // Stop the app before the realm goes away, so shutdown never races a 404.
  await server?.stop();

  // Always drop the enrolled mTLS identity — on success AND on failure. It is
  // bound to this run's realm, it is private key material, and a leftover key
  // is REUSED by the next run instead of enrolling against the new realm.
  const removed = clearEnrollment();
  if (removed.length) console.log(`cleared mTLS enrollment material: ${removed.join(', ')}`);

  if (!createdRealms.length) return;

  const toRemove = createdRealms.filter(
    (r) => r.startsWith(RECIPE_REALM_PREFIX) || r.startsWith(GATE_REALM_PREFIX),
  );

  if (KEEP_REALM) {
    // test.info() is unavailable outside a test, so this goes to the console.
    console.log(
      `KEEP_REALM set — leaving realms up. The recipe realm (the one the manual CA + ` +
        `certificate step targets) is: ${provisionedRealm ?? '(not provisioned)'}\n` +
        `Tear down later with, in the iga-engine-tests repo:\n` +
        `  npm run cleanup -- ${toRemove.join(' ')}`,
    );
    return;
  }

  // Governed teardown — a bare DELETE on an IGA realm is intercepted and would
  // leave the realm behind. Exact names only, so a concurrent run is untouched.
  if (toRemove.length) cleanupRealms(toRemove);
});
