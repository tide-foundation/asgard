using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.SetupHelpers;

// Everything realm-specific comes from the environment: the adaptor in TIDECLOAK_JSON, the paths of this resource's
// keys and certificates in Asgard__*, and an http and an https url in ASPNETCORE_URLS. Every https endpoint requires a
// realm client certificate, so the DPoP cases go over http and the mTLS cases over https.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.AddAsgard(options => options.RequireHttpsMetadata = false) // the local stack is plain http
	.RequireTokenBinding(options => options.SupportBindings = Binding.DPoP | Binding.mTLS);
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
