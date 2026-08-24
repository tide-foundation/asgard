# Asgard ASP.NET Playwright tests

## Quick start

```bash
cd tests/aspnet
npm install
npx playwright install chromium    # only needed for the `app` project
```

Two projects, split by what each needs running:

| Command | Runs | Needs |
|---|---|---|
| `npm run test:harness` | the iga-engine-tests harness | the local Tide stack |
| `npm run test:app` | tests against the example app | the example app on :3000 |
| `npm test` | both | both |
| `npm run report` | open the last HTML report | — |

## Running the harness

The harness proves the foundation this project stands on: that the
[tidecloak-iga-engine-tests](../../../tidecloak-iga-engine-tests) framework is present, and that we
can drive it to run a recipe whose realm stays alive after the run.

**1. Bring up the local Tide stack**

```bash
cd ~/project/tidecloak/Tidified/localtest
docker compose up -d
```

Wait for it, then give it a few more seconds to finish initialising:

```bash
until curl -fsS http://localhost:8080 >/dev/null 2>&1; do sleep 2; done && sleep 5
```

> If `tidecloakP` is stuck restarting, **do not** `docker compose restart tidecloakP` — the image's
> nginx bootstrap writes a file then `chmod 0400`s it, so the next in-place boot cannot rewrite it
> and the container crash-loops. Recreate it instead:
> `docker compose up -d --force-recreate tidecloakP`

**2. Confirm the framework is installed**

```bash
cd ~/tidecloak-iga-engine-tests && npm install
```

**3. Run the harness**

```bash
cd tests/aspnet
npm run test:harness
```

The first test is pure filesystem and needs no stack. The second shells out to the framework, which
runs its own gated suite — the ORK-sign precondition first, then the recipe — so a red here can mean
either the harness or the stack. The framework's full output is attached to the test result as
`recipe-output.txt`; read that before assuming the harness is at fault.

**4. Clean up leftover realms** (the harness does this itself, but if a run is interrupted):

```bash
cd ~/tidecloak-iga-engine-tests
npx ts-node scripts/cleanup-realms.ts iga-realm-keepalive- iga-precond-
```

### Configuration

| Env var | Purpose | Default |
|---|---|---|
| `IGA_ENGINE_DIR` | location of the iga-engine-tests framework | `~/tidecloak-iga-engine-tests` |
| `KC_BASE_URL` | TideCloak base URL | `http://localhost:8080` |
| `KC_ADMIN_USER` / `KC_ADMIN_PASSWORD` | master-realm admin | `admin` / `password` |

These are the framework's own variable names, so one set configures both suites.

## How the harness is put together

[tests/iga-engine-harness.spec.ts](tests/iga-engine-harness.spec.ts) is the **base test**. It proves:

1. The framework is present and usable — pure filesystem, no stack needed.
2. A recipe run with `KEEP_REALM=1` leaves its realm **alive**, which is how this suite will
   provision a realm for the example app.

The realm recipe is [recipes/realm-keepalive.recipe.json](recipes/realm-keepalive.recipe.json) — a
deliberately cheap placeholder that exercises the pipeline, not TideCloak. **The real Asgard realm
recipe replaces it in place** once defined; nothing else needs to change.

[lib/iga-engine.ts](lib/iga-engine.ts) is the **seam**. It shells out to the framework's documented
CLI rather than importing its TypeScript: that repo is `private: true` with no entry point, and its
Playwright version differs from ours, so a cross-repo import would load two copies of
`@playwright/test`. Everything we depend on over there is pinned in `REQUIRED_PATHS` /
`REQUIRED_SCRIPTS`, so a rename fails loudly and specifically instead of as an opaque spawn error.

[lib/tidecloak.ts](lib/tidecloak.ts) is read-only by design. Realm *writes* on an IGA realm go
through governance, and the framework already implements those semantics — duplicating them here
would mean two implementations drifting apart. Teardown likewise goes through the framework's
governed cleanup, because a bare `DELETE` on an IGA realm is intercepted and silently does nothing.

Two things that are easy to miss, both handled:

- A `KEEP_REALM=1` run leaves **two** realms behind, not one — the recipe's realm and the realm the
  framework's ORK-sign precondition gate bootstraps, since the gate honours `KEEP_REALM` too.
- Surviving is asserted as surviving *intact*: IGA still enabled, EdDSA, and on the ORK signing path.

## Running the app tests

[Tide.Asgard.AspNetCore.Example](../../aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/)
must be running (default `http://localhost:3000`, override with `ASGARD_EXAMPLE_URL`).

`mtls-token-exchange.spec.ts` exercises token exchange over mTLS and skips itself unless configured:

| Env var | Purpose | Default |
|---|---|---|
| `TIDECLOAK_URL` | HTTPS base URL of TideCloak (required to run the suite) | — |
| `TIDECLOAK_REALM` | Realm name | `test` |
| `TIDECLOAK_CLIENT_ID` | Requesting client id (`client_id` in the exchange) | `backend` |
| `TIDECLOAK_REQUESTED_AUDIENCE` | Target audience for the exchanged token | unset |
| `TIDECLOAK_SUBJECT_TOKEN` | Caller access token to exchange (required for the success-path test) | — |
| `MTLS_CLIENT_CERT_PATH` | Path to the client `.pfx` presented for mTLS | the example app's `client.pfx` |
| `MTLS_CLIENT_CERT_PASSPHRASE` | Passphrase for the `.pfx`, if any | unset |

## Known blocker

The local stack currently cannot provision a Tide realm — `setUpTideRealm` fails with
`TIDE-IDPEXT-VENDOR-KEYGEN_FAILED`, from `Midgard.FinalizeWallet: signature failed`. Until that is
resolved, the harness's second test cannot go green. It is a stack problem, not a harness one.
