using System.Collections.Generic;
using Oqtane.Models;

namespace Oqtane.Infrastructure
{
    public class ServerState
    {
        public string SiteKey { get; set; }
        public List<string> Assemblies { get; set; } = new List<string>();
        public bool IsInitialized { get; set; } = false;
    }
}
