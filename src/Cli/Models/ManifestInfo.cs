using System;
using System.Collections.Generic;
using System.Text;

namespace Cli.Models
{
    internal sealed class ManifestInfo
    {
        public string Module { get; init; } = "";
        public string Action { get; init; } = "";
        public int Version { get; init; }
        public string Hash { get; init; } = "";
        public string Content { get; init; } = "";
        public bool Enabled { get; init; }
        public bool IsDefault { get; init; }
        public int ManifestSize { get; init; }
    }
}
