using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Tide.Asgard.AspNetCore.Authentication.DPoP;

namespace Tide.Asgard.AspNetCore.Authentication;

/// <summary>
///     Post-configures <see cref="JwtBearerOptions" />
/// </summary>
internal class AsgardJwtBearerPostConfigureOptions : IPostConfigureOptions<JwtBearerOptions>
{
	private readonly string _authenticationScheme;

	public AsgardJwtBearerPostConfigureOptions(string authenticationScheme)
	{
		_authenticationScheme = authenticationScheme;
	}
	public void PostConfigure(string? name, JwtBearerOptions options)
    {
		if (name != _authenticationScheme) return;

		options.Events = DPoPEventsFactory.Create(options);
	}
}
