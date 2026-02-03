using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;

namespace Tide.Asgard.AspNetCore.Authentication;

/// <summary>
///     Builder to add functionality on top of Auth0 API authentication.
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
    ///     The authentication scheme to use for the Auth0 authentication handler.
    /// </param>
    public AsgardAuthenticationBuilder(IServiceCollection services, string authenticationScheme)
    {
        Services = services;
        AuthenticationScheme = authenticationScheme;
    }

    public string AuthenticationScheme { get; }
    public IServiceCollection Services { get; }
}
