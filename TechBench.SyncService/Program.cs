using Microsoft.Extensions.Options;
using TechBench.Providers;
using TechBench.SyncService;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "TECHBENCH_SYNC_");
builder.Services.Configure<SyncServiceOptions>(
    builder.Configuration.GetSection(SyncServiceOptions.SectionName));

if (args.Any(static value => value.Equals("--set-whd-secret", StringComparison.OrdinalIgnoreCase)
    || value.Equals("--delete-whd-secret", StringComparison.OrdinalIgnoreCase)
    || value.Equals("--check-whd-secret", StringComparison.OrdinalIgnoreCase)))
{
    using var commandHost = builder.Build();
    var store = new WhdSecretStore(commandHost.Services.GetRequiredService<IOptions<SyncServiceOptions>>());
    if (args.Any(static value => value.Equals("--set-whd-secret", StringComparison.OrdinalIgnoreCase)))
    {
        var secret = ReadSecret();
        try
        {
            store.Write(secret);
            Console.WriteLine($"Protected WHD credential saved at {store.Path}.");
        }
        finally
        {
            secret = string.Empty;
        }
    }
    else if (args.Any(static value => value.Equals("--delete-whd-secret", StringComparison.OrdinalIgnoreCase)))
    {
        store.Delete();
        Console.WriteLine("Protected WHD credential removed.");
    }
    else
    {
        Console.WriteLine(store.Exists
            ? $"A protected WHD credential exists at {store.Path}."
            : $"No protected WHD credential exists at {store.Path}.");
    }

    return;
}

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "TechBench WHD Sync Service";
});
builder.Services.AddSingleton<WhdSecretStore>();
builder.Services.AddSingleton<SyncSqlRepository>();
builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<SyncServiceOptions>>().Value;
    return new WhdRestClient(new HttpClient { Timeout = options.WhdRequestTimeout });
});
builder.Services.AddSingleton<WhdSyncEngine>();
builder.Services.AddHostedService<WhdSyncWorker>();

await builder.Build().RunAsync();

static string ReadSecret()
{
    if (Console.IsInputRedirected)
    {
        var redirected = Console.In.ReadToEnd().TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(redirected))
        {
            throw new InvalidOperationException("No WHD credential was provided on standard input.");
        }

        return redirected;
    }

    Console.Write("WHD API key, token, or password: ");
    var characters = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            break;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (characters.Count > 0)
            {
                characters.RemoveAt(characters.Count - 1);
            }

            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            characters.Add(key.KeyChar);
        }
    }

    var value = new string(characters.ToArray());
    characters.Clear();
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException("A nonempty WHD credential is required.");
    }

    return value;
}
