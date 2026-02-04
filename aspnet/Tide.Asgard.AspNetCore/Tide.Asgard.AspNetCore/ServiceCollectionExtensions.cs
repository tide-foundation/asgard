// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

﻿using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Tide.Asgard.AspNetCore.Authentication;

namespace Tide.Asgard.AspNetCore.Authentication;

/// <summary>
///     Contains
///     <see href="https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.dependencyinjection.iservicecollection">IServiceCollection</see>
///     extension(s) for registering Asgard.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	///     Adds Asgard API authentication to the specified <see cref="IServiceCollection" />.
	/// </summary>
	/// <param name="services">The service collection to add authentication to.</param>
	/// <param name="configureOptions">An action to configure the <see cref="JwtBearerOptions" />.</param>
	/// <returns>An <see cref="AsgardAuthenticationBuilder" /> for further configuration.</returns>
	/// <exception cref="ArgumentNullException">
	///     Thrown when <paramref name="services" /> or
	///     <paramref name="configureOptions" /> is null.
	/// </exception>
	public static AsgardAuthenticationBuilder AddAsgardAuthentication(
        this IServiceCollection services,
        Action<JwtBearerOptions>? configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions, nameof(configureOptions));

        return services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddAsgardAuthentication(JwtBearerDefaults.AuthenticationScheme, configureOptions);
    }

	/// <summary>
	///     Adds Asgard API authentication to the specified <see cref="IServiceCollection" />.
	/// </summary>
	/// <param name="services">The service collection to add authentication to.</param>
	/// <param name="authenticationScheme">The authentication scheme to use.</param>
	/// <param name="configureOptions">An action to configure the <see cref="JwtBearerOptions" />.</param>
	/// <returns>An <see cref="AsgardAuthenticationBuilder" /> for further configuration.</returns>
	/// <exception cref="ArgumentNullException">
	///     Thrown when <paramref name="services" /> or
	///     <paramref name="configureOptions" /> is null.
	/// </exception>
	/// <exception cref="ArgumentException">Thrown when <paramref name="authenticationScheme" /> is null or empty.</exception>
	public static AsgardAuthenticationBuilder AddAsgardAuthentication(this IServiceCollection services,
        string? authenticationScheme, Action<JwtBearerOptions>? configureOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme, nameof(authenticationScheme));
        ArgumentNullException.ThrowIfNull(configureOptions, nameof(configureOptions));

        return services
            .AddAuthentication(options => { options.DefaultScheme = authenticationScheme; })
            .AddAsgardAuthentication(authenticationScheme, configureOptions);
    }
}
