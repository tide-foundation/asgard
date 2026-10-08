using System.Text;
using Keycloak.AuthServices.Authentication;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.AspNetCore.DPoP;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// ADAPTOR INSTALLATION
//
// Two files, named exactly as the Playwright suite downloads them into
// tests/aspnet/.adaptors/<realm>/:
//
//   backend.keycloak.json   -> becomes the "Keycloak" configuration section
//   frontend.keycloak.json  -> served to the SPA at /keycloak.json
//
// Neither is edited into appsettings.json and neither is copied into wwwroot,
// so pointing this server at a different realm means pointing it at a different
// directory — no file surgery, no rebuild.
//
//   ASGARD_ADAPTORS_DIR=/path/to/.adaptors/<realm> dotnet run
// ---------------------------------------------------------------------------
var adaptorDir = Path.GetFullPath(
	builder.Configuration["Adaptors:Directory"]
	?? Environment.GetEnvironmentVariable("ASGARD_ADAPTORS_DIR")
	?? Path.Combine(builder.Environment.ContentRootPath, "adaptors"));

var backendAdaptor = Path.Combine(adaptorDir, "backend.keycloak.json");
var frontendAdaptor = Path.Combine(adaptorDir, "frontend.keycloak.json");

foreach (var required in new[] { backendAdaptor, frontendAdaptor })
{
	if (!File.Exists(required))
	{
		throw new FileNotFoundException(
			$"Adaptor config not found: {required}\n" +
			$"Download the frontend + backend adaptors for a realm and put them in {adaptorDir}, " +
			$"or set ASGARD_ADAPTORS_DIR to the directory that holds them.",
			required);
	}
}

// The downloaded adaptor is a BARE object, but Keycloak.AuthServices reads its
// settings from a "Keycloak" section — and UseTideSecuredDPoP hardcodes the
// "Keycloak:" prefix — so nest it in memory instead of rewriting the file.
//
// This source is added LAST, so any key it carries beats appsettings.json. It
// carries no enrollment_poll_interval_seconds, which is why the 5s override in
// appsettings.json survives.
builder.Configuration.AddJsonStream(
	new MemoryStream(Encoding.UTF8.GetBytes($"{{\"Keycloak\":{File.ReadAllText(backendAdaptor)}}}")));

builder.Services.AddControllers();

builder.Services
	.AddKeycloakWebApiAuthentication(builder.Configuration, options =>
	{
		// The local stack is plain http, and the realm signs EdDSA — a key type
		// the stock JWKS path does not resolve, so it comes from the adaptor.
		options.RequireHttpsMetadata = false;
		options.TokenValidationParameters.IssuerSigningKey =
			Utils.GetEd25519IssuerKey(builder.Configuration);
	})
	.WithDPoP(opts => opts.Mode = DPoPModes.Required);

builder.Services.AddAsgard(builder.Configuration, ResourceAuthenticationMode.AutoMTLSEnrollment); // needed

// ---------------------------------------------------------------------------
// mTLS VHOST RESOLUTION (test environment only)
//
// AddAsgard's "Tidecloak" http client talks to https://{realm}.client.{host}:8443
// — the reverse proxy picks the realm's certificate from the TLS server name.
// With the local stack {host} is "localhost", and the Playwright suite mints a
// fresh realm name every run, so the vhost is something like
// iga-mtls-exchange-mt8251050.client.localhost — which glibc will NOT resolve
// (only bare "localhost" is special; subdomains need a wildcard resolver or an
// /etc/hosts line that cannot be pre-written for a random name).
//
// Rather than require a machine-level DNS tweak, dial the CONFIGURED host's
// address for any "*.client.{host}" name. Only the TCP destination changes: the
// request's Host header and the TLS SNI keep the realm vhost, so the proxy still
// selects the right realm certificate. Appended after AddAsgard so it runs after
// the library's own handler configuration and only tweaks the socket dial —
// the client-certificate and trust-bundle SslOptions are untouched.
//
// A real deployment has DNS for its realm vhosts and must not do this.
// ---------------------------------------------------------------------------
{
	var tidecloakHost = new Uri(builder.Configuration["Keycloak:auth-server-url"]!).Host;
	var vhostSuffix = $".client.{tidecloakHost}";

	builder.Services.Configure<Microsoft.Extensions.Http.HttpClientFactoryOptions>("Tidecloak", options =>
		options.HttpMessageHandlerBuilderActions.Add(handlerBuilder =>
		{
			if (handlerBuilder.PrimaryHandler is not SocketsHttpHandler sockets) return;

			sockets.ConnectCallback = async (context, cancellationToken) =>
			{
				var target = context.DnsEndPoint;
				if (target.Host.EndsWith(vhostSuffix, StringComparison.OrdinalIgnoreCase))
				{
					target = new System.Net.DnsEndPoint(tidecloakHost, target.Port);
				}

				var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
				{
					NoDelay = true,
				};
				try
				{
					await socket.ConnectAsync(target, cancellationToken);
					return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
				}
				catch
				{
					socket.Dispose();
					throw;
				}
			};
		}));
}

// AddAsgard registers an IExceptionHandler, which only runs behind
// UseExceptionHandler — and that wants ProblemDetails. Both are marked "needed"
// in the example app for the same reason.
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

app.UseDefaultFiles();
app.UseStaticFiles();

// Hand the SPA its adaptor from the adaptor directory rather than from wwwroot,
// so a vite rebuild (which empties wwwroot) can never clobber it.
app.MapGet("/keycloak.json", () =>
	Results.Text(File.ReadAllText(frontendAdaptor), "application/json"));

// Serves the embedded Tide DPoP auth page the enclave loads during login.
app.UseTideSecuredDPoP(builder.Configuration, "frontend");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapFallbackToFile("index.html");

app.Logger.LogInformation(
	"adaptors: {Dir} | realm: {Realm} | backend client: {Client}",
	adaptorDir,
	builder.Configuration["Keycloak:realm"],
	builder.Configuration["Keycloak:resource"]);

app.Run();
