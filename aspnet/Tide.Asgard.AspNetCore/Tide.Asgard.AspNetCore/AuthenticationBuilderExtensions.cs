using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Tide.Asgard.AspNetCore.Authentication.DPoP;
using Tide.Asgard.AspNetCore.Authentication.DPoP.EventHandlers;

namespace Tide.Asgard.AspNetCore.Authentication;

/// <summary>
///     Provides extension methods for
///     <see href="https://docs.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.authentication.authenticationbuilder">
///         AuthenticationBuilder
///     </see>
///     to simplify the registration and configuration of Asgard authentication.
/// </summary>
public static class AuthenticationBuilderExtensions
{
	/// <summary>
	///     Adds Asgard authentication for API
	///     specified <see cref="AuthenticationBuilder" />.
	/// </summary>
	/// <param name="builder">
	///     The <see cref="AuthenticationBuilder" /> instance to configure.
	/// </param>
	/// <param name="authenticationScheme">
	///     The authentication scheme to use for Asgard authentication.
	/// </param>
	/// <param name="configureOptions">
	///     A delegate used to configure the <see cref="JwtBearerOptions" /> for JwtBearerOptions.
	/// </param>
	/// <returns>
	///     The configured <see cref="AuthenticationBuilder" /> instance.
	/// </returns>
	public static AsgardAuthenticationBuilder AddAsgardAuthentication(
		this AuthenticationBuilder builder, string authenticationScheme, Action<JwtBearerOptions>? configureOptions)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);
		ArgumentNullException.ThrowIfNull(configureOptions);

		builder.AddJwtBearer(
			authenticationScheme, configureOptions);

		builder.Services.Configure(authenticationScheme, configureOptions);
		builder.Services.TryAddEnumerable(
			ServiceDescriptor.Singleton<IPostConfigureOptions<JwtBearerOptions>>(
				new AsgardJwtBearerPostConfigureOptions(authenticationScheme)));

		return new AsgardAuthenticationBuilder(builder.Services, authenticationScheme);
	}
	/// <summary>
	///     Enables DPoP (Demonstration of Proof-of-Possession) support
	///     with default configuration using a specified authentication scheme.
	/// </summary>
	/// <param name="builder">
	///     The <see cref="AsgardAuthenticationBuilder" /> instance to configure.
	/// </param>
	/// <param name="authenticationScheme">
	///     The authentication scheme to use for DPoP integration.
	/// </param>
	/// <returns>
	///     The configured <see cref="AsgardAuthenticationBuilder" /> instance.
	/// </returns>
	public static AsgardAuthenticationBuilder WithDPoP(
        this AsgardAuthenticationBuilder builder,
        string authenticationScheme)
    {
        return builder.WithDPoP(authenticationScheme, _ => { });
    }

	/// <summary>
	///     Enables DPoP (Demonstration of Proof-of-Possession) support
	///     using the default Auth0 authentication scheme.
	/// </summary>
	/// <param name="builder">
	///     The <see cref="AsgardAuthenticationBuilder" /> instance to configure.
	/// </param>
	/// <param name="configureDPoPOptions">
	///     A delegate to configure the <see cref="DPoPOptions" /> for DPoP integration.
	/// </param>
	/// <returns>
	///     The configured <see cref="AsgardAuthenticationBuilder" /> instance.
	/// </returns>
	public static AsgardAuthenticationBuilder WithDPoP(
        this AsgardAuthenticationBuilder builder,
        Action<DPoPOptions> configureDPoPOptions)
    {
        return builder.WithDPoP(JwtBearerDefaults.AuthenticationScheme, configureDPoPOptions);
    }

	/// <summary>
	///     Enables DPoP (Demonstration of Proof-of-Possession) support for the Auth0 API authentication builder
	///     using a specified authentication scheme.
	/// </summary>
	/// <param name="builder">
	///     The <see cref="AsgardAuthenticationBuilder" /> instance to configure.
	/// </param>
	/// <param name="authenticationScheme">
	///     The authentication scheme to use for DPoP integration.
	/// </param>
	/// <param name="configureDPoPOptions">
	///     A delegate to configure the <see cref="DPoPOptions" /> for DPoP integration.
	/// </param>
	/// <returns>
	///     The configured <see cref="AsgardAuthenticationBuilder" /> instance.
	/// </returns>
	/// <exception cref="ArgumentNullException">
	///     Thrown when <paramref name="builder" /> or
	///     <paramref name="configureDPoPOptions" /> is null.
	/// </exception>
	/// <exception cref="ArgumentException">
	///     Thrown when <paramref name="authenticationScheme" /> is empty or null.
	/// </exception>
	public static AsgardAuthenticationBuilder WithDPoP(
        this AsgardAuthenticationBuilder builder,
        string authenticationScheme,
        Action<DPoPOptions> configureDPoPOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);
        ArgumentNullException.ThrowIfNull(configureDPoPOptions);

        var dPoPOptions = new DPoPOptions();
        configureDPoPOptions(dPoPOptions);

        builder.Services.TryAddSingleton(dPoPOptions);
        builder.Services.TryAddScoped<IDPoPProofValidationService, DPoPProofValidationService>();
        builder.Services.TryAddScoped<MessageReceivedHandler>();
        builder.Services.TryAddScoped<TokenValidationHandler>();
        builder.Services.TryAddScoped<ChallengeHandler>();
        return builder;
    }
}
