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
      name: 'app',
      testIgnore: [/iga-engine-harness\.spec\.ts$/, /mtls-exchange\.spec\.ts$/],
      use: { ...devices['Desktop Chrome'], baseURL: EXAMPLE_APP_URL },
    },
  ],
});
