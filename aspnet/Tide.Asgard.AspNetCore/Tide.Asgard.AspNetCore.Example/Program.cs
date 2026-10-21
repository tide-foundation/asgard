using Keycloak.AuthServices.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.mTLS;
using Tide.Asgard.AspNetCore.Authentication.SetupHelpers;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.AspNetCore.DPoP;

var builder = WebApplication.CreateBuilder(args);

builder
	.AddAsgard(opts =>
	{
		opts.RequireHttpsMetadata = false; // for local testing
	})
	.RequireTokenBinding(opts =>
	{
		opts.SupportBindings = Binding.DPoP | Binding.mTLS;
	});

builder.Services.AddControllers();


// we let the developer define the custom root ca path and all that like we did with dpop
// var rootCa = AsgardCertificateLoader.LoadRootCertificate(builder.Configuration); // needed for mtls


builder.Services.AddScoped<TidecloakPolicyProvider>(); // needed
builder.Services.AddProblemDetails(); // needed

var app = builder.Build();

app.UseExceptionHandler(); // needed

app.UseDefaultFiles();
app.UseStaticFiles();


app.UseTide_over_DPoP(builder.Configuration, "frontend"); // needed

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// SPA fallback - serve index.html for any non-API, non-file route
app.MapFallbackToFile("index.html");

app.Run();
