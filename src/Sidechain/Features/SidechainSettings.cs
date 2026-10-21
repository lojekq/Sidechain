using Sidechain.Features;
using Sidechain.Features.Core;

namespace Sidechain.Shell
{
    public sealed partial class Settings
    {
        public List<Rule>? Rules { get; set; }              // null = first start: the default rule
        public PinSettings Pin { get; set; } = new();
        public int? WindowsDuckingBackup { get; set; }       // value before "Turn off"; -1 = there was none
    }
}

namespace Sidechain.Features
{
    public sealed class PinSettings
    {
#if DEBUG
        public bool Enabled { get; set; }                    // 00-shared §9.4: pinning is off in Debug while 8 sessions share the PC
#else
        public bool Enabled { get; set; } = true;
#endif
        public bool AlsoCommunications { get; set; }
        public List<AudioDevice> Priority { get; set; } = [];
        public bool Seeded { get; set; }                     // the first default device was put on the list once

        public PinSettings Clone() => new() { Enabled = Enabled, AlsoCommunications = AlsoCommunications, Priority = [.. Priority], Seeded = Seeded };
    }
}
