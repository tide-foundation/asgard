---
name: asgard-sdk
description: How to work on the Asgard repo - TideCloak's server-side SDK for ASP.NET Core - safely and correctly. Covers what lives where, how to build, the rules about live credentials and the sibling checkouts the SDK builds against, and how DPoP, resource delegation, token exchange, Tide locking and unlocking, policies, mTLS enrollment and the example apps fit together. Use it whenever you work anywhere under /home/sam/asgard - SDK code in aspnet/Tide.Asgard.AspNetCore, the example apps and their Docker setup, tests/aspnet, or README / USAGE / GETTING_STARTED - even for a small edit, a review or a question about how something works.
---

# Asgard SDK

Asgard lets an ASP.NET Core backend use TideCloak: it verifies TideCloak logins (DPoP-bound tokens), gets the user's
approval to act for them (resource delegation), exchanges tokens, locks and unlocks data with the Tide network without
ever holding its key, and gives the backend its own certificate identity for mTLS. The browser side always uses **tidecloak-js**.

Nothing is released - it is all greenfield. Names change often, so treat the names here as pointers: grep the code
for each one before you use it, and never write comments that talk about "old" or "legacy" behaviour.

## Repo map

| Path | What it is |
|---|---|
| `aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/` | The main SDK (`Tide.Asgard.AspNetCore.Authentication.csproj`): `AddAsgard`, delegation, token exchange, locking and unlocking, policies, mTLS, and DPoP validation and `UseTide_over_DPoP` in `DPoP/` |
| `aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/` | Crypto helpers, `PolicyBuilder`, `AsgardException` |
| `aspnet/Tide.Asgard.AspNetCore/Examples/` | Example apps - see [examples-and-docker.md](references/examples-and-docker.md) |
| `aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/` | End-to-end sample: lock and unlock from a plain Vite page - see [examples-and-docker.md](references/examples-and-docker.md) |
| `tests/aspnet/` | Playwright tests with their own test server; they need the local Tide stack (see its README) |
| `src/`, root `package.json` | A separate TypeScript package (`asgard-tide`) - not covered here |
| `README.md`, `GETTING_STARTED.md`, `USAGE.md`, `EXTENSION.md` | Docs. README diagrams are mermaid, rendered by GitHub |

## Rules - and why

1. **Never print or copy credentials.** `appsettings.json`, `tidecloak.json`, `.env`, `*.key`, `*.crt`, `*.csr` and
   `client.pfx` hold live keys, tokens and enrollment secrets. If you need to know what is in one, print key names only
   (e.g. a small Python walk over the JSON), never values.
2. **Don't edit code outside this repo.** The SDK builds against sibling checkouts: `../project/ork` (Ork.Clients,
   Ork.Models, Cryptide) and, for the example frontends, `~/tidecloak-js`, which links `~/heimdall`, which links
   `~/project/tide-js`. Read them only if the user points you there.
3. **Build, don't run, unless asked.** `dotnet build` in the project folder (.NET 10). Running an app talks to the
   user's TideCloak and can use up a one-time enrollment token.
4. **Commit or push only when asked.**
5. **Prove behaviour with a throwaway harness.** When you need to know how .NET or the SDK really behaves, write a tiny
   console project in the scratchpad (reference the SDK project, call internals through reflection if needed), run it,
   then delete it. Don't add test scaffolding to the repo.
6. **Match the code around you.** Same comment style and density, tabs in the SDK. Example apps should look like
   Microsoft's standard templates, because developers copy them.
7. **Keep effort in proportion.** A quick question gets a quick answer; a "quick check" by a sub-agent should be a few
   minutes, not a deep dive.

## How one request flows

1. The browser logs in with tidecloak-js and gets a **DPoP-bound** token.
2. It calls the backend with `IAMService.fetch` (which adds the DPoP proof).
3. An action marked `[RequireExchangeApproval]` answers **401 with a challenge**; tidecloak-js signs an approval and
   retries, all inside the same `fetch`.
4. Asgard **exchanges** the user's token with TideCloak over **mTLS**, using the backend's enrolled certificate. The new
   token is bound to that certificate.
5. The backend uses that token for TideCloak's APIs (policies, users), and the **doken** for the Tide network (locking and
   unlocking).

## Read next

- Auth, delegation, token exchange, policies, locking and unlocking, tidecloak-js → [auth-and-delegation.md](references/auth-and-delegation.md)
- mTLS enrollment, certificates, calling other resources, certificate-bound tokens → [mtls.md](references/mtls.md)
- Example apps, running locally, Docker and compose → [examples-and-docker.md](references/examples-and-docker.md)

## Common errors

| You see | It means |
|---|---|
| `503` + `Asgard-Exception: ResourceIdentityNotRegistered` | The backend's certificate is waiting for approval. Approve it in TideCloak; Asgard collects it within a minute, no restart |
| `401` + `Asgard-Exception: DelegationProofNotFound` | The action has no `[RequireExchangeApproval]`, or the browser didn't call through `IAMService.fetch` with `Authorization: Bearer <token>` |
| "Invalid enrollment token" at startup | The one-time token is used up. Download a fresh adapter config |
| `Name or service not known` for `{realm}.client.localhost` | Add `127.0.0.1 {realm}.client.localhost` to `/etc/hosts` (local TideCloak only) |
| 500: "Unable to resolve service for type TidecloakPolicyProvider" | Add `builder.Services.AddScoped<TidecloakPolicyProvider>();` |
| Build error "No file exists for the asset ... wwwroot/assets" | The server csproj needs `<DefaultItemExcludes>$(DefaultItemExcludes);wwwroot\**</DefaultItemExcludes>` |
| `Cannot load library libgssapi_krb5.so.2` in a container | Harmless: Npgsql tries Kerberos first, then connects normally |
