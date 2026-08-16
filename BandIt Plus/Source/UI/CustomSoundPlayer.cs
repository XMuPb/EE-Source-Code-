using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BandItPlus.UI
{
    // Wave 4.25.8: Bannerlord 1.4.5 does NOT load community-mod sound data.
    //
    // Engine-log proof (rgl_log): the module-sound loader runs ONCE at startup and
    // iterates only the 4 BASE modules (Native / SandBox / StoryMode / NavalDLC).
    // A community mod's module_sounds.xml (loose .wav/.ogg) AND its FMOD banks are
    // never registered — GetEventIdFromString("bp/...") returns -1 forever. So we
    // bypass Bannerlord's audio entirely and hand the .wav to the OS via the Win32
    // winmm PlaySound API.
    //
    // Why winmm over System.Media.SoundPlayer: the path-based PlaySound call keeps
    // no managed object alive during playback, so there is no SoundPlayer instance
    // whose garbage collection could cut the sound off mid-clip.
    //
    // Caveats (acceptable for short UI stings):
    //   - plays on the OS mixer, NOT Bannerlord's FMOD bus → ignores the in-game
    //     sound sliders.
    //   - single channel: a new Play() stops the previous one. This suits our
    //     sequential flow (cinematic theme → stop → book page sound) and the Stop()
    //     call on cinematic skip.
    //   - .wav only (PlaySound can't decode .ogg) — both clips ship as .wav.
    public static class CustomSoundPlayer
    {
        [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        private const uint SND_ASYNC = 0x0001;        // return immediately, play on a background thread
        private const uint SND_FILENAME = 0x00020000; // pszSound is a file path
        private const uint SND_NODEFAULT = 0x0002;    // no system-default beep if the file fails
        private const uint SND_LOOP = 0x0008;         // loop until Stop()/next Play() (needs SND_ASYNC)

        private static string SoundPath(string file)
        {
            try { return Path.Combine(TaleWorlds.Library.BasePath.Name, "Modules", "BandItPlus", "ModuleSounds", file); }
            catch { return null; }
        }

        // Fire-and-forget .wav playback (book page sound, cinematic theme).
        public static void Play(string wavFile)
        {
            try
            {
                var path = SoundPath(wavFile);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Log("missing file: " + (path ?? "<null>")); return; }
                bool ok = PlaySound(path, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT);
                Log("play " + wavFile + " -> " + ok);
            }
            catch (Exception ex) { Log("play fail (" + wavFile + "): " + ex.Message); }
        }

        // Looping .wav (typewriter clatter while text types). Loops until Stop() or
        // the next Play(). SND_LOOP requires SND_ASYNC. Single channel — see header.
        public static void PlayLoop(string wavFile)
        {
            try
            {
                var path = SoundPath(wavFile);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Log("missing loop file: " + (path ?? "<null>")); return; }
                bool ok = PlaySound(path, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT | SND_LOOP);
                Log("loop " + wavFile + " -> " + ok);
            }
            catch (Exception ex) { Log("loop fail (" + wavFile + "): " + ex.Message); }
        }

        // Stop whatever is currently playing (cinematic skip / close).
        public static void Stop()
        {
            try { PlaySound(null, IntPtr.Zero, 0); }
            catch { }
        }

        private static void Log(string m)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[CustomSound] " + m); }
            catch { }
        }
    }
}
