using Servli;

try { await new App().RunAsync(args); }
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    if (args.Contains("--debug")) Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
