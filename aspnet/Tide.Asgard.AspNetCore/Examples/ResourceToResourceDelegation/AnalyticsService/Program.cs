using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Cryptide.Tools;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Ork.Models;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.SetupHelpers;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core.PolicyHelpers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.AddAsgard(options => options.RequireHttpsMetadata = false) // local dev only
	.RequireTokenBinding(options => options.SupportBindings = Binding.mTLS); // only certificate-bound tokens, over mutual TLS
builder.Services.AddProblemDetails(); // required by UseExceptionHandler

// the web application's Postgres database (Database/schema.sql), which this service may only read patients from
builder.Services.AddNpgsqlDataSource(builder.Configuration.GetConnectionString("DefaultConnection")
	?? throw new InvalidOperationException("Missing the connection string 'DefaultConnection'. docker compose provides it - see the README."));

var app = builder.Build();

app.UseExceptionHandler(); // required by Asgard, which reports its errors through it
app.UseDefaultFiles(); // the frontend, which the client project builds into wwwroot/
app.UseStaticFiles();
// this app does not have a log in page so UseTide_over_DPoP is not required here
//app.UseTide_over_DPoP(builder.Configuration, "browser-login-page"); // the id of the public client the frontend logs in with
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();


[Authorize]
[ApiController]
[Route("analytics")]
public class AnalyticsController(IAspAsgardService asgard, TidecloakPolicyProvider policies, NpgsqlDataSource dataSource, IHttpClientFactory httpClientFactory) : ControllerBase
{
	public record Patient(string Name, int Age, string Address, string MedicalDetails); // same data type as web app
	public record PatientInfo(string Name, int Age, string Address); // same data type as web app
	public record EncryptedPatient(byte[] EncPatientInfo, byte[] EncAge, byte[] EncMedicalDetails); // same data type as web app

	[HttpGet("personal")]
	[RequireExchangeApproval(ApprovalRequirement.TideEnabled)] // this is a server only contactible over mtls, requires tide delegation
	public async Task<IActionResult> PersonalSummary([FromQuery] Guid id)
	{
		// the patient's ciphers, as the web application stored them
		var patient = await GetEncryptedPatient(id);
		if (patient is null) return NotFound();

		// the summary of patient.EncPatientInfo goes here
		return Ok();
	}

	/// <summary>The patient's ciphers from the web application's database, or null if there is no patient with that id.</summary>
	private async Task<EncryptedPatient?> GetEncryptedPatient(Guid id)
	{
		await using var command = dataSource.CreateCommand("SELECT personal_info, age, medical_details FROM patients WHERE id = @id");
		command.Parameters.AddWithValue("id", id);
		await using var reader = await command.ExecuteReaderAsync();
		if (!await reader.ReadAsync()) return null;
		return new EncryptedPatient(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2));
	}
}
