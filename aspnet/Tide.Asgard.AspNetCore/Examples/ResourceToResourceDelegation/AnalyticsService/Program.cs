using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Ork.Models;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.SetupHelpers;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

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
[RequireExchangeApproval(ApprovalRequirement.TideEnabled)] // unlocking needs a Tide-secured delegation, which the calling web application passes on
public class AnalyticsController(IAspAsgardService asgard, NpgsqlDataSource dataSource) : ControllerBase
{
	// the policy the web application locks patient records under (WebApp.Server/Program.cs) - it allows unlocking too
	private const string PolicyId = "patient-records-policy";

	public record PatientInfo(string Name, int Age, string Address); // same data type as web app
	public record EncryptedPatient(byte[] EncPatientInfo, byte[] EncAge, byte[] EncMedicalDetails); // same data type as web app

	[HttpGet("personal")]
	public async Task<IActionResult> PersonalSummary([FromQuery] Guid id)
	{
		// the patient's ciphers, as the web application stored them
		var patient = await GetEncryptedPatient(id);
		if (patient is null) return NotFound("No patient has that id.");

		// unlock the name, age and address, with the tags the web application locked them under
		var response = await asgard.CreateLockContext()
			.UsePolicy(PolicyId)
			.Unlock(new UnlockOptions().AddItemToUnlock(new ItemToUnlock
			{
				ItemId = "patient personal info",
				Tags = ["name", "age", "address"],
				Data = patient.EncPatientInfo,
			}));
		var info = JsonSerializer.Deserialize<PatientInfo>(response.GetUnlockedItemById("patient personal info").PlainText.Span)!;

		var ageGroup = info.Age / 10 * 10;
		return Ok($"{info.Name}, aged {info.Age} (age group {ageGroup}-{ageGroup + 9}), lives at {info.Address}.");
	}

	[HttpGet("medical")]
	public async Task<IActionResult> MedicalSummary([FromQuery] Guid id)
	{
		var patient = await GetEncryptedPatient(id);
		if (patient is null) return NotFound("No patient has that id.");

		// unlock only the medical details - nothing in them says who the patient is
		var response = await asgard.CreateLockContext()
			.UsePolicy(PolicyId)
			.Unlock(new UnlockOptions().AddItemToUnlock(new ItemToUnlock
			{
				ItemId = "patient medical details",
				Tags = ["medical"],
				Data = patient.EncMedicalDetails,
			}));
		var details = Encoding.UTF8.GetString(response.GetUnlockedItemById("patient medical details").PlainText.Span);

		// an agentic service would hand the details to its model here - this one just measures them
		var words = details.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
		return Ok($"{words} words of medical details:\n{details}");
	}

	[HttpGet("average-age")]
	public async Task<IActionResult> AverageAge()
	{
		var ages = await GetEncryptedAges();
		if (ages.Count == 0) return Ok("There are no patients to average.");

		// unlock every age in one call - each was locked on its own, so this reveals no names or addresses
		var response = await asgard.CreateLockContext()
			.UsePolicy(PolicyId)
			.Unlock(new UnlockOptions().AddItemListToUnlock(ages.Select(age => new ItemToUnlock { Tags = ["age"], Data = age })));
		var average = response.UnlockedItems.Average(item => BitConverter.ToInt32(item.PlainText.Span));

		return Ok($"{average:0.#} years, across {ages.Count} patients.");
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

	/// <summary>Every patient's age cipher - the ages on their own, with nothing that says whose they are.</summary>
	private async Task<List<byte[]>> GetEncryptedAges()
	{
		await using var command = dataSource.CreateCommand("SELECT age FROM patients");
		await using var reader = await command.ExecuteReaderAsync();
		var ages = new List<byte[]>();
		while (await reader.ReadAsync()) ages.Add(reader.GetFieldValue<byte[]>(0));
		return ages;
	}
}