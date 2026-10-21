using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.SetupHelpers;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.AddAsgard(options => options.RequireHttpsMetadata = false) // local dev only
	.RequireTokenBinding(options => options.SupportBindings = Binding.DPoP); // only DPoP-bound tokens are accepted
builder.Services.AddProblemDetails(); // required by UseExceptionHandler

var app = builder.Build();

app.UseExceptionHandler(); // required by Asgard, which reports its errors through it
app.UseDefaultFiles(); // the frontend, which the client project builds into wwwroot/
app.UseStaticFiles();
app.UseTide_over_DPoP(builder.Configuration, "browser-login-page"); // the id of the public client the frontend logs in with
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

[Authorize] // the caller's TideCloak token and DPoP proof must be valid
[RequireExchangeApproval] // and the caller must have delegated to this backend - answers 401 until their browser has
[ApiController]
[Route("users")]
public class UsersController(IAspAsgardService asgard, IHttpClientFactory httpClientFactory) : ControllerBase
{
	[HttpGet]
	public async Task<IActionResult> Get()
	{
		// the HTTP client AddAsgard registers - it reaches TideCloak with this backend's mTLS certificate
		var tidecloak = httpClientFactory.CreateClient("Tidecloak");

		// uses the caller's delegation to exchange their token for one this backend can act for them with
		tidecloak.DefaultRequestHeaders.Authorization = new("Bearer", await asgard.ExchangeContextTokenForApplicationToken());

		using var response = await tidecloak.GetAsync($"/admin/realms/{asgard.GetSettings().RealmName}/users");

		// hand TideCloak's answer back as it is - the user list, or its refusal
		return new ContentResult
		{
			StatusCode = (int)response.StatusCode,
			Content = await response.Content.ReadAsStringAsync(),
			ContentType = response.Content.Headers.ContentType?.ToString(),
		};
	}
}
