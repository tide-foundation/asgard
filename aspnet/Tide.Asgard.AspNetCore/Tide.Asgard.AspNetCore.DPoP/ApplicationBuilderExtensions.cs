using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using System.Text;

namespace Tide.Asgard.AspNetCore.Authentication;

public static class ApplicationBuilderExtensions
{
	// commenting this out since this is only used with IGA enabled (which we aren't doing for the mmmmmmmmvp)

	public static IApplicationBuilder UseTideSecuredDPoP(
		this IApplicationBuilder app,
		IConfiguration configuration,
		string publicAuthClientId)
	{
		var fileProvider = new EmbeddedFileProvider(
			typeof(ApplicationBuilderExtensions).Assembly,
			"Tide.Asgard.AspNetCore.DPoP.Views");

		var issuerUrl = configuration["Keycloak:auth-server-url"]?.TrimEnd('/') + "/realms/" + configuration["Keycloak:realm"];

		Console.WriteLine(Convert.ToHexString(Encoding.UTF8.GetBytes(issuerUrl)));
		Console.WriteLine(Convert.ToHexString(Encoding.UTF8.GetBytes(publicAuthClientId)));

		app.UseStaticFiles(new StaticFileOptions
		{
			FileProvider = fileProvider,
			RequestPath = $"/tide_dpop/iss/{Convert.ToHexString(Encoding.UTF8.GetBytes(issuerUrl))}/aud/{Convert.ToHexString(Encoding.UTF8.GetBytes(publicAuthClientId))}",
			OnPrepareResponse = ctx =>
			{
				if (ctx.File.Name.Equals("tide_dpop_auth.html", StringComparison.OrdinalIgnoreCase))
				{
					var headers = ctx.Context.Response.Headers;
					headers.Remove("X-Frame-Options");
					headers.ContentSecurityPolicy = "default-src 'self'; script-src 'unsafe-inline'";
					headers["Allow-CSP-From"] = "*";
				}
			}
		});

		return app;
	}
}