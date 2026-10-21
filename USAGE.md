# Using Asgard

This guide covers what you do with Asgard once it is set up: authenticating users, locking data under a policy, managing policies and exchanging tokens.

It assumes you have finished the setup steps in the [getting started guide](GETTING_STARTED.md). The snippets follow the [examples](aspnet/Tide.Asgard.AspNetCore/Examples/).

## Before you start

Three things must be true for the examples below to work:

- **Your API accepts DPoP-bound tokens** (`Binding.DPoP`, [getting started, step 2](GETTING_STARTED.md#2-register-asgard)). Browser callers get to locking, policy management and token exchange with DPoP.
- **Your backend's certificate has been approved in TideCloak.** Asgard requests it on first start and collects it by itself once an admin approves — see [how that works](README.md#asgard-mtls-certificate-auto-registration). Until then, anything that needs TideCloak answers `503` with the header `Asgard-Exception: ResourceIdentityNotRegistered`.
- **Your browser app calls your API through tidecloak-js** (`IAMService.fetch`, [getting started, step 3](GETTING_STARTED.md#3-connect-your-frontend)). It attaches the DPoP proof and answers Asgard's approval requests for you — see the [LockingUserDataWithTide](aspnet/Tide.Asgard.AspNetCore/Examples/LockingUserDataWithTide/lockinguserdatawithtide.client/src/App.jsx) example's page.

## 1. Authenticate users

### Protect an endpoint

Add `[Authorize]` as you would in any ASP.NET Core app. Asgard validates the TideCloak token and its DPoP proof before your action runs, and the caller is available as `User`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[Authorize]
[ApiController]
[Route("api/profile")]
public class ProfileController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        username = User.FindFirst("preferred_username")?.Value,
    });
}
```

A request without a valid, bound token — from a browser, a DPoP-bound token with its DPoP proof — never reaches your action.

### Let your backend act for the caller

Locking data, managing policies and exchanging tokens all need your backend to act **on the caller's behalf**. The caller has to approve that first, and `[RequireExchangeApproval]` is how you ask:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

[Authorize]
[RequireExchangeApproval(ApprovalRequirement.TideEnabled)]
[ApiController]
[Route("api/employees")]
public class EmployeesController : ControllerBase
{
    // ...
}
```

Put it on a controller or on a single action. There are two levels:

| Level | The caller's approval is signed by | Use it for |
| --- | --- | --- |
| `ApprovalRequirement.Basic` (default) | the browser's DPoP key | managing policies, token exchange |
| `ApprovalRequirement.TideEnabled` | the browser's DPoP key **and** the caller's Tide session key | locking data |

You don't handle the approval yourself. Asgard answers the first request with a `401` challenge, tidecloak-js signs the approval and retries, and Asgard reuses that approval until the exchanged token expires or the caller's token is refreshed. The full flow is drawn in [Asgard Resource Delegation](README.md#asgard-resource-delegation).

The attribute's second argument names the client the exchanged token is for, and defaults to your backend itself — see [Call other services for the caller](#4-call-other-services-for-the-caller).

Without the attribute, the calls in the sections below fail with `401` and the header `Asgard-Exception: DelegationProofNotFound`.

## 2. Lock data with a policy

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
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

public record CreateEmployeeRequest(string Name, string DateOfBirth, string MedicalNotes);

[Authorize]
[RequireExchangeApproval(ApprovalRequirement.TideEnabled)] // locking needs the Tide-secured level
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

- `UsePolicy(policyId)` selects the policy for this context — here the `hr-staff-data-policy` created in [Manage policies](#3-manage-policies) below. Your application decides which policy fits which flow — basic users lock with the basic-user policy, admins with their own.
- `Lock()` performs the operation and returns a `LockResponse`. Ciphers come back in the same order the items were added (`response.LockedItems`), or look one up with `GetLockedItemById`. Each `Cipher` is raw bytes (`ReadOnlyMemory<byte>`), ready to store wherever you keep your data.

Under the hood, `Lock()` uses the caller's approval to exchange their token for an application token and doken, fetches the policy from TideCloak (cached for five minutes), and asks the Tide network to lock the data under that policy. The full flow is drawn in [Encryption](README.md#encryption).

## 3. Manage policies

A **policy provider** is the single source of policies for your application. `TidecloakPolicyProvider` stores them in TideCloak. Register it once in `Program.cs`, then inject it into the controller that manages your policies:

```csharp
builder.Services.AddScoped<TidecloakPolicyProvider>();
```

The provider talks to TideCloak as your backend acting for the caller, so hand it the application token before you use it. `ExchangeContextTokenForApplicationToken()` exchanges the caller's token the first time and serves it from the cache afterwards.

Build a policy with `PolicyBuilder`, passing your vendor id and contract id. Here we create the `hr-staff-data-policy` used by the lock example above, with the `SimpleTagBasedDecryption:1` contract the examples use:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core.PolicyHelpers;

[Authorize] // lock this endpoint down to your policy admins
[RequireExchangeApproval(ApprovalRequirement.Basic)]
[ApiController]
[Route("api/policies")]
public class PoliciesController(IAspAsgardService asgardService, TidecloakPolicyProvider policyProvider) : ControllerBase
{
    [HttpPost("staff-data")]
    public async Task<IActionResult> CreateStaffDataPolicy()
    {
        // authenticate the provider as your backend, acting for the caller
        policyProvider.SetAuthentication(await asgardService.ExchangeContextTokenForApplicationToken());

        var policyBuilder = new PolicyBuilder(asgardService.GetSettings().VendorId, contractId: "SimpleTagBasedDecryption:1");

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

- `BypassExplicitUserConsent()` — executing the policy does not require explicit approval from other Tide users.
- `UseForEncyption()` — allow this policy to serve encryption (locking) requests.

`AddPolicyWithChangeRequest` uploads the new policy to TideCloak. If **QEA** (Quorum-Enforced Authorization) is enabled, it returns a change request id — the policy takes effect once the quorum approves it. If QEA is disabled, it returns `null` and the policy is live immediately.

`GetPolicy(id)` returns `null` when no policy has that id — or while its change request waits for approval — which makes it a handy check before creating one. The [LockingUserDataWithTide](aspnet/Tide.Asgard.AspNetCore/Examples/LockingUserDataWithTide/LockingUserDataWithTide.Server/Program.cs) example builds its policy at startup, then adds it the first time a caller locks something: a policy can only be added on behalf of a logged-in caller.

## 4. Call other services for the caller

Sometimes your API needs to call another protected service on the caller's behalf. You *could* widen the caller's token so its audience covers every service — but then one stolen token opens all of them. **Token exchange** keeps tokens narrow: your backend swaps the caller's token for a new one, for the one service it is about to call. The new token is bound to your backend's certificate, so it only works on your backend's own mutual TLS connections.

Ask for the caller's approval with `[RequireExchangeApproval]` as usual. Its second argument names the client the exchanged token is for; leave it out when that client is your own backend, as it is for TideCloak's APIs.

### TideCloak's APIs

The HTTP client named `"Tidecloak"` reaches your realm over mutual TLS, with your backend's certificate. Hand it the exchanged token and call TideCloak as the caller. Here, from the [StandardResourceDelegation](aspnet/Tide.Asgard.AspNetCore/Examples/StandardResourceDelegation/StandardResourceDelegation.Server/Program.cs) example, the backend fetches the realm's users:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

[Authorize]
[RequireExchangeApproval] // the caller must have delegated to this backend
[ApiController]
[Route("users")]
public class UsersController(IAspAsgardService asgard, IHttpClientFactory httpClientFactory) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var tidecloak = httpClientFactory.CreateClient("Tidecloak");
        tidecloak.DefaultRequestHeaders.Authorization = new("Bearer", await asgard.ExchangeContextTokenForApplicationToken());

        var users = await tidecloak.GetStringAsync($"/admin/realms/{asgard.GetSettings().RealmName}/users");

        return Content(users, "application/json");
    }
}
```

TideCloak checks the caller's own permissions too: the user list needs the `view-users` and `query-users` roles of the `realm-management` client.

### Other Asgard backends

`asgardService.GetHttpClient(clientId)` gives you a client for another Asgard backend, named by its client id. For every request it:

- connects over mutual TLS with your backend's certificate, and only accepts the server certificate the realm issued to that client id;
- exchanges the caller's token for one for that client, and sends it as a `Bearer` token;
- answers the called backend's approval request itself, signing with your backend's keys, and retries;
- forwards `Asgard-*` headers, and turns an Asgard error from the called backend into the same error response from yours.

Here, from the [ResourceToResourceDelegation](aspnet/Tide.Asgard.AspNetCore/Examples/ResourceToResourceDelegation/) example, a web app asks its analytics service for a summary of a patient's details:

```csharp
[RequireExchangeApproval(ApprovalRequirement.TideEnabled, "analytics")] // the exchanged token is for the analytics client
[HttpGet("PersonalSummary")]
public async Task<IActionResult> PersonalSummary([FromQuery] Guid patientId)
{
    var analytics = asgardService.GetHttpClient("analytics");
    var response = await analytics.GetAsync($"https://analytics.internal.example/analytics/personal?id={patientId}");

    return Ok(await response.Content.ReadAsStringAsync());
}
```

The analytics service accepts certificate-bound tokens, and asks for an approval of its own:

```csharp
builder.AddAsgard(options => options.RequireHttpsMetadata = false) // local dev only
    .RequireTokenBinding(options => options.SupportBindings = Binding.mTLS);
```

```csharp
[HttpGet("personal")]
[RequireExchangeApproval(ApprovalRequirement.TideEnabled)]
public async Task<IActionResult> PersonalSummary([FromQuery] Guid id)
```

Good to know:

- The client only sends requests to `https://` URLs, and only once your backend's certificate has been approved.
- Pass the fixed set of client ids your code calls, never ids from a request — each one keeps its own connections for the life of the application.

### The exchanged token itself

`ITokenExchangeService` does the exchanges above, and Asgard registers it. Inject it when you need the token itself: `ExchangeToken(requestingClientId, requestedAudience)` returns the new access token (`ApplicationAccessToken`) and when it expires (`ExpiresAt`). Called with no arguments, it exchanges for your own backend client. Remember the token is bound to your backend's certificate.
