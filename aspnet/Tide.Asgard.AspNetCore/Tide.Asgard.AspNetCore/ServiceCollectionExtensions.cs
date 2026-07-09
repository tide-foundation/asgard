// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ork.Clients;
using Ork.Clients.Providers;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Tide.Asgard.AspNetCore.Authentication.Middleware;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core;
using Tide.Asgard.Core.PolicyHelpers;

namespace Tide.Asgard.AspNetCore.Authentication;

/// <summary>
///     Contains
///     <see href="https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.dependencyinjection.iservicecollection">IServiceCollection</see>
///     extension(s) for registering Asgard.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	/// Enables OAuth 2.0 Token Exchange in your application.
	/// </summary>
	/// <returns></returns>
	/// <exception cref="InvalidOperationException"></exception>
	//public static IServiceCollection AddTokenExchange(
	//	this IServiceCollection services,
	//	string tidecloakHttpsDomain,
	//	string realm,
	//	string clientId,
	//	string[] certificatePaths
	//	)
	//{
	//	ArgumentNullException.ThrowIfNull(services);
	//	ArgumentNullException.ThrowIfNull(certificatePaths);

	//	var domainUri = new Uri(tidecloakHttpsDomain);
	//	if(domainUri.Scheme != Uri.UriSchemeHttps)
	//	{
	//		throw new InvalidOperationException($"The provided domain '{tidecloakHttpsDomain}' is not a valid HTTPS URL.");
	//	}
	//	var httpsRealmDomain = domainUri.GetLeftPart(UriPartial.Authority) + $"/realms/{realm}/";

	//	X509CertificateCollection certList = [];
	//	foreach (var certPath in certificatePaths)
	//	{
	//		if (!File.Exists(certPath))
	//		{
	//			throw new InvalidOperationException($"Certificate file not found at path: {certPath}");
	//		}
	//		certList.Add(X509CertificateLoader.LoadPkcs12FromFile(certPath, password: null));
	//	}

	//	services.AddHttpClient("asgard-token-exchange-client:" + clientId, client =>
	//	{
	//		client.BaseAddress = new Uri(httpsRealmDomain);
	//	})
	//	.ConfigurePrimaryHttpMessageHandler(() =>
	//	{
	//		return new SocketsHttpHandler
	//		{
	//			SslOptions = new SslClientAuthenticationOptions
	//			{
	//				ClientCertificates = certList
	//			}
	//		};
	//	});

	//	services.TryAddSingleton<ITokenExchangeService, TokenExchangeService>();

	//	return services;
	//}

	// add support for the configuration to be a section to allow for multiple token exchange clients to be configured in the same app
	public static IServiceCollection AddTokenExchangeForClient(
		this IServiceCollection services,
		IConfigurationSection configurationSection
		)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configurationSection);
		string clientId = configurationSection["resource"] ?? throw new InvalidOperationException("Missing required configuration: resource");
		string realm = configurationSection["realm"] ?? throw new InvalidOperationException("Missing required configuration: realm");
		string clientSecret = configurationSection["credentials:secret"] ?? throw new InvalidOperationException("Missing required configuration: credentials:secret");
		string tidecloakDomain = configurationSection["auth-server-url"] ?? throw new InvalidOperationException("Missing required configuration: auth-server-url");

		string baseRealmUrl = new Uri(tidecloakDomain).GetLeftPart(UriPartial.Authority) + $"/realms/{realm}/";

		var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

		services.AddHttpClient("asgard-token-exchange-client:" + clientId, client =>
		{
			client.BaseAddress = new Uri(baseRealmUrl);
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
		});

		services.TryAddScoped<ITokenExchangeService, TokenExchangeService>();
		services.AddHttpContextAccessor();

		return services;
	}

	/// <summary>
	/// Enables OAuth 2.0 Token Exchange in your application.
	/// </summary>
	/// <returns></returns>
	/// <exception cref="InvalidOperationException"></exception>
	public static IServiceCollection AddTokenExchange(
		this IServiceCollection services,
		IConfiguration configuration
		)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configuration);

		return AddTokenExchangeForClient(services, configuration.GetSection("Keycloak") ?? throw new InvalidOperationException("Missing required configuration section: Keycloak"));
	}

	/// <summary>
	/// Default implementation of Asgard registration. Uses a FileDeviceKeyProvider to store the device key in a file.
	/// </summary>
	/// <param name="services"></param>
	/// <param name="asgardConfiguration"></param>
	/// <returns></returns>
	public static IServiceCollection AddAsgard(
		this IServiceCollection services,
		IConfiguration asgardConfiguration
		)
	{
		return AddAsgard(services, asgardConfiguration, new FileDeviceKeyProvider());
	}
	/// <summary>
	/// Default implementation of Asgard registration. Uses the provided IDeviceKeyProvider to store the device key.
	/// </summary>
	/// <param name="services"></param>
	/// <param name="config"></param>
	/// <param name="deviceKeyProvider"></param>
	/// <returns></returns>
	/// <exception cref="Exception"></exception>
	public static IServiceCollection AddAsgard(
		this IServiceCollection services,
		IConfiguration config,
		IDeviceKeyProvider deviceKeyProvider
		)
	{
		// add the tide client manager provider		
		var configurationSection = config.GetSection("Keycloak") ?? throw new InvalidOperationException("Missing required configuration section: Keycloak");

		string realm = configurationSection["realm"] ?? throw new InvalidOperationException("Missing required configuration: realm");
		string tidecloakDomain = configurationSection["auth-server-url"] ?? throw new InvalidOperationException("Missing required configuration: auth-server-url");
		string baseRealmUrl = new Uri(tidecloakDomain).GetLeftPart(UriPartial.Authority) + $"/realms/{realm}/";

		var tideClientManagerProvider = new TideClientManagerProvider(
			baseRealmUrl,
			deviceKeyProvider,
			config["homeOrkUrl"],
			config["networkThreshold"] == null ? null : int.Parse(config["networkThreshold"]!)
			);
		services.AddSingleton(tideClientManagerProvider);

		// add http context accessor
		services.AddHttpContextAccessor();

		// add token exchange service
		services.TryAddSingleton<ITokenExchangeService, TokenExchangeService>();

		// add default cache service
		services.AddScoped<IAsgardCache, AspDefaultAsgardCache>();

		// add default tidecloak policy provider
		services.AddScoped<IPolicyProvider, TidecloakPolicyProvider>();

		// add asgard service
		services.AddScoped<IAspAsgardService, AspAsgardService>();

		// add asgrd exception handler
		services.AddExceptionHandler<AsgardExceptionHandler>();

		// add asgard request handler
		services.AddTransient<AsgardMessageHandler>();
		services.AddHttpClient("Asgard")
			.AddHttpMessageHandler<AsgardMessageHandler>();

		return services;
	}
}
