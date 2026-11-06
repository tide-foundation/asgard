# Auth, delegation, token exchange, policies, locking and unlocking

## Backend setup (Program.cs)

```csharp
builder.Services.AddControllers();
builder.AddAsgard(options => options.RequireHttpsMetadata = false) // local dev only
	.RequireTokenBinding(options => options.SupportBindings = Binding.DPoP);
builder.Services.AddScoped<TidecloakPolicyProvider>(); // only if you manage policies
builder.Services.AddProblemDetails();                  // UseExceptionHandler needs it

var app = builder.Build();
app.UseExceptionHandler();     // Asgard reports its errors through it
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseTide_over_DPoP(builder.Configuration, "<public client id>");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
```

- Usings: `Tide.Asgard.AspNetCore.Authentication` and `Tide.Asgard.AspNetCore.Authentication.SetupHelpers`.
- The adapter config (downloaded from TideCloak) goes in the `Keycloak` section, or in the `TIDECLOAK_JSON` env var.
- `AddAsgard` sets up Keycloak JWT validation with the realm's Ed25519 key from the adapter config. Its optional action
  configures `JwtBearerOptions`.
- That validation is Keycloak.AuthServices' `AddKeycloakWebApiAuthentication`, so `User` is a Keycloak.AuthServices
  principal (`Identity.Name` = `preferred_username`, role claim type `role`). `Keycloak.AuthServices.Authorization` comes
  with the SDK: `AddKeycloakAuthorization(builder.Configuration)` gives `RequireRealmRoles`, `RequireResourceRoles` (on
  the adapter's `resource`), `RequireResourceRolesForClient(client, [roles])`, and role mapping through
  `EnableRolesMapping` (`Realm`, `ResourceAccess`, `All`) in the `Keycloak` section.
- `RequireTokenBinding` is what registers Asgard's services and starts enrollment, so `AddAsgard()` alone gives you no
  `IAspAsgardService`. Enrollment is automatic.
- Bindings: `Binding.DPoP` (browsers), `Binding.mTLS` (other Asgard backends), or both. A token bound neither way is
  refused - there is no way to accept plain bearer tokens.
- `UseTide_over_DPoP`'s second argument must equal `resource` in the browser's `tidecloak.json`, or Tide-secured
  approvals fail. A backend with only `Binding.mTLS` has no login page and skips it.

## Resource delegation - `[RequireExchangeApproval]`

`[RequireExchangeApproval(level, exchangeTargetResource)]` asks the user to approve the backend acting for them.

- It is an **MVC filter**: put it on controllers or actions. It does nothing on minimal-API endpoints.
- Levels:
  - `ApprovalRequirement.Basic` - signed with the browser's DPoP key. Enough for TideCloak APIs (policies, users).
  - `ApprovalRequirement.TideEnabled` - also signed with the user's Tide session key. Needed for locking and unlocking (gives a doken).
- `exchangeTargetResource` - the client the exchanged token is for (default: this backend).
- The flow: a `401` with `Resource-Delegation-Key` and `Resource-Delegation-Challenge` headers. The challenge is signed
  over the token's **raw `jti`** - don't hash it, tidecloak-js signs the raw value. tidecloak-js only answers when the
  request carries `Authorization: Bearer <its own token>`.

## Token exchange

- `asgard.ExchangeContextTokenForApplicationToken(exchangeTargetClient?)` exchanges the user's token (or returns it from
  the cache).
  The cache is keyed per token **and** target (`Utils.GetTokenCacheId`), so tokens for different clients don't collide.
- The exchanged token is **mTLS-bound** to this backend's certificate (`cnf.x5t#S256`), and comes with a doken only for
  `TideEnabled` approvals.
- `asgard.GetHttpClient(clientId)` exchanges for that client and attaches the token itself (see [mtls.md](mtls.md)),
  so the action needs `[RequireExchangeApproval(level, clientId)]`.
- Delegation chains: a backend called through `GetHttpClient` delegates on to the next backend the same way, so the
  user's delegation reaches as many backends as the flow needs. The subject token is DPoP-bound from a browser, and
  mTLS-bound from another backend.

## Policies

```csharp
policies.SetAuthentication(await asgard.ExchangeContextTokenForApplicationToken());
if (await policies.GetPolicy(id) is null)                      // missing, or waiting for approval
	changeRequestId = await policies.AddPolicyWithChangeRequest(id, policyBytes); // null = live immediately; id = admins must approve
```

Build the bytes with `new PolicyBuilder(vendorId, "SimpleTagBasedDecryption:1")` plus `BypassExplicitUserConsent()`,
`UseForEncyption()` (sic) to lock and `UseForDecryption()` to unlock, then `BuildPolicy()`. Lock and unlock requests
(`PolicyEnabledEncryption:1`, `PolicyEnabledDecryption:1`) support implicit approval only, so their policies need
`BypassExplicitUserConsent()`.

## Locking and unlocking

```csharp
var locked = await asgard.CreateLockContext()
	.UsePolicy(policyId)
	.Lock(new LockOptions()
		.AddItemToLock(new ItemToLock { ItemId = "notes", Tags = ["medical"], Data = bytes }));
var cipher = locked.GetLockedItemById("notes").Cipher; // ReadOnlyMemory<byte>

var unlocked = await asgard.CreateLockContext()
	.UsePolicy(policyId)
	.Unlock(new UnlockOptions()
		.AddItemToUnlock(new ItemToUnlock { ItemId = "notes", Tags = ["medical"], Data = cipher }));
var text = Encoding.UTF8.GetString(unlocked.GetUnlockedItemById("notes").PlainText.Span);
```

- `CreateLockContext()` returns one `ILockContext` with both `Lock(LockOptions)` and `Unlock(UnlockOptions)`. One
  context = one policy.
- Tags are bound to the cipher: unlock with the tags it was locked with. The policy's contract decides who may lock or
  unlock which tags, and the policy must allow the operation (`UseForEncyption()` / `UseForDecryption()`).
- Both actions need `[RequireExchangeApproval(ApprovalRequirement.TideEnabled)]`.
- Unlocking (`LockClientManager.Unlock` in `../project/ork/Ork.Clients`): each ORK applies its key share to the
  cipher's key, Asgard combines the replies into a per-item key and decrypts in the backend. The plain text exists only
  in the backend.
- Results: `LockedItems` / `UnlockedItems` in the order the items were added, or by id with `GetLockedItemById` /
  `GetUnlockedItemById`.
- `Cipher` and `PlainText` are `ReadOnlyMemory<byte>`. Decode with `Encoding.UTF8.GetString(x.Span)` - string
  concatenation prints the type name (`System.ReadOnlyMemory<Byte>[10]`), not the data.

## Errors

`AsgardException` carries an `AsgardErrorCode` and becomes a response with an `Asgard-Exception` header.
`AsgardMessageHandler` forwards `Asgard-*` headers on outgoing calls and turns a downstream `Asgard-Exception` back into
an exception, so errors survive service hops.

## What apps can see

Only what an example or the test server uses is public; everything else is internal, and Core shares its internals with
the SDK through `InternalsVisibleTo`. Make something public only when an example needs it.

## tidecloak-js (browser)

```js
import { IAMService } from "@tidecloak/js";
import config from "../tidecloak.json"; // the public client's adapter config

IAMService.on("tokenExpired", () => IAMService.updateToken().catch(() => IAMService.doLogin()));
await IAMService.init(config, {
	onLoad: "login-required",
	checkLoginIframe: false,
	setupRequestEnclave: false,
	dpopConfig: { mode: "strict", alg: "EdDSA" },
});
const response = await IAMService.fetch("/api/thing", { headers: { Authorization: `Bearer ${IAMService.getToken()}` } });
```

The functions are `fetch` (not `secureFetch`) and `dpopConfig` (not `useDPoP`).
