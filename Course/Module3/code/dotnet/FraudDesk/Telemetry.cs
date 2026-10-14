// See inside a run: the framework's OpenTelemetry spans, printed as they finish.
//
// DEMONSTRATION CODE - written to be read rather than deployed.
//
// Microsoft Agent Framework is instrumented with OpenTelemetry. The workflow (`WithOpenTelemetry`
// in Desk.BuildWorkflow) and both agents (`UseOpenTelemetry`) emit a span for every workflow run,
// executor step, agent run, model call and tool call, with timings and token counts. .NET records a
// span only when something is listening, so without --telemetry they cost nothing. This file is
// the listener, with two consumers:
//
//   - a printer, so `dotnet run -- start --telemetry` shows the spans in the terminal;
//   - Azure Monitor, if APPLICATIONINSIGHTS_CONNECTION_STRING is set, so the same spans reach
//     Application Insights, where a team would watch them in production.
//
// Neither changes what the desk does. Prompts and replies are NOT recorded: the framework keeps
// message content out of spans unless you opt in to sensitive data, and this desk does not.
using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FraudDesk;

public static class Telemetry
{
    /// <summary>The one source every span comes from: the workflow's and both agents'.</summary>
    public const string Source = "claims365-fraud-desk";
    public static readonly ActivitySource Activities = new(Source);

    // The span names already say what ran (an executor, an agent, a model call, a tool); these are
    // the attributes worth a glance beside them: what each model call cost, in tokens.
    static readonly (string Label, string Key)[] Shown =
    {
        ("in", "gen_ai.usage.input_tokens"),
        ("out", "gen_ai.usage.output_tokens"),
    };

    /// <summary>
    /// One line per finished span, indented by nesting, on stderr so it never mixes with results.
    /// </summary>
    sealed class PrintSpans : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity span)
        {
            var depth = 0;
            for (var p = span.Parent; p is not null; p = p.Parent) depth++;
            var extra = string.Join("  ", Shown
                .Where(s => span.GetTagItem(s.Key) is not null)
                .Select(s => $"{s.Label}={span.GetTagItem(s.Key):N0}"));
            Console.Error.WriteLine(
                $"  [span] {new string(' ', 2 * depth)}{span.DisplayName}  "
                + $"{span.Duration.TotalMilliseconds:N0} ms  {extra}".TrimEnd());
        }
    }

    /// <summary>
    /// Start listening: the printer always, Application Insights when a connection string is set.
    /// The caller disposes the provider before the process exits, which flushes the exporter.
    /// </summary>
    public static TracerProvider Enable()
    {
        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(Source))
            .AddSource(Source)
            .AddProcessor(new PrintSpans());
        var conn = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(conn))
            builder.AddAzureMonitorTraceExporter(o => o.ConnectionString = conn);
        return builder.Build()!;
    }
}
