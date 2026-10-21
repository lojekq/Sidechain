namespace Sidechain.Demo;

/// <summary>
/// --autoplay for gif.ps1 (spec §10), seconds after the window is on screen; recording starts about 1 s later:
/// Spotify plays → Discord talks at 2.5 s, Spotify and Chrome step down by 12 dB in 80 ms → silence at 6 s,
/// they come back after the 0.4 s hold → calm until the loop.
/// </summary>
public static class Autoplay
{
    public static void Start(MainWindow w)
    {
        DemoBackend.Clock.Restart();
        DemoBackend.TalkFrom = 2.5;
        DemoBackend.TalkTo = 6.0;
    }
}
