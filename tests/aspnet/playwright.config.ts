import { defineConfig, devices } from '@playwright/test';

/**
 * Base URL of the running Tide.Asgard.AspNetCore.Example app.
 * See aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/Properties/launchSettings.json.
 */
const EXAMPLE_APP_URL = process.env.ASGARD_EXAMPLE_URL ?? 'http://localhost:3000';

/**
 * Two projects, split by what they need running:
 *
 *  - `harness` — proves the tidecloak-iga-engine-tests framework is present and
 *    can provision a realm that outlives the run. Needs the local Tide stack;
 *    does NOT need the example app, and launches no browser. Shelling out to the
 *    framework (which runs its own gated Playwright suite) is slow, hence the
 *    long timeout.
 *  - `mtls-exchange` — the staged mTLS/token-exchange suite. Needs BOTH: it
 *    provisions its own realm through the framework (slow, hence the long
 *    timeout) and then drives the example app in a browser.
 *  - `app` — tests against the running example app.
 *  - `negative` — requests Asgard must turn away. Needs the local Tide stack:
 *    `negative-setup` provisions its realms and starts its Asgard instance once
 *    (a failed test restarts the worker, which would otherwise redo all of it),
 *    and `negative-teardown` stops it and tears the realms down.
 *
 * Kept separate so `npm run test:app` stays fast and a missing Tide stack fails
 * in one obvious place instead of everywhere.
 */
export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: 1,
  reporter: 'html',

  use: {
    trace: 'on-first-retry',
  },

  projects: [
    {
      name: 'harness',
      testMatch: /iga-engine-harness\.spec\.ts$/,
      timeout: 10 * 60_000,
    },
    {
      name: 'mtls-exchange',
      testMatch: /mtls-exchange\.spec\.ts$/,
      timeout: 10 * 60_000,
      // headless explicitly: this suite starts its own web server and must be
      // runnable on a headless box / in CI with no display.
      use: { ...devices['Desktop Chrome'], baseURL: EXAMPLE_APP_URL, headless: true },
    },
    {
      name: 'negative-setup',
      testMatch: /negative\.setup\.ts$/,
      teardown: 'negative-teardown',
      timeout: 10 * 60_000,
    },
    {
      name: 'negative-teardown',
      testMatch: /negative\.teardown\.ts$/,
      timeout: 5 * 60_000,
    },
    {
      name: 'negative',
      testMatch: /negative\.spec\.ts$/,
      dependencies: ['negative-setup'],
    },
    {
      name: 'app',
      testIgnore: [/iga-engine-harness\.spec\.ts$/, /mtls-exchange\.spec\.ts$/, /negative\.spec\.ts$/],
      use: { ...devices['Desktop Chrome'], baseURL: EXAMPLE_APP_URL },
    },
  ],
});
