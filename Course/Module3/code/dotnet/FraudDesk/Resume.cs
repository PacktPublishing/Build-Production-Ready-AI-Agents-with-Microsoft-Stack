// Running and resuming the fraud desk.
//
// THE REAL DIFFERENCE BETWEEN THE TWO LANGUAGE TRACKS.
//
// Python answers a pending human gate with a dictionary:
//
//     wf.run(responses={request_id: decision}, checkpoint_id=cp)
//
// .NET does not. Restoring from a checkpoint RE-EMITS the pending request as a `RequestInfoEvent`,
// and you answer the event. So the C# resume path reads the request back off the restored run:
//
//     var run = await InProcessExecution.ResumeAsync(workflow, checkpoint, manager);
//     var waiting = run.NewEvents.OfType<RequestInfoEvent>().FirstOrDefault();
//     await run.ResumeAsync(new[] { waiting.Request.CreateResponse(decision) });
//
// Same durability, same guarantee, different shape. It is also why the C# graph has five nodes to
// Python's four: the gate is a `RequestPort` in the topology rather than a call inside a method.
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace FraudDesk;

public static class Resume
{
    /// <summary>
    /// Did an executor throw during the last run? The framework does NOT let a handler's exception
    /// escape - it becomes an `ExecutorFailedEvent` and the run completes normally - so without
    /// this the process would print an error and still exit 0.
    /// </summary>
    public static bool Failed { get; private set; }

    /// <summary>
    /// The message worth showing a person. The event carries the exception the handler threw, and
    /// the desk's own exceptions are written as sentences that say what to do next.
    /// </summary>
    static string Describe(object? data) =>
        data is Exception ex ? ex.Message : data?.ToString() ?? "unknown error";

    /// <summary>
    /// Checkpoints on disk, so a claim survives the process exiting: the framework's own
    /// `FileSystemJsonCheckpointStore`, the .NET twin of Python's `FileCheckpointStorage`. It writes
    /// one JSON file per checkpoint, named after the session and the checkpoint with the dot
    /// escaped (`...%2Ejson`), plus `index.jsonl`. It holds the index open while in use, so each
    /// command disposes it before exiting.
    ///
    /// The files hold the claimants' details, and anyone who can change them can change what the
    /// next `approve` resumes. Keep the folder yours.
    /// </summary>
    static FileSystemJsonCheckpointStore Store(string dir) => new(new DirectoryInfo(dir));

    /// <summary>Start a fresh investigation and run until the desk needs a human.</summary>
    public static async Task StartAsync(string dir, string sessionId, SeedClaim seed)
    {
        using var store = Store(dir);
        await using var run = await InProcessExecution.RunStreamingAsync(
            Desk.BuildWorkflow(), seed, CheckpointManager.CreateJson(store), sessionId);
        await PumpAsync(run, dir);
    }

    /// <summary>
    /// Answer the pending gate, in a process that never started the investigation. Returns false,
    /// having changed nothing, if the restored run re-emits no request.
    ///
    /// The non-streaming `ResumeAsync` restores the run and lets it halt before returning, so the
    /// request it re-emitted can be read straight off it. Call this only while something is
    /// waiting (Program.cs checks pending.json): in Agent Framework 1.17.0, restoring a run that
    /// has already finished does not return.
    /// </summary>
    public static async Task<bool> AnswerAsync(string dir, string sessionId, ReferralDecision decision)
    {
        using var store = Store(dir);
        var manager = CheckpointManager.CreateJson(store);
        var latest = await manager.GetLatestCheckpointAsync(sessionId)
            ?? throw new Exception("no checkpoint for this session. run 'start' first.");
        await using var run = await InProcessExecution.ResumeAsync(Desk.BuildWorkflow(), latest, manager);

        var waiting = run.NewEvents.OfType<RequestInfoEvent>().FirstOrDefault();
        if (waiting is null) return false;
        await run.ResumeAsync(new[] { waiting.Request.CreateResponse(decision) });
        foreach (var evt in run.NewEvents) Handle(evt, dir);
        return true;
    }

    /// <summary>
    /// Drain a live run's event stream until the desk needs a human, then stop: the request has been
    /// recorded, so a later process can report it without replaying anything.
    /// </summary>
    static async Task PumpAsync(StreamingRun run, string dir)
    {
        await foreach (var evt in run.WatchStreamAsync(blockOnPendingRequest: true))
            if (Handle(evt, dir)) return;
    }

    /// <summary>
    /// What one workflow event means to the desk. Returns true for a request for a human, which is
    /// where a run started by `start` stops.
    /// </summary>
    static bool Handle(WorkflowEvent evt, string dir)
    {
        // M3_TRACE=1 prints every workflow event. Worth knowing: a silent run is almost always a
        // routing problem, and this is how you see it.
        if (Environment.GetEnvironmentVariable("M3_TRACE") == "1")
            Console.WriteLine($"    [event] {evt.GetType().Name}");
        switch (evt)
        {
            // A failing executor is reported as an EVENT, not thrown, so record it here. Print the
            // innermost message only, with the full trace behind M3_TRACE: the sentence that tells
            // you what to do is otherwise buried under framework frames.
            case ExecutorFailedEvent bad:
                Failed = true;
                Console.Error.WriteLine();
                Console.Error.WriteLine($"  !! the '{bad.ExecutorId}' step failed: {Describe(bad.Data)}");
                Console.Error.WriteLine("  (set M3_TRACE=1 for the full stack trace)");
                if (Environment.GetEnvironmentVariable("M3_TRACE") == "1")
                    Console.Error.WriteLine(bad.Data);
                return false;

            // Usually the same exception again, one layer out. Only useful with the trace on.
            case WorkflowErrorEvent werr:
                Failed = true;
                if (Environment.GetEnvironmentVariable("M3_TRACE") == "1")
                    Console.Error.WriteLine($"    !! workflow error: {werr.Data}");
                return false;

            // Nobody is here to answer. Write down what is waiting. PortableValue is the
            // framework's type-erased envelope; `Is<T>(out T)` is how you get your own type back.
            case RequestInfoEvent req:
                if (req.Request.Data.Is<ReferralApprovalRequest>(out var ask))
                    Pending.Write(dir, ask);
                return true;

            case WorkflowOutputEvent { Data: FraudOutcome outcome }:
                Outcomes.Last = outcome;
                Pending.Clear(dir);
                return false;

            default:
                return false;
        }
    }
}
