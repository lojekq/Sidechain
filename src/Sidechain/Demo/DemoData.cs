using Sidechain.Features;
using Sidechain.Features.Core;
using Sidechain.Shell;

namespace Sidechain.Demo;

/// <summary>
/// --demo: believable data for screenshots and GIFs (spec §10). Never real data; nothing is written,
/// no real audio is touched: the engine runs on DemoBackend, settings live in memory.
/// </summary>
public static class DemoData
{
    public static void Fill(Engine engine)
    {
        var s = Settings.Current;
        s.Rules = Rules.Default();
        s.Rules[0].BoostTriggersDb = 6; // v1.1: Discord gets 6 dB louder while it talks; the headphones' master has 10 dB of room
        s.Pin = new PinSettings
        {
            Enabled = true, Seeded = true,
            Priority = [new("demo-phones", Loc.T("Demo.Phones")), new("demo-speakers", Loc.T("Demo.Speakers"))],
            Mic = new() { Enabled = true, Seeded = true, Priority = [new("demo-mic-phones", Loc.T("Demo.Mic"))] },
        };
        s.Leveler = [new("chrome", -20)];
        engine.SeedLog(new PinEvent(DateTime.Today.AddHours(21).AddMinutes(14),
            new("demo-dell", "DELL U2723QE (NVIDIA High Definition Audio)"), new("demo-phones", Loc.T("Demo.Phones"))));
        if (Args.Current.Autoplay) DemoBackend.TalkFrom = double.PositiveInfinity; // Autoplay starts the talk on cue
    }
}
