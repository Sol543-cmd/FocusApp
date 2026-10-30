using System;
using System.IO;
using System.Media;

namespace FocusApp;

/// <summary>Chimes generated in code, so there are no sound files to lose.</summary>
public static class Sounds
{
    private static SoundPlayer? _player; // kept so playback isn't cut off early

    // Rising chime: a focus session is starting.
    public static void PlayStart(int volume) => Play(new[] { 523.25, 659.25, 783.99, 1046.50 }, volume);

    // Falling chime: a focus session is over and a break (or the end of the day) begins.
    public static void PlayEnd(int volume) => Play(new[] { 1046.50, 783.99, 659.25, 523.25 }, volume);

    private static void Play(double[] notes, int volume)
    {
        if (volume <= 0) return;
        try
        {
            var wav = MakeChime(notes, volume / 100.0);
            _player?.Stop();
            _player = new SoundPlayer(new MemoryStream(wav));
            _player.Play();
        }
        catch
        {
            // No sound device, or the device is busy. The timer keeps working either way.
        }
    }

    private static byte[] MakeChime(double[] notes, double volume)
    {
        const int rate = 44100;
        const double gap = 0.17;     // seconds between notes
        const double ring = 1.1;     // how long each note rings
        int length = (int)(rate * (gap * (notes.Length - 1) + ring));
        var mix = new double[length];

        for (int i = 0; i < notes.Length; i++)
        {
            int start = (int)(i * gap * rate);
            double f = notes[i];
            for (int n = 0; start + n < length && n < ring * rate; n++)
            {
                double t = n / (double)rate;
                double attack = Math.Min(1.0, t / 0.004);
                double body = Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t * 4.5);
                double shimmer = 0.3 * Math.Sin(2 * Math.PI * f * 2 * t) * Math.Exp(-t * 8);
                mix[start + n] += attack * (body + shimmer);
            }
        }

        double peak = 0;
        foreach (var s in mix) peak = Math.Max(peak, Math.Abs(s));
        double scale = peak > 0 ? 0.9 * volume / peak : 0;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = length * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataBytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataBytes);
        foreach (var s in mix) w.Write((short)Math.Clamp(s * scale * short.MaxValue, short.MinValue, short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
