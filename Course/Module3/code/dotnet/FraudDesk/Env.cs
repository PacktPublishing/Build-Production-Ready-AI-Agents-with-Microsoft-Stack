// Configuration. The C# twin of `m3_env.py`.
//
// DEMONSTRATION CODE - written to be read rather than deployed.
//
// Everything environment-specific comes from `lab/.env`, the same file the Python track reads.
// Nothing here is a secret in source.
namespace FraudDesk;

public static class Env
{
    static Dictionary<string, string>? _cache;
    static bool _found;                     // was a file located at all, as opposed to an empty one

    /// <summary>
    /// Parse `lab/.env` into a dictionary, searching upward from the binary rather than the working
    /// directory, which depends on where <c>dotnet run</c> was invoked.
    ///
    /// Returns an EMPTY dictionary when there is no file: only the values without a default treat
    /// that as fatal, so the decision to throw belongs to the caller.
    /// </summary>
    static Dictionary<string, string> Values()
    {
        if (_cache is not null) return _cache;
        _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        FileInfo? envFile = null;
        while (dir is not null && envFile is null)
        {
            var candidate = Path.Combine(dir.FullName, "lab", ".env");
            if (File.Exists(candidate)) envFile = new FileInfo(candidate);
            dir = dir.Parent;
        }
        if (envFile is null) return _cache;
        _found = true;

        foreach (var line in File.ReadAllLines(envFile.FullName))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#') || !t.Contains('=')) continue;
            var i = t.IndexOf('=');
            _cache[t[..i].Trim()] = Unquote(t[(i + 1)..]);
        }
        return _cache;
    }

    /// <summary>
    /// The value without one matching pair of surrounding quotes, as many .env files write them. The
    /// same rule as the Python track's readers, so every reader of lab/.env sees the same value.
    /// </summary>
    static string Unquote(string value)
    {
        var v = value.Trim();
        return v.Length >= 2 && v[0] == v[^1] && (v[0] == '"' || v[0] == '\'') ? v[1..^1] : v;
    }

    /// <summary>
    /// One setting: the process environment first, then `lab/.env`, then the supplied default.
    /// Environment first so a shell variable can override the file for a single run.
    /// </summary>
    static string Setting(string name, string fallback)
    {
        var fromProcess = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(fromProcess)) return fromProcess;
        var value = Values().TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v : fallback;
        // A value still wrapped in <angle brackets> is the sample's placeholder: treat it as
        // UNSET. Half-editing the file is the common mistake, and this is what turns it into a
        // named error.
        return value.Contains('<') && value.Contains('>') ? "" : value;
    }

    /// <summary>
    /// Which tenant you sign in to, and which environment you read.
    ///
    /// THROWS on purpose when either is still the sample's placeholder. There is no sensible
    /// default for "which directory am I": guessing one signs you in to a tenant you are not a
    /// member of, and Entra's answer (AADSTS50020) names the tenant rather than the setting, so
    /// the mistake reads as a broken lab rather than an unedited config file.
    ///
    /// The trailing slash is trimmed - a pasted URL often carries one, and
    /// <c>{org}//api/data/v9.2</c> 404s without mentioning it.
    /// </summary>
    public static (string tenant, string org) DataverseTarget()
    {
        var tenant = Setting("DATAVERSE_TENANT", "");
        var org = Setting("DATAVERSE_ORG", "");
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(tenant)) missing.Add("DATAVERSE_TENANT");
        if (string.IsNullOrWhiteSpace(org)) missing.Add("DATAVERSE_ORG");
        if (missing.Count > 0)
            throw new Exception(
                (_found ? "lab/.env still holds the sample's <placeholder> for, or is missing, "
                        : "lab/.env was not found, so nothing sets ")
                + string.Join(", ", missing) + ".\n"
                + "  From the repository root:  cp lab/.env.sample lab/.env\n"
                + "  Then put your own tenant id and Dataverse org URL there, angle brackets removed.\n"
                + "  The tenant id is on the Entra ID overview page; the org URL is your\n"
                + "  environment's, ending .crm<n>.dynamics.com.\n"
                + "  Chapter 1 of the book says where both come from.");
        return (tenant, org.TrimEnd('/'));
    }

    /// <summary>
    /// The Foundry endpoint and deployment, plus FOUNDRY_KEY when a real one is set.
    ///
    /// The key is OPTIONAL and normally empty: with no key, the desk uses your sign-in for its
    /// model calls too (see <see cref="Desk.ChatClient"/>), so there is no key to hand out and
    /// nothing shared. A real value - your own resource's key, or a token you minted yourself -
    /// overrides that. The sample's &lt;placeholder&gt; counts as unset, so an untouched lab/.env
    /// is keyless.
    /// </summary>
    public static (string endpoint, string key, string model) Foundry()
    {
        var endpoint = Setting("FOUNDRY_OPENAI_V1", "");
        var model = Setting("MODEL_DEPLOYMENT", "");
        var missing = new List<string>();
        if (endpoint.Length == 0) missing.Add("FOUNDRY_OPENAI_V1");
        if (model.Length == 0) missing.Add("MODEL_DEPLOYMENT");
        if (missing.Count > 0)
            throw new Exception(
                (_found ? "lab/.env still holds a <placeholder> for, or is missing, "
                        : "lab/.env was not found, so nothing sets ")
                + string.Join(", ", missing) + ".\n"
                + "  From the repository root:  cp lab/.env.sample lab/.env\n"
                + "  Then replace the placeholders with your own Foundry account and deployment -\n"
                + "  Chapter 1 of the book, Creating the Microsoft Foundry resources, says where\n"
                + "  they come from.");
        return (endpoint, Setting("FOUNDRY_KEY", ""), model);
    }
}
