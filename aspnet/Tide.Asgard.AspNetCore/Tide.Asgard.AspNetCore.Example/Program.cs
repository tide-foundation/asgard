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

builder.Services
    .AddAsgardAuthentication(options =>
    {
        // Keycloak realm URL acts as both authority and issuer
        options.Authority = keycloak["Authority"];
        options.Audience = keycloak["Audience"];
        options.RequireHttpsMetadata = false; // dev only - set true in production
        options.TokenValidationParameters.IssuerSigningKey = signingKey;
    })
    .WithDPoP(op =>
    {
        op.Mode = DPoPModes.Required;
    })
    // Use the below to set up tidecloak token exchange
    .WithTokenExchange(tcMtls =>
    {
        tcMtls.X509Certificate2 = new X509Certificate2();
        tcMtls.BaseUri = new Uri("");
    })
    // Use the below to set up communication with ANOTHER server (who knows who) which you have MTLs set up with
    .WithMutualTLS(mtls =>
    {

    });

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// SPA fallback - serve index.html for any non-API, non-file route
app.MapFallbackToFile("index.html");

app.Run();
