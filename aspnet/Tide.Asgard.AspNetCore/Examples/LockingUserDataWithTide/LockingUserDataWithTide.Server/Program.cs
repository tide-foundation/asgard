using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ork.Models;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.SetupHelpers;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core.PolicyHelpers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.AddAsgard(options => options.RequireHttpsMetadata = false) // local dev only
	.RequireTokenBinding(options => options.SupportBindings = Binding.DPoP); // only DPoP-bound tokens are accepted
builder.Services.AddScoped<TidecloakPolicyProvider>(); // keeps policies in TideCloak
builder.Services.AddProblemDetails(); // required by UseExceptionHandler

// Create the policy that data is locked under. It can only be added to TideCloak on behalf of a logged-in user,
// so that waits until the first time someone locks something.
var policy = new PolicyBuilder(builder.Configuration["Keycloak:vendorId"]!, "SimpleTagBasedDecryption:1");
policy.BypassExplicitUserConsent(); // using the policy needs no explicit approval from other Tide users
policy.UseForEncyption(); // the policy may serve locking requests
builder.Services.AddSingleton(new LockingPolicy("locking-user-data-example", policy.BuildPolicy()));

var app = builder.Build();

app.UseExceptionHandler(); // required by Asgard, which reports its errors through it
app.UseDefaultFiles(); // the frontend, which the client project builds into wwwroot/
app.UseStaticFiles();
app.UseTide_over_DPoP(builder.Configuration, "browser-login-page"); // the id of the public client the frontend logs in with
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public record LockRequest(string Text);

public record LockingPolicy(string Id, ReadOnlyMemory<byte> Data)
{
	public string? ChangeRequestId { get; set; } // set while TideCloak is waiting for admins to approve the policy
}

[Authorize] // the caller's TideCloak token and DPoP proof must be valid
[ApiController]
public class LockingController(IAspAsgardService asgard, TidecloakPolicyProvider policies, LockingPolicy policy) : ControllerBase
{
	[RequireExchangeApproval(ApprovalRequirement.TideEnabled)] // the caller must have delegated to this backend, signed by their Tide session key too - answers 401 until their browser has
	[HttpPost("lock")]
	public async Task<IActionResult> Lock(LockRequest request)
	{
		// add the policy to TideCloak if it hasn't been added yet - the provider reaches TideCloak as this backend, acting for the caller
		policies.SetAuthentication(await asgard.ExchangeContextTokenForApplicationToken());
		if (await policies.GetPolicy(policy.Id) is null)
		{
			// TideCloak answers with a change request when the realm wants a quorum of admins to approve the policy first
			policy.ChangeRequestId ??= await policies.AddPolicyWithChangeRequest(policy.Id, policy.Data);
			if (policy.ChangeRequestId is not null)
				return Accepted(value: $"Policy {policy.Id} is awaiting approval - change request {policy.ChangeRequestId}. Lock again once an admin has approved it.");
		}

		var options = new LockOptions().AddItemToLock(new ItemToLock
		{
			ItemId = "text",
			Tags = ["user data"], // tied to the cipher, for the policy's contract to judge
			Data = Encoding.UTF8.GetBytes(request.Text),
		});

		// the Tide Network locks the data under the policy - no key ever reaches this backend
		var response = await asgard.CreateLockContext(options).UsePolicy(policy.Id).Lock();

		// the cipher is what an app would store - here it goes straight back, and nothing is kept
		return Ok(new { policy = policy.Id, cipher = Convert.ToBase64String(response.GetLockedItemById("text").Cipher.Span) });
	}
}
