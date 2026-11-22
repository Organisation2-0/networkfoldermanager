using System.IO;
using System.Media;
using System.Windows.Media;

namespace NetworkFolderManager.Services;

/// <summary>
/// Spielt den Klingelton für eingehende Anrufe in einer Schleife ab.
/// Entweder eine eigene Audiodatei (mp3, wav, wma, m4a, aac …) oder – ohne Auswahl bzw. bei Fehlern –
/// ein eingebauter Klingelton, der zur Laufzeit erzeugt wird. Nur auf dem UI-Thread verwenden.
/// </summary>
public sealed class RingtoneService : IDisposable
{
    private MediaPlayer? _player;
    private SoundPlayer? _builtIn;

    public bool IsPlaying { get; private set; }

    /// <param name="path">Audiodatei oder leer für den Standard-Klingelton</param>
    /// <param name="volume">0–100</param>
    public void Play(string? path, int volume)
    {
        Stop();
        volume = Math.Clamp(volume, 0, 100);
        IsPlaying = true;

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && TryPlayFile(path, volume))
            return;

        PlayBuiltIn(volume);
    }

    private bool TryPlayFile(string path, int volume)
    {
        try
        {
            var player = new MediaPlayer { Volume = volume / 100.0 };
            player.MediaEnded += (_, _) =>
            {
                player.Position = TimeSpan.Zero;
                player.Play();
            };
            var failed = false;
            player.MediaFailed += (_, e) =>
            {
                failed = true;
                LogService.Info($"Klingelton konnte nicht abgespielt werden ({path}): {e.ErrorException?.Message}");
                if (ReferenceEquals(_player, player) && IsPlaying)
                {
                    StopPlayer();
                    PlayBuiltIn(volume);
                }
            };
            player.Open(new Uri(path, UriKind.Absolute));
            player.Play();
            _player = player;
            return !failed;
        }
        catch (Exception ex)
        {
            LogService.Error("Ringtone/File", ex);
            return false;
        }
    }

    private void PlayBuiltIn(int volume)
    {
        try
        {
            _builtIn = new SoundPlayer(new MemoryStream(BuildDefaultRingtone(volume / 100.0)));
            _builtIn.PlayLooping();
        }
        catch (Exception ex)
        {
            LogService.Error("Ringtone/BuiltIn", ex);
        }
    }

    public void Stop()
    {
        IsPlaying = false;
        StopPlayer();
        if (_builtIn != null)
        {
            try { _builtIn.Stop(); } catch { /* ignorieren */ }
            _builtIn.Dispose();
            _builtIn = null;
        }
    }

    private void StopPlayer()
    {
        if (_player == null)
            return;
        try { _player.Stop(); _player.Close(); } catch { /* ignorieren */ }
        _player = null;
    }

    /// <summary>Erzeugt einen WAV-Klingelton: zwei Doppeltöne, danach Pause (ca. 3,2 s, wird wiederholt).</summary>
    private static byte[] BuildDefaultRingtone(double volume)
    {
        const int rate = 44100;
        var samples = new short[(int)(rate * 3.2)];

        void Tone(double start, double length, double f1, double f2)
        {
            var from = (int)(start * rate);
            var count = (int)(length * rate);
            for (var i = 0; i < count && from + i < samples.Length; i++)
            {
                var t = i / (double)rate;
                // weiche Ein-/Ausblendung gegen Knacken
                var env = Math.Min(1.0, Math.Min(i, count - i) / (0.02 * rate));
                var v = (Math.Sin(2 * Math.PI * f1 * t) + Math.Sin(2 * Math.PI * f2 * t)) * 0.5 * env;
                samples[from + i] = (short)(v * 0.55 * volume * short.MaxValue);
            }
        }

        Tone(0.00, 0.35, 880, 1108);
        Tone(0.45, 0.35, 880, 1108);
        Tone(0.90, 0.35, 988, 1244);
        Tone(1.35, 0.35, 988, 1244);

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var dataLen = samples.Length * 2;
            w.Write("RIFF"u8);
            w.Write(36 + dataLen);
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)1);      // PCM
            w.Write((short)1);      // mono
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(dataLen);
            foreach (var s in samples)
                w.Write(s);
        }
        return ms.ToArray();
    }

    public void Dispose() => Stop();
}
