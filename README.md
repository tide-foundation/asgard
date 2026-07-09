# Asgard

Asgard is a .NET authentication SDK that extends `Keycloak.AuthServices` with Tide-specific capabilities and an OAuth 2.0 Token Exchange service. It is designed to work with **Tidecloak** — Tide's distribution of Keycloak — so your ASP.NET Core APIs can validate tokens issued by a Tidecloak realm, perform key-less locking (encryption) of data under Tide policies, and exchange tokens between clients.

The fastest way to see it working end-to-end is the [Tide.Asgard.AspNetCore.Example](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/) project — the snippets below mirror its setup.

## Prerequisites

- .NET 10 SDK
- A running Tidecloak instance with a configured realm and licence

To avoid re-documenting Keycloak-specific setup, this guide assumes your realm already has:

- A **public client** for the browser-side login page (e.g. `browser-login-page`)
- A **confidential client** for your .NET backend (e.g. `backend`)

**The public client must include an audience mapper for the backend client**, so any token issued to the public client can be read by the backend client.

## 1. Add the adapter config to your app

Each Tidecloak client exposes an **adapter config** — a JSON blob describing how an SDK should talk to it. Asgard reads the backend client's adapter config from `appsettings.json`.

**Download it:** in your realm -> Clients -> `backend` -> top-right **Action** dropdown -> **Download adapter config**, then copy the JSON.

**Paste it into `appsettings.json`** under a `Keycloak` section. The nesting is required because `Keycloak.AuthServices` reads its configuration from the `Keycloak` section by default.

```json
{
  "Keycloak": {
    "realm": "test",
    "auth-server-url": "http://localhost:8080",
    "ssl-required": "external",
    "resource": "backend",
    "credentials": {
      "secret": "<client secret>"
    },
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

> If you're also using tidecloak-js on the browser side, download its adapter config separately and follow the tidecloak-js instructions for installing it.

## 2. Register authentication and Asgard

Tidecloak signs tokens with EdDSA (Ed25519), which .NET does not support natively. Asgard fills the gap: `GetEd25519IssuerKey` reads the Ed25519 JWK from the `Keycloak` config section and returns a key you can plug into the standard `Keycloak.AuthServices` setup.

`Program.cs`:

```csharp
using Keycloak.AuthServices.Authentication;
using Tide.Asgard.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Validate Tidecloak-issued EdDSA tokens
builder.Services
    .AddKeycloakWebApiAuthentication(builder.Configuration, options =>
    {
        options.RequireHttpsMetadata = false; // local dev only
        options.TokenValidationParameters.IssuerSigningKey = builder.Configuration.GetEd25519IssuerKey();
    });

// Tide locking, policies and token exchange
builder.Services.AddAsgard(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler(); // required — Asgard registers an exception handler that relies on it

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
```

`AddAsgard` registers everything the rest of this guide uses: the `IAspAsgardService` for locking, a policy provider/cache backed by Tidecloak, the token exchange service, and an exception handler that translates Asgard errors into `Asgard-*` response headers (which is why `app.UseExceptionHandler()` is required).

Authentication and authorization themselves (attributes, policies, role mapping) are handled by `Keycloak.AuthServices` — see its docs for anything beyond the setup above.

## 3. Lock data with a policy

Asgard lets you perform **key-less locking** (encryption) of data using only the caller's token and an application policy — no key material is stored by your app.

Inject `IAspAsgardService`, describe the items to lock, and choose a policy:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ork.Models;
using System.Text;
using Tide.Asgard.AspNetCore.Authentication;

[Authorize]
[ApiController]
[Route("[controller]")]
public class AccountController(IAspAsgardService asgardService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> EncryptAccount()
    {
        var lockOptions = new LockOptions()
            .AddItemToLock(new ItemToLock
            {
                ItemId = "id1",
                Tags = ["staff data", "date of birth"],
                Data = Encoding.UTF8.GetBytes("hello!"),
            });

        LockResponse response = await asgardService.CreateLockContext(lockOptions)
            .UsePolicy("vendor:user1:assessment1:policy1")
            .Lock();

        // grab the resulting ciphers
        var cipher = response.GetLockedItemById("id1").Cipher;
        // or: response.LockedItems.First().Cipher;

        return Ok(cipher);
    }
}
```

`Lock()` fetches the policy from Tidecloak (authenticated as the calling user, then cached), exchanges the caller's token for an application token, and asks the Tide network to lock the data under that policy.

Key-less **unlocking** and **signing** contexts are coming soon.

### Calling other Asgard-enabled services

When your API calls another Asgard-enabled service, use the HTTP client from `asgardService.GetHttpClient()`. It forwards `Asgard-*` headers on requests and surfaces Asgard errors from downstream responses as `AsgardException`s, so error flows work across service boundaries.

```csharp
var client = asgardService.GetHttpClient();
await client.GetAsync("https://other-asgard-service/...");
```

## 4. Create policies

Policies define what a lock/unlock operation is allowed to do and who has to consent to it. Build one with `PolicyBuilder` and register it in Tidecloak:

```csharp
using Tide.Asgard.Core.PolicyHelpers;

var policyBuilder = new PolicyBuilder(vendorId, contractId);

policyBuilder.AllowPublicUse();
policyBuilder.BypassExplicitUserConsent();
policyBuilder.UseForEncyption();

var changeRequestId = await policyProvider.AddPolicyWithChangeRequest("example-policy", policyBuilder.BuildPolicy());
```

Adding a policy creates a **change request** in Tidecloak that must be approved (via IGA) before the policy can be used.

## 5. (Optional) Token exchange

OAuth 2.0 Token Exchange lets your service swap an incoming user token for a new token targeting a different audience — useful when your API needs to call another protected service on behalf of the caller.

Register the service:

```csharp
builder.Services.AddTokenExchange(builder.Configuration);
```

`AddTokenExchange` reads the `Keycloak` section. To register token exchange for multiple clients in the same app, call `AddTokenExchangeForClient(IConfigurationSection)` with a different section per client.

Inject `ITokenExchangeService` and call `ExchangeToken` — it picks up the caller's token from the current HTTP context:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

[Authorize]
[ApiController]
[Route("[controller]")]
public class HelloController(ITokenExchangeService exchangeService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var token = await exchangeService.ExchangeToken(
            requestingClientId: "backend",
            requestedAudience: "account");

        return Ok(token);
    }
}
```

## Repository layout

The .NET solution lives at [aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.sln](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.sln) and contains:

| Project | Purpose |
|---|---|
| [Tide.Asgard.AspNetCore.Authentication](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/) | Main SDK — service-collection extensions, Ed25519 helpers, locking, token exchange |
| [Tide.Asgard.Core](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/) | Cryptography primitives (Ed25519 / EdDSA), policy helpers |
| [Tide.Asgard.AspNetCore.DPoP](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.DPoP/) | DPoP (proof-of-possession) support — work in progress |
| [Tide.Asgard.AspNetCore.Example](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/) | End-to-end working sample |

The SDK is currently consumed via `<ProjectReference>` — see [Tide.Asgard.AspNetCore.Example.csproj](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/Tide.Asgard.AspNetCore.Example.csproj) for the wiring.


## License

This project is dual-licensed:

- **ASP.NET Core authentication libraries** (`aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/`): Derived from the [Auth0 ASP.NET Core Authentication API](https://github.com/auth0/aspnetcore-api), which is licensed under the [Apache License 2.0](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/LICENSE-APACHE-2.0). Modifications by Tide Foundation Limited are subject to both the Apache 2.0 license (for the derived portions) and the Tide Community Open Code License (for new additions). See the [NOTICE](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/NOTICE) file for full attribution details.
- **Core cryptography libraries** (`aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/`): Derived from [ScottBrady.IdentityModel](https://github.com/scottbrady91/IdentityModel), which is licensed under the [Apache License 2.0](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/LICENSE-APACHE-2.0). Modifications by Tide Foundation Limited are subject to both the Apache 2.0 license (for the derived portions) and the Tide Community Open Code License (for new additions). See the [NOTICE](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/NOTICE) file for full attribution details.
