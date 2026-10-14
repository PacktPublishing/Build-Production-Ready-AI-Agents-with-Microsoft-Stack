// Two small pieces the desk needs: somewhere to catch the workflow's output, and the pending-request
// copy that lets `status` read from disk in a fresh process.
//
// DEMONSTRATION CODE - written to be read rather than deployed.
using System.Text.Json;

namespace FraudDesk;

/// <summary>
/// Where the workflow's final output lands, so the CLI can print it. A static is acceptable only
/// because each command is a whole process running one investigation.
/// </summary>
public static class Outcomes
{
    public static FraudOutcome? Last { get; set; }
}

/// <summary>
/// What is waiting for a human, written beside the checkpoints so `status` in a NEW process can
/// report it without replaying the workflow. A readable COPY: the checkpoint holds the real
/// request, and `approve` answers the request the checkpoint re-emits. `approve` reads the copy
/// first, to know there is something to answer before it restores anything.
/// </summary>
public static class Pending
{
    public static string PathIn(string dir) => Path.Combine(dir, "pending.json");

    public static void Write(string dir, ReferralApprovalRequest req) =>
        File.WriteAllText(PathIn(dir), JsonSerializer.Serialize(req));

    /// <summary>The pending request, or null when the desk is not waiting on anybody.</summary>
    public static ReferralApprovalRequest? Read(string dir)
    {
        var f = PathIn(dir);
        return File.Exists(f)
            ? JsonSerializer.Deserialize<ReferralApprovalRequest>(File.ReadAllText(f))
            : null;
    }

    /// <summary>Forget the pending request. Called when the workflow produces its outcome, so that
    /// `status` says "finished" rather than describing a gate that has already been answered.</summary>
    public static void Clear(string dir)
    {
        var f = PathIn(dir);
        if (File.Exists(f)) File.Delete(f);
    }
}
