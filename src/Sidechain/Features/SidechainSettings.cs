using Sidechain.Features;
using Sidechain.Features.Core;

namespace Sidechain.Shell
{
    public sealed partial class Settings
    {
        public List<Rule>? Rules { get; set; }              // null = first start: the default rule
        public PinSettings Pin { get; set; } = new();
        public int? WindowsDuckingBackup { get; set; }       // value before "Turn off"; -1 = there was none
        public List<LevelerEntry> Leveler { get; set; } = []; // v1.0: apps turned down when they get loud, 3 at most
        public List<BoostEntry> Boost { get; set; } = [];     // v1.1: apps made louder by hand
        public string? PauseHotkey { get; set; } = "Ctrl+Alt+Shift+S"; // v1.0: pause / resume; null = none
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
        public MicPin Mic { get; set; } = new();             // v1.0: the same for the microphone

        public PinSettings Clone() => new()
        {
            Enabled = Enabled, AlsoCommunications = AlsoCommunications, Priority = [.. Priority], Seeded = Seeded,
            Mic = new() { Enabled = Mic.Enabled, Priority = [.. Mic.Priority], Seeded = Mic.Seeded },
        };
    }

    public sealed class MicPin
    {
#if DEBUG
        public bool Enabled { get; set; }
#else
        public bool Enabled { get; set; } = true;
#endif
        public List<AudioDevice> Priority { get; set; } = [];
        public bool Seeded { get; set; }
    }
}
