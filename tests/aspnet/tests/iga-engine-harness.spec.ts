import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import {
  IGA_ENGINE_DIR,
  cleanupRealms,
  inspectIgaEngineRepo,
  runRecipeKeepingRealm,
} from '../lib/iga-engine';
import { KC_BASE_URL, inspectRealm, isTidecloakReachable, listRealmNames } from '../lib/tidecloak';

/**
 * BASE TEST for the Asgard test project.
 *
 * Establishes the foundation everything else stands on: that the
 * tidecloak-iga-engine-tests framework is present and usable, and that we can
 * drive it to provision a Tide+IGA+ORK realm that OUTLIVES the run. Once that
 * holds, the Asgard realm recipe (to be defined) drops straight into
 * recipes/realm-keepalive.recipe.json's place.
 *
 * This spec asserts the PIPELINE, not TideCloak behaviour. Failures here mean
 * the harness is broken, never that TideCloak has a bug.
 */

const RECIPE = path.join(__dirname, '..', 'recipes', 'realm-keepalive.recipe.json');

/** Realm-name prefixes a KEEP_REALM=1 run leaves behind (see the note in the test). */
const RECIPE_REALM_PREFIX = 'iga-realm-keepalive-';
const GATE_REALM_PREFIX = 'iga-precond-';

test.describe('iga-engine-tests harness', () => {
  test('the iga-engine-tests framework is present and usable', async () => {
    const repo = inspectIgaEngineRepo();

    test.info().annotations.push({ type: 'iga-engine-dir', description: repo.dir });

    expect(
      repo.problems,
      `The iga-engine-tests framework at ${repo.dir} is not usable:\n` +
        repo.problems.map((p) => `  - ${p}`).join('\n') +
        `\n(set IGA_ENGINE_DIR to point elsewhere)`,
    ).toEqual([]);

    expect(repo.ok).toBe(true);
    expect(repo.packageName).toBe('tidecloak-iga-engine-tests');
  });

  test('executes a recipe that keeps the realm alive', async ({ request }) => {
    // The framework must be usable before this can mean anything.
    const repo = inspectIgaEngineRepo();
    expect(repo.ok, `framework unusable: ${repo.problems.join('; ')}`).toBe(true);

    expect(
      await isTidecloakReachable(request),
      `TideCloak is not reachable at ${KC_BASE_URL} — bring up the local Tide stack ` +
        `(TideCloak :8080 + ORKs :1001-1005 + postgresP) before running the harness suite.`,
    ).toBe(true);

    const before = new Set(await listRealmNames(request));

    // Track what we create so teardown runs even when an assertion fails.
    let created: string[] = [];

    try {
      const run = runRecipeKeepingRealm(RECIPE);
      test.info().annotations.push({
        type: 'recipe-exit',
        description: `status=${run.status}`,
      });
      await test.info().attach('recipe-output.txt', {
        body: run.output,
        contentType: 'text/plain',
      });

      expect(run.ok, `recipe run failed (exit ${run.status}):\n${run.output.slice(-4000)}`).toBe(true);

      const after = await listRealmNames(request);
      created = after.filter((r) => !before.has(r));

      // A KEEP_REALM=1 run leaves TWO realms behind, not one: the recipe's own
      // realm, and the realm the framework's ORK-sign precondition gate
      // bootstraps (the `iga-engine` project dependsOn `setup`, and the gate
      // honours KEEP_REALM too). Assert on the recipe's realm specifically.
      const recipeRealms = created.filter((r) => r.startsWith(RECIPE_REALM_PREFIX));

      expect(
        recipeRealms,
        `expected exactly one surviving realm named ${RECIPE_REALM_PREFIX}* after a ` +
          `KEEP_REALM=1 run; new realms were: ${created.join(', ') || '(none)'}`,
      ).toHaveLength(1);

      const realm = recipeRealms[0];
      test.info().annotations.push({ type: 'kept-realm', description: realm });

      // Surviving is necessary but not sufficient — it must survive INTACT, on
      // the same footing the framework guarantees at bootstrap.
      const shape = await inspectRealm(request, realm);
      expect(shape.exists, `realm ${realm} vanished between listing and inspection`).toBe(true);
      expect(shape.igaEnabled, `realm ${realm} should still have IGA enabled`).toBe(true);
      expect(shape.defaultSignatureAlgorithm, `realm ${realm} should be on EdDSA`).toBe('EdDSA');
      expect(
        shape.orkPath,
        `realm ${realm} must be on the ORK signing path (tide-vendor-key with no local eddsaPrivateKey)`,
      ).toBe(true);
    } finally {
      // Tear down through the framework's governed cleanup — a bare DELETE on an
      // IGA realm is intercepted and would leave the realm behind. Clean up the
      // gate's realm as well as the recipe's, by exact name so a concurrent run
      // is never touched.
      const toRemove = created.filter(
        (r) => r.startsWith(RECIPE_REALM_PREFIX) || r.startsWith(GATE_REALM_PREFIX),
      );
      if (toRemove.length) {
        const cleaned = cleanupRealms(toRemove);
        test.info().annotations.push({
          type: 'cleanup',
          description: `${toRemove.join(', ')} -> exit ${cleaned.status}`,
        });
      }
    }
  });
});
