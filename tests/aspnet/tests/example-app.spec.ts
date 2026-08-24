import { test, expect } from '@playwright/test';

/**
 * Smoke test against a running Tide.Asgard.AspNetCore.Example instance
 * (see aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example),
 * independent of any TideCloak/mTLS setup — just confirms the harness
 * can reach the app.
 */
test('rejects unauthenticated requests to a protected endpoint', async ({ request }) => {
  const response = await request.get('/Hello', { failOnStatusCode: false });
  expect(response.status()).toBe(401);
});
