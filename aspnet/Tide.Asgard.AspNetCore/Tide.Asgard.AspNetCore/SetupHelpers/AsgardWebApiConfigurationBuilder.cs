using Keycloak.AuthServices.Authentication;
using Microsoft.AspNetCore.Builder;
using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.AspNetCore.Authentication.SetupHelpers;

public class AsgardWebApiConfigurationBuilder(WebApplicationBuilder webBuilder, KeycloakWebApiAuthenticationBuilder keycloakBuilder)
{
	internal WebApplicationBuilder WebBuilder { get; set; } = webBuilder;
	internal KeycloakWebApiAuthenticationBuilder KeycloakBuilder { get; set; } = keycloakBuilder;
}
