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
	.RequireTokenBinding(options => options.SupportBindings = Binding.DPoP); // only DPoP-bound tokens are accepted
builder.Services.AddScoped<TidecloakPolicyProvider>(); // keeps policies in TideCloak
builder.Services.AddProblemDetails(); // required by UseExceptionHandler

// the Postgres database (Database/schema.sql) the encrypted patient records and the locking policy are kept in
builder.Services.AddNpgsqlDataSource(builder.Configuration.GetConnectionString("DefaultConnection")
	?? throw new InvalidOperationException("Missing the connection string 'DefaultConnection'. docker compose provides it - see the README."));
builder.Services.AddSingleton<Database>();

// Create the policy patient records are locked and unlocked under. It can only be added to TideCloak on behalf of a
// logged-in user, so the setup endpoint adds it.
var policy = new PolicyBuilder(builder.Configuration["Keycloak:vendorId"]!, "SimpleTagBasedDecryption:1");
policy.BypassExplicitUserConsent(); // using the policy needs no explicit approval from other Tide users
policy.UseForEncyption(); // the policy may serve locking requests - this app's
policy.UseForDecryption(); // and unlocking requests - the analytics service's
builder.Services.AddSingleton(new LockingPolicy("patient-records-policy", policy.BuildPolicy()));

var app = builder.Build();

app.UseExceptionHandler(); // required by Asgard, which reports its errors through it
app.UseDefaultFiles(); // the frontend, which the client project builds into wwwroot/
app.UseStaticFiles();
app.UseTide_over_DPoP(builder.Configuration, "webapp_login"); // the id of the public client the frontend logs in with
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public record LockingPolicy(string Id, ReadOnlyMemory<byte> Data);

public record EncryptedPatient(byte[] EncPatientInfo, byte[] EncAge, byte[] EncMedicalDetails);

[Authorize]
[ApiController]
[Route("server")]
public class ServerController(IAspAsgardService asgard, TidecloakPolicyProvider policies, LockingPolicy policy, Database database) : ControllerBase
{
	public record Patient(string Name, int Age, string Address, string MedicalDetails);
	public record PatientInfo(string Name, int Age, string Address);

	/// <summary>
	/// Need an authenticated user to call this so to setup the policy with tidecloak
	/// </summary>
	/// <returns></returns>
	[HttpGet("setup")]
	[RequireExchangeApproval(ApprovalRequirement.Basic)] // only need basic DPoP delegation to contract tidecloak
	public async Task<IActionResult> Setup()
	{
		policies.SetAuthentication(await asgard.ExchangeContextTokenForApplicationToken());
		if (await policies.GetPolicy(policy.Id) is null)
		{
			// TideCloak answers with a change request when the realm wants a quorum of admins to approve the policy first.
			// The database keeps it, so the policy is only submitted once.
			var changeRequestId = await database.GetPolicyChangeRequest(policy.Id);
			if (changeRequestId is null)
			{
				changeRequestId = await policies.AddPolicyWithChangeRequest(policy.Id, policy.Data);
				await database.SavePolicy(policy.Id, policy.Data, changeRequestId);
			}
			if (changeRequestId is not null)
				return Accepted(value: $"Policy {policy.Id} is awaiting approval - change request {changeRequestId}. Reload the page once an admin has approved it.");
		}
		return Ok();
	}

	/// <summary>
	/// Add a patient record to the server. Store encrypted.
	/// </summary>
	/// <param name="patient"></param>
	/// <returns></returns>
	[HttpPost("AddPatient")]
	[RequireExchangeApproval(ApprovalRequirement.TideEnabled)] // need tide delegation to encrypt on this resource
	public async Task<IActionResult> Add([FromBody] Patient patient)
	{
		var patientId = Guid.NewGuid();

		var lockOptions = new LockOptions()
			.AddItemToLock(new ItemToLock
			{
				// this encrypted data will hold the patient's PII, so we will bundle them to encrypt together
				ItemId = "patient personal info",
				Tags = ["name", "age", "address"], // use tags to describe the data we are encrypting
				Data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new PatientInfo(patient.Name, patient.Age, patient.Address)))
			})
			.AddItemToLock(new ItemToLock
			{
				// this encrypted data will hold a patient age without other patient info
				ItemId = "anonymous patient age",
				Tags = ["age"], // just an age tag, only encrypting age
				Data = BitConverter.GetBytes(patient.Age)
			})
			.AddItemToLock(new ItemToLock
			{
				// a new item to encrypt that only contains medical details, no patient information
				ItemId = "patient medical details",
				Tags = ["medical"], // tags say data is just generic medical details
				Data = Encoding.UTF8.GetBytes(patient.MedicalDetails)
			});
		
		var response = await asgard.CreateLockContext()
			.UsePolicy(policy.Id)
			.Lock(lockOptions);
		
		var encryptedPII = response.GetLockedItemById("patient personal info");
		var encryptedAge = response.GetLockedItemById("anonymous patient age");
		var encryptedMedicalDetails = response.GetLockedItemById("patient medical details");

		await database.AddPatient(patientId, new EncryptedPatient(encryptedPII.Cipher.ToArray(), encryptedAge.Cipher.ToArray(), encryptedMedicalDetails.Cipher.ToArray()));

		return Ok(patientId);
	}
	
	/// <summary>
	/// Get a medical summary from the analytics service, which unlocks only the patient's medical details.
	/// </summary>
	[RequireExchangeApproval(ApprovalRequirement.TideEnabled, "analytics")] // need tide delegation to delegate AGAIN for the analytics resource (so it can unlock)
	[HttpGet("MedicalSummary")]
	public Task<IActionResult> MedicalSummary([FromQuery] Guid patientId) => AskAnalytics($"medical?id={patientId}");

	/// <summary>
	/// Get a personal summary from the analytics service, which unlocks the patient's name, age and address.
	/// </summary>
	[RequireExchangeApproval(ApprovalRequirement.TideEnabled, "analytics")] // need tide delegation to delegate AGAIN for the analytics resource (so it can unlock)
	[HttpGet("PersonalSummary")]
	public Task<IActionResult> PersonalSummary([FromQuery] Guid patientId) => AskAnalytics($"personal?id={patientId}");

	/// <summary>
	/// Get the average patient age from the analytics service, which unlocks only the ages.
	/// </summary>
	[RequireExchangeApproval(ApprovalRequirement.TideEnabled, "analytics")] // need tide delegation to delegate AGAIN for the analytics resource (so it can unlock)
	[HttpGet("AverageAge")]
	public Task<IActionResult> AverageAge() => AskAnalytics("average-age");

	/// <summary>
	/// Calls the analytics service over mutual TLS, on behalf of the logged-in user, and passes its answer on. The
	/// analytics service reads the patient ciphers from the database itself, so only the patient id goes over.
	/// </summary>
	private async Task<IActionResult> AskAnalytics(string pathAndQuery)
	{
		// in compose both apps share one network, so localhost reaches the analytics service there too
		var response = await asgard.GetHttpClient("analytics").GetAsync("https://localhost:3001/analytics/" + pathAndQuery);
		return StatusCode((int)response.StatusCode, await response.Content.ReadAsStringAsync());
	}
}

/// <summary>
/// The web application's Postgres database (Database/schema.sql). Patients are only ever stored as the ciphers the
/// Tide network locked them into.
/// </summary>
public class Database(NpgsqlDataSource dataSource)
{
	public async Task AddPatient(Guid id, EncryptedPatient patient)
	{
		await using var command = dataSource.CreateCommand(
			"INSERT INTO patients (id, personal_info, age, medical_details) VALUES (@id, @personalInfo, @age, @medicalDetails)");
		command.Parameters.AddWithValue("id", id);
		command.Parameters.AddWithValue("personalInfo", patient.EncPatientInfo);
		command.Parameters.AddWithValue("age", patient.EncAge);
		command.Parameters.AddWithValue("medicalDetails", patient.EncMedicalDetails);
		await command.ExecuteNonQueryAsync();
	}

	/// <summary>The change request TideCloak raised for the policy, or null if the policy was never submitted or needed none.</summary>
	public async Task<string?> GetPolicyChangeRequest(string policyId)
	{
		await using var command = dataSource.CreateCommand("SELECT change_request_id FROM policies WHERE id = @id");
		command.Parameters.AddWithValue("id", policyId);
		return await command.ExecuteScalarAsync() as string; // no row, or a null column
	}

	/// <summary>Records the policy as submitted to TideCloak, with the change request it raised, if any.</summary>
	public async Task SavePolicy(string id, ReadOnlyMemory<byte> data, string? changeRequestId)
	{
		await using var command = dataSource.CreateCommand(
			"""
			INSERT INTO policies (id, data, change_request_id) VALUES (@id, @data, @changeRequestId)
			ON CONFLICT (id) DO UPDATE SET data = excluded.data, change_request_id = excluded.change_request_id
			""");
		command.Parameters.AddWithValue("id", id);
		command.Parameters.AddWithValue("data", data.ToArray());
		command.Parameters.AddWithValue("changeRequestId", (object?)changeRequestId ?? DBNull.Value);
		await command.ExecuteNonQueryAsync();
	}
}
