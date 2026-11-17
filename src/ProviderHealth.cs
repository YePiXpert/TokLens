using System;
namespace TokLens
{
    public sealed class ProviderHealth
    {
        public string Tool="",Status="not_installed",Error="";
        public int FilesFound,ParsedRequests,SkippedRecords;
        public long LastSuccessMs;
    }
}
