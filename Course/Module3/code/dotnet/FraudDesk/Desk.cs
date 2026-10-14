// Claims 365 Fraud Desk (Microsoft Agent Framework, .NET).
//
// =======================================================================================
// DEMONSTRATION CODE for a fictional insurer, written to be READ: it shows how a workflow,
// agents, tools and a human approval gate fit together, in as few lines as the idea allows.
//
// It is NOT production code. A real fraud desk would add secret management, idempotent
// writes, monitoring and alerting on the telemetry (--telemetry emits it; nothing here
// watches it), data protection controls, and ongoing evaluation of the model's judgments.
// Take the shape away with you, not the file.
// =======================================================================================
//
// The same single artifact as the Python track: a WORKFLOW whose nodes are AGENTS WITH TOOLS, which
// is Microsoft's own recommended production shape.
//
//     "A workflow defines the high-level process, and individual executors within that workflow use
//      agents for the steps that benefit from LLM reasoning."
//     https://learn.microsoft.com/agent-framework/journey/workflows
//
//     intake -> investigate -> challenge -> [approval port] -> refer
//     (code)   (agent+tools)  (agent)      (a human)          (code + Dataverse)
//
// EVERY EXECUTOR IS A `partial` CLASS DERIVING FROM `Executor`. Its handlers are methods marked
// [MessageHandler], and the Microsoft.Agents.AI.Workflows.Generators package reads them at compile
// time and writes the executor's routing, which is why the class must be partial. [SendsMessage] and
// [YieldsOutput] on the class declare what it may emit, so the whole protocol of a node is visible
// in its declaration and testable through `DescribeProtocol()`.
//
// ONE REAL DIFFERENCE FROM PYTHON: .NET makes the human gate a NODE, `RequestPort.Create<TReq,
// TResp>(id)`, with edges in and out. So this graph has five nodes to Python's four, and the gate
// is visible in the topology rather than inside a method.
using System.ClientModel;
using System.ClientModel.Primitives;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenAI;

namespace FraudDesk;

public static partial class Desk
{
    public const int MaxHops = 3;
    // The fewest confirmed claimants the desk will refer. The challenger's instructions say the
    // same, and the code enforces it: a rule belongs in code, where it cannot be argued with.
    public const int MinToRefer = 3;
    public const string Language = "dotnet";
    public const string WorkflowName = "claims365-fraud-desk";

    // Python's `ctx.set_state` is shared WORKFLOW state. .NET's `QueueStateUpdateAsync` writes to
    // the CALLING EXECUTOR'S scope by default, so anything handed between executors must name an
    // explicit scope - otherwise the reader finds nothing, and only after a resume.
    public const string Shared = "desk";

    // Every tool call the MODEL chose to make, recorded so the run can print it. "The agent decided
    // to look something up" is a claim you should be able to check rather than take on trust.
    public static readonly List<string> ToolLog = new();

    // Every claimant ref a tool actually returned in this investigation. The investigator may only
    // report people in this set, so a name that appears in no record cannot become a candidate.
    public static readonly HashSet<string> Reached = new(StringComparer.OrdinalIgnoreCase);

    // Every link `Expand` found, both ways round: claimant ref -> identifier -> the refs it is shared
    // with. The challenge step is shown these, from the records, rather than the one link type the
    // investigator chose to report for each person.
    public static readonly Dictionary<string, Dictionary<string, HashSet<string>>> Links =
        new(StringComparer.OrdinalIgnoreCase);

    static void RecordLink(string from, string attribute, string to)
    {
        if (!Links.TryGetValue(from, out var byAttr)) Links[from] = byAttr = new();
        if (!byAttr.TryGetValue(attribute, out var refs)) byAttr[attribute] = refs = new();
        refs.Add(to);
    }

    // ----------------------------------------------------------------- the tools
    //
    // The [Description] on each tool is PROMPT, not documentation: it is what the model reads when
    // deciding whether to call one, so rewording it changes the agent's behavior.
    //
    // WHY `Expand` TAKES AN ARRAY. An agent's cost is dominated by ROUND TRIPS, not tool calls,
    // because every trip re-sends the whole conversation. A hop is a set operation, so batching it
    // into one call is several times cheaper for the same answer - and the model still decides
    // whether to hop, who to expand and when to stop.
    /// <summary>
    /// ONE HOP. Given everyone found so far, return everyone newly connected and say how.
    ///
    /// Up to four queries whatever the size of the set: one to read the claimants passed in, then
    /// one per identifier type. `shared_with` records WHICH known person each new one connects to,
    /// because the challenge step judges links between members.
    ///
    /// A bad argument comes back as an error object, which the model can read and correct. A failed
    /// query throws, and <see cref="ToolFailureGuard"/> stops the investigation rather than letting
    /// it carry on with less data than it thinks it has.
    /// </summary>
    [Description("Follow every shared identifier out from a set of claimants, in one step. Give it "
               + "every claimant you know about; it returns who else shares a bank account, phone "
               + "or postcode with any of them, and says which. This is one hop.")]
    public static async Task<string> Expand(
        [Description("every claimant reference found so far")] string[] claimantRefs)
    {
        ToolLog.Add($"expand({claimantRefs.Length} claimant(s))");
        var known = await Dataverse.ClaimantsByRefsAsync(claimantRefs);
        if (known.Count == 0)
            return JsonSerializer.Serialize(new { error = "none of those claimant refs exist" });
        Reached.UnionWith(known.Select(k => Dataverse.Str(k, "cp_claimantref")));

        var byAttr = new Dictionary<string, string>
        {
            ["bank_account"] = "cp_bankaccount",
            ["phone"] = "cp_phone",
            ["postcode"] = "cp_postcode",
        };
        var seen = known.Select(k => Dataverse.Str(k, "cp_claimantref")).ToHashSet();
        var found = new Dictionary<string, Dictionary<string, object>>();

        foreach (var (attr, col) in byAttr)
        {
            var values = known.Select(k => Dataverse.Str(k, col)).Where(v => v != "").Distinct();
            foreach (var row in await Dataverse.ClaimantsSharingAnyAsync(attr, values))
            {
                var reference = Dataverse.Str(row, "cp_claimantref");
                if (seen.Contains(reference)) continue;
                var value = Dataverse.Str(row, col);
                var sharedWith = known.Where(k => Dataverse.Str(k, col) == value)
                                      .Select(k => Dataverse.Str(k, "cp_claimantref")).ToList();
                if (!found.TryGetValue(reference, out var entry))
                {
                    entry = new Dictionary<string, object>
                    {
                        ["claimant_ref"] = reference,
                        ["full_name"] = Dataverse.Str(row, "cp_fullname"),
                        ["links"] = new List<object>(),
                    };
                    found[reference] = entry;
                }
                ((List<object>)entry["links"]).Add(
                    new { attribute = attr, value, shared_with = sharedWith });
                foreach (var other in sharedWith)
                {
                    RecordLink(reference, attr, other);
                    RecordLink(other, attr, reference);
                }
            }
        }
        Reached.UnionWith(found.Keys);
        return JsonSerializer.Serialize(new
        {
            queried = known.Count,
            new_claimants = found.Values.ToList(),
            new_count = found.Count,
        });
    }

    /// <summary>Identifiers for people the model already knows about, without hopping.</summary>
    [Description("Identifiers held by a set of claimants: bank account, phone, postcode.")]
    public static async Task<string> GetClaimants(string[] claimantRefs)
    {
        ToolLog.Add($"get_claimants({claimantRefs.Length})");
        var rows = await Dataverse.ClaimantsByRefsAsync(claimantRefs);
        Reached.UnionWith(rows.Select(c => Dataverse.Str(c, "cp_claimantref")));
        return JsonSerializer.Serialize(new
        {
            claimants = rows.Select(c => new
            {
                claimant_ref = Dataverse.Str(c, "cp_claimantref"),
                full_name = Dataverse.Str(c, "cp_fullname"),
                bank_account = Dataverse.Str(c, "cp_bankaccount"),
                phone = Dataverse.Str(c, "cp_phone"),
                postcode = Dataverse.Str(c, "cp_postcode"),
            }),
        });
    }

    /// <summary>
    /// The claims behind a set of claimants, so the model can see scale. It may quote these
    /// figures, which is harmless: the number a human sees is summed from the same rows in C#.
    /// </summary>
    [Description("Claims belonging to a set of claimants.")]
    public static async Task<string> ListClaimsFor(string[] claimantRefs)
    {
        ToolLog.Add($"list_claims_for({claimantRefs.Length})");
        var rows = await Dataverse.ClaimsOfManyAsync(claimantRefs);
        return JsonSerializer.Serialize(new
        {
            count = rows.Count,
            claims = rows.Select(r => new
            {
                claim_ref = Dataverse.Str(r, "cp_bookclaimref"),
                claimant_ref = Dataverse.Str(r, "cp_claimantref"),
                repairer = Dataverse.Str(r, "cp_repairername"),
                amount_gbp = Dataverse.Num(r, "cp_amountclaimed"),
            }),
        });
    }

    /// <summary>
    /// A COUNT, deliberately. The seed claim's repairer has 42 claims across 36 people whose only
    /// connection is a garage; returning that list would drown the traversal. What a tool REFUSES
    /// to return shapes behavior as much as what it returns.
    /// </summary>
    [Description("How many claims a repairer handled. A COUNT only, because a shared repairer is "
               + "not a link between people.")]
    public static async Task<string> CountClaimsAtRepairer(string repairerName)
    {
        ToolLog.Add($"count_claims_at_repairer('{repairerName}')");
        var rows = await Dataverse.ClaimsByRepairerAsync(repairerName);
        return JsonSerializer.Serialize(new
        {
            repairer = repairerName,
            claim_count = rows.Count,
            distinct_claimants = rows.Select(r => Dataverse.Str(r, "cp_claimantref"))
                                     .Distinct().Count(),
        });
    }

    /// <summary>
    /// The four tools published to the investigator, with the names the model sees. The explicit
    /// second argument matters: without it each tool is named after its C# method.
    ///
    /// UNLIKE PYTHON, THERE IS NO PER-TOOL CALL CEILING HERE - `AIFunctionFactory` has no
    /// equivalent of `max_invocations`. The enforced bound lives in `ChatClient()` instead: the
    /// function-invocation pipeline caps iterations per request at 8. (A workflow-level cap like
    /// Python's `max_iterations` is not reachable in 1.17.0 - `InProcessExecutionOptions` is
    /// internal - so that half is documented rather than attempted.)
    /// </summary>
    public static IList<AITool> LinkTools() => new List<AITool>
    {
        AIFunctionFactory.Create(Expand, "expand"),
        AIFunctionFactory.Create(GetClaimants, "get_claimants"),
        AIFunctionFactory.Create(ListClaimsFor, "list_claims_for"),
        AIFunctionFactory.Create(CountClaimsAtRepairer, "count_claims_at_repairer"),
    };

    // ----------------------------------------------------------------- tool failures
    /// <summary>
    /// Function middleware: a tool that fails stops the investigation.
    ///
    /// Without it, the function-invocation pipeline catches a tool's exception and tells the model
    /// only "Error: Function failed." The model then carries on with whatever it has, and a
    /// Dataverse outage becomes a SMALLER investigation that still reaches the human gate looking
    /// complete. Here the failure is recorded, the tool loop is told to stop, and the investigate
    /// step fails with the reason.
    /// </summary>
    public sealed class ToolFailureGuard
    {
        public List<string> Failures { get; } = new();

        public async ValueTask<object?> InvokeAsync(
            AIAgent agent, FunctionInvocationContext context,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
            CancellationToken cancellationToken)
        {
            try
            {
                return await next(context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var name = context.Function.Name;
                Failures.Add($"{name}: {ex.Message}");
                ToolLog.Add($"{name} FAILED");
                context.Terminate = true;       // no further model round trips in this run
                return $"{name} failed. The investigation has been stopped.";
            }
        }
    }

    // ----------------------------------------------------------------- the agents
    public const string Investigator = """
You are a claims investigator at Contoso Insurance, a fictional insurer used for training. You have
been handed one claim that triage flagged.

Your job is to find out whether that claim is part of an ORGANISED GROUP, by following identifiers
that separate households do not normally share.

How to work:
- Call `expand` with EVERY claimant you know about so far. That is one hop, and it returns everyone
  newly connected plus the identifier that connects them.
- If a hop finds someone new, hop again with the enlarged set. Stop after at most 3 hops, or as soon
  as a hop returns nobody new.
- Do not call `expand` one person at a time. Pass the whole set. One call per hop.
- A shared REPAIRER is not a link between people. A busy garage serves hundreds of unconnected
  customers. Check its volume if you like, but never treat it as evidence of a connection.

Report every claimant you reached, including the one you started from, and say which identifier
brought each of them in. Report candidates, not conclusions. Somebody else decides what they mean.
""";

    public const string Challenger = """
You are the fraud desk's challenge officer at Contoso Insurance, a fictional insurer used for
training. An investigator has handed you a list of claimants reached by following shared
identifiers. Your job is to ARGUE AGAINST referring them, and to concede only what survives.

You are judging a GROUP, not a list of people each measured against the seed. These claimants form a
chain: A shares an account with B, B shares a phone with C, and so on. A link between ANY TWO members
counts, and it does not matter whether the seed claimant is one of them. Strength is a property of
the identifier, never of how close someone sits to the starting claim.

Weigh each identifier honestly:
- A shared BANK ACCOUNT between any two claimants is close to conclusive. Separate households do not
  receive settlements into one account. A second, different shared account inside the same group is
  more evidence, not less.
- A shared PHONE number between any two claimants is strong. It is occasionally innocent, so say
  when you are unsure, but it is not grounds to exclude on its own.
- A shared POSTCODE is close to worthless on its own. A UK postcode covers a terrace or a block of
  flats, so neighbours share one constantly. Exclude anyone whose ONLY link is a postcode.
- A shared repairer is not a link between people at all.

So: CONFIRM anyone holding at least one strong link to any other member of the group. EXCLUDE anyone
whose only connection to the group is a shared postcode.

Refer only if at least three claimants survive that test. Excluding an innocent person matters as
much as catching a guilty one: a fraud referral follows someone for years.

Do not manufacture doubt to appear rigorous, and do not wave things through to appear decisive.
""";

    /// <summary>
    /// A chat client for the Foundry deployment, configured from `lab/.env`.
    ///
    /// The credential is your own sign-in unless a FOUNDRY_KEY is set: the session that reads the
    /// book also pays for the model calls, so one `dotnet run -- login` covers everything and there
    /// is no shared key to hand around. `BearerTokenPolicy` asks the sign-in for a token whenever a
    /// request needs one, so a long run never sends an expired one. The endpoint is the Foundry
    /// resource's OpenAI-compatible `/openai/v1/` route, which needs no api-version.
    ///
    /// This is the Chat Completions API, which keeps nothing on the service unless a request asks
    /// it to (the Python track's Responses client has to be told `store: False`).
    /// </summary>
    public static IChatClient ChatClient()
    {
        var (endpoint, key, model) = Env.Foundry();
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
        // The token-policy constructor is marked for evaluation (OPENAI001) in this SDK version;
        // it is the route Microsoft Learn's v1 API page shows for Microsoft Entra ID, pragma included.
#pragma warning disable OPENAI001
        var client = string.IsNullOrEmpty(key)
            ? new OpenAIClient(
                new BearerTokenPolicy(Dataverse.Credential(), Dataverse.FoundryScope), options)
            : new OpenAIClient(new ApiKeyCredential(key), options);
#pragma warning restore OPENAI001
        // The ceiling on tool-calling, this SDK's way round. Python caps each tool with
        // `max_invocations`; this SDK has no per-tool cap, so the function-invocation pipeline
        // caps ITERATIONS per request instead - enough for a three-hop investigation plus
        // headroom, and an agent that tried to enumerate the whole book would be cut off.
        //
        // `UseOpenTelemetry` sits INSIDE the tool loop, so each model call is its own span and the
        // loop adds a span for each tool call. With nothing listening (no --telemetry) the spans
        // cost nothing; see Telemetry.cs.
        return client.GetChatClient(model).AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = 8)
            .UseOpenTelemetry(sourceName: Telemetry.Source)
            .Build();
    }

    /// <summary>
    /// The traversal agent: instructions, the four link tools, and the tool-failure guard as
    /// function middleware. Built fresh per call, so each investigation starts with an empty
    /// conversation rather than the last run's history. `UseOpenTelemetry` adds the span for the
    /// agent run around the model and tool spans.
    /// </summary>
    public static AIAgent InvestigatorAgent(ToolFailureGuard guard) =>
        ChatClient().AsAIAgent(Investigator, "investigator", tools: LinkTools())
            .AsBuilder()
            .Use(guard.InvokeAsync)
            .UseOpenTelemetry(Telemetry.Source)
            .Build();

    /// <summary>
    /// The adversarial agent. NO TOOLS, on purpose: it judges evidence it has been handed, and
    /// letting it find its own would remove the separation the step exists to create.
    /// </summary>
    public static AIAgent ChallengerAgent() =>
        ChatClient().AsAIAgent(Challenger, "challenge-officer")
            .AsBuilder()
            .UseOpenTelemetry(Telemetry.Source)
            .Build();

    // ENFORCE THE SCHEMA RATHER THAN ASKING FOR IT IN THE PROMPT. A model that drifts from a
    // merely REQUESTED shape returns well-formed JSON carrying a different JUDGMENT - dropping a
    // real member, or confirming everyone. A parse error is obvious; a plausible wrong answer is
    // not.
    /// <summary>
    /// Run options carrying a JSON schema the deployment enforces. `strict` must be asked for: on
    /// its own, `ForJsonSchema` sends the schema without it, and the service then treats the schema
    /// as guidance. The adapter turns this key into `"strict": true` on the request.
    /// </summary>
    public static ChatClientAgentRunOptions Enforce(string schemaName, string schema) =>
        new(new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                JsonSerializer.Deserialize<JsonElement>(schema), schemaName),
            AdditionalProperties = new() { ["strict"] = true },
        });

    public const string TraversalSchema = """
    {"type":"object","additionalProperties":false,
     "required":["claimants","hops","narrative"],
     "properties":{
       "hops":{"type":"integer"},
       "narrative":{"type":"string",
                    "description":"How you moved, hop by hop. Three or four sentences."},
       "claimants":{"type":"array","items":{
         "type":"object","additionalProperties":false,
         "required":["claimant_ref","full_name","why_linked","link_type","shared_with"],
         "properties":{
           "claimant_ref":{"type":"string"},
           "full_name":{"type":"string"},
           "why_linked":{"type":"string"},
           "link_type":{"type":"string","enum":["seed","bank_account","phone","postcode"]},
           "shared_with":{"type":"array","items":{"type":"string"},
                          "description":"claimant refs this person shares the identifier with"}}}}}}
    """;

    public const string JudgementSchema = """
    {"type":"object","additionalProperties":false,
     "required":["confirmed","excluded","recommend_refer","rationale"],
     "properties":{
       "recommend_refer":{"type":"boolean"},
       "rationale":{"type":"string"},
       "confirmed":{"type":"array","items":{
         "type":"object","additionalProperties":false,
         "required":["claimant_ref","reason","strength"],
         "properties":{"claimant_ref":{"type":"string"},"reason":{"type":"string"},
                       "strength":{"type":"string","enum":["strong","weak"]}}}},
       "excluded":{"type":"array","items":{
         "type":"object","additionalProperties":false,
         "required":["claimant_ref","reason"],
         "properties":{"claimant_ref":{"type":"string"},"reason":{"type":"string"}}}}}}
    """;

    /// <summary>
    /// The agent's JSON reply, or a sentence saying why there is none. An empty reply is what a
    /// model returns when it runs out of output budget or its answer is filtered; parsing it would
    /// report a JSON error, which explains nothing.
    /// </summary>
    public static JsonElement ParseReply(string? text, string step)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(
                $"the {step} agent returned no reply. Its output budget may be too small, or the "
                + "reply was filtered.");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    // ----------------------------------------------------------------- what the models may name
    /// <summary>
    /// `refs` kept only where they match `allowed`, case-insensitively, spelled as in `allowed`,
    /// without duplicates and in their original order.
    ///
    /// The models write claimant refs into their replies, and in a fraud desk the people being
    /// investigated wrote the data the models read. A ref no tool returned, or one the investigator
    /// never put forward, is dropped here rather than priced and written to Dataverse.
    /// </summary>
    public static List<string> KnownRefs(IEnumerable<string> refs, IEnumerable<string> allowed)
    {
        var spelled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in allowed) spelled.TryAdd(a, a);
        var output = new List<string>();
        foreach (var r in refs)
            if (spelled.TryGetValue(r, out var canon) && !output.Contains(canon))
                output.Add(canon);
        return output;
    }

    /// <summary>The challenger recommends; the threshold decides. Pure, so it is tested for real.</summary>
    public static bool ShouldRefer(bool modelRecommends, int confirmedCount) =>
        modelRecommends && confirmedCount >= MinToRefer;

    /// <summary>What the records link `reference` to, among `members`: one entry per identifier,
    /// strongest first.</summary>
    public static List<string> RecordLinks(string reference, ICollection<string> members)
    {
        string[] order = { "bank_account", "phone", "postcode" };
        if (!Links.TryGetValue(reference, out var byAttr)) return new();
        return byAttr.OrderBy(kv => { var i = Array.IndexOf(order, kv.Key); return i >= 0 ? i : 9; })
            .Select(kv => (kv.Key, With: kv.Value
                .Where(r => members.Contains(r) && !string.Equals(r, reference, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r, StringComparer.Ordinal).ToList()))
            .Where(x => x.With.Count > 0)
            .Select(x => $"{x.Key} with {string.Join(", ", x.With)}")
            .ToList();
    }

    /// <summary>
    /// What the challenger reads: each candidate and EVERY identifier the records say they share,
    /// and with whom. The pairwise links are the evidence. A chain is judged by its links between
    /// members, so the challenger has to see them rather than infer them from the investigator's
    /// prose, and it has to see all of them: the investigator reports one link type per person, and
    /// a person reported as a postcode match may also share a bank account. Where the records hold
    /// nothing, the investigator's own label is shown instead.
    /// </summary>
    public static string ChallengeListing(IEnumerable<LinkedClaimant> candidates) =>
        string.Join("\n", candidates.Select(c =>
        {
            var links = string.Join("; ", c.Evidence.Select(e => $"shares {e}"));
            if (c.LinkType == "seed")
                return $"  {c.ClaimantRef}  {c.FullName}  the seed claimant"
                     + (links.Length > 0 ? $"; {links}" : "") + $": {c.WhyLinked}";
            if (links.Length == 0)
                links = $"shares {c.LinkType} with "
                      + (c.SharedWith.Count > 0 ? string.Join(", ", c.SharedWith) : "a member not named");
            return $"  {c.ClaimantRef}  {c.FullName}  {links}: {c.WhyLinked}";
        }));

    // ----------------------------------------------------------------- 1. intake
    [SendsMessage(typeof(VerifiedClaim))]
    public sealed partial class Intake() : Executor("intake")
    {
        /// <summary>
        /// Turn a reference into a verified claim, or refuse. A rule, not a judgment, so it is
        /// code: a model asked "does this claim exist?" can be argued with, a lookup cannot. It
        /// also starts the shared `trail` the graph appends to, and records who is investigating,
        /// which travels with the checkpoint to whoever approves.
        /// </summary>
        [MessageHandler]
        public async ValueTask HandleAsync(SeedClaim seed, IWorkflowContext ctx)
        {
            var row = await Dataverse.GetClaimAsync(seed.ClaimRef)
                ?? throw new InvalidOperationException(
                    $"{seed.ClaimRef} is not in the claims book. The fraud desk only investigates "
                    + "claims that exist; it does not take a reference on trust.");
            var v = new VerifiedClaim(
                Dataverse.Str(row, "cp_bookclaimref"),
                Dataverse.Str(row, "cp_claimantref"),
                Dataverse.Str(row, "cp_repairername"),
                Dataverse.Num(row, "cp_amountclaimed"));
            Console.WriteLine($"[intake]      {v.ClaimRef}  claimant={v.ClaimantRef}  "
                            + $"repairer={v.Repairer}  GBP {v.AmountClaimed:N0}");
            await ctx.QueueStateUpdateAsync("seed", v, Shared);
            await ctx.QueueStateUpdateAsync("investigator", await Dataverse.WhoAmIAsync(), Shared);
            await ctx.QueueStateUpdateAsync("trail",
                new List<string> { $"intake: accepted {v.ClaimRef} from triage" }, Shared);
            await ctx.SendMessageAsync(v);
        }
    }

    // ----------------------------------------------------------------- 2. investigate
    [SendsMessage(typeof(TraversalComplete))]
    public sealed partial class Investigate() : Executor("investigate")
    {
        /// <summary>
        /// Let the model plan and run the traversal, then hand on CANDIDATES, not conclusions.
        ///
        /// The node that justifies the whole tier: the second hop's arguments are the first hop's
        /// results, which no fixed query can express.
        /// </summary>
        [MessageHandler]
        public async ValueTask HandleAsync(VerifiedClaim seed, IWorkflowContext ctx)
        {
            ToolLog.Clear();
            Reached.Clear();
            Links.Clear();
            Reached.Add(seed.ClaimantRef);
            // Hand the agent what the graph already knows. Making the model fetch the seed
            // claimant's identifiers costs two round trips, and a round trip is the unit this bill
            // is denominated in.
            var start = await Dataverse.GetClaimantAsync(seed.ClaimantRef);
            string Ident(string col, string fallback) =>
                start is { } s ? Dataverse.Str(s, col) : fallback;
            var opening =
                $"Seed claim {seed.ClaimRef}, claimant {seed.ClaimantRef} "
                + $"({Ident("cp_fullname", "unknown")}), "
                + $"repairer {seed.Repairer}, GBP {seed.AmountClaimed:N0}.\n"
                + $"Their identifiers: bank {Ident("cp_bankaccount", "?")}, "
                + $"phone {Ident("cp_phone", "?")}, "
                + $"postcode {Ident("cp_postcode", "?")}.\n\n"
                + $"Begin by calling expand with [\"{seed.ClaimantRef}\"]. "
                + "Find out who else is connected.";

            var guard = new ToolFailureGuard();
            var result = await InvestigatorAgent(guard).RunAsync(
                opening, options: Enforce("traversal", TraversalSchema));
            if (guard.Failures.Count > 0)
                throw new InvalidOperationException(
                    $"the investigation stopped because a tool failed - {guard.Failures[0]}. "
                    + "Nothing was referred; fix the cause and run start again.");
            var reply = ParseReply(result.Text, "investigator");

            // Only claimants a tool actually returned can become candidates.
            var reported = reply.GetProperty("claimants").EnumerateArray().ToList();
            var byRef = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in reported) byRef.TryAdd(c.GetProperty("claimant_ref").GetString()!, c);
            var kept = KnownRefs(
                reported.Select(c => c.GetProperty("claimant_ref").GetString()!), Reached);
            var cands = kept.Select(r => new LinkedClaimant(
                r,
                byRef[r].GetProperty("full_name").GetString()!,
                byRef[r].GetProperty("why_linked").GetString()!,
                byRef[r].GetProperty("link_type").GetString()!,
                KnownRefs(byRef[r].GetProperty("shared_with").EnumerateArray()
                                  .Select(x => x.GetString()!), Reached),
                RecordLinks(r, kept))).ToList();
            var hops = reply.GetProperty("hops").GetInt32();

            Console.WriteLine($"[investigate] the model made {ToolLog.Count} tool call(s):");
            foreach (var t in ToolLog) Console.WriteLine($"                -> {t}");
            Console.WriteLine($"[investigate] reached {cands.Count} claimant(s) over {hops} hop(s)");
            var dropped = reported.Count - kept.Count;
            if (dropped > 0)
                Console.WriteLine($"[investigate] ignored {dropped} claimant(s) that no tool returned");

            var trail = await ctx.ReadStateAsync<List<string>>("trail", Shared) ?? new();
            trail.Add($"investigate: {cands.Count} candidates over {hops} hops, "
                    + $"{ToolLog.Count} tool calls");
            await ctx.QueueStateUpdateAsync("trail", trail, Shared);
            await ctx.QueueStateUpdateAsync("tool_log", ToolLog.ToList(), Shared);
            await ctx.SendMessageAsync(new TraversalComplete(
                seed.ClaimRef, cands, hops, reply.GetProperty("narrative").GetString() ?? ""));
        }
    }

    // ----------------------------------------------------------------- 3. challenge
    /// <summary>
    /// The money, computed from claim rows the model never wrote. Pure and separate so the test
    /// suite can prove the arithmetic against fixture rows. The model decides WHO is confirmed;
    /// every figure a human sees comes out of this function, over rows that exist.
    /// </summary>
    public static (List<string> ClaimRefs, double Exposure) PriceConfirmed(IEnumerable<JsonElement> rows)
    {
        var list = rows.ToList();
        return (list.Select(r => Dataverse.Str(r, "cp_bookclaimref")).ToList(),
                list.Sum(r => Dataverse.Num(r, "cp_amountclaimed")));
    }

    [SendsMessage(typeof(ReferralApprovalRequest))]
    public sealed partial class Challenge() : Executor("challenge")
    {
        /// <summary>
        /// Argue against the traversal, keep what survives, price it in C#, and raise the gate.
        ///
        /// The traversal casts a wide net, and some of what it catches is coincidence. Unlike
        /// Python, this node also emits the approval REQUEST, because the gate is a separate node -
        /// so the proposal is parked in shared state for a Refer that runs in another process.
        /// </summary>
        [MessageHandler]
        public async ValueTask HandleAsync(TraversalComplete t, IWorkflowContext ctx)
        {
            // Schema enforced, not requested - see the note on Enforce above. This step decides
            // who gets accused, so it is the last place to take a shape on trust.
            var result = await ChallengerAgent().RunAsync(
                $"Seed claim {t.SeedClaimRef}.\nThe investigator reached these claimants over "
                + $"{t.Hops} hops:\n{ChallengeListing(t.Candidates)}\n\n"
                + "Which of them would you actually refer?",
                options: Enforce("judgement", JudgementSchema));
            var reply = ParseReply(result.Text, "challenger");

            // Only people the investigator put forward can be confirmed or excluded.
            var candidateRefs = t.Candidates.Select(c => c.ClaimantRef).ToList();
            List<JudgedClaimant> Judged(string list, Func<JsonElement, string> strength)
            {
                var rows = reply.GetProperty(list).EnumerateArray().ToList();
                var byRef = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in rows) byRef.TryAdd(c.GetProperty("claimant_ref").GetString()!, c);
                return KnownRefs(
                        rows.Select(c => c.GetProperty("claimant_ref").GetString()!), candidateRefs)
                    .Select(r => new JudgedClaimant(
                        r, byRef[r].GetProperty("reason").GetString() ?? "", strength(byRef[r])))
                    .ToList();
            }
            var confirmed = Judged("confirmed", c => c.GetProperty("strength").GetString() ?? "strong");
            var excluded = Judged("excluded", _ => "weak")
                .Where(e => !confirmed.Any(c => c.ClaimantRef == e.ClaimantRef)).ToList();
            var refer = ShouldRefer(reply.GetProperty("recommend_refer").GetBoolean(), confirmed.Count);
            var rationale = reply.GetProperty("rationale").GetString() ?? "";

            // THE MONEY IS COMPUTED HERE, IN C#, FROM THE RECORDS. The model decides WHO. It never
            // decides HOW MUCH. One batched read for the whole confirmed set.
            var (claimRefs, exposure) = PriceConfirmed(
                await Dataverse.ClaimsOfManyAsync(confirmed.Select(c => c.ClaimantRef)));

            Console.WriteLine($"[challenge]   confirmed {confirmed.Count}, "
                            + $"excluded {excluded.Count}  -> refer={refer}");
            foreach (var e in excluded)
                Console.WriteLine($"                excluded {e.ClaimantRef}: "
                                + e.Reason[..Math.Min(70, e.Reason.Length)]);

            var trail = await ctx.ReadStateAsync<List<string>>("trail", Shared) ?? new();
            trail.Add($"challenge: confirmed {confirmed.Count}, excluded {excluded.Count}, "
                    + $"recommend_refer={refer}");
            await ctx.QueueStateUpdateAsync("trail", trail, Shared);
            await ctx.QueueStateUpdateAsync("proposal", new ReferralProposal(
                t.SeedClaimRef, confirmed, excluded, refer, rationale, t.Hops, claimRefs, exposure),
                Shared);

            Console.WriteLine($"[refer]       {confirmed.Count} claimant(s), {claimRefs.Count} "
                            + $"claim(s), GBP {exposure:N0} needs a named human. PAUSING (durable).");
            await ctx.SendMessageAsync(new ReferralApprovalRequest(
                t.SeedClaimRef, confirmed.Count, claimRefs.Count, exposure, rationale,
                confirmed.Select(c => c.ClaimantRef).ToList(),
                excluded.Select(c => c.ClaimantRef).ToList()));
        }
    }

    // ----------------------------------------------------------------- 4. refer
    //
    // What the human's answer means. Pure, so it can be tested for real.
    //   not approved               -> dismissed      a person overrode the recommendation
    //   approved + recommended     -> referred       it goes to the investigations unit
    //   approved + not recommended -> not_referred   the desk cleared them, and a person agreed
    public static string DecideOutcome(bool approved, bool recommended) =>
        !approved ? "dismissed" : recommended ? "referred" : "not_referred";

    [YieldsOutput(typeof(FraudOutcome))]
    public sealed partial class Refer() : Executor("refer")
    {
        /// <summary>
        /// A person answered. Record what they decided, and only now write to Dataverse.
        ///
        /// Runs in a process that never saw the traversal, so everything comes off the checkpoint.
        /// The ONLY place that writes, and the only way in is a ReferralDecision, which can arrive
        /// only through the approval port - so the guarantee is structural, not a promise. A
        /// decline is written too, with the same evidence lines: that the desk looked, what it
        /// found and that somebody said no is worth keeping. A line's strength describes the LINK,
        /// not anyone's guilt.
        /// </summary>
        [MessageHandler]
        public async ValueTask HandleAsync(ReferralDecision decision, IWorkflowContext ctx)
        {
            var p = await ctx.ReadStateAsync<ReferralProposal>("proposal", Shared)
                ?? throw new InvalidOperationException("no proposal in state");
            var trail = await ctx.ReadStateAsync<List<string>>("trail", Shared) ?? new();
            var toolLog = await ctx.ReadStateAsync<List<string>>("tool_log", Shared) ?? new();
            var investigator = await ctx.ReadStateAsync<string>("investigator", Shared)
                               ?? await Dataverse.WhoAmIAsync();
            var outcome = DecideOutcome(decision.Approved, p.RecommendRefer);

            var runId = Dataverse.NewRunId();
            // The ref carries WHO investigated and WHEN, so two investigations never produce the
            // same ref and nobody overwrites anybody.
            var referralRef = $"FR-{investigator.Split('@')[0]}-{runId}";
            if (referralRef.Length > 99) referralRef = referralRef[..99];

            trail.Add($"refer: approved={decision.Approved} outcome={outcome} "
                    + $"by {decision.Approver} run={runId}");
            if (!string.IsNullOrWhiteSpace(decision.Note)) trail.Add($"note: {decision.Note}");

            string Refs(IEnumerable<JudgedClaimant> js) =>
                js.Any() ? string.Join(", ", js.Select(c => c.ClaimantRef)) : "none";
            var evidence = $"{p.Rationale}\n\nHops: {p.Hops}\n"
                + $"Confirmed: {Refs(p.Confirmed)}\n"
                + $"Excluded: {Refs(p.Excluded)}\n"
                + $"Tool calls: {string.Join("; ", toolLog)}"
                + (string.IsNullOrWhiteSpace(decision.Note) ? "" : $"\nNote: {decision.Note}");

            // Every evidence line is built BEFORE anything is written, so a surprise in the data
            // cannot leave a referral header in Dataverse without its lines.
            var judged = p.Confirmed.ToDictionary(c => c.ClaimantRef, StringComparer.OrdinalIgnoreCase);
            var lines = new List<(string ClaimRef, string ClaimantRef, JudgedClaimant Judged)>();
            foreach (var row in await Dataverse.ClaimsOfManyAsync(p.Confirmed.Select(c => c.ClaimantRef)))
                if (judged.TryGetValue(Dataverse.Str(row, "cp_claimantref"), out var j))
                    lines.Add((Dataverse.Str(row, "cp_bookclaimref"), j.ClaimantRef, j));

            await Dataverse.WriteReferralAsync(
                referralRef: referralRef, seedClaim: p.SeedClaimRef, decision: outcome,
                ringSize: p.Confirmed.Count, claimCount: p.ClaimRefs.Count,
                exposure: p.ExposureGbp, hops: p.Hops, investigator: investigator,
                approvedBy: decision.Approver, evidence: evidence, language: Language,
                runId: runId);
            foreach (var (claimRef, claimantRef, j) in lines)
                await Dataverse.WriteReferralClaimAsync(referralRef, claimRef, claimantRef,
                                                        j.Reason, j.Strength);

            Console.WriteLine($"[refer]       {outcome.ToUpperInvariant()} by {decision.Approver}");
            Console.WriteLine($"[refer]       written to Dataverse as {referralRef}");

            await ctx.QueueStateUpdateAsync("trail", trail, Shared);
            await ctx.YieldOutputAsync(new FraudOutcome(
                p.SeedClaimRef, outcome, p.Confirmed.Count, p.ClaimRefs.Count, p.ExposureGbp,
                p.Hops, decision.Approver, referralRef, trail));
        }
    }

    // ----------------------------------------------------------------- assembly
    // THE HUMAN GATE, AS A NODE. Python suspends inside an executor; .NET puts a typed port in the
    // graph, so the wiring alone shows that nothing reaches Refer without passing through a person.
    public static readonly RequestPort ApprovalPort =
        RequestPort.Create<ReferralApprovalRequest, ReferralDecision>("approval");

    /// <summary>
    /// The orchestration graph. This is the artifact: versioned, reviewable, testable. It is a
    /// DESCRIPTION, not a running thing, so rebuilding it in a fresh process and pointing it at a
    /// checkpoint is how a resume works. Five nodes to Python's four, because of the port above.
    ///
    /// The executor ids ("intake", "investigate", ...) name the nodes in every checkpoint. Renaming
    /// one after a run has been saved makes that checkpoint unrestorable, so treat them as a schema.
    /// </summary>
    public static Workflow BuildWorkflow()
    {
        var intake = new Intake();
        var investigate = new Investigate();
        var challenge = new Challenge();
        var refer = new Refer();

        // Every edge carries a DISTINCT message type. Routing is by type, so two executors
        // accepting the same type would both receive it.
        return new WorkflowBuilder(intake)
            .WithName(WorkflowName)
            .AddEdge(intake, investigate)             // VerifiedClaim
            .AddEdge(investigate, challenge)          // TraversalComplete
            .AddEdge(challenge, ApprovalPort)         // ReferralApprovalRequest
            .AddEdge(ApprovalPort, refer)             // ReferralDecision
            .WithOutputFrom(refer)
            .WithOpenTelemetry(activitySource: Telemetry.Activities)
            .Build();
    }
}
