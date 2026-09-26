namespace Neo.Companion.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"] || args.Length == 0)
        {
            Console.WriteLine("neo new feature <Name> --namespace <App.Namespace> --route <items> --output <new-directory> --neo-root <matching-checkout>");
            return 0;
        }
        try
        {
            if (args.Length != 11 || args[0] != "new" || args[1] != "feature") throw new ArgumentException("Use --help for syntax.");
            var options = new Dictionary<string,string>(StringComparer.Ordinal);
            for (var i = 3; i < args.Length; i += 2)
                if (!options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Duplicate option.");
            if (!options.Keys.Order().SequenceEqual(new[] { "--namespace", "--neo-root", "--output", "--route" }.Order()))
                throw new ArgumentException("Specify --namespace, --route, --output and --neo-root exactly once.");
            var output = FeatureGenerator.Generate(args[2], options["--namespace"], options["--route"], options["--output"], options["--neo-root"]);
            Console.WriteLine($"Created {output}. Read README.md, then run dotnet run --project <output>. No build, database or application was executed.");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message); return 1;
        }
    }
}
