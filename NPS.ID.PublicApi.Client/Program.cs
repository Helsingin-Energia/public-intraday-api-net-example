﻿// See https://aka.ms/new-console-template for more information

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NPS.ID.PublicApi.Client;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole();

builder.Services
    .AddEndpointsOptions()
    .AddCredentialsOptions()
    .AddSsoClientOptions();

builder.Services
    .AddSsoClient()
    .AddStompClient();

builder.Services
    .AddWebSocketConnector()
    .AddApplicationWorker();

builder.Services.AddMemoryCache();

using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();

var services = scope.ServiceProvider;

try
{
    await services
        .GetRequiredService<ApplicationWorker>()
        .RunAsync();
}
catch (Exception e)
{
    Console.WriteLine(e);
    throw;
}
