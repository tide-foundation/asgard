using Keycloak.AuthServices.Authentication;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.AspNetCore.DPoP;

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

 
builder.Services.AddAsgard(builder.Configuration, ResourceAuthenticationMode.AutoMTLSEnrollment); // needed
builder.Services.AddScoped<TidecloakPolicyProvider>(); // needed
builder.Services.AddProblemDetails(); // needed

var app = builder.Build();

app.UseExceptionHandler(); // needed

app.UseDefaultFiles();
app.UseStaticFiles();


app.UseTideSecuredDPoP(builder.Configuration, "frontend"); // needed

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// SPA fallback - serve index.html for any non-API, non-file route
app.MapFallbackToFile("index.html");

app.Run();
