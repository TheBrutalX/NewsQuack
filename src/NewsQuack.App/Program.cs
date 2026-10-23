using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

await builder.Build().RunAsync();
