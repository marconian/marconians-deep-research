using System;

namespace Marconian.ResearchAgent.ConsoleApp;

internal sealed class CommandLineOptions
{
    public string? ResumeSessionId { get; set; }

    public string? Query { get; set; }

    public string? ReportsDirectory { get; set; }

    public string? DumpSessionId { get; set; }

    public string? DumpDirectory { get; set; }

    public string? DumpType { get; set; }

    public int DumpLimit { get; set; } = 200;

    public string? DiagnoseComputerUse { get; set; }

    public int DiagnoseComputerUseConcurrency { get; set; } = 1;

    public string? ReportSessionId { get; set; }

    public string? DiagnosticsMode { get; set; }

    public string? DeleteSessionId { get; set; }

    public bool DeleteIncomplete { get; set; }

    public bool DeleteAll { get; set; }

    public bool SkipPlanner { get; set; }

    public bool ListSessions { get; set; }

    public bool ShowHelp { get; set; }

    public bool HasDeleteRequest => DeleteAll || DeleteIncomplete || !string.IsNullOrWhiteSpace(DeleteSessionId);

    public bool HasInteractiveOperation =>
        !string.IsNullOrWhiteSpace(Query) ||
        !string.IsNullOrWhiteSpace(ResumeSessionId) ||
        !string.IsNullOrWhiteSpace(ReportSessionId) ||
        HasDeleteRequest ||
        !string.IsNullOrWhiteSpace(DiagnosticsMode) ||
        !string.IsNullOrWhiteSpace(DumpSessionId) ||
        !string.IsNullOrWhiteSpace(DiagnoseComputerUse);

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string current = args[i];
            switch (current)
            {
                case "--resume":
                    if (TryGetNext(args, ref i, out var resume))
                    {
                        options.ResumeSessionId = resume;
                    }
                    break;
                case "--query":
                    if (TryGetNext(args, ref i, out var query))
                    {
                        options.Query = query;
                    }
                    break;
                case "--reports":
                    if (TryGetNext(args, ref i, out var reports))
                    {
                        options.ReportsDirectory = reports;
                    }
                    break;
                case "--dump-session":
                    if (TryGetNext(args, ref i, out var dump))
                    {
                        options.DumpSessionId = dump;
                    }
                    break;
                case "--dump-dir":
                case "--dump-output":
                    if (TryGetNext(args, ref i, out var dumpDir))
                    {
                        options.DumpDirectory = dumpDir;
                    }
                    break;
                case "--dump-type":
                    if (TryGetNext(args, ref i, out var dumpType))
                    {
                        options.DumpType = dumpType;
                    }
                    break;
                case "--dump-limit":
                    if (TryGetNext(args, ref i, out var limit) && int.TryParse(limit, out int parsedLimit) && parsedLimit > 0)
                    {
                        options.DumpLimit = parsedLimit;
                    }
                    break;
                case "--diagnose-computer-use":
                    if (TryGetNext(args, ref i, out var diagnose))
                    {
                        options.DiagnoseComputerUse = diagnose;
                    }
                    break;
                case "--diagnose-computer-use-concurrency":
                    if (TryGetNext(args, ref i, out var diagnoseConcurrency) &&
                        int.TryParse(diagnoseConcurrency, out int parsedConcurrency) &&
                        parsedConcurrency > 0)
                    {
                        options.DiagnoseComputerUseConcurrency = parsedConcurrency;
                    }
                    break;
                case "--report-session":
                    if (TryGetNext(args, ref i, out var reportSession))
                    {
                        options.ReportSessionId = reportSession;
                    }
                    break;
                case "--diagnostics-mode":
                    if (TryGetNext(args, ref i, out var diagnosticsMode))
                    {
                        options.DiagnosticsMode = diagnosticsMode;
                    }
                    break;
                case "--delete-session":
                    if (TryGetNext(args, ref i, out var deleteSession))
                    {
                        options.DeleteSessionId = deleteSession;
                    }
                    break;
                case "--delete-incomplete":
                    options.DeleteIncomplete = true;
                    break;
                case "--delete-all":
                    options.DeleteAll = true;
                    break;
                case "--skip-planner":
                    options.SkipPlanner = true;
                    break;
                case "--list-sessions":
                    options.ListSessions = true;
                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
            }
        }

        return options;
    }

    private static bool TryGetNext(string[] args, ref int index, out string? value)
    {
        if (index + 1 < args.Length)
        {
            value = args[++index];
            return true;
        }

        value = null;
        return false;
    }
}
