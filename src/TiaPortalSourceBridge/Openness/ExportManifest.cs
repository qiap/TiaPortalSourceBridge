using System;
using System.Collections.Generic;

namespace TiaPortalSourceBridge.Openness
{
    public sealed class ExportManifest
    {
        public int ManifestVersion { get; set; }
        public string TiaPortalVersion { get; set; }
        public string ProjectPath { get; set; }
        public string Plc { get; set; }
        public string ExportedAtUtc { get; set; }
        public List<ManifestEntry> Objects { get; set; }

        public ExportManifest()
        {
            ManifestVersion = 1;
            ExportedAtUtc = DateTime.UtcNow.ToString("o");
            Objects = new List<ManifestEntry>();
        }
    }

    public sealed class ManifestEntry
    {
        public string Kind { get; set; }
        public string Name { get; set; }
        public string GroupPath { get; set; }
        public string Language { get; set; }
        public string Format { get; set; }
        public string RelativePath { get; set; }
        public string Note { get; set; }
    }

    internal sealed class ExportResult
    {
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
    }

    internal sealed class ImportResult
    {
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public string ErrorLogPath { get; set; }
        public List<ImportFailure> Failures { get; private set; }

        public ImportResult()
        {
            ErrorLogPath = "";
            Failures = new List<ImportFailure>();
        }
    }

    internal sealed class ImportFailure
    {
        public ManifestEntry Entry { get; set; }
        public string FirstError { get; set; }
        public string RetryError { get; set; }
    }
}
