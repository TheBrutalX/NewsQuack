using NewsQuack.App.Configuration;
using Microsoft.Extensions.Hosting;

// T-002: load + validate the v2 config before anything else runs. On any failure, print each message to stderr
// (one per line) and exit with code 1 — never throw out of startup, never print a stack trace.
var searchPaths = ConfigLoader.GetSearchPaths(args, () => Environment.GetEnvironmentVariable(ConfigLoader.ConfigEnvironmentVariable));

var configResult = ConfigLoader.FindFirstExisting(searchPaths) is { } configPath
    ? ConfigLoader.LoadAndValidate(configPath)
    : ConfigLoader.MissingFile(searchPaths);

if (!configResult.IsSuccess)
{
    foreach (var message in configResult.Errors)
        Console.Error.WriteLine(message);

    Environment.Exit(1);
}

var builder = Host.CreateApplicationBuilder(args);

await builder.Build().RunAsync();
