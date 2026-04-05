using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PriorityControl.Models
{
    [DataContract]
    public sealed class AppSettings
    {
        public AppSettings()
        {
            Entries = new List<AppEntry>();
        }

        [DataMember(Order = 0)]
        public List<AppEntry> Entries { get; set; }

        [DataMember(Order = 1)]
        public bool StartWithWindows { get; set; }
    }
}
