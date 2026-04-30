using Keycloak.AuthServices.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.AspNetCore.DPoP;
using Tide.Asgard.AspNetCore.DPoP.EventHandlers;

namespace Tide.Asgard.AspNetCore.Authentication;

public static class KeycloakWebApiAuthenticationBuilderExtensions
{
	/// <summary>
	///     Enables DPoP (Demonstration of Proof-of-Possession) support for the Asgard API authentication builder.
	/// </summary>
	/// <param name="builder">
	///     The <see cref="KeycloakWebApiAuthenticationBuilder" /> instance to configure.
	/// </param>
	/// <param name="configureDPoPOptions">
	///     A delegate to configure the <see cref="DPoPOptions" /> for DPoP integration.
	/// </param>
	/// <returns>
	///     The configured <see cref="KeycloakWebApiAuthenticationBuilder" /> instance.
	/// </returns>
	/// <exception cref="ArgumentNullException">
	///     Thrown when <paramref name="builder" /> or
	///     <paramref name="configureDPoPOptions" /> is null.
	/// </exception>
	public static KeycloakWebApiAuthenticationBuilder WithDPoP(
		this KeycloakWebApiAuthenticationBuilder builder,
		Action<DPoPOptions> configureDPoPOptions)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(configureDPoPOptions);

		var dPoPOptions = new DPoPOptions();
		configureDPoPOptions(dPoPOptions);

		builder.Services.TryAddEnumerable(
			ServiceDescriptor.Singleton<IPostConfigureOptions<JwtBearerOptions>>(
				new DPoPJwtBearerPostConfigureOptions(builder.JwtBearerAuthenticationScheme)));

		builder.Services.TryAddSingleton(dPoPOptions);
		builder.Services.TryAddScoped<IDPoPProofValidationService, DPoPProofValidationService>();
		builder.Services.TryAddScoped<MessageReceivedHandler>();
		builder.Services.TryAddScoped<TokenValidationHandler>();
		builder.Services.TryAddScoped<ChallengeHandler>();

		return builder;
	}
	/// <summary>
	///     Enables DPoP (Demonstration of Proof-of-Possession) support for the Asgard API authentication builder.
	/// </summary>
	/// <param name="builder">
	///     The <see cref="KeycloakWebApiAuthenticationBuilder" /> instance to configure.
	/// </param>
	/// <returns>
	///     The configured <see cref="KeycloakWebApiAuthenticationBuilder" /> instance.
	/// </returns>
	public static KeycloakWebApiAuthenticationBuilder WithDPoP(
		this KeycloakWebApiAuthenticationBuilder builder)
	{
		return builder.WithDPoP(_ => { });
	}
}
