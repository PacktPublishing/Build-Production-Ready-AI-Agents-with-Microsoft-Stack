// Dataverse access: sign in as yourself, read the book, write the referral.
//
// DEMONSTRATION CODE - written to be read rather than deployed. See Desk.cs for what a production
// system would add.
//
// Same two points as the Python track, and they are the point, not plumbing:
//   1. You sign in as YOU. No shared secret, no service account, so the agent's tools run with your
//      permissions. "What can your agent see?" answers itself: exactly what its caller can see.
//   2. The desk never changes the claims book. Everything it writes is a new row stamped with the
//      investigator's user name, so two investigations never overwrite each other.
//
// Sections, in the order the desk uses them: configuration, sign-in, HTTP, the book, the referral.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace FraudDesk;

public static class Dataverse
{
    // ----------------------------------------------------------------- configuration
    // Tenant and org come from `lab/.env` (see Env.cs) because they are environment-specific.
    // ClientId does not: it is the Azure CLI's public client, the same in every tenant, which is
    // why the sign-in page asks "Are you trying to sign in to Microsoft Azure CLI?".
    // Resolved on FIRST USE, not in a static initializer: `test` touches this class without ever
    // calling Dataverse, and an unedited lab/.env must not stop it. The error still arrives by
    // name the moment something needs a directory to sign in to.
    static (string Tenant, string Org)? _target;
    static (string Tenant, string Org) Target => _target ??= Env.DataverseTarget();
    public static string Org => Target.Org;
    const string ClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";   // Azure CLI public client
    static string Api => $"{Org}/api/data/v9.2";

    // The model endpoint's scope. A constant like ClientId, not config: it names Azure's
    // Cognitive Services API surface and is the same in every tenant.
    public const string FoundryScope = "https://cognitiveservices.azure.com/.default";

    // One HttpClient for the process: a new one per request exhausts sockets under the burst of
    // short-lived calls a hop makes.
    static readonly HttpClient Http = new();

    // ----------------------------------------------------------------- sign-in
    // Microsoft's identity library (Azure.Identity, built on MSAL) does the OAuth work: the
    // device-code flow, the token refresh and the token cache. Two files take part, and neither
    // holds a secret you can read:
    //
    //   .m3_token.json   beside the binary (bin/Debug/net8.0): WHO is signed in - tenant,
    //                    account, user name. No token. Delete it to sign out, or to sign in as
    //                    somebody else; the next command then asks you to run `login` again.
    //   the token cache  in your user profile, managed by the library. Encrypted by the operating
    //                    system where it can be (Windows, macOS, a Linux desktop with a keyring);
    //                    where it cannot, as in a Codespace, it falls back to a file only your
    //                    account can read. It holds a refresh token for the Azure CLI's client,
    //                    which can be exchanged for other Azure resources too, not only this claims
    //                    book - so treat your machine's account as the thing protecting it.
    static readonly string SessionFile = Path.Combine(AppContext.BaseDirectory, ".m3_token.json");
    static readonly TokenCachePersistenceOptions Cache = new()
    {
        Name = "claims365-fraud-desk-dotnet",
        UnsafeAllowUnencryptedStorage = true,
    };

    static DeviceCodeCredential? _credential;
    static string _user = "";

    /// <summary>What you see while signing in. The address and the code come from Microsoft Entra.</summary>
    static Task ShowCode(DeviceCodeInfo info, CancellationToken _)
    {
        Console.WriteLine(new string('=', 72));
        Console.WriteLine("  SIGN IN AS YOURSELF");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"  1. open {info.VerificationUri}");
        Console.WriteLine($"  2. enter code  {info.UserCode}");
        Console.WriteLine("  3. sign in with your own account, the one holding the Claims 365 Attendee role");
        Console.WriteLine(new string('=', 72));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Interactive, and needed once: until the cached session expires or you delete it. Device code
    /// is the flow that works everywhere this code runs: a Codespace has no browser to redirect to
    /// and cannot listen on localhost. `AuthenticateAsync` signs in and returns a record of WHO
    /// signed in; the tokens themselves go to the library's cache, never to this folder.
    /// </summary>
    public static async Task DeviceLoginAsync()
    {
        var (tenant, org) = Target;
        var cred = new DeviceCodeCredential(new DeviceCodeCredentialOptions
        {
            ClientId = ClientId,
            TenantId = tenant,
            DeviceCodeCallback = ShowCode,
            TokenCachePersistenceOptions = Cache,
        });
        AuthenticationRecord record;
        try
        {
            record = await cred.AuthenticateAsync(new TokenRequestContext(new[] { $"{org}/.default" }));
        }
        catch (AuthenticationFailedException ex)
        {
            throw new Exception("sign-in failed: " + FirstLine(ex));
        }
        using (var f = File.Create(SessionFile)) record.Serialize(f);
        _credential = null;                 // the next call builds the silent credential from the file
        Console.WriteLine("signed in. session saved next to the binary (gitignored); "
                        + "tokens kept in your user profile's token cache.");
    }

    /// <summary>Whether a session file exists. Cheap: it reads nothing and calls nobody.</summary>
    public static bool SignedIn => File.Exists(SessionFile);

    /// <summary>
    /// The signed-in credential for THIS process, built silently from the session file. The model
    /// client uses it too (see Desk.ChatClient), so one sign-in covers the data and the model.
    ///
    /// `DisableAutomaticAuthentication` is the important flag: a command that needs a token and
    /// cannot get one silently stops with a sentence, rather than starting a sign-in in the middle
    /// of an investigation.
    /// </summary>
    public static TokenCredential Credential()
    {
        if (_credential is not null) return _credential;
        if (!File.Exists(SessionFile))
            throw new Exception("not signed in. run:  dotnet run -- login");
        AuthenticationRecord? record = null;
        try
        {
            using var f = File.OpenRead(SessionFile);
            record = AuthenticationRecord.Deserialize(f);
        }
        catch (JsonException) { }
        if (record?.TenantId is null || record.Username is null)
            throw new Exception("the session file .m3_token.json is unreadable. Delete it and run:  "
                              + "dotnet run -- login");
        if (!string.Equals(record.TenantId, Target.Tenant, StringComparison.OrdinalIgnoreCase))
            throw new Exception("you are signed in to a different tenant from the one in lab/.env. "
                              + "Delete .m3_token.json and run:  dotnet run -- login");
        _credential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
        {
            ClientId = ClientId,
            TenantId = record.TenantId,
            AuthenticationRecord = record,
            TokenCachePersistenceOptions = Cache,
            DisableAutomaticAuthentication = true,
        });
        _user = record.Username;
        return _credential;
    }

    /// <summary>The first line of an identity error, which carries the AADSTS code and its meaning.</summary>
    static string FirstLine(Exception ex)
    {
        var line = ex.Message.Trim().Split('\n')[0].Trim();
        return line[..Math.Min(300, line.Length)];
    }

    /// <summary>A Dataverse token from the cached sign-in, refreshed by the library as needed.</summary>
    static async Task<string> TokenAsync()
    {
        try
        {
            var scope = new TokenRequestContext(new[] { $"{Org}/.default" });
            return (await Credential().GetTokenAsync(scope, default)).Token;
        }
        catch (AuthenticationRequiredException)
        {
            throw new Exception("session expired. run:  dotnet run -- login");
        }
        catch (AuthenticationFailedException ex)
        {
            // Kept, not deleted: a Conditional Access block or a missing consent is not fixed by
            // signing in again, and the AADSTS code in this line is what says which it is.
            throw new Exception("token request refused: " + FirstLine(ex));
        }
    }

    /// <summary>
    /// Your user name, from the session record. It stamps every referral row. Asks for a token
    /// first, so a command that starts with it stops early if the session has expired.
    /// </summary>
    public static async Task<string> WhoAmIAsync()
    {
        await TokenAsync();
        return _user;
    }

    // ----------------------------------------------------------------- HTTP
    // A string value made safe for an OData filter: quotes doubled, then percent-encoded. Two
    // parsers see this value, and each has a character that breaks it. OData escapes a single
    // quote by doubling it - a claimant called O'Neill would otherwise produce a syntax error
    // that reads like a permissions problem. The URL splits on & - a repairer called
    // Eccles Panel & Paint would otherwise truncate the query at the ampersand. Encoding here,
    // once, covers both.
    static string Esc(string v) => Uri.EscapeDataString(v.Replace("'", "''"));

    /// <summary>Build one Web API request with the bearer token and the OData headers.</summary>
    static async Task<HttpRequestMessage> RequestAsync(HttpMethod m, string path, string? body)
    {
        var req = new HttpRequestMessage(m, $"{Api}/{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        req.Headers.Add("OData-MaxVersion", "4.0");
        req.Headers.Add("OData-Version", "4.0");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return req;
    }

    // Throttled or briefly unavailable: the server says it did not do the work, so it is safe to
    // ask again, for a write as much as a read.
    static bool Transient(HttpStatusCode s) => (int)s is 429 or 502 or 503 or 504;

    /// <summary>
    /// How long to wait before retrying: the server's Retry-After when it gives one, else a backoff.
    /// Dataverse's service-protection limits answer 429 with a Retry-After header, and retrying
    /// sooner only extends the block.
    /// </summary>
    static TimeSpan RetryAfter(HttpResponseMessage resp, int attempt)
    {
        var ra = resp.Headers.RetryAfter;
        var wait = ra?.Delta ?? (ra?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(2 * attempt);
        return wait < TimeSpan.Zero ? TimeSpan.Zero
             : wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait;
    }

    /// <summary>
    /// One Web API call with the retry rule, returning the status and the body.
    ///
    /// A throttled or briefly unavailable service (429, 502, 503, 504) is retried for every method.
    /// A connection that failed is retried for reads only: a POST may have landed before the
    /// connection dropped, and repeating it would write a second row. A 400 or 403 comes straight
    /// back, since repeating it only delays the explanation.
    /// </summary>
    static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method, string path, string? body = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            // A new request per attempt: an HttpRequestMessage cannot be sent twice.
            using var req = await RequestAsync(method, path, body);
            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(req);
            }
            catch (HttpRequestException) when (method == HttpMethod.Get && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new Exception(
                    $"Dataverse unreachable: {ex.Message} Check your connection. A paused "
                    + "investigation is safe on disk; if this happened while writing a referral, "
                    + "look in Dataverse before writing it again.");
            }
            using (resp)
            {
                if (Transient(resp.StatusCode) && attempt < 4)
                {
                    await Task.Delay(RetryAfter(resp, attempt));
                    continue;
                }
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            }
        }
    }

    /// <summary>
    /// GET an OData path and return the rows. Throws on anything that is not a success: a tool that
    /// swallowed a 403 would report "nobody is connected to this claimant", and a wrong answer that
    /// looks right is the worst outcome here. The desk's tool middleware turns that exception into
    /// a failed investigation rather than a smaller one.
    ///
    /// One page only. Dataverse returns up to 5,000 rows a page and the claims book holds 234, so
    /// the desk never needs `@odata.nextLink`; a larger book would.
    /// </summary>
    public static async Task<List<JsonElement>> QueryAsync(string path)
    {
        var (status, raw) = await SendAsync(HttpMethod.Get, path);
        if ((int)status is < 200 or > 299)
            throw new Exception($"Dataverse read failed {(int)status}: " + raw[..Math.Min(300, raw.Length)]);
        return JsonDocument.Parse(raw).RootElement.GetProperty("value")
            .EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>Create a row, and throw if Dataverse did not.</summary>
    static async Task PostAsync(string set, object body)
    {
        var (status, raw) = await SendAsync(HttpMethod.Post, set, JsonSerializer.Serialize(body));
        if ((int)status is < 200 or > 299)
            throw new Exception($"write to {set} failed {(int)status}: " + raw[..Math.Min(300, raw.Length)]);
    }

    // ----------------------------------------------------------------- the book, read-only
    // Named once so every query returns the same shape and adding a column is one edit.
    const string ClaimCols = "cp_bookclaimref,cp_claimantref,cp_repairername,cp_claimtype," +
                             "cp_amountclaimed,cp_claimstatus,cp_incidentdate,cp_incidentpostcode";
    const string ClaimantCols = "cp_claimantref,cp_fullname,cp_email,cp_phone," +
                                "cp_bankaccount,cp_postcode";

    /// <summary>A string column, or "". Dataverse omits empty columns rather than returning
    /// null, so a plain GetString would throw on a sparse row.</summary>
    public static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : "";

    /// <summary>A numeric column, or 0 when absent. Same reason as <see cref="Str"/>.</summary>
    public static double Num(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : 0;

    /// <summary>One claim by reference, or null. Intake uses this to refuse a claim that is not real.</summary>
    public static async Task<JsonElement?> GetClaimAsync(string claimRef)
    {
        var rows = await QueryAsync(
            $"cp_bookclaims?$select={ClaimCols}&$filter=cp_bookclaimref eq '{Esc(claimRef)}'");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>One claimant by reference, or null. This is where the identifiers a ring shares live.</summary>
    public static async Task<JsonElement?> GetClaimantAsync(string claimantRef)
    {
        var rows = await QueryAsync(
            $"cp_claimants?$select={ClaimantCols}&$filter=cp_claimantref eq '{Esc(claimantRef)}'");
        return rows.Count > 0 ? rows[0] : null;
    }

    // Far above anything this book can produce. It exists because these values are chosen by a
    // MODEL, and asking about hundreds at once builds a URL past what Dataverse accepts, failing
    // with an error that never mentions length.
    const int MaxFilterTerms = 150;

    // A hop is a set operation. Doing it one person at a time costs a model round trip each, and
    // round trips are what an agent's bill is made of. These do a whole hop in one query per identifier type.

    /// <summary>
    /// `col eq 'a' or col eq 'b' or ...`, or "" when there is nothing to ask. Sorted and
    /// de-duplicated so the same hop always produces the same URL. Callers must treat "" as "ask
    /// nothing": a bare `$filter=` is a 400.
    /// </summary>
    static string OrFilter(string col, IEnumerable<string> values)
    {
        var uniq = values.Where(v => !string.IsNullOrEmpty(v)).Distinct().OrderBy(v => v).ToList();
        if (uniq.Count > MaxFilterTerms)
            throw new ArgumentException(
                $"asked about {uniq.Count} values in one query, more than the limit of "
                + $"{MaxFilterTerms}. A filter that long exceeds the URL length Dataverse accepts, "
                + "and fails with an error that does not mention length.");
        return string.Join(" or ", uniq.Select(v => $"{col} eq '{Esc(v)}'"));
    }

    /// <summary>Many claimants in one query. The first half of a hop: who are the people we have?</summary>
    public static async Task<List<JsonElement>> ClaimantsByRefsAsync(IEnumerable<string> refs)
    {
        var f = OrFilter("cp_claimantref", refs);
        return string.IsNullOrEmpty(f) ? new()
            : await QueryAsync($"cp_claimants?$select={ClaimantCols}&$filter={f}");
    }

    /// <summary>
    /// Everyone holding ANY of these values. The second half of a hop. The attribute names are the
    /// agent-facing vocabulary, so a tool description never teaches the model a Dataverse schema.
    /// </summary>
    public static async Task<List<JsonElement>> ClaimantsSharingAnyAsync(
        string attribute, IEnumerable<string> values)
    {
        var col = attribute switch
        {
            "bank_account" => "cp_bankaccount",
            "phone" => "cp_phone",
            "postcode" => "cp_postcode",
            _ => throw new ArgumentException("attribute must be bank_account, phone or postcode"),
        };
        var f = OrFilter(col, values);
        return string.IsNullOrEmpty(f) ? new()
            : await QueryAsync($"cp_claimants?$select={ClaimantCols}&$filter={f}");
    }

    /// <summary>Every claim belonging to any of these claimants. The exposure figure is summed from
    /// these rows, in C#, never by a model.</summary>
    public static async Task<List<JsonElement>> ClaimsOfManyAsync(IEnumerable<string> refs)
    {
        var f = OrFilter("cp_claimantref", refs);
        return string.IsNullOrEmpty(f) ? new()
            : await QueryAsync($"cp_bookclaims?$select={ClaimCols}&$filter={f}");
    }

    /// <summary>Every claim at one repairer. Joined by NAME, so no relationship metadata is
    /// needed.</summary>
    public static async Task<List<JsonElement>> ClaimsByRepairerAsync(string repairer) =>
        await QueryAsync(
            $"cp_bookclaims?$select={ClaimCols}&$filter=cp_repairername eq '{Esc(repairer)}'");

    // ----------------------------------------------------------------- the referral
    // The only writes. Each is a NEW row, so nobody overwrites anybody.

    /// <summary>A UTC timestamp identifying one investigation, scoped to one person by the
    /// referral reference.</summary>
    public static string NewRunId() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

    /// <summary>
    /// Write the referral header: one row per investigation. `investigator` is the account that
    /// STARTED the investigation, carried through the checkpoint, which may differ from the account
    /// that approved it. `approvedBy` is the name typed with --by. Call it with named arguments: a
    /// dozen parameters, several of them integers, is the shape where a transposition silently
    /// writes the ring size into the claim count.
    /// </summary>
    public static async Task WriteReferralAsync(
        string referralRef, string seedClaim, string decision, int ringSize, int claimCount,
        double exposure, int hops, string investigator, string approvedBy, string evidence,
        string language, string runId)
    {
        await PostAsync("cp_fraudreferrals", new Dictionary<string, object?>
        {
            ["cp_referralref"] = referralRef,
            ["cp_seedclaimref"] = seedClaim,
            ["cp_decision"] = decision,
            ["cp_ringsize"] = ringSize,
            ["cp_claimcount"] = claimCount,
            ["cp_exposure"] = exposure,
            ["cp_hops"] = hops,
            ["cp_investigator"] = investigator,
            ["cp_approvedby"] = approvedBy,
            ["cp_language"] = language,
            ["cp_runid"] = runId,
            ["cp_evidence"] = evidence.Length > 3999 ? evidence[..3999] : evidence,
        });
    }

    /// <summary>
    /// One evidence line: this claim is in this referral, and why. These make the referral
    /// auditable rather than a summary row with a number on it.
    /// </summary>
    public static async Task WriteReferralClaimAsync(
        string referralRef, string claimRef, string claimantRef, string reason, string strength)
    {
        var id = $"{referralRef}|{claimRef}";
        await PostAsync("cp_referralclaims", new Dictionary<string, object?>
        {
            ["cp_referralclaimref"] = id.Length > 139 ? id[..139] : id,
            ["cp_referralref"] = referralRef,
            ["cp_bookclaimref"] = claimRef,
            ["cp_claimantref"] = claimantRef,
            ["cp_linkreason"] = reason.Length > 199 ? reason[..199] : reason,
            ["cp_linkstrength"] = strength,
        });
    }

    /// <summary>
    /// Every referral YOU wrote, newest first. The payoff of stamping the UPN: "mine" is a filter,
    /// not a race. Everyone can read everyone's rows and nobody can change them.
    /// </summary>
    public static async Task<List<JsonElement>> MyReferralsAsync()
    {
        var me = Esc(await WhoAmIAsync());
        return await QueryAsync(
            "cp_fraudreferrals?$select=cp_referralref,cp_decision,cp_ringsize,cp_claimcount," +
            "cp_exposure,cp_hops,cp_approvedby,cp_language,cp_runid" +
            $"&$filter=cp_investigator eq '{me}'&$orderby=createdon desc");
    }
}
