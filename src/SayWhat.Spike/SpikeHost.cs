namespace SayWhat.Spike;

public static class SpikeHost
{
    private const string PrepareName = "prepare";
    private const string RunName = "run";

    public static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] == PrepareName)
        {
            return RunPrepare(args[1..]);
        }

        if (args.Length > 0 && args[0] == RunName)
        {
            return RunHost(args[1..]);
        }

        return 0;
    }

    private static int RunHost(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        return RunCommand.Execute(args, Console.Out, Console.Error, stop.Token);
    }

    // TEMP: spike-only asset download; remove when the PH-2 Download Manager is in place.
    private static int RunPrepare(string[] args)
    {
        var folder = PrepareOptions.DefaultModelFolder;
        var target = PrepareOptions.DefaultTarget;
        IReadOnlyList<string>? sources = null;
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                Console.Error.WriteLine($"prepare: {args[index]} needs a value");
                return 2;
            }

            var value = args[index + 1];
            switch (args[index])
            {
                case "--models":
                    folder = value;
                    break;
                case "--target":
                    target = value;
                    break;
                case "--sources":
                    sources = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                default:
                    Console.Error.WriteLine($"prepare: unknown option {args[index]}");
                    return 2;
            }
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var command = new PrepareCommand(client, PrepareSources.Published, Console.Out, Console.Error);
        return command.RunAsync(new PrepareOptions(folder, target, sources)).GetAwaiter().GetResult();
    }
}
