using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography.X509Certificates;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.DPoP;
using Tide.Asgard.Core.Crypto.Ed25519;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var keycloak = builder.Configuration.GetSection("Keycloak");

var signingKeySection = keycloak.GetSection("SigningKey");
var jwk = new JsonWebKey
{
    Kty = signingKeySection["kty"],
    Crv = signingKeySection["crv"],
    X = signingKeySection["x"]
};
var signingKey = jwk.ToSecurityKey();

// Dev only: allow the authz-server callback scheme to fetch metadata over HTTP
// (Keycloak is on http://localhost:8080 in this example).
// Must be registered BEFORE AddJwtBearer runs inside the SDK chain so this
// PostConfigure fires before the framework's HTTPS validator.
builder.Services.PostConfigure<JwtBearerOptions>(
	AsgardAuthenticationSchemes.ClientCertificationAuthority,
	o => o.RequireHttpsMetadata = false);

builder.Services
    .AddAsgardAuthentication(options =>
    {
        // Keycloak realm URL acts as both authority and issuer
        options.Authority = keycloak["Authority"];
        options.Audience = keycloak["Audience"];
        options.RequireHttpsMetadata = false; // dev only - set true in production
       // options.TokenValidationParameters.IssuerSigningKey = signingKey;
    })
    .SetupConfidentialClient("asgard_client", mtls =>
    {
        mtls.X509Certificate2 = new X509Certificate2("client.pfx");   //  <- testing auto reg
        mtls.BaseUri = new Uri("https://localhost:8443/realms/aaa/");
	})
	// Use the below to set up tidecloak token exchange
	.WithTokenExchange("audience")
    .WithAutoClientCertification("/home/sam/creds")
	;

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// SPA fallback - serve index.html for any non-API, non-file route
app.MapFallbackToFile("index.html");

app.Run();
