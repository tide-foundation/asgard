// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Keycloak.AuthServices.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Tide.Asgard.AspNetCore.Authentication.SetupHelpers;
using static Tide.Asgard.AspNetCore.Authentication.SetupHelpers.TidecloakAdapter;

namespace Tide.Asgard.AspNetCore.Authentication;

/// <summary>
/// Adds Asgard to an application.
/// </summary>
public static class AsgardSetup
{
	public static AsgardWebApiConfigurationBuilder AddAsgard(this WebApplicationBuilder builder, Action<JwtBearerOptions>? tokenValidationOptions = null)
	{
		var config = builder.Configuration;
		var configurationSection = GetKeycloakSection(config);
		var keycloakBuilder = builder.Services
			.AddKeycloakWebApiAuthentication(config, options =>
			{
				options.TokenValidationParameters.IssuerSigningKey = Utils.GetEd25519IssuerKey(configurationSection);
				tokenValidationOptions?.Invoke(options);
			});
		return new AsgardWebApiConfigurationBuilder(builder, keycloakBuilder);
	}
}
