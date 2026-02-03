using Auth0.AspNetCore.Authentication.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.DPoP;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var keycloak = builder.Configuration.GetSection("Keycloak");

builder.Services
    .AddAsgardAuthentication(options =>
    {
        // Keycloak realm URL acts as both authority and issuer
        options.Authority = keycloak["Authority"];
        options.Audience = keycloak["Audience"];
        options.RequireHttpsMetadata = false; // dev only - set true in production
    })
    .WithDPoP(op =>
    {
        op.Mode = DPoPModes.Required;
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
