// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;

namespace Tide.Asgard.AspNetCore.Authentication;

/// <summary>
///     Builder to add functionality on top of Asgard API authentication.
/// </summary>
public class AsgardAuthenticationBuilder
{
    /// <summary>
    ///     Constructs an instance of <see cref="AsgardAuthenticationBuilder" />.
    /// </summary>
    /// <param name="services">
    ///     The <see cref="IServiceCollection" /> instance used to register authentication services.
    /// </param>
    /// <param name="authenticationScheme">
    ///     The authentication scheme to use for the Asgard authentication handler.
    /// </param>
    public AsgardAuthenticationBuilder(IServiceCollection services, string authenticationScheme)
    {
        Services = services;
        AuthenticationScheme = authenticationScheme;
    }

    public string AuthenticationScheme { get; }
    public IServiceCollection Services { get; }
}
