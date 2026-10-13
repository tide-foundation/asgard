# Getting started with Asgard

This guide integrates Asgard into a new ASP.NET Core project: by the end, your backend verifies TideCloak logins and is ready for locking, policy management and token exchange.

The steps follow the [Tide.Asgard.AspNetCore.Example](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/) project. New to TideCloak? Read the [5 concepts to know](README.md#5-concepts-to-know) first.

## Prerequisites

- .NET 10 SDK
- The Asgard SDK referenced from your project. It is currently consumed via `<ProjectReference>` — see [Tide.Asgard.AspNetCore.Example.csproj](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/Tide.Asgard.AspNetCore.Example.csproj) for the wiring.
- A running TideCloak instance with a configured realm and licence
- Two clients in that realm:
  - a **public client** for the browser-side login page (e.g. `browser-login-page`)
  - a **confidential client** for your .NET backend (e.g. `backend`), with its client authenticator set to `tide-mtls` and standard token exchange enabled

Give the public client an **audience mapper** targeting the backend client — it stamps the backend's name into every login token, which is what makes your backend accept tokens from your login page.

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

> **Why "Keycloak"?** TideCloak is built on Keycloak, and Asgard builds on the `Keycloak.AuthServices` library — which reads its configuration from this section. That's the only reason the name appears here and in a few APIs below.

> Make sure you're also using `@tidecloak/js` on the browser side, download its adapter config separately and follow its instructions for installing it.

## 2. Register authentication and Asgard

TideCloak signs tokens with EdDSA (Ed25519), a modern signature scheme .NET can't validate natively. Asgard's `GetEd25519IssuerKey()` reads the signing key from your adapter config so standard token validation can use it.

`Program.cs`:

```csharp
using Keycloak.AuthServices.Authentication;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Validate TideCloak-issued EdDSA tokens
builder.Services
    .AddKeycloakWebApiAuthentication(builder.Configuration, options =>
    {
        options.RequireHttpsMetadata = false; // local dev only
        options.TokenValidationParameters.IssuerSigningKey = builder.Configuration.GetEd25519IssuerKey();
    });

// Ineffable locking, policies and token exchange
builder.Services.AddAsgard(builder.Configuration, ResourceAuthenticationMode.AutoMTLSEnrollment);
builder.Services.AddProblemDetails(); // required by app.UseExceptionHandler()

var app = builder.Build();

app.UseExceptionHandler(); // required — Asgard registers an exception handler that relies on it

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
```

`AddAsgard` registers everything the [usage guide](USAGE.md) builds on: `IAspAsgardService` for locking, a policy provider backed by TideCloak, the token exchange service, and an exception handler that translates Asgard errors into `Asgard-*` response headers (which is why `app.UseExceptionHandler()` is required).

`ResourceAuthenticationMode.AutoMTLSEnrollment` tells Asgard to request its own certificate from TideCloak on first start, using the enrollment token from step 1. An admin has to approve that request in TideCloak; Asgard keeps checking (every minute by default) and picks the certificate up without a restart — the flow is drawn in [Asgard mTLS certificate auto-registration](README.md#asgard-mtls-certificate-auto-registration). Use `ResourceAuthenticationMode.MTLS` instead when the certificate is already in place and no enrollment should happen.

## 3. Enable DPoP

DPoP is an additional layer of security that ensures access tokens can't be stolen: it cryptographically ties the caller's token to their browser, rendering a stolen token useless anywhere else.

It is optional if you only verify logins, but locking, policy management and token exchange all require it.

To enable it, add `.WithDPoP` to the authentication registration from step 2:

```csharp
using Tide.Asgard.AspNetCore.DPoP;

builder.Services
    .AddKeycloakWebApiAuthentication(builder.Configuration, options =>
    {
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters.IssuerSigningKey = builder.Configuration.GetEd25519IssuerKey();
    })
    .WithDPoP(opts =>
    {
        opts.Mode = DPoPModes.Required;
    });
```

Every API protected by this authentication scheme will now require a valid DPoP proof.

Then serve Tide's DPoP confirmation page, which the Tide login uses to check that the browser really holds its DPoP key. Pass the id of your **public** client, and place the call before `app.UseAuthentication()`:

```csharp
app.UseTideSecuredDPoP(builder.Configuration, "browser-login-page");
```

## Next: use it

Setup is done. The [usage guide](USAGE.md) covers what comes next: protecting endpoints, locking data with a policy, managing policies and exchanging tokens.
