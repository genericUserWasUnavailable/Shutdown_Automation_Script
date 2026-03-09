using System.Text.Json;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Mixer;
namespace AntiADbreakScript
{
    internal class Program
    {

        public class AppConfig
        {
            public HashSet<string> BrowserTypes { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
            "Opera",
            "chrome",
            "firefox"
                };
            public string OAth { get; set; }
            public string clientID { get; set; }
            public string VlcPath { get; set; } = "vlc";
        }
        private static readonly string ConfigFile = "config.json";
        static void LoadConfig()
        {
            if (File.Exists(ConfigFile))
            {
                try
                {
                    string json = File.ReadAllText(ConfigFile);
                    Config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                catch
                {
                    Console.WriteLine("Config file corrupted. Recreating default config.");
                    Config = new AppConfig();
                    Console.WriteLine("Paste in your VLC directory");
                    Config.VlcPath = Console.ReadLine() ?? "vlc";
                    SaveConfig();
                }
            }
            else
            {
                Config = new AppConfig();
                SaveConfig();
            }

            vlcPath = Config.VlcPath;
        }
        static void SaveConfig()
        {
            string json = JsonSerializer.Serialize(
                Config,
                new JsonSerializerOptions { WriteIndented = true }
            );

            File.WriteAllText(ConfigFile, json);
        }
        #region Fields
        private static MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
        private static MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        private static readonly CancellationTokenSource exitCts = new CancellationTokenSource();
        private static readonly CancellationToken exitToken = exitCts.Token;
        private static bool vlcActive = false;
        private static Process? vlcProcess = null;
        private static string? twitchUrl;
        private static string? vlcPath;
        private static AppConfig Config = default!;
        #endregion
        static Program()
        {
            LoadConfig();
        }
        static async Task Main(string[] args)
        {
            using var mutex = new Mutex(initiallyOwned: true, name: "Global\\VLCMutex", out bool isNew);
            if (exitToken.IsCancellationRequested || !isNew) return;


            while (!exitCts.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;

                    if (key == ConsoleKey.Spacebar)
                        await ToggleVLC();
                }

                await Task.Delay(50);
            }
        }
        public static Task ToggleVLC()
        {
            if (vlcActive && (vlcProcess == null || vlcProcess.HasExited))
                vlcActive = false;
            else vlcActive = !vlcActive;

            if (vlcActive)
                StartVLC();
            else
                StopVLC();

            return Task.CompletedTask;
        }
        static string ExtractChannelName(string input)
        {
            input = input.Trim();

            // If user enters a full URL
            if (input.Contains("twitch.tv"))
            {
                var parts = input.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return parts[^1]; // last segment
            }
            // Otherwise assume it's already a username
            return input;
        }
        static string BuildM3U8(string channel)
        {
            return $"https://usher.ttvnw.net/api/channel/hls/{channel}.m3u8";
        }
        static void StartVLC()
        {
            if (twitchUrl == null)
            {
                Console.Write("Enter Twitch channel (username or URL): ");
                string input = Console.ReadLine();

                string channel = ExtractChannelName(input);
                twitchUrl = BuildM3U8(channel);
            }

            vlcProcess = Process.Start(new ProcessStartInfo
            {
                FileName = vlcPath,
                Arguments = $"\"{twitchUrl}\" --no-video-title-show",
                UseShellExecute = false
            });

            MuteBrowser();
            Console.WriteLine("VLC started, browser muted.");
        }
        static void StopVLC()
        {
            try
            {
                if (vlcProcess != null && !vlcProcess.HasExited)
                    vlcProcess.Kill();
            }
            catch { }

            UnmuteBrowser();
            Console.WriteLine("VLC stopped, browser unmuted.");
        }
        static void MuteBrowser()
        {
            var sessions = device.AudioSessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                string name = session.DisplayName?.ToLower() ?? "";

                if (Config.BrowserTypes.Any(browser => name.Contains(browser, StringComparison.OrdinalIgnoreCase)))
                    session.SimpleAudioVolume.Mute = true;
            }
        }
        static void UnmuteBrowser()
        {
            var sessions = device.AudioSessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                string name = session.DisplayName?.ToLower() ?? "";

                if (Config.BrowserTypes.Any(browser => name.Contains(browser, StringComparison.OrdinalIgnoreCase)))
                    session.SimpleAudioVolume.Mute = false;
            }
        }
    }
}
