using System;
namespace TokLens
{
    // One row per tool whose usage was read, plus scanners that found log files but no
    // usage or failed. Status: ok | no_data | stale | error | not_installed.
    public sealed class ProviderHealth
    {
        public string Tool="",Status="not_installed",Error="";
        // Scanner that produced this tool's rows when it differs from the tool itself,
        // e.g. the Claude Code scanner for "Claude Desktop".
        public string Source="";
        public int FilesFound,ParsedRequests,SkippedRecords;
        public long LastSuccessMs,LastRequestMs;
    }
}
