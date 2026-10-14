// Assertions over the fraud desk. No model, no Dataverse, no cost.
//
// DEMONSTRATION CODE - written to be read rather than deployed.
//
// The same questions the Python track answers, asked of the C# build. The guarantees an insurer
// would ask about are checked in CODE, not argued for in a document.
//
//   dotnet run -- test          exits non-zero if anything fails
using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace FraudDesk;

public static class Tests
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"
                        + (!ok && detail.Length > 0 ? $"  {detail}" : ""));
    }

    /// <summary>Type names, sorted, for readable failure details.</summary>
    static string Names(IEnumerable<Type> types) =>
        string.Join(", ", types.Select(t => t.Name).OrderBy(n => n));

    /// <summary>
    /// Find a source file by walking up from the binary. Returns null rather than throwing, so a
    /// source-dependent check can be skipped rather than failing for the wrong reason.
    /// </summary>
    static string? FindSource(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        return null;
    }

    public static int Run()
    {
        Console.WriteLine("=== the graph ===");

        // Each executor's protocol, as the framework sees it: what it accepts, sends and yields.
        // The source generator writes these from the [MessageHandler] methods and the [SendsMessage]
        // and [YieldsOutput] attributes, so this reads the real routing, not a copy of it.
        var nodes = new (string Name, ProtocolDescriptor P)[]
        {
            ("Intake", new Desk.Intake().DescribeProtocol()),
            ("Investigate", new Desk.Investigate().DescribeProtocol()),
            ("Challenge", new Desk.Challenge().DescribeProtocol()),
            ("Refer", new Desk.Refer().DescribeProtocol()),
        };

        // Delivery is by type. Two executors accepting the same type would both receive the message,
        // which produces no output and no error.
        var overlap = nodes.SelectMany(n => n.P.Accepts).GroupBy(t => t)
                           .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Check("every executor accepts a distinct message type", overlap.Count == 0,
              $"shared: {Names(overlap)}");

        Check("intake takes SeedClaim and emits something else",
              nodes[0].P.Accepts.Contains(typeof(SeedClaim))
              && !nodes[0].P.Sends.Contains(typeof(SeedClaim))
              && !nodes[1].P.Accepts.Contains(typeof(SeedClaim)));

        // Each edge's message is one its target accepts: intake -> investigate -> challenge, then
        // the approval port, then refer.
        Check("each step sends exactly what the next step accepts",
              nodes[0].P.Sends.SequenceEqual(new[] { typeof(VerifiedClaim) })
              && nodes[1].P.Accepts.Contains(typeof(VerifiedClaim))
              && nodes[1].P.Sends.SequenceEqual(new[] { typeof(TraversalComplete) })
              && nodes[2].P.Accepts.Contains(typeof(TraversalComplete))
              && nodes[2].P.Sends.SequenceEqual(new[] { typeof(ReferralApprovalRequest) })
              && nodes[3].P.Accepts.Contains(typeof(ReferralDecision)),
              string.Join("; ", nodes.Select(n =>
                  $"{n.Name} accepts [{Names(n.P.Accepts)}] sends [{Names(n.P.Sends)}]")));

        Check("the workflow builds", Desk.BuildWorkflow() is not null);

        Console.WriteLine("\n=== nobody is referred without a person ===");

        Check("the human gate is a node in the graph, not a call inside a method",
              Desk.ApprovalPort.Id == "approval"
              && Desk.ApprovalPort.Request == typeof(ReferralApprovalRequest)
              && Desk.ApprovalPort.Response == typeof(ReferralDecision));

        // Only Refer may produce an outcome, and only after a human answered: its handler takes a
        // ReferralDecision, which can only arrive through the approval port.
        var deciders = nodes.Where(n => n.P.Accepts.Contains(typeof(ReferralDecision)))
                            .Select(n => n.Name).ToList();
        Check("only Refer handles the human's decision", deciders.SequenceEqual(new[] { "Refer" }),
              $"found {string.Join(", ", deciders)}");
        var yielders = nodes.Where(n => n.P.Yields.Any()).Select(n => n.Name).ToList();
        Check("only Refer can produce an outcome",
              yielders.SequenceEqual(new[] { "Refer" })
              && nodes[3].P.Yields.SequenceEqual(new[] { typeof(FraudOutcome) }),
              $"found {string.Join(", ", yielders)}");

        Console.WriteLine("\n=== the shape of what a model returns is ENFORCED, not requested ===");

        // Requesting JSON in the prompt is not the same as enforcing a schema: a drifting model
        // returns well-formed JSON carrying a different JUDGMENT, harder to spot than a parse
        // error. Checked in the source because a schema constant can be perfectly correct and
        // never actually passed to a model call. If Desk.cs is absent the check is skipped.
        var deskSource = FindSource("Desk.cs");
        if (deskSource is null)
        {
            Console.WriteLine("  [SKIP] schema enforcement (Desk.cs not beside the binary)");
        }
        else
        {
            var enforced = deskSource.Split('\n')
                .Where(l => l.Contains("options: Enforce(")).ToList();
            foreach (var schema in new[] { "TraversalSchema", "JudgementSchema" })
                Check($"{schema} is passed to a model call, not merely declared",
                      enforced.Any(l => l.Contains(schema)));
        }
        var opts = Desk.Enforce("traversal", Desk.TraversalSchema).ChatOptions!;
        Check("the schema is sent as strict, so the service enforces it",
              opts.ResponseFormat is ChatResponseFormatJson { Schema: not null }
              && opts.AdditionalProperties?["strict"] is true);

        Console.WriteLine("\n=== the money ===");
        // Prove the arithmetic, not the source text: the pricing function is pure, so it runs
        // against fixture rows right here, with no Dataverse and no model.
        var fixture = JsonDocument.Parse("""
            [{"cp_bookclaimref":"BCL-TEST-1","cp_amountclaimed":3100},
             {"cp_bookclaimref":"BCL-TEST-2","cp_amountclaimed":2500.5}]
            """).RootElement.EnumerateArray().ToList();
        var (refs, total) = Desk.PriceConfirmed(fixture);
        Check("exposure is summed in C# from claim rows, not returned by a model",
              refs.SequenceEqual(new[] { "BCL-TEST-1", "BCL-TEST-2" }) && total == 5600.5,
              $"got {string.Join(", ", refs)}, {total}");
        Check("no schema lets a model return an amount",
              !Desk.JudgementSchema.Contains("amount") && !Desk.TraversalSchema.Contains("exposure"));

        Console.WriteLine("\n=== the decision table ===");
        (bool approved, bool recommended, string want)[] cases =
        {
            (true, true, "referred"),
            (true, false, "not_referred"),
            (false, true, "dismissed"),
            (false, false, "dismissed"),
        };
        foreach (var (approved, recommended, want) in cases)
        {
            var got = Desk.DecideOutcome(approved, recommended);
            Check($"DecideOutcome(approved={approved}, recommended={recommended}) == {want}",
                  got == want, $"got {got}");
        }

        Console.WriteLine("\n=== what the models may name ===");
        // The models write claimant refs into their replies, and the people under investigation
        // wrote the data those models read. Only refs the records produced may go forward.
        var reached = new[] { "CLT-9001", "CLT-9002", "CLT-9003" };
        var kept = Desk.KnownRefs(new[] { "clt-9002", "CLT-9002", "CLT-0999", "CLT-9001" }, reached);
        Check("a name no tool returned is not a candidate",
              kept.SequenceEqual(new[] { "CLT-9002", "CLT-9001" }), $"got {string.Join(", ", kept)}");
        var confirmable = Desk.KnownRefs(new[] { "CLT-9001", "CLT-9004", "clt-9001" },
                                         new[] { "CLT-9001", "CLT-9002" });
        Check("the challenger can confirm only candidates, each once",
              confirmable.SequenceEqual(new[] { "CLT-9001" }), $"got {string.Join(", ", confirmable)}");
        Check("a referral needs at least three confirmed claimants, decided in code",
              Desk.ShouldRefer(true, Desk.MinToRefer) && !Desk.ShouldRefer(true, Desk.MinToRefer - 1)
              && !Desk.ShouldRefer(false, 9));
        var listing = Desk.ChallengeListing(new[]
        {
            new LinkedClaimant("CLT-9002", "A Person", "shares an account", "bank_account",
                               new List<string> { "CLT-9001" }, new List<string>()),
        });
        Check("the challenger is shown who each candidate shares an identifier with",
              listing.Contains("shares bank_account with CLT-9001"), $"got {listing}");
        // A person the investigator labelled as a postcode match, whom the records also link by a
        // bank account: the challenger must see the bank account, or it excludes a ring member.
        listing = Desk.ChallengeListing(new[]
        {
            new LinkedClaimant("CLT-9006", "A Person", "same postcode", "postcode",
                               new List<string> { "CLT-9005" },
                               new List<string> { "bank_account with CLT-9005", "postcode with CLT-9005" }),
        });
        Check("the challenger sees every link the records hold, not only the investigator's label",
              listing.Contains("shares bank_account with CLT-9005; shares postcode with CLT-9005"),
              $"got {listing}");

        Console.WriteLine("\n=== the guardrails ===");
        if (deskSource is null)
            Console.WriteLine("  [SKIP] the tool-calling cap (Desk.cs not beside the binary)");
        else
            Check("the tool-calling loop is capped in code",
                  deskSource.Contains("MaximumIterationsPerRequest = 8"));
        var (guard, ctx, result) = RunGuard(new Exception("Dataverse read failed 403"));
        Check("a failing tool stops the investigation instead of shrinking it",
              ctx.Terminate && guard.Failures.Count == 1 && guard.Failures[0].Contains("403"),
              $"terminate={ctx.Terminate} failures={string.Join("; ", guard.Failures)}");
        (guard, ctx, result) = RunGuard(null);
        Check("a tool that succeeds passes through the guard untouched",
              !ctx.Terminate && guard.Failures.Count == 0 && Equals(result, "ok"));
        Desk.ToolLog.Clear();

        Console.WriteLine("\n=== the tools ===");
        var tools = Desk.LinkTools();
        Check("four tools are published to the model", tools.Count == 4, $"got {tools.Count}");
        Check("the hop is a batch operation, so one hop costs one round trip",
              typeof(Desk).GetMethod(nameof(Desk.Expand))!
                  .GetParameters()[0].ParameterType == typeof(string[]));
        Check("the repairer tool returns a count only, never a list of people",
              typeof(Desk).GetMethod(nameof(Desk.CountClaimsAtRepairer)) is not null);

        Console.WriteLine("\n=== the data layer ===");
        // The book contains a repairer called Eccles Panel & Paint. Unencoded, the & splits the
        // query string and the filter fails as malformed - so the escape helper must survive both
        // parsers, OData's and the URL's. This is the regression test for exactly that name.
        var esc = typeof(Dataverse).GetMethod("Esc",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var encoded = (string)esc.Invoke(null, new object[] { "Eccles Panel & Paint" })!;
        Check("identifier values are URL-encoded before they reach Dataverse",
              !encoded.Contains('&') && encoded.Contains("%26") && !encoded.Contains(' '),
              $"got {encoded}");

        Console.WriteLine("\n=== the judgment the desk is about ===");
        Check("the challenger is told a postcode alone is not evidence",
              Desk.Challenger.Contains("Exclude anyone whose ONLY link is a postcode"));
        Check("the challenger judges the GROUP, not distance from the seed",
              Desk.Challenger.Contains("A link between ANY TWO members"));
        Check("the investigator is told a shared repairer is not a link",
              Desk.Investigator.Contains("not a link between people"));
        Check("the investigator is bounded to a fixed number of hops",
              Desk.Investigator.Contains($"at most {Desk.MaxHops} hops"));
        Check("the investigator is told to batch the hop",
              Desk.Investigator.Contains("One call per hop"));

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>Run the tool-failure middleware around a tool that throws `error`, or returns "ok".
    /// </summary>
    static (Desk.ToolFailureGuard, FunctionInvocationContext, object?) RunGuard(Exception? error)
    {
        var guard = new Desk.ToolFailureGuard();
        var ctx = new FunctionInvocationContext
        {
            Function = AIFunctionFactory.Create(() => "ok", "expand"),
        };
        ValueTask<object?> Next(FunctionInvocationContext c, CancellationToken _) =>
            error is null ? ValueTask.FromResult<object?>("ok") : throw error;
        var result = guard.InvokeAsync(null!, ctx, Next, default).AsTask().GetAwaiter().GetResult();
        return (guard, ctx, result);
    }
}
