await Planner.Api.Checks.PasskeyChecks.RunAsync((condition, message) =>
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS: {message}");
});
