// Moves users from the old platform (SQL Server: idp + profile) into the new IAM and Profile services.
// It only reads the old databases and calls the new services' import endpoints, so each service writes its own data.
// Safe to run again: already imported users are skipped or refreshed.
//
//   dotnet run -- --idp "<old Identity connection string>" --profile "<old Profile connection string>"
//                 --iam http://185.26.32.176/iam --userprofile http://185.26.32.176/userprofile
//                 --token <super admin access token> --refresh-token <its refresh token> [--batch 200] [--dry-run]
//                 [--source-timezone Asia/Tehran]
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

var options = Options.Parse(args);
if (options is null)
{
    Console.WriteLine("Usage: --idp <conn> --profile <conn> --iam <url> --userprofile <url> --token <jwt> [--refresh-token <token>] [--batch 200] [--dry-run] [--source-timezone <tz>]");
    return 1;
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var report = new StringBuilder("stage,legacyId,reason\n");
using var api = new Api(options, json);

Console.WriteLine($"Users from idp (dry run: {options.DryRun})");
var registeredAt = new Dictionary<int, DateTimeOffset?>();
var userTotals = new Totals();
await foreach (var batch in ReadUsers(options))
{
    foreach (var user in batch) registeredAt[user.LegacyId] = user.RegisteredAt;
    userTotals.Read += batch.Count;
    if (options.DryRun) continue;

    var result = await api.PostAsync<UserImportResult>($"{options.IamUrl}/api/v1/legacy-import/users", batch);
    userTotals.Created += result.Created;
    userTotals.Existing += result.AlreadyImported;
    foreach (var issue in result.Issues) report.AppendLine($"iam,{issue.LegacyId},\"{issue.Reason.Replace("\"", "'")}\"");
    Console.WriteLine($"  up to id {batch[^1].LegacyId}: read {userTotals.Read}, created {userTotals.Created}, existing {userTotals.Existing}");
}

Console.WriteLine("Profiles from profile");
var profileTotals = new Totals();
await foreach (var batch in ReadProfiles(options, registeredAt))
{
    profileTotals.Read += batch.Count;
    if (options.DryRun) continue;

    var result = await api.PostAsync<ProfileImportResult>($"{options.ProfileUrl}/api/v1/legacy-import/profiles", batch);
    profileTotals.Created += result.Created;
    profileTotals.Existing += result.Updated;
    profileTotals.Verified += result.KycVerified;
    foreach (var issue in result.Issues) report.AppendLine($"profile,{issue.LegacyId},\"{issue.Reason.Replace("\"", "'")}\"");
    Console.WriteLine($"  up to id {batch[^1].LegacyId}: read {profileTotals.Read}, created {profileTotals.Created}, updated {profileTotals.Existing}, kyc {profileTotals.Verified}");
}

var reportPath = Path.GetFullPath($"legacy-import-{DateTime.Now:yyyyMMdd-HHmm}.csv");
await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8);
Console.WriteLine($"Users: {userTotals.Read} read, {userTotals.Created} created, {userTotals.Existing} already there");
Console.WriteLine($"Profiles: {profileTotals.Read} read, {profileTotals.Created} created, {profileTotals.Existing} enriched, {profileTotals.Verified} KYC verified");
Console.WriteLine($"Issues: {report.ToString().Count(c => c == '\n') - 1}, see {reportPath}");
return 0;

async IAsyncEnumerable<List<LegacyUser>> ReadUsers(Options o)
{
    const string sql = """
        SELECT TOP (@take) u.Id, u.PhoneNumber, u.UserName, u.RegisterDate, u.LastLoginDate, u.RequesterIp, u.IsBanned,
               (SELECT STRING_AGG(r.Name, ',') FROM idp.AspNetUserRoles ur JOIN idp.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = u.Id) AS Roles
        FROM idp.AspNetUsers u
        WHERE u.Id > @after
        ORDER BY u.Id
        """;

    await using var connection = new SqlConnection(o.IdpConnection);
    await connection.OpenAsync();
    var after = 0;
    while (true)
    {
        var batch = new List<LegacyUser>();
        await using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("@take", o.Batch);
            command.Parameters.AddWithValue("@after", after);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var phone = reader.IsDBNull(1) ? (reader.IsDBNull(2) ? null : reader.GetString(2)) : reader.GetString(1);
                var roles = reader.IsDBNull(7) ? [] : reader.GetString(7).Split(',', StringSplitOptions.RemoveEmptyEntries);
                batch.Add(new LegacyUser(
                    reader.GetInt32(0), phone, o.ToUtc(reader, 3), o.ToUtc(reader, 4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetBoolean(6), roles));
            }
        }

        if (batch.Count == 0) yield break;
        after = batch[^1].LegacyId;
        yield return batch;
    }
}

async IAsyncEnumerable<List<LegacyProfile>> ReadProfiles(Options o, IReadOnlyDictionary<int, DateTimeOffset?> registered)
{
    const string sql = """
        SELECT TOP (@take) u.Id, u.Phone, u.FirstName, u.LastName, u.FatherName, u.Gender, u.BirthDate, u.NationalCode, u.KycLevel,
               u.ZipCode, u.Address, u.UtmSource, u.UtmMedium, u.UtmCampaign, u.UtmTerm, u.UtmContent, u.TermsAccepted, u.TermsAcceptedAt,
               a.AffiliatedBy, a.DateTime
        FROM prf.Users u
        LEFT JOIN prf.Affiliations a ON a.UserId = u.Id
        WHERE u.Id > @after
        ORDER BY u.Id
        """;

    await using var connection = new SqlConnection(o.ProfileConnection);
    await connection.OpenAsync();
    var after = 0;
    while (true)
    {
        var batch = new List<LegacyProfile>();
        await using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("@take", o.Batch);
            command.Parameters.AddWithValue("@after", after);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetInt32(0);
                batch.Add(new LegacyProfile(
                    id,
                    Text(reader, 1),
                    registered.GetValueOrDefault(id),
                    Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5),
                    reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                    Text(reader, 7),
                    reader.GetInt32(8),
                    Text(reader, 9), Text(reader, 10),
                    Text(reader, 11), Text(reader, 12), Text(reader, 13), Text(reader, 14), Text(reader, 15),
                    reader.GetBoolean(16),
                    o.ToUtc(reader, 17),
                    reader.IsDBNull(18) ? null : reader.GetInt32(18),
                    o.ToUtc(reader, 19)));
            }
        }

        if (batch.Count == 0) yield break;
        after = batch[^1].LegacyId;
        yield return batch;
    }
}

static string? Text(SqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

internal sealed record Options(
    string IdpConnection,
    string ProfileConnection,
    string IamUrl,
    string ProfileUrl,
    string Token,
    string? RefreshToken,
    int Batch,
    bool DryRun,
    TimeZoneInfo SourceTimeZone)
{
    public static Options? Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            map[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        string? Get(string key) => map.GetValueOrDefault(key);
        if (Get("idp") is not { } idp || Get("profile") is not { } profile) return null;

        var dryRun = Get("dry-run") == "true";
        if (!dryRun && (Get("iam") is null || Get("userprofile") is null || Get("token") is null)) return null;

        return new Options(
            idp,
            profile,
            (Get("iam") ?? string.Empty).TrimEnd('/'),
            (Get("userprofile") ?? string.Empty).TrimEnd('/'),
            Get("token") ?? string.Empty,
            Get("refresh-token"),
            int.TryParse(Get("batch"), out var batch) ? Math.Clamp(batch, 1, 500) : 200,
            dryRun,
            TimeZoneInfo.FindSystemTimeZoneById(Get("source-timezone") ?? "UTC"));
    }

    /// <summary>Old columns are datetime2 without an offset; they are read in the source time zone.</summary>
    public DateTimeOffset? ToUtc(SqlDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;

        var value = reader.GetValue(index);
        if (value is DateTimeOffset offset) return offset.ToUniversalTime();

        var local = DateTime.SpecifyKind((DateTime)value, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, SourceTimeZone), TimeSpan.Zero);
    }
}

/// <summary>Calls the import endpoints, renewing the 15-minute access token with the refresh token when it expires.</summary>
internal sealed class Api(Options options, JsonSerializerOptions json) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private string _token = options.Token;
    private string? _refreshToken = options.RefreshToken;

    public async Task<T> PostAsync<T>(string url, object body)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: json) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await _http.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1 && _refreshToken is not null)
            {
                await RefreshAsync();
                continue;
            }

            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{url} returned {(int)response.StatusCode}: {text}");

            return JsonSerializer.Deserialize<T>(text, json) ?? throw new InvalidOperationException($"{url} returned an empty body.");
        }
    }

    private async Task RefreshAsync()
    {
        using var response = await _http.PostAsJsonAsync($"{options.IamUrl}/api/v1/auth/token/refresh", new { refreshToken = _refreshToken }, json);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Token refresh failed: {text}");

        var tokens = JsonSerializer.Deserialize<TokenPair>(text, json)!;
        _token = tokens.AccessToken;
        _refreshToken = tokens.RefreshToken;
        Console.WriteLine("  access token renewed");
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class Totals
{
    public int Read { get; set; }

    public int Created { get; set; }

    public int Existing { get; set; }

    public int Verified { get; set; }
}

internal sealed record TokenPair(string AccessToken, string RefreshToken);

internal sealed record LegacyUser(int LegacyId, string? PhoneNumber, DateTimeOffset? RegisteredAt, DateTimeOffset? LastLoginAt, string? RegistrationIp, bool IsBanned, IReadOnlyList<string> Roles);

internal sealed record LegacyProfile(
    int LegacyId, string? PhoneNumber, DateTimeOffset? RegisteredAt, string? FirstName, string? LastName, string? FatherName, string? Gender,
    DateTime? BirthDate, string? NationalCode, int KycLevel, string? ZipCode, string? Address, string? UtmSource, string? UtmMedium,
    string? UtmCampaign, string? UtmTerm, string? UtmContent, bool TermsAccepted, DateTimeOffset? TermsAcceptedAt, int? ReferredByLegacyId,
    DateTimeOffset? ReferredAt);

internal sealed record Issue(int LegacyId, string Reason);

internal sealed record UserImportResult(int Created, int AlreadyImported, IReadOnlyList<Issue> Issues);

internal sealed record ProfileImportResult(int Created, int Updated, int KycVerified, IReadOnlyList<Issue> Issues);
