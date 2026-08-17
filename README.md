# Asgard

Asgard is a .NET authentication SDK that extends `Keycloak.AuthServices` with Tide-specific cryptography (Ed25519 signing keys) and an opinionated OAuth 2.0 Token Exchange client. It is designed to work with **Tidecloak** — Tide's distribution of Keycloak — so your ASP.NET Core APIs can validate tokens issued by a Tidecloak realm and exchange them between clients.

The fastest way to see it working end-to-end is the [Tide.Asgard.AspNetCore.Example](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/) project — the snippets below mirror its setup.

## Prerequisites

- .NET 10 SDK
- A running Tidecloak instance with a configured realm and licence

## Repository layout

The .NET solution lives at [aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.sln](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.sln) and contains:

| Project | Purpose |
|---|---|
| [Tide.Asgard.AspNetCore.Authentication](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/) | Main SDK — service-collection extensions, Ed25519 helpers, token exchange |
| [Tide.Asgard.Core](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/) | Cryptography primitives (Ed25519 / EdDSA) |
| [Tide.Asgard.AspNetCore.Example](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/) | End-to-end working sample |
| [Tide.Asgard.Scheduler](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Scheduler/) | Task scheduling: schedule expressions, job store contract, worker and retries. No dependencies |
| [Tide.Asgard.Scheduler.Postgres](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Scheduler.Postgres/) | Durable Postgres job store for the scheduler |
| [Tide.Asgard.Scheduler.Tests](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Scheduler.Tests/) | Scheduler test suite, no test framework required |

The SDK is currently consumed via `<ProjectReference>` — see [Tide.Asgard.AspNetCore.Example.csproj](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/Tide.Asgard.AspNetCore.Example.csproj) for the wiring.

## How to add Asgard .NET Authentication to your web app

### 1. Set up your Tidecloak clients

A typical setup uses two clients in your realm:

- A **public client** for the browser-side login page (e.g. `browser-login-page`)
- A **confidential client** for your .NET backend (e.g. `backend`)

You can add more backend clients later if you want to separate, say, admin endpoints from user endpoints.

> **Note:** if you're using Tide for user authentication, create your licence in your realm before creating any clients.

#### Create the browser client

In your realm -> Clients -> Create client:
- Client ID: `browser-login-page`
- Set the redirect URIs and web origins for your login page
- Save

#### Create the backend client

In your realm -> Clients -> Create client:
- Client ID: `backend`
- Enable **Client authentication**
- Enable **Standard Token Exchange** (required for step 4)
- Set the web origins
- Save

Then open the **Credentials** tab and ensure **Client Authenticator** is set to *Client ID and Secret*.

#### Add an audience mapper to the browser client

For the backend to accept tokens issued by `browser-login-page`, those tokens need `backend` in their `aud` claim.

In your realm -> Clients -> `browser-login-page` -> Client scopes -> `browser-login-page-dedicated` -> Add mapper -> By configuration -> Audience:
- Name: `backend-mapper`
- Included Client Audience: `backend`
- Save

### 2. Add the adapter config to your app

Each Tidecloak client exposes an **adapter config** — a JSON blob describing how an SDK should talk to it. The asgard SDK reads the backend client's adapter config from `appsettings.json`.

> If you're also using tidecloak-js on the browser side, download its adapter config separately and follow the tidecloak-js instructions for installing it.

**Download it:** in your realm -> Clients -> `backend` -> top-right **Action** dropdown -> **Download adapter config**, then copy the JSON.

**Paste it into `appsettings.json`** under a `Keycloak` key. The nesting is required because `Keycloak.AuthServices` reads its configuration from the `Keycloak` section by default.

Example:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Keycloak.AuthServices": "Debug"
    }
  },
  "AllowedHosts": "*",

  "Keycloak": {
    "realm": "test",
    "auth-server-url": "http://localhost:8080",
    "ssl-required": "external",
    "resource": "backend",
    "credentials": {
        "secret": "dXrEdnwK5nXYa9QdRkQY8mxpBoj5G8TP"
    },
    "confidential-port": 0,
    "jwk": {
        "keys": [
        {
            "kid": "lQfpu9UmEbiORUienjTlbxiV5teMVbT1neXjEGgd8V4",
            "kty": "OKP",
            "alg": "EdDSA",
            "use": "sig",
            "crv": "Ed25519",
            "x": "HcYJ2a_4pi-9g5_aVbE4_gZoPIXTlg6IQw-jiuFuifk"
        }
        ]
    },
    "backgroundUrl": "http://localhost:8080/realms/test/tide-idp-resources/images/BACKGROUND_IMAGE",
    "logoUrl": "http://localhost:8080/realms/test/tide-idp-resources/images/LOGO",
    "homeOrkUrl": "http://localhost:1001"
  }
}
```
### 3. Adding the Asgard SDK to your .NET Web Application
Asgard currently works alongside `Keycloak.AuthServices` to provide:
- Ed25519 Signing Key Support

Register authentication in `Program.cs`:
```csharp
using Keycloak.AuthServices.Authentication;
using Tide.Asgard.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services
	.AddKeycloakWebApiAuthentication(builder.Configuration, options =>
	{
		options.RequireHttpsMetadata = false;
		options.TokenValidationParameters.IssuerSigningKey = Utils.GetEd25519IssuerKey(builder.Configuration);
	});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.Run();
```

Protect endpoints with `[Authorize]`:
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[Authorize]
[ApiController]
[Route("[controller]")]
public class HelloController : ControllerBase
{
	[HttpGet]
	public IActionResult Get() => Ok($"Hello, {User.Identity?.Name}");
}
```

For more information on authentication - see `Keycloak.AuthServices`. That is the package that directly manages the authentication / authorization.

### 4. (Optional) Configuring Token Exchange Service
OAuth 2.0 Token Exchange lets your service swap an incoming user token for a new token targeting a different audience — useful when your API needs to call another protected service on behalf of the caller.

Register the service:
```csharp
using Tide.Asgard.AspNetCore.Authentication;

builder.Services.AddTokenExchange(builder.Configuration);
```

`AddTokenExchange` also has an overload taking an `IConfigurationSection`, so you can register multiple token-exchange clients in the same app by passing different sections.

Inject `ITokenExchangeService` and call `ExchangeToken`:
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
			HttpContext.Request.Headers,
			requestingClientId: "backend",
			requestedAudience: "account");

		return Ok(token);
	}
}
```

## Task scheduler

Asgard ships a schedule expression language with matching TypeScript and .NET
implementations, so both runtimes agree on when a job should next run. Neither
takes a third party dependency.

A job definition ties a name, a payload type and a handler together, so
enqueueing a job cannot disagree with the handler that receives it:

```csharp
var reconcile = Job.Define<ReconcilePayload>(
    "reconcile-orks", async (payload, ctx) => await Reconcile(payload.RealmId));

await using var worker = await Worker.CreateAsync(new WorkerOptions
{
    Store = PostgresJobStore.Create(connectionString),
    Jobs = [reconcile],
    Schedules =
    [
        ScheduleDefinition.For(
            "nightly", "on 03:00 tz=Australia/Sydney", reconcile, new ReconcilePayload("tide"))
    ]
});

worker.Start();
```

You supply a connection string, the SDK supplies the schema, the claim and lease
queries, retries with backoff, and the reaper.

See [docs/task-scheduler.md](docs/task-scheduler.md) for the language reference,
recipes, and what is still to be built. Runnable examples live in
[examples/](examples/).

## License

This project is dual-licensed:

- **ASP.NET Core authentication libraries** (`aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/`): Derived from the [Auth0 ASP.NET Core Authentication API](https://github.com/auth0/aspnetcore-api), which is licensed under the [Apache License 2.0](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/LICENSE-APACHE-2.0). Modifications by Tide Foundation Limited are subject to both the Apache 2.0 license (for the derived portions) and the Tide Community Open Code License (for new additions). See the [NOTICE](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/NOTICE) file for full attribution details.
- **Core cryptography libraries** (`aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/`): Derived from [ScottBrady.IdentityModel](https://github.com/scottbrady91/IdentityModel), which is licensed under the [Apache License 2.0](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/LICENSE-APACHE-2.0). Modifications by Tide Foundation Limited are subject to both the Apache 2.0 license (for the derived portions) and the Tide Community Open Code License (for new additions). See the [NOTICE](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/NOTICE) file for full attribution details.
