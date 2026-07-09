# Asgard

Asgard is a .NET **Cyber Immunity SDK** — authentication, authorization and authority for ASP.NET Core apps — built for **TideCloak**, Tide's identity and access management platform. With Asgard, your API can verify TideCloak logins, perform **ineffable locking** (encryption) of data under programmable policies, and securely exchange tokens between services.

The fastest way to see it working end-to-end is the [Tide.Asgard.AspNetCore.Example](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/) project — the snippets below mirror its setup.

## Concepts in 30 seconds

New to TideCloak? These are the only terms you need for this guide:

- **Realm** — your app's own space in TideCloak: its users, clients and policies.
- **Client** — a registration in the realm for each app that talks to TideCloak. A **public** client (e.g. your login page) runs in the browser and can't keep a secret; a **confidential** client (your backend) authenticates with one.
- **Caller** — the logged-in user behind the current request. Their token arrives in the `Authorization` header of every call to your API.
- **Programmable policy** — a rule stored in TideCloak and enforced by the Tide network that defines who may lock or unlock what.
- **Ineffable locking** — encryption performed by the Tide network where the key never exists anywhere in full — no key material should be stored by your app.

For a deeper dive into policies and contracts, see the [Forseti engine docs](https://docs.tide.org/Core-Concepts/forseti-engine).

## Prerequisites

- .NET 10 SDK
- A running TideCloak instance with a configured realm and licence
- Two clients in that realm:
  - a **public client** for the browser-side login page (e.g. `browser-login-page`)
  - a **confidential client** for your .NET backend (e.g. `backend`)

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

> **Why "Keycloak"?** TideCloak is built on Keycloak, and Asgard builds on the `Keycloak.AuthServices` library — which reads its configuration from this section. That's the only reason the name appears here and in a few APIs below.

> If you're also using `@tidecloak/js` on the browser side, download its adapter config separately and follow its instructions for installing it.

## 2. Register authentication and Asgard

TideCloak signs tokens with EdDSA (Ed25519), a modern signature scheme .NET can't validate natively. Asgard's `GetEd25519IssuerKey()` reads the signing key from your adapter config so standard token validation can use it.

`Program.cs`:

```csharp
using Keycloak.AuthServices.Authentication;
using Tide.Asgard.AspNetCore.Authentication;

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
builder.Services.AddAsgard(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler(); // required — Asgard registers an exception handler that relies on it

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
```

`AddAsgard` registers everything the rest of this guide uses: `IAspAsgardService` for locking, a policy provider backed by TideCloak, the token exchange service, and an exception handler that translates Asgard errors into `Asgard-*` response headers (which is why `app.UseExceptionHandler()` is required).

## 3. (Optional) Require DPoP

DPoP is an additional layer of security that ensures access tokens can't be stolen: it cryptographically ties the caller's token to their browser, rendering a stolen token useless anywhere else.

To enable it, add `.WithDPoP` to the authentication registration from step 2:

```csharp
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

## 4. Lock data with a policy

A **lock context** represents one atomic lock operation. It tells the Tide network: *"lock all of these items using this specific policy."* You can lock many items in one context, but each context uses exactly one policy.

**Why does locking need a policy — shouldn't anyone be able to lock?** No. Imagine the database holding the Coca-Cola recipe: it would be a disaster if *any* employee could lock it. A policy states the logic that allows a caller to lock something — and because that logic is enforced during locking, the locked data is also **attested**: you know the locked recipe is legit, because only the CEO could have locked it.

Each item is described by an `ItemToLock`:

- `ItemId` — your identifier for the item
- `Data` — the bytes to lock
- `Tags` — labels **cryptographically tied to the resulting cipher**. The policy's contract uses them to decide whether this caller may lock data with these tags — e.g. data tagged `"secret recipe"` may only be locked by a caller who also holds the CEO role.

Inject `IAspAsgardService` into your controller, build the context, pick a policy, and lock. Here an HR API locks an employee's sensitive fields before saving the record — the ciphers go in the database, the plain data never does:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ork.Models;
using System.Text;
using Tide.Asgard.AspNetCore.Authentication;

public record CreateEmployeeRequest(string Name, string DateOfBirth, string MedicalNotes);

[Authorize]
[ApiController]
[Route("api/employees")]
public class EmployeesController(IAspAsgardService asgardService, AppDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateEmployeeRequest request)
    {
        // lock the sensitive fields — the name stays in plain text
        var lockOptions = new LockOptions()
            .AddItemToLock(new ItemToLock
            {
                ItemId = "date-of-birth",
                Tags = ["staff data", "date of birth"],
                Data = Encoding.UTF8.GetBytes(request.DateOfBirth),
            })
            .AddItemToLock(new ItemToLock
            {
                ItemId = "medical-notes",
                Tags = ["staff data", "medical"],
                Data = Encoding.UTF8.GetBytes(request.MedicalNotes),
            });

        LockResponse response = await asgardService.CreateLockContext(lockOptions)
            .UsePolicy("hr-staff-data-policy")
            .Lock();

        db.Employees.Add(new Employee
        {
            Name = request.Name,
            DateOfBirthCipher = response.GetLockedItemById("date-of-birth").Cipher.ToArray(),
            MedicalNotesCipher = response.GetLockedItemById("medical-notes").Cipher.ToArray(),
        });
        await db.SaveChangesAsync();

        return Created();
    }
}
```

- `UsePolicy(policyId)` selects the policy for this context — here the `hr-staff-data-policy` created in [Manage policies](#5-manage-policies) below. Your application decides which policy fits which flow — basic users lock with the basic-user policy, admins with their own.
- `Lock()` performs the operation and returns a `LockResponse`. Ciphers come back in the same order the items were added (`response.LockedItems`), or look one up with `GetLockedItemById`. Each `Cipher` is raw bytes (`ReadOnlyMemory<byte>`), ready to store wherever you keep your data.

Under the hood, `Lock()` fetches the policy from TideCloak (authenticated as the caller, then cached), exchanges the caller's token for an application token, and asks the Tide network to lock the data under that policy.

### Calling other Asgard-enabled services

When your API calls another Asgard-enabled service, use the HTTP client from `asgardService.GetHttpClient()`. It forwards `Asgard-*` headers on requests and surfaces Asgard errors from downstream responses as `AsgardException`s, so error flows work across service boundaries.

```csharp
var client = asgardService.GetHttpClient();
var response = await client.GetAsync("https://inventory.internal.example/api/items");
```

## 5. Manage policies

A **policy provider** is the single source of policies for your application. `TidecloakPolicyProvider` stores them in TideCloak — inject it into the controller that manages your policies.

Build a policy with `PolicyBuilder`, passing your vendor id and contract id. Here we create the `hr-staff-data-policy` used by the lock example above:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.Core.PolicyHelpers;

[Authorize] // lock this endpoint down to your policy admins
[ApiController]
[Route("api/policies")]
public class PoliciesController(IConfiguration config, TidecloakPolicyProvider policyProvider) : ControllerBase
{
    [HttpPost("staff-data")]
    public async Task<IActionResult> CreateStaffDataPolicy()
    {
        var policyBuilder = new PolicyBuilder(config["vendorId"]!, contractId: "staff-data-contract");

        policyBuilder.AllowPublicUse();
        policyBuilder.BypassExplicitUserConsent();
        policyBuilder.UseForEncyption();

        var changeRequestId = await policyProvider.AddPolicyWithChangeRequest(
            "hr-staff-data-policy", policyBuilder.BuildPolicy());

        return changeRequestId is null
            ? Ok("Policy is live.") // QEA disabled — applied immediately
            : Accepted(value: $"Awaiting quorum approval. Change request: {changeRequestId}");
    }
}
```

- `AllowPublicUse()` — **anyone** can execute this policy; its contract will not check who initiated the request.
- `BypassExplicitUserConsent()` — executing the policy does not require explicit approval from other Tide users.
- `UseForEncyption()` — allow this policy to serve encryption (locking) requests.

`AddPolicyWithChangeRequest` uploads the new policy to TideCloak. If **QEA** (Quorum-Enforced Authorization) is enabled, it returns a change request id — the policy takes effect once the quorum approves it. If QEA is disabled, it returns `null` and the policy is live immediately.

## 6. (Optional) Token exchange

Sometimes your API needs to call another protected service on the caller's behalf. You *could* widen the caller's token so its audience covers every service — but then one stolen token opens all of them. **Token exchange** keeps tokens narrow: your backend swaps the incoming token for a new one targeting only the service it's about to call.

Register the service:

```csharp
builder.Services.AddTokenExchange(builder.Configuration);
```

`AddTokenExchange` reads the `Keycloak` section. To register token exchange for multiple clients in the same app, call `AddTokenExchangeForClient(IConfigurationSection)` with a different section per client.

Inject `ITokenExchangeService` and call `ExchangeToken` — it picks up the caller's token from the current HTTP context. Here the API fetches the caller's payslips from a separate payroll service:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

[Authorize]
[ApiController]
[Route("api/payslips")]
public class PayslipsController(
    ITokenExchangeService exchangeService,
    IHttpClientFactory httpClientFactory) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        // swap the caller's token for one that only the payroll service accepts
        var token = await exchangeService.ExchangeToken(
            requestingClientId: "backend",
            requestedAudience: "payroll-service");

        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var payslips = await client.GetStringAsync("https://payroll.internal.example/api/payslips/me");

        return Content(payslips, "application/json");
    }
}
```

## Repository layout

The .NET solution lives at [aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.sln](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.sln) and contains:

| Project | Purpose |
|---|---|
| [Tide.Asgard.AspNetCore.Authentication](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/) | Main SDK — service-collection extensions, Ed25519 helpers, locking, token exchange |
| [Tide.Asgard.Core](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/) | Cryptography primitives (Ed25519 / EdDSA), policy helpers |
| [Tide.Asgard.AspNetCore.DPoP](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.DPoP/) | DPoP (proof-of-possession) support |
| [Tide.Asgard.AspNetCore.Example](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/) | End-to-end working sample |

The SDK is currently consumed via `<ProjectReference>` — see [Tide.Asgard.AspNetCore.Example.csproj](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/Tide.Asgard.AspNetCore.Example.csproj) for the wiring.


## License

This project is dual-licensed:

- **ASP.NET Core authentication libraries** (`aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/`): Derived from the [Auth0 ASP.NET Core Authentication API](https://github.com/auth0/aspnetcore-api), which is licensed under the [Apache License 2.0](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/LICENSE-APACHE-2.0). Modifications by Tide Foundation Limited are subject to both the Apache 2.0 license (for the derived portions) and the Tide Community Open Code License (for new additions). See the [NOTICE](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/NOTICE) file for full attribution details.
- **Core cryptography libraries** (`aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/`): Derived from [ScottBrady.IdentityModel](https://github.com/scottbrady91/IdentityModel), which is licensed under the [Apache License 2.0](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/LICENSE-APACHE-2.0). Modifications by Tide Foundation Limited are subject to both the Apache 2.0 license (for the derived portions) and the Tide Community Open Code License (for new additions). See the [NOTICE](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/NOTICE) file for full attribution details.
