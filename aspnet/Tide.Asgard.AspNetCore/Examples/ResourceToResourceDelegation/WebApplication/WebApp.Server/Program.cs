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

// Create the policy that data is locked under. It can only be added to TideCloak on behalf of a logged-in user,
// so the setup endpoint adds it.
var policy = new PolicyBuilder(builder.Configuration["Keycloak:vendorId"]!, "SimpleTagBasedDecryption:1");
policy.BypassExplicitUserConsent(); // using the policy needs no explicit approval from other Tide users
policy.UseForEncyption(); // the policy may serve locking requests
builder.Services.AddSingleton(new LockingPolicy("locking-user-data-example", policy.BuildPolicy()));

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
public class ServerController(IAspAsgardService asgard, TidecloakPolicyProvider policies, LockingPolicy policy, Database database, IHttpClientFactory httpClientFactory) : ControllerBase
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
		
		var response = await asgard.CreateLockContext(lockOptions)
			.UsePolicy(policy.Id)
			.Lock();
		
		var encryptedPII = response.GetLockedItemById("patient personal info");
		var encryptedAge = response.GetLockedItemById("anonymous patient age");
		var encryptedMedicalDetails = response.GetLockedItemById("patient medical details");

		await database.AddPatient(patientId, new EncryptedPatient(encryptedPII.Cipher.ToArray(), encryptedAge.Cipher.ToArray(), encryptedMedicalDetails.Cipher.ToArray()));

		return Ok(patientId);
	}
	
	/// <summary>
	/// Get a medical summary from the agent service regarding a specific patien'ts medical details.
	/// </summary>
	/// <param name="patientId"></param>
	/// <returns></returns>
	[RequireExchangeApproval(ApprovalRequirement.TideEnabled, "analytics")] // need tide delegation to delegate AGAIN for the agent resource (so agent can decrypt)
	[HttpGet("MedicalSummary")]
	public async Task<IActionResult> MedicalSummary([FromQuery] Guid patientId)
	{
		var patient = await database.GetPatient(patientId);
		if (patient is null) return NotFound();

		// send request to agent service to get medical summary, given the encrypted patient medical details
		var asgardClient = asgard.GetHttpClient("agent");
		var response = await asgardClient.PostAsJsonAsync("http://localhost:3001/analytics/medical", patient.EncMedicalDetails);
		response.EnsureSuccessStatusCode();
		return Ok(await response.Content.ReadAsStreamAsync());
	}

	/// <summary>
	/// Get a personal summary from the agent service regarding a patient's personal details.
	/// </summary>
	/// <param name="patientId"></param>
	/// <returns></returns>
	[RequireExchangeApproval(ApprovalRequirement.TideEnabled, "analytics")] // need tide delegation to delegate AGAIN for the agent resource (so agent can decrypt)
	[HttpGet("PersonalSummary")]
	public async Task<IActionResult> PersonalSummary([FromQuery] Guid patientId)
	{
		var patient = await database.GetPatient(patientId);
		if (patient is null) return NotFound("patient not found");

		// send request to agent service to get medical summary, given the encrypted patient medical details
		var asgardClient = asgard.GetHttpClient("analytics");
		Console.WriteLine("SENDING");
		var response = await asgardClient.GetAsync("https://localhost:3001/analytics/personal?id=" + patientId);
		//response.EnsureSuccessStatusCode();
		Console.WriteLine("SENT");

		// debug: what the analytics service answered. Only the response is printed - the request's headers can carry the
		// exchanged bearer token.
		var body = await response.Content.ReadAsStringAsync();
		Console.WriteLine($"analytics answered {(int)response.StatusCode} {response.ReasonPhrase} over HTTP/{response.Version}");
		foreach (var header in response.Headers.Concat(response.Content.Headers))
			Console.WriteLine($"  {header.Key}: {string.Join(", ", header.Value)}");
		Console.WriteLine($"  body ({body.Length} chars): {(body.Length > 2000 ? body[..2000] + "..." : body)}");

		return Ok(body);
	}

	/// <summary>
	/// Get the average patient age from the agent service;
	/// </summary>
	/// <param name="patientId"></param>
	/// <returns></returns>
	[RequireExchangeApproval(ApprovalRequirement.TideEnabled, "agent")] // need tide delegation to delegate AGAIN for the agent resource (so agent can decrypt)
	[HttpGet("AverageAge")]
	public async Task<IActionResult> AverageAge()
	{
		var allEncryptedAges = await database.GetAllAges();

		var asgardClient = asgard.GetHttpClient("agent");
		var response = await asgardClient.PostAsJsonAsync("http://localhost:3001/analytics/age_average", allEncryptedAges);
		response.EnsureSuccessStatusCode();
		return Ok(await response.Content.ReadAsStreamAsync());
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

	/// <summary>The patient's ciphers, or null if there is no patient with that id.</summary>
	public async Task<EncryptedPatient?> GetPatient(Guid id)
	{
		await using var command = dataSource.CreateCommand("SELECT personal_info, age, medical_details FROM patients WHERE id = @id");
		command.Parameters.AddWithValue("id", id);
		await using var reader = await command.ExecuteReaderAsync();
		if (!await reader.ReadAsync()) return null;
		return new EncryptedPatient(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2));
	}

	/// <summary>Every patient's age cipher - the ages on their own, with nothing that says whose they are.</summary>
	public async Task<byte[][]> GetAllAges()
	{
		await using var command = dataSource.CreateCommand("SELECT age FROM patients");
		await using var reader = await command.ExecuteReaderAsync();
		var ages = new List<byte[]>();
		while (await reader.ReadAsync()) ages.Add(reader.GetFieldValue<byte[]>(0));
		return [.. ages];
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
