using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.FileProviders;

namespace Tide.Asgard.AspNetCore.Authentication;

public static class ApplicationBuilderExtensions
{
	/// <summary>
	/// Mounts the Asgard client-registration dashboard at <c>/asgard</c>.
	/// Serves an embedded static <c>index.html</c> that polls
	/// <c>/AsgardClientCertification/ready-status</c> for live state.
	/// </summary>
	public static IApplicationBuilder UseTidecloakDashboard(
		this IApplicationBuilder app,
		TidecloakDashboardOptions options)
	{

		// fix this




		var fileProvider = new EmbeddedFileProvider(
			typeof(ApplicationBuilderExtensions).Assembly,
			"Tide.Asgard.AspNetCore.Authentication.ClientCeritifcation.Views");

		var opts = new StaticFileOptions
		{
			FileProvider = fileProvider,
			RequestPath = "/asgard"
		};

		app.UseDefaultFiles(new DefaultFilesOptions
		{
			FileProvider = fileProvider,
			RequestPath = "/asgard"
		});

		app.UseStaticFiles(opts);

		return app;
	}
}
