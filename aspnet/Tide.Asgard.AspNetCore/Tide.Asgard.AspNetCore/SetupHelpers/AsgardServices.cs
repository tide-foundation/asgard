// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ork.Clients;
using Ork.Clients.Providers;
using Tide.Asgard.AspNetCore.Authentication.Middleware;
using Tide.Asgard.AspNetCore.Authentication.mTLS;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core;
using Tide.Asgard.Core.PolicyHelpers;
using static Tide.Asgard.AspNetCore.Authentication.SetupHelpers.TidecloakAdapter;

namespace Tide.Asgard.AspNetCore.Authentication.SetupHelpers;

/// <summary>
/// Registers the services behind Asgard: token exchange, locking, policies and this resource's mTLS identity.
/// </summary>
internal static class AsgardServices
{
	/// <summary>
	/// Default implementation of Asgard registration. Uses a FileDeviceKeyProvider to store the device key in a file.
	/// </summary>
	/// <param name="services"></param>
	/// <param name="asgardConfiguration"></param>
	/// <returns></returns>
	public static IServiceCollection AddTideExtensions(
		this IServiceCollection services,
		IConfiguration asgardConfiguration
		)
	{
		var identityKeyPath = asgardConfiguration.GetSection("Asgard")["identity_key_path"] ?? Utils.RESOURCE_KEY_DEFAULT_PATH;
		var tideKeyPath = asgardConfiguration.GetSection("Asgard")["tide_key_path"] ?? Utils.TIDE_KEY_DEFAULT_PATH;

		return AddTideExtensions(services, asgardConfiguration, new FileResourceKeyProvider(identityKeyPath, tideKeyPath));
	}
	/// <summary>
	/// Default implementation of Asgard registration. Uses the provided IDeviceKeyProvider to store the device key.
	/// </summary>
	/// <param name="services"></param>
	/// <param name="config"></param>
	/// <param name="resourceKeyProvider"></param>
	/// <returns></returns>
	/// <exception cref="Exception"></exception>
	internal static IServiceCollection AddTideExtensions(
		this IServiceCollection services,
		IConfiguration config,
		IResourceKeyProvider resourceKeyProvider
		)
	{
		// add the tide client manager provider
		var configurationSection = GetKeycloakSection(config);

		string baseRealmUrl = GetBaseRealmUrl(configurationSection);

		var tideClientManagerProvider = new TideClientManagerProvider(
			baseRealmUrl,
			resourceKeyProvider,
			configurationSection["homeOrkUrl"],
			configurationSection["thresholdT"] == null ? null : int.Parse(configurationSection["thresholdT"]!)
			);
		services.AddSingleton(tideClientManagerProvider);

		var asgardSettings = new AsgardSettings(
			GetClientId(configurationSection),
			GetRealm(configurationSection),
			configurationSection["vendorId"] ?? throw new InvalidOperationException("Missing required configuration: vendorId")
		);
		services.AddSingleton(asgardSettings);

		// add http context accessor
		services.AddHttpContextAccessor();

		// add token exchange service
		services.TryAddScoped<ITokenExchangeService, TokenExchangeService>();

		// add default cache service
		services.AddScoped<IAsgardCache, AspDefaultAsgardCache>();

		// add default tidecloak policy provider
		services.AddScoped<IPolicyProvider, TidecloakPolicyProvider>();

		// add asgard service
		services.AddScoped<IAspAsgardService, AspAsgardService>();

		// add asgrd exception handler
		services.AddExceptionHandler<AsgardExceptionHandler>();
		services.AddProblemDetails();

		// add the clients that call other resources over mTLS
		MTLSClientSetup.AddResourceClients(services);

		// enroll this resource's mTLS identity, or keep trying in the background until Tidecloak approves it
		MTLSClientSetup.AddResourceIdentity(services, configurationSection, resourceKeyProvider, GetResourceIdentityPaths(config)).GetAwaiter().GetResult();

		// add the tidecloak client with correct credentials 
		MTLSClientSetup.AddTidecloakClient(services, baseRealmUrl, GetRealm(configurationSection));

		// add device key provider
		services.AddSingleton(resourceKeyProvider);

		return services;
	}
}
