using Tideline.Cli;

return await new CliApp(Console.Out, TimeProvider.System).RunAsync(args);
