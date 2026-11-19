using Layeron.Plugin.Experimental;

var scenarios = new (string Name, Action Run)[]
{
    ("application lifetime + assembly dependency chain", Scenarios.AssemblyDependencyChain),
    ("application lifetime callback order", Scenarios.ApplicationLifetimeOrder),
    ("typed extension point identity", Scenarios.TypedPointIdentity),
    ("child loaders", Scenarios.ChildLoaders),
    ("dependency validation", Scenarios.DependencyValidation)
};

Console.WriteLine("Layeron experimental plugin system");
Console.WriteLine("A tiny design probe: scoped contributions, declared dependencies, application lifetime.");
Console.WriteLine();

var failures = 0;

foreach (var (name, run) in scenarios)
{
    Console.WriteLine($"Scenario: {name}");

    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {name}");
        Console.WriteLine($"     {ex.Message}");
    }

    Console.WriteLine();
}

Console.WriteLine(failures == 0
    ? $"All {scenarios.Length} scenarios passed."
    : $"{failures} of {scenarios.Length} scenarios failed.");

return failures == 0 ? 0 : 1;


