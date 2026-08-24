# Asgard test server

A deliberately minimal ASP.NET Core app that mimics
`aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example` with the business
logic removed. It exists to answer one question: **can a user authenticate, and
is the resulting access token DPoP-bound?**

- one controller, one endpoint — `GET /Hello`, `[Authorize]`
- DPoP is `DPoPModes.Required` globally, so a 200 from `/Hello` is only
  reachable with a DPoP-bound token
- no `AddAsgard`, no policy provider, no token exchange, no mTLS (see
  [Not here yet](#not-here-yet))

## Installing adaptors

The server holds **no** TideCloak settings of its own — `appsettings.json` has
only logging. Everything comes from two adaptor files, named exactly as the
Playwright suite downloads them into `tests/aspnet/.adaptors/<realm>/`:

| file | becomes |
| --- | --- |
| `backend.keycloak.json` | the `Keycloak` configuration section |
| `frontend.keycloak.json` | served to the SPA at `/keycloak.json` |

Point the server at the directory holding them:

```bash
ASGARD_ADAPTORS_DIR=../.adaptors/<realm> dotnet run
```

or drop both files in `testserver/adaptors/`, which is the default. The
directory is also settable as the `Adaptors:Directory` config key.

Neither file is edited into `appsettings.json` and neither is copied into
`wwwroot`, so switching realms is a matter of changing that one path — no file
surgery, no rebuild. Startup fails immediately, naming the missing path, if
either file is absent.

## Running

```bash
# once — builds the SPA into ../wwwroot
cd ClientApp && npm install --ignore-scripts && npm run build

# each time
ASGARD_ADAPTORS_DIR=../.adaptors/<realm> dotnet run
```

`--ignore-scripts` is required, not cosmetic: `@tidecloak/js` is linked from
source and its `prepare` script rebuilds the package, which currently fails on
this machine's TypeScript (`TS6046: Argument for '--moduleResolution' option`).
The package ships a prebuilt `dist/`, so skipping the lifecycle scripts is
enough — a plain `npm install` fails.

Listens on <http://localhost:3000> — the origin the `frontend` client's redirect
URIs and web origins are registered against.

## What the endpoint returns

`GET /Hello` with no DPoP proof answers **400**, not 401:

```
HTTP/1.1 400 Bad Request
WWW-Authenticate: DPoP error="invalid_request"
```

The DPoP layer rejects the missing proof before the bearer challenge is ever
reached, so a test asserting "unauthenticated is 401" would fail here. With a
valid DPoP-bound token it returns the username and the token's `cnf` claim.

## Enrollment poll interval

`AddAsgard(..., AutoMTLSEnrollment)` files the certificate change requests at
startup and then retries collection in the background until they are approved,
so an approval landing *after* startup needs no restart. The default wait is 60
seconds — far too slow for a test loop — so `appsettings.json` sets:

```json
"Keycloak": { "enrollment_poll_interval_seconds": 5 }
```

The adaptor config is layered on top of `appsettings.json`, but it carries no
such key, so this override survives. It must be a positive number; anything else
throws at startup.

## Not here yet

- **`AddAsgard(..., AutoMTLSEnrollment)`** is intentionally omitted. It enrols
  the app's client certificate at startup, which depends on the realm CA and
  certificate change request being authorized first. Adding it is a one-line
  change once that ceremony is done.
- **The `:3000` origin must be signed.** A freshly bootstrapped realm only has
  `client-origin-auth-http://localhost:8080` in its adaptor. Without a signed
  `http://localhost:3000`, the Tide enclave refuses the login with *"Client
  origin could not be verified"*.
