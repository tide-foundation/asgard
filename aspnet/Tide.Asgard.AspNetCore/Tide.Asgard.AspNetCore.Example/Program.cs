using Keycloak.AuthServices.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography.X509Certificates;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.DPoP;
using Tide.Asgard.Core.Crypto.Ed25519;
using Tide.Asgard.Core.PolicyHelpers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services
	.AddKeycloakWebApiAuthentication(builder.Configuration, options =>
	{
		options.RequireHttpsMetadata = false;
		options.TokenValidationParameters.IssuerSigningKey = Utils.GetEd25519IssuerKey(builder.Configuration);
	})
	.WithDPoP(opts =>
	{
		opts.Mode = DPoPModes.Required;
	});


builder.Services.AddAsgard(builder.Configuration);
builder.Services.AddScoped<TidecloakPolicyProvider>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

app.UseDefaultFiles();
app.UseStaticFiles();


// they must do this for the single public client users use to log into the application
app.UseTideSecuredDPoP(builder.Configuration, "asgard-front");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// SPA fallback - serve index.html for any non-API, non-file route
app.MapFallbackToFile("index.html");

app.Run();
