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

var hasWhdSecretCommand = args.Any(static value =>
    value.Equals("--set-whd-secret", StringComparison.OrdinalIgnoreCase)
    || value.Equals("--delete-whd-secret", StringComparison.OrdinalIgnoreCase)
    || value.Equals("--check-whd-secret", StringComparison.OrdinalIgnoreCase));
var hasSageSecretCommand = args.Any(static value =>
    value.Equals("--set-sage-secret", StringComparison.OrdinalIgnoreCase)
    || value.Equals("--delete-sage-secret", StringComparison.OrdinalIgnoreCase)
    || value.Equals("--check-sage-secret", StringComparison.OrdinalIgnoreCase));
if (hasWhdSecretCommand || hasSageSecretCommand)
{
    if (hasWhdSecretCommand && hasSageSecretCommand)
    {
        throw new InvalidOperationException("Run one credential-management command at a time.");
    }

    using var commandHost = builder.Build();
    var options = commandHost.Services.GetRequiredService<IOptions<SyncServiceOptions>>();
    if (hasWhdSecretCommand)
    {
        var store = new WhdSecretStore(options);
        if (args.Any(static value => value.Equals("--set-whd-secret", StringComparison.OrdinalIgnoreCase)))
        {
            var secret = ReadSecret("WHD API key, token, or password: ");
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
    }
    else
    {
        var store = new SageSecretStore(options);
        if (args.Any(static value => value.Equals("--set-sage-secret", StringComparison.OrdinalIgnoreCase)))
        {
            var secret = ReadSecret("Sage ODBC password: ");
            try
            {
                store.Write(secret);
                Console.WriteLine($"Protected Sage ODBC credential saved at {store.Path}.");
            }
            finally
            {
                secret = string.Empty;
            }
        }
        else if (args.Any(static value => value.Equals("--delete-sage-secret", StringComparison.OrdinalIgnoreCase)))
        {
            store.Delete();
            Console.WriteLine("Protected Sage ODBC credential removed.");
        }
        else
        {
            Console.WriteLine(store.Exists
                ? $"A protected Sage ODBC credential exists at {store.Path}."
                : $"No protected Sage ODBC credential exists at {store.Path}.");
        }
    }

    return;
}

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "TechBench Sync Service";
});
builder.Services.AddSingleton<WhdSecretStore>();
builder.Services.AddSingleton<SageSecretStore>();
builder.Services.AddSingleton<FireDrillSecretStore>();
builder.Services.AddSingleton<SyncSqlRepository>();
builder.Services.AddSingleton<ISageOdbcWorkerProcessClient, SageOdbcWorkerProcessClient>();
builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<SyncServiceOptions>>().Value;
    return new WhdRestClient(new HttpClient { Timeout = options.WhdRequestTimeout });
});
builder.Services.AddSingleton<WhdSyncEngine>();
builder.Services.AddSingleton<SageCustomerSyncEngine>();
builder.Services.AddSingleton<FireDrillSyncEngine>();
builder.Services.AddHostedService<WhdSyncWorker>();
builder.Services.AddHostedService<SageCustomerSyncWorker>();
builder.Services.AddHostedService<FireDrillSyncWorker>();

await builder.Build().RunAsync();

static string ReadSecret(string prompt)
{
    if (Console.IsInputRedirected)
    {
        var redirected = Console.In.ReadToEnd().TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(redirected))
        {
            throw new InvalidOperationException("No credential was provided on standard input.");
        }

        return redirected;
    }

    Console.Write(prompt);
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
        throw new InvalidOperationException("A nonempty credential is required.");
    }

    return value;
}
