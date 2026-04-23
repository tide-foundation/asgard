// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Tls;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using Tide.Asgard.AspNetCore.Authentication.ClientCertification;
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

		var opts = new JwtBearerOptions();
		configureOptions(opts);

		ArgumentException.ThrowIfNullOrWhiteSpace(opts.Authority);

		builder.AddJwtBearer(
			authenticationScheme, configureOptions);

		builder.Services.Configure(authenticationScheme, configureOptions);
		
		return new AsgardAuthenticationBuilder(builder.Services, authenticationScheme, opts.Authority, new Uri(opts.Authority).Authority);
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
        this AsgardAuthenticationBuilder builder)
    {
        return builder.WithDPoP(_ => { });
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
		Action<DPoPOptions> configureDPoPOptions)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(configureDPoPOptions);

		var dPoPOptions = new DPoPOptions();
		configureDPoPOptions(dPoPOptions);

		builder.Services.TryAddEnumerable(
			ServiceDescriptor.Singleton<IPostConfigureOptions<JwtBearerOptions>>(
				new AsgardJwtBearerPostConfigureOptions(builder.AuthenticationScheme)));

		builder.Services.TryAddSingleton(dPoPOptions);
		builder.Services.TryAddScoped<IDPoPProofValidationService, DPoPProofValidationService>();
		builder.Services.TryAddScoped<MessageReceivedHandler>();
		builder.Services.TryAddScoped<TokenValidationHandler>();
		builder.Services.TryAddScoped<ChallengeHandler>();
		return builder;
	}

	/// <summary>
	///     Sets up a confidential client with mTLS authentication.
	/// </summary>
	/// <param name="builder">
	///     The <see cref="AsgardAuthenticationBuilder" /> instance to configure.
	/// </param>
	/// <param name="clientId">
	///     The client id of the confidential client.
	/// </param>
	/// <param name="configureMtlsOptions">
	///     A delegate to configure the <see cref="MTLSOptions" /> for mTLS authentication.
	/// </param>
	/// <returns>
	///     The configured <see cref="AsgardConfidentialClientBuilder" /> instance.
	/// </returns>
	public static AsgardConfidentialClientBuilder SetupConfidentialClient(
		this AsgardAuthenticationBuilder builder,
		string clientId,
		Action<MTLSOptions> configureMtlsOptions
		)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

		var mtlsOptions = new MTLSOptions();
		configureMtlsOptions(mtlsOptions);

		ArgumentNullException.ThrowIfNull(mtlsOptions.BaseUri);

		var confidentialBuilder = new AsgardConfidentialClientBuilder(clientId, builder, ConfidentialClientType.MTLS, mtlsOptions.X509Certificate2 != null, mtlsOptions.BaseUri);

		if(mtlsOptions.X509Certificate2 != null)
		{
			// We allow it to be null so Auto Client Registration can work. If token exchange service is called before the client auto registers an error is thrown
			confidentialBuilder.WithMutualTLS("confidental-client", mtls =>
			{
				mtls.X509Certificate2 = mtlsOptions.X509Certificate2;
				mtls.BaseUri = mtlsOptions.BaseUri;
			});
		}

		return confidentialBuilder;
	}

	// This is how you'd add a different kind of confidential client
	private static AsgardConfidentialClientBuilder SetupConfidentialClient(
		this AsgardAuthenticationBuilder builder,
		string clientId,
		string clientSecret
		)
	{
		// then set up the http client here

		throw new NotImplementedException();
	}

	/// <summary>
	/// Enables Mutual TLS (mTLS) to communicate with the authorization server.
	/// </summary>
	/// <param name="builder"></param>
	/// <param name="configureMutualTLS"></param>
	/// <returns></returns>
	private static AsgardConfidentialClientBuilder WithMutualTLS(
		this AsgardConfidentialClientBuilder builder,
		string clientName,
		Action<MTLSOptions> configureMutualTLS
		)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(configureMutualTLS);

		var mtlsOptions = new MTLSOptions();
		configureMutualTLS(mtlsOptions);

		ArgumentNullException.ThrowIfNull(mtlsOptions.BaseUri);
		ArgumentNullException.ThrowIfNull(mtlsOptions.X509Certificate2);
		ArgumentException.ThrowIfNullOrEmpty(clientName);

		builder.AuthBuilder.Services.AddHttpClient(clientName, client =>
		{
			var baseUri = mtlsOptions.BaseUri.ToString();
			if (!baseUri.EndsWith('/'))
				baseUri += '/';
			client.BaseAddress = new Uri(baseUri);
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
	public static AsgardConfidentialClientBuilder WithTokenExchange(
		this AsgardConfidentialClientBuilder builder,
		string requestedAudience
		)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentException.ThrowIfNullOrEmpty(requestedAudience);

		builder.AuthBuilder.Services.TryAddSingleton<ITokenExchangeOptions>(new TokenExchangeOptions { ClientId = builder.ClientId });
		builder.AuthBuilder.Services.AddScoped(sp => new TokenExchangeService(
			sp.GetService<IHttpClientFactory>(),
			sp.GetRequiredService<ITokenExchangeOptions>()));

		return builder;
	}

	public static AsgardConfidentialClientBuilder WithAutoClientCertification(
		this AsgardConfidentialClientBuilder builder,
		string credentialStorePath
		)
	{
		if (builder.ClientType != ConfidentialClientType.MTLS)
			throw new InvalidOperationException($"Cannot perform auto client certification if client type is not {ConfidentialClientType.MTLS}. Remove {nameof(WithAutoClientCertification)} from ASP.NET build");

		var certFilePath = Path.Combine(credentialStorePath, ClientCertificationOptions.CredentialFileName);

		RegistrationStatus regoStatus;
		if (builder.ClientCertSet)
		{
			regoStatus = RegistrationStatus.Registered;
		}
		else if (!File.Exists(certFilePath))
		{
			regoStatus = RegistrationStatus.Unregistered;
		}
		else
		{
			using var cert = new X509Certificate2(certFilePath);
			regoStatus = RegistrationStatus.Registered;
		}

		builder.AuthBuilder.Services.AddControllers().AddApplicationPart(System.Reflection.Assembly.GetExecutingAssembly());

		builder.AuthBuilder.Services
			.AddAuthentication()
			.AddJwtBearer(AsgardAuthenticationSchemes.ClientCertificationAuthority, o =>
			{
				o.Authority = builder.AuthBuilder.Authority;
				o.Audience = AsgardInitPageClient.ClientId;
			});

		var regoOptions = new ClientCertificationOptions
		{
			CredentialPath = credentialStorePath,
			ClientId = builder.ClientId,
			ClientType = builder.ClientType,
			RegistrationStatus = regoStatus,
			AuthorizationServerHost = builder.AuthBuilder.AuthorizationServerHost
		};

		builder.AuthBuilder.Services.AddSingleton(regoOptions);

		if (builder.ClientCertSet) return builder;

		// Register the named HttpClient up-front. The primary handler factory loads the cert
		// from disk on each handler refresh (~2 min default), so the client picks up the
		// certify-written cert without touching DI at runtime.
		// IHttpClientFactory becomes resolvable as soon as ANY AddHttpClient call is made,
		// which satisfies TokenExchangeService's nullable-factory check.
		var pfxPath = certFilePath;
		var baseUri = builder.BaseUri.ToString();
		if (!baseUri.EndsWith('/')) baseUri += '/';

		builder.AuthBuilder.Services.AddHttpClient("confidental-client", client =>
		{
			client.BaseAddress = new Uri(baseUri);
		})
		.ConfigurePrimaryHttpMessageHandler(() =>
		{
			if (!File.Exists(pfxPath))
				throw new InvalidOperationException(
					$"Client certificate not found at '{pfxPath}'. Complete /generate and /certify before calling token exchange.");

			var cert = new X509Certificate2(pfxPath);
			return new SocketsHttpHandler
			{
				SslOptions = new SslClientAuthenticationOptions
				{
					ClientCertificates = [cert]
				}
			};
		});

		return builder;
	}

	public static AsgardAuthenticationBuilder FinishConfidentialClientSetup(this AsgardConfidentialClientBuilder builder) => builder.AuthBuilder;
}
