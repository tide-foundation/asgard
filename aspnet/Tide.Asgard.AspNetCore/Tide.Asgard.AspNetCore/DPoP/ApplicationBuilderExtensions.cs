using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using System.Text;

namespace Tide.Asgard.AspNetCore.Authentication;

public static class ApplicationBuilderExtensions
{
	// commenting this out since this is only used with IGA enabled (which we aren't doing for the mmmmmmmmvp)

	public static IApplicationBuilder UseTide_over_DPoP(
		this IApplicationBuilder app,
		IConfiguration configuration,
		string publicAuthClientId)
	{
		var fileProvider = new EmbeddedFileProvider(
			typeof(ApplicationBuilderExtensions).Assembly,
			"Tide.Asgard.AspNetCore.DPoP.Views");

		var issuerUrl = configuration["Keycloak:auth-server-url"]?.TrimEnd('/') + "/realms/" + configuration["Keycloak:realm"];

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
					headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'sha256-utc6UrebuHOyLd/2aiMXS/p1EDy9UZBDe/XEMKDw9Mc='; style-src 'self' 'sha256-1tYy8m3c1KLuGI2eID9TfLkc50Y+iSPJMpI7n/apN/w=' 'sha256-F7OJTdJYct4J+cQfuJUoDauitndqt8pAc8EbA8gwDPU='";
					headers["Allow-CSP-From"] = "*";
				}
			}
		});

		return app;
	}
}