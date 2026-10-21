# Getting started with Asgard

This guide integrates Asgard into a new ASP.NET Core project: by the end, your backend verifies TideCloak logins and is ready for locking, policy management and token exchange.

The steps follow the [StandardResourceDelegation](aspnet/Tide.Asgard.AspNetCore/Examples/StandardResourceDelegation/) example, the smallest app that does all of this. New to TideCloak? Read the [5 concepts to know](README.md#5-concepts-to-know) first.

## Prerequisites

- .NET 10 SDK
- The Asgard SDK referenced from your project. It is consumed via `<ProjectReference>` — see [StandardResourceDelegation.Server.csproj](aspnet/Tide.Asgard.AspNetCore/Examples/StandardResourceDelegation/StandardResourceDelegation.Server/StandardResourceDelegation.Server.csproj) for the wiring.
- A running TideCloak instance with a configured realm and licence
- Two clients in that realm:
  - a **public client** for the browser-side login page (e.g. `browser-login-page`)
  - a **confidential client** for your .NET backend (e.g. `backend`), with its client authenticator set to `tide-mtls` and standard token exchange enabled

Give the public client an **audience mapper** targeting the backend client — it stamps the backend's name into every login token, which is what makes your backend accept tokens from your login page.

> [!NOTE]
> **Running TideCloak locally?** Asgard reaches TideCloak for mTLS at `https://{realm}.client.localhost:8443`, a name your computer does not resolve by itself. Add it to `/etc/hosts`, using your realm's name:
>
> ```
> 127.0.0.1   {realm}.client.localhost
> ```

## 1. Add the adapter config to your app

Every TideCloak client exposes an **adapter config** — a small JSON blob that tells an SDK how to reach your realm and authenticate as that client. Asgard reads the backend client's adapter config from `appsettings.json`.

**Download it** from the Admin UI — in your realm -> Clients -> `backend` -> top-right **Action** dropdown -> **Download adapter config** — or fetch it via the API.

**Paste it into `appsettings.json`** under a section named `Keycloak`:

```json
{
  "Keycloak": {
    "realm": "test",
    "auth-server-url": "http://localhost:8080",
    "ssl-required": "external",
    "resource": "backend",
    "vendorId": "...",
    "enrollment_token": "<one-time enrollment token>",
    "jwk": {
      "keys": [
        {
          "kid": "...",
          "kty": "OKP",
          "alg": "EdDSA",
          "use": "sig",
          "crv": "Ed25519",
          "x": "..."
        }
      ]
    }
  }
}
```

The downloaded file carries more Tide fields than shown here — paste it whole. There is no client secret: the `enrollment_token` lets your backend request its own certificate the first time it starts (step 2).

Rather not keep it in `appsettings.json`? Put the downloaded JSON, as it is, in the `TIDECLOAK_JSON` environment variable instead. Asgard reads it from there when the `Keycloak` section has no realm.

> **Why "Keycloak"?** TideCloak is built on Keycloak, and Asgard builds on the `Keycloak.AuthServices` library — which reads its configuration from this section. That's the only reason the name appears here.

## 2. Register Asgard

`Program.cs`:

```csharp
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.SetupHelpers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.AddAsgard(options => options.RequireHttpsMetadata = false) // local dev only
    .RequireTokenBinding(options => options.SupportBindings = Binding.DPoP); // only DPoP-bound tokens are accepted
builder.Services.AddProblemDetails(); // required by UseExceptionHandler

var app = builder.Build();

app.UseExceptionHandler(); // required by Asgard, which reports its errors through it
app.UseTide_over_DPoP(builder.Configuration, "browser-login-page"); // the id of the public client your frontend logs in with
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
```

`AddAsgard` sets up token validation for your realm. TideCloak signs tokens with EdDSA (Ed25519), which .NET can't validate on its own, so Asgard takes the signing key from your adapter config — there is nothing to configure. The optional action changes anything else about the JWT bearer validation; here it lets a plain-http TideCloak through during development.

`RequireTokenBinding` sets which tokens your API accepts, and registers everything the [usage guide](USAGE.md) builds on: `IAspAsgardService` for locking, a policy provider backed by TideCloak, the token exchange service, and an exception handler that translates Asgard errors into `Asgard-*` response headers (which is why `app.UseExceptionHandler()` is required). Always call it.

### Token binding

A bound token is useless to anyone who steals it: it only works for the holder of the key it is bound to. Asgard only accepts bound tokens — a token that isn't bound is refused.

| Binding | Accepts | Use it for |
| --- | --- | --- |
| `Binding.DPoP` (default) | tokens bound to the caller's browser by DPoP | an API your browser app calls |
| `Binding.mTLS` | tokens bound to another backend's certificate, sent over mutual TLS | a backend that other Asgard backends call |

Combine them with `Binding.DPoP | Binding.mTLS`. With `Binding.mTLS`, every https endpoint of your app requires a client certificate the realm issued, so browsers can't reach it over https — keep it for backends that only other backends call, like the analytics service in the [ResourceToResourceDelegation](aspnet/Tide.Asgard.AspNetCore/Examples/ResourceToResourceDelegation/) example.

### Your backend's certificate

On first start, Asgard requests a certificate for your backend from TideCloak, using the enrollment token from step 1. An admin has to approve that request in TideCloak; Asgard keeps checking (every minute by default) and picks the certificate up without a restart — the flow is drawn in [Asgard mTLS certificate auto-registration](README.md#asgard-mtls-certificate-auto-registration). Until then, anything that needs TideCloak answers `503` with the header `Asgard-Exception: ResourceIdentityNotRegistered`.

The enrollment token works once. If the request was sent but its files were lost, download a fresh adapter config.

Asgard keeps the keys and certificate in the app's working folder (`resource.key`, `tide.key`, `resource.crt`, `root.crt`). To keep them somewhere else — say, a volume that outlives a container — set `identity_key_path`, `tide_key_path`, `certificate_path`, `root_ca_path` and `certificate_request_path` in an `Asgard` configuration section.

### The DPoP confirmation page

`UseTide_over_DPoP` serves Tide's DPoP confirmation page, which the Tide login uses to check that the browser really holds its DPoP key. Pass the id of your **public** client — the `resource` in your frontend's adapter config (step 3) — and place the call before `app.UseAuthentication()`. A backend that only other backends call has no login page, and doesn't need it.

## 3. Connect your frontend

Your browser app logs in and calls your API with [tidecloak-js](https://github.com/tide-foundation/tidecloak-js). Download the **public** client's adapter config the same way as in step 1, save it in your frontend as `tidecloak.json`, then:

```js
import { IAMService } from "@tidecloak/js";
import config from "./tidecloak.json"; // the public client's adapter config

// keep the token fresh while the page stays open
IAMService.on("tokenExpired", () => IAMService.updateToken().catch(() => IAMService.doLogin()));

// log in with a DPoP-bound token
await IAMService.init(config, {
    onLoad: "login-required",
    checkLoginIframe: false,
    setupRequestEnclave: false,
    dpopConfig: { mode: "strict", alg: "EdDSA" },
});

const response = await IAMService.fetch("/users", {
    headers: { Authorization: `Bearer ${IAMService.getToken()}` },
});
```

`IAMService.fetch` sends the token with a DPoP proof, and answers your backend's approval requests (see [Let your backend act for the caller](USAGE.md#let-your-backend-act-for-the-caller)) by signing and retrying — all inside the one call. Pass the token as a `Bearer` header as shown: tidecloak-js only answers approval requests for its own token.

## Next: use it

Setup is done. The [usage guide](USAGE.md) covers what comes next: protecting endpoints, locking data with a policy, managing policies and calling other services for the caller.
