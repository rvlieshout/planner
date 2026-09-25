await Planner.Api.Checks.PasskeyChecks.RunAsync((condition, message) =>
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS: {message}");
});

await Planner.Api.Checks.IssuerChecks.RunAsync((condition, message) =>
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS: {message}");
});

await Planner.Api.Checks.McpAuthorizeChecks.RunAsync((condition, message) =>
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS: {message}");
});

await Planner.Api.Checks.McpEndpointChecks.RunAsync((condition, message) =>
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS: {message}");
});
