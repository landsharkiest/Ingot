using Ingot.Evals;

// Ring 3 eval harness. Offline by default (deterministic, no network) so the scorecard math is
// verifiable in CI/dry-run; point the client factory at a live IChatClient to score a real model.
//
//   dotnet run --project eval/Ingot.Evals -- --out ./eval-output --verbose
//
// Exit code is non-zero if any case's observed outcome disagrees with its expectation, so the
// offline run doubles as a self-test of the scoring pipeline.

var outputDirectory = "./eval-output";
var verbose = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out" when i + 1 < args.Length:
            outputDirectory = args[++i];
            break;
        case "--verbose":
            verbose = true;
            break;
    }
}

Console.WriteLine($"Running {EvalSuite.Cases.Count} offline eval cases…");

var runner = new EvalRunner(EvalRunner.Offline, verbose);
var (scorecard, outcomes) = await runner.RunAsync(EvalSuite.Cases);

ReportWriter.Write(outputDirectory, scorecard, outcomes);

Console.WriteLine();
Console.WriteLine(ReportWriter.Markdown(scorecard, outcomes));
Console.WriteLine($"Reports written to {Path.GetFullPath(outputDirectory)}");

if (!scorecard.AllSelfChecksPassed)
{
    Console.Error.WriteLine("Self-check failed: an eval case's outcome did not match its expectation.");
    return 1;
}

return 0;
