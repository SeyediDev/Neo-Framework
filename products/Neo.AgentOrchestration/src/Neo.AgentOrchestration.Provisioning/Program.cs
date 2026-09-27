using Neo.AgentOrchestration.Provisioning;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
return await ProvisioningCommand.RunAsync(args, Console.Out, Console.Error, shutdown.Token);
