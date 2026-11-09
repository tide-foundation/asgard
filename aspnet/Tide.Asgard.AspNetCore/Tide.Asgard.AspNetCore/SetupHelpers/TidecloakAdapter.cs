// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;
using Tide.Asgard.AspNetCore.Authentication.mTLS;

namespace Tide.Asgard.AspNetCore.Authentication.SetupHelpers;

/// <summary>
/// Reads the Tidecloak adaptor, and this resource's own settings in the Asgard section, from the configuration.
/// </summary>
internal static class TidecloakAdapter
{
	/// <summary>
	/// The "Keycloak" configuration section, which is where the Tidecloak adaptor is read from. When the adaptor is not
	/// in it, it is taken from the <see cref="Utils.TIDECLOAK_JSON_ENVIRONMENT_VARIABLE"/> environment variable and
	/// added to the configuration itself - so everything else that reads the section, like the token validation, is
	/// looking at the same adaptor.
	/// </summary>
	internal static IConfigurationSection GetKeycloakSection(IConfiguration config)
	{
		var configurationSection = config.GetSection("Keycloak");

		// the section existing says nothing - it may only hold this resource's own settings, like the key paths. The
		// realm is what every adaptor carries.
		if (configurationSection["realm"] != null) return configurationSection;

		var tidecloakJson = Environment.GetEnvironmentVariable(Utils.TIDECLOAK_JSON_ENVIRONMENT_VARIABLE);
		if (string.IsNullOrWhiteSpace(tidecloakJson))
			throw new InvalidOperationException($"Missing the Tidecloak adaptor. Put it in the 'Keycloak' configuration section, or in the {Utils.TIDECLOAK_JSON_ENVIRONMENT_VARIABLE} environment variable.");

		// builder.Configuration is one. Anything else is a read-only view, and using the adaptor for Asgard alone would
		// leave the rest of the application without it.
		if (config is not IConfigurationManager configurationManager)
			throw new InvalidOperationException($"The Tidecloak adaptor in {Utils.TIDECLOAK_JSON_ENVIRONMENT_VARIABLE} can only be added to a configuration that takes new sources. Pass builder.Configuration to {nameof(AsgardSetup.AddAsgard)}.");

		IConfigurationRoot adaptor;
		try
		{
			using var stream = new MemoryStream(Encoding.UTF8.GetBytes(tidecloakJson));
			adaptor = new ConfigurationBuilder().AddJsonStream(stream).Build();
		}
		catch (Exception e) when (e is JsonException or FormatException)
		{
			throw new InvalidOperationException($"The {Utils.TIDECLOAK_JSON_ENVIRONMENT_VARIABLE} environment variable does not hold a json object", e);
		}

		// the adaptor is a bare object, so its values are nested under the section. They are added as plain values rather
		// than as the stream - a stream source cannot be read again when the sources reload
		configurationManager.AddInMemoryCollection(adaptor.AsEnumerable()
			.Where(setting => setting.Value != null)
			.Select(setting => KeyValuePair.Create($"Keycloak:{setting.Key}", setting.Value))
			.ToArray());

		return configurationSection;
	}

	internal static string GetClientId(IConfigurationSection section)
		=> section["resource"] ?? throw new InvalidOperationException("Missing required configuration: resource");

	internal static string GetRealm(IConfigurationSection section)
		=> section["realm"] ?? throw new InvalidOperationException("Missing required configuration: realm");

	internal static string GetBaseRealmUrl(IConfigurationSection section)
	{
		string realm = GetRealm(section);
		string tidecloakDomain = section["auth-server-url"] ?? throw new InvalidOperationException("Missing required configuration: auth-server-url");

		return new Uri(tidecloakDomain).GetLeftPart(UriPartial.Authority) + $"/realms/{realm}/";
	}	

	internal static ResourceIdentityPaths GetResourceIdentityPaths(IConfiguration configuration) => GetResourceIdentityPaths(configuration.GetSection("Asgard"));
	internal static ResourceIdentityPaths GetResourceIdentityPaths(IConfigurationSection section) => new(
		section["certificate_path"] ?? Utils.RESOURCE_CERTIFICATE_DEFAULT_PATH,
		section["root_ca_path"] ?? Utils.ROOT_CA_DEFAULT_PATH,
		section["certificate_request_path"] ?? Utils.RESOURCE_CERTIFICATE_REQUEST_DEFAULT_PATH);
}
