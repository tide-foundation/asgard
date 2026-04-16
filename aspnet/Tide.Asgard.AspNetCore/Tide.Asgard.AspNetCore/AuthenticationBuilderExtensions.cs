// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using Tide.Asgard.AspNetCore.Authentication.DPoP;
using Tide.Asgard.AspNetCore.Authentication.DPoP.EventHandlers;
using Tide.Asgard.AspNetCore.Authentication.mTLS;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

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
	///     using the default authentication scheme.
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
	///     Enables DPoP (Demonstration of Proof-of-Possession) support for the Asgard API authentication builder
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

	/// <summary>
	/// Enables Mutual TLS (mTLS) to communicate with the authorization server. This is required for using other services such as DelegatedTokenExchange.
	/// </summary>
	/// <param name="builder"></param>
	/// <param name="configureMutualTLS"></param>
	/// <returns></returns>
	public static AsgardAuthenticationBuilder WithMutualTLS(
		this AsgardAuthenticationBuilder builder,
		Action<MTLSOptions> configureMutualTLS
		)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(configureMutualTLS);

		var mtlsOptions = new MTLSOptions();
		configureMutualTLS(mtlsOptions);

		ArgumentNullException.ThrowIfNull(mtlsOptions.BaseUri);
		ArgumentNullException.ThrowIfNull(mtlsOptions.X509Certificate2);
		ArgumentException.ThrowIfNullOrEmpty(mtlsOptions.Name);

		builder.Services.AddHttpClient(mtlsOptions.Name, client =>
		{
			client.BaseAddress = mtlsOptions.BaseUri;
		})
		.ConfigurePrimaryHttpMessageHandler(() =>
		{
			return new SocketsHttpHandler
			{
				SslOptions = new SslClientAuthenticationOptions
				{
					ClientCertificates = [mtlsOptions.X509Certificate2]
				}
			};
		});

		return builder;
	}

	/// <summary>
	/// Enables OAuth 2.0 Token Exchange in your application. Use the injected <see cref="TokenExchangeService" /> in your controllers to perform exchanges.
	/// </summary>
	/// <param name="builder"></param>
	/// <returns></returns>
	/// <exception cref="InvalidOperationException"></exception>
	public static AsgardAuthenticationBuilder WithTokenExchange(
		this AsgardAuthenticationBuilder builder,
		Action<TokenExchangeMTLSOptions> configureExchangeMutualTLS
		)
	{
		ArgumentNullException.ThrowIfNull(builder);

		var authzMtlsOptions = new TokenExchangeMTLSOptions();
		configureExchangeMutualTLS(authzMtlsOptions);

		builder.WithMutualTLS(mtls =>
		{
			mtls.Name = authzMtlsOptions.Name;
			mtls.X509Certificate2 = authzMtlsOptions.X509Certificate2;
			mtls.BaseUri = authzMtlsOptions.BaseUri;
		});

		builder.Services.AddScoped<TokenExchangeService>();

		return builder;
	}
}
