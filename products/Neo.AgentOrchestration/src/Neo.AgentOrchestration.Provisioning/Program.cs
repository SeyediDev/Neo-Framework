using Neo.AgentOrchestration.Provisioning;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
return args.FirstOrDefault() == "lsp"
    ? await LspProbeCommand.Run(args, Console.Out, Console.Error, shutdown.Token)
    : await ProvisioningCommand.RunAsync(args, Console.Out, Console.Error, shutdown.Token);
