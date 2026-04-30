// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Keycloak.AuthServices.Authentication;
using Keycloak.AuthServices.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.ClientCertification;
using Tide.Asgard.AspNetCore.Authentication.mTLS;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core.Crypto.Ed25519;

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
	/// <param name="builder"></param>
	/// <returns></returns>
	/// <exception cref="InvalidOperationException"></exception>
	public static IServiceCollection AddTokenExchange(
		this IServiceCollection services,
		string tidecloakHttpsDomain,
		string realm,
		string[] certificatePaths
		)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(certificatePaths);

		var domainUri = new Uri(tidecloakHttpsDomain);
		if(domainUri.Scheme != Uri.UriSchemeHttps)
		{
			throw new InvalidOperationException($"The provided domain '{tidecloakHttpsDomain}' is not a valid HTTPS URL.");
		}
		var httpsRealmDomain = domainUri.GetLeftPart(UriPartial.Authority) + $"/realms/{realm}/";

		X509CertificateCollection certList = [];
		foreach (var certPath in certificatePaths)
		{
			if (!File.Exists(certPath))
			{
				throw new InvalidOperationException($"Certificate file not found at path: {certPath}");
			}
			certList.Add(X509CertificateLoader.LoadPkcs12FromFile(certPath, password: null));
		}

		if (services.Any(s => s.ServiceType == typeof(TokenExchangeClientMarker)))
		{
			throw new InvalidOperationException(
				"An HTTP client for token exchange has already been registered. " +
				"Multiple registrations for different realms are not allowed.");
		}
		services.AddSingleton<TokenExchangeClientMarker>(); // marker to keep track of registration and prevent multiple registrations with different certificates

		services.AddHttpClient("asgard-token-exchange-client", client =>
		{
			client.BaseAddress = new Uri(httpsRealmDomain);
		})
		.ConfigurePrimaryHttpMessageHandler(() =>
		{
			return new SocketsHttpHandler
			{
				SslOptions = new SslClientAuthenticationOptions
				{
					ClientCertificates = certList
				}
			};
		});

		services.TryAddSingleton<ITokenExchangeService, TokenExchangeService>();

		return services;
	}

	public static IServiceCollection AddAutoClientCeritificationToDashboard(
		this IServiceCollection services,	
		TidecloakDashboardOptions dashboardOptions
		)
	{

		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(dashboardOptions);

		services.AddControllers()
			.AddApplicationPart(System.Reflection.Assembly.GetExecutingAssembly());

		services.AddKeycloakAuthorization();

		services.AddAuthorizationBuilder()
			.AddPolicy(TidecloakDashboardAuthenticationSchemes.ClientCertificationPolicy, builder =>
			{
				builder.AuthenticationSchemes = dashboardOptions.AllowedClientCertificationAuthenticationSchemes;
				builder.RequireResourceRolesForClient(dashboardOptions.DashboardClientName, dashboardOptions.AllowedClientCertificationRoles);
			});

		return services;
	}
}
