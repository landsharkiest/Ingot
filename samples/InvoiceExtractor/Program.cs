using Ingot;
using Ingot.Diagnostics;
using InvoiceExtractor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// A runnable tour of Ingot. Everything here runs offline against a scripted client — no API key.
// The scripted model returns a malformed date first, so you can watch the repair loop in action.

const string badThenGood_first = """{"vendorName":"Globex","issuedOn":"June 1st, 2026","total":1200.50}""";
const string badThenGood_second = """{"vendorName":"Globex","issuedOn":"2026-06-01","total":1200.50}""";
const string cleanJson = """{"vendorName":"Acme Corp","issuedOn":"2026-06-01","total":450.00}""";

// ---------------------------------------------------------------- 1. Direct extension-method use

Console.WriteLine("== 1. Direct usage ==");
IChatClient direct = new ScriptedChatClient("openai", cleanJson);
var invoice = await direct.ExtractAsync<Invoice>("Extract the invoice from this email.");
Console.WriteLine($"Extracted: {invoice.VendorName}, {invoice.IssuedOn}, {invoice.Total:C}");
Console.WriteLine();

// ---------------------------------------------------------------- 2. Dependency injection

Console.WriteLine("== 2. Dependency injection ==");
var services = new ServiceCollection();
services.AddSingleton<IChatClient>(new ScriptedChatClient("openai", cleanJson));
services.AddIngotExtraction(options => options.Retry = RetryPolicy.Default);

var provider = services.BuildServiceProvider();
var extractor = provider.GetRequiredService<IExtractor>();
var injected = await extractor.ExtractAsync<Invoice>("Extract the invoice.");
Console.WriteLine($"Extracted via IExtractor: {injected.VendorName}, {injected.Total:C}");
Console.WriteLine();

// ---------------------------------------------------------------- 3. The repair loop, traced

Console.WriteLine("== 3. Repair loop with an OpenTelemetry trace ==");
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .ConfigureResource(r => r.AddService("InvoiceExtractor"))
    .AddSource(IngotDiagnostics.SourceName)   // subscribe to Ingot's spans by name
    .AddConsoleExporter()
    .Build();

IChatClient repairing = new ScriptedChatClient("openai", badThenGood_first, badThenGood_second);
var result = await repairing.TryExtractAsync<Invoice>("Extract the invoice.");

Console.WriteLine($"Success: {result.IsSuccess} after {result.Attempts.Count} attempt(s), " +
    $"{result.AggregateUsage.TotalTokenCount} tokens.");
for (var i = 0; i < result.Attempts.Count; i++)
{
    var attempt = result.Attempts[i];
    var detail = attempt.Succeeded ? "ok" : string.Join(", ", attempt.Failures.Select(f => f.ToString()));
    Console.WriteLine($"  attempt {attempt.Number}: {detail}");
}
Console.WriteLine();
Console.WriteLine("(The Activity lines above are emitted by the OpenTelemetry console exporter.)");
