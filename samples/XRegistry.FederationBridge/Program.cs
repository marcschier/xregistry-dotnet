// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using XRegistry.Samples;
using XRegistry.Samples.Bridge;

if (args.Length == 1 && args[0] == "--runtime-info")
{
    Console.WriteLine(BridgeRuntimeInfo.Json());
    return 0;
}

if (args.Length != 2 || args[0] != "--config")
{
    Console.Error.WriteLine("Usage: XRegistry.FederationBridge --config <explicit-bridge-configuration.json> | --runtime-info");
    return 2;
}

using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var configuration = await BridgeConfiguration.LoadAsync(args[1], cancellationToken: startup.Token).ConfigureAwait(false);
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
RegistrySampleHosting.Configure(builder, configuration.Hosting);
var app = builder.Build();
await using (app.ConfigureAwait(false))
{
    RegistrySampleHosting.UseSecurity(app, configuration.Hosting);
    BridgeApplication.Map(app, configuration.Options, configuration.Sources, configuration.Mounts)
        .AllowAnonymousRegistryReads();
    await app.RunAsync().ConfigureAwait(false);
}
return 0;
