using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Tide.Asgard.AspNetCore.Authentication;

namespace Auth0.AspNetCore.Authentication.Api;

/// <summary>
///     Contains
///     <see href="https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.dependencyinjection.iservicecollection">IServiceCollection</see>
///     extension(s) for registering Auth0.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Adds Auth0 API authentication to the specified <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="services">The service collection to add authentication to.</param>
    /// <param name="configureOptions">An action to configure the <see cref="Auth0ApiOptions" />.</param>
    /// <returns>An <see cref="Auth0ApiAuthenticationBuilder" /> for further configuration.</returns>
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
    ///     Adds Auth0 API authentication to the specified <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="services">The service collection to add authentication to.</param>
    /// <param name="authenticationScheme">The authentication scheme to use.</param>
    /// <param name="configureOptions">An action to configure the <see cref="Auth0ApiOptions" />.</param>
    /// <returns>An <see cref="Auth0ApiAuthenticationBuilder" /> for further configuration.</returns>
    /// <exception cref="ArgumentNullException">
    ///     Thrown when <paramref name="services" /> or
    ///     <paramref name="configureOptions" /> is null.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="authenticationScheme" /> is null or empty.</exception>
    public static AsgardAuthenticationBuilder AddAuth0ApiAuthentication(this IServiceCollection services,
        string? authenticationScheme, Action<JwtBearerOptions>? configureOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme, nameof(authenticationScheme));
        ArgumentNullException.ThrowIfNull(configureOptions, nameof(configureOptions));

        return services
            .AddAuthentication(options => { options.DefaultScheme = authenticationScheme; })
            .AddAsgardAuthentication(authenticationScheme, configureOptions);
    }
}
