using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.AspNetCore.Authentication;

public class TidecloakDashboardOptions
{
	public string DashboardClientName { get; set; } = "tidecloak-dashboard";
	public string[] AllowedClientCertificationAuthenticationSchemes { get; set; } = [];
	public string[] AllowedClientCertificationRoles { get; set; } = [];
}
