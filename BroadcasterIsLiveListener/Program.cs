using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Media;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static BroadcasterIsLiveListener.Program;
using static BroadcasterIsLiveListener.TwitchApi;

namespace BroadcasterIsLiveListener
{
    static internal class     AnsiSupport
    {
        private const int STD_OUTPUT_HANDLE = -11;
        private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        public static void Enable()
        {
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);

            if (!GetConsoleMode(handle, out uint mode))
                return;

            SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
    }
    static internal class     Program
    {
        public const bool debugMode = false;
        private static void ShowImageForDuration(string path, TimeSpan duration) // to be implemented, maybe
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });

                if (proc == null)
                    return;

                Task.Run(async () =>
                {
                    await Task.Delay(duration);
                    try
                    {
                        if (!proc.HasExited)
                            proc.Kill();
                    }
                    catch { }
                });
            }
            catch
            { }
        }
        public static DateTime LastKeepAlive
        {
            get => new DateTime(Interlocked.Read(ref _lastKeepAliveTicks), DateTimeKind.Utc);
            set => Interlocked.Exchange(ref _lastKeepAliveTicks, value.Ticks);
        }
        #region fields
        private static int ColourfulArrayUpdateMS => Config.ColourfulArrayRefreshRate;
        private static double ColourfulArrayAnimation => Config.ColourfulArrayAnimationSpeed;
        private static bool ColourfulArray => Config.ColourfulArray;
        private static volatile string _currentFrame = "";
        private static volatile bool _layoutChanged = false;
        private static long _lastKeepAliveTicks = 0;
        private static DateTime UIDisabled = DateTime.Now;
        private static readonly HttpClientHandler _ytHandler;
        private static readonly HttpClient _ytClient;
        private static readonly HashSet<string> youtubeSet = new HashSet<string>();
        private static readonly List<string> youtubeList = new List<string>();
        private static readonly CancellationTokenSource exitCts = new CancellationTokenSource();
        private static readonly SoundPlayer _player;
        private static Dictionary<string, IAlertSoundPlayer> _streamerSounds = new();
        private static Dictionary<string, string> _streamerSoundPaths = new();
        private static TwitchApi twitch;
        private static bool watcherActive = true;
        private static bool watchOnline = true;
        private static bool UpdateConfig = false;
        private static bool LegacyPolling = false;
        private static bool _configNotFound = false;
        private static string SelectedWebhookKey = string.Empty;
        private static string ConfigFileName = "broadcastListener.json";
        public static string ConfigPath = string.Empty;
        public static readonly SemaphoreSlim _saveConfigSemaphore = new(1, 1);
        public static readonly CancellationToken exitToken = exitCts.Token;
        public static Dictionary<string, StreamerConfig> streamerSet = new(StringComparer.OrdinalIgnoreCase);
        public static StringBuilder _sb = new StringBuilder();
        public static int _uiInteractionActive = 0;
        public static string CurrentFrame => _currentFrame;
        public static AppConfig Config { get; private set; } = null!;
        private const string
            STREAMER_LIVE = "🔴 **{0} is now LIVE!** @here\nhttps://twitch.tv/{0} ",
            TUTORIAL =
      @"1. Open the config file in the folder that just opened.
        2. Log into https://dev.twitch.tv/console/apps
        3. Click your registered application.
        4. Copy the Client ID into the config file.
        5. Click 'New Secret' and paste the Client Secret into the config file.
        6. Save the file and restart this program.",
            WebhookCancelled = "NO_WEBHOOK";
        #endregion
        public static MMDevice? GetPlaybackDevice(string? preferredName)
        {
            var enumerator = new MMDeviceEnumerator();

            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (device.FriendlyName.Contains(preferredName, StringComparison.OrdinalIgnoreCase))
                        return device;
                }
            }
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        private static ParsedArgs ParseArgs(string[] args)
        {
            var parsed = new ParsedArgs();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--webhook":
                    case "-w":
                        if (i + 1 < args.Length)
                            parsed.WebhookKey = args[++i];
                        break;

                    case "--config":
                    case "-c":
                        if (i + 1 < args.Length)
                            ConfigFileName = args[++i];
                        break;

                    case "--streamer":
                    case "-s":
                        if (i + 1 < args.Length)
                        {
                            var name = args[++i];
                            var cfg = ResolveStreamer(name, streamerSet);

                            parsed.Streamers[name] = cfg;
                            streamerSet[name] = cfg;
                            parsed.AutoRun = true;
                        }
                        break;

                    default:
                        var parsedName = args[i];
                        var cfgDefault = ResolveStreamer(parsedName, streamerSet);

                        parsed.Streamers[parsedName] = cfgDefault;
                        streamerSet[parsedName] = cfgDefault;

                        parsed.AutoRun = streamerSet.Count > 0;
                        break;
                }
            }
            return parsed;
        }
        private static StreamerConfig ResolveStreamer(string name, Dictionary<string, StreamerConfig> streamerSet)
        {
            if (Config.Streams.TryGetValue(name, out var fromConfig))
                return fromConfig;

            if (streamerSet.TryGetValue(name, out var fromSet))
                return fromSet;

            return new StreamerConfig();
        }
        private static async void KeyPressHandler(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            Console.CursorVisible = true;
            if (_uiInteractionActive == 1 && DateTime.Now >= UIDisabled)
            {
                _uiInteractionActive = 0;
            }
            if (Interlocked.Exchange(ref _uiInteractionActive, 1) == 1)
            {
                Thread.Sleep(20);
                return;
            }
            UIDisabled = DateTime.Now.AddSeconds(5);
            watcherActive = false;

            if (e.SpecialKey == ConsoleSpecialKey.ControlBreak)
            {
                if (AskYesNo("open config-folder?"))
                    OpenConfigFolder();
                Config.AccessTokenExpiresAt = null;
                Config.AccessToken = "";

                PlayWav();
                string currentList = "";
                foreach (var b in streamerSet)
                    currentList += b.Key + '\n';

                if (AskYesNo(currentList + "\nupdate Streamer list?"))
                {
                    foreach (var kv in streamerSet)
                        Config.Streams[kv.Key] = kv.Value;
                    Console.Clear();
                    await ConfigStore.SaveConfig(); // prompted by user
                }
                exitCts.Cancel();
                return;
            }
            await RunInteractiveSetup(true); // from CtrlC
            await Task.Delay(200, exitToken);
            watcherActive = true;
            Console.CursorVisible = false;
            _uiInteractionActive = 0;
        }
        public static bool AskYesNo(string inputText)
        {
            Console.Clear();
            Console.WriteLine(inputText);
            Console.WriteLine();

            int selected = 0;
            int optionStartLine = Console.CursorTop;
            bool isYesNoQuestion = inputText.TrimEnd().EndsWith('?');
            string YES = isYesNoQuestion ? "[ YES ]" : "LIVE", NO = isYesNoQuestion ? "[ NO  ]" : "HYPETRAIN";

            while (!exitToken.IsCancellationRequested)
            {
                Console.SetCursorPosition(0, optionStartLine);
                Console.ForegroundColor = selected == 0 ? ConsoleColor.Green : ConsoleColor.DarkGray;
                Console.Write(YES);
                Console.Write("  ");
                Console.ForegroundColor = selected == 1 ? ConsoleColor.Green : ConsoleColor.DarkGray;
                Console.Write(NO);
                Console.ResetColor();
                Console.Write(new string(' ', Console.BufferWidth - Console.CursorLeft - 1));
                var key = Console.ReadKey(true).Key;
                switch (key)
                {
                    case ConsoleKey.LeftArrow:
                    case ConsoleKey.A:
                        selected = 0;
                        break;

                    case ConsoleKey.RightArrow:
                    case ConsoleKey.D:
                        selected = 1;
                        break;

                    case ConsoleKey.Tab:
                        selected = 1 - selected; // Toggle
                        break;

                    case ConsoleKey.Y:
                        return true;

                    case ConsoleKey.N:
                        return false;

                    case ConsoleKey.Enter:
                    case ConsoleKey.Spacebar:
                        return selected == 0;

                    case ConsoleKey.Escape:
                        return false; // Escape defaults to No
                }
            }
            return true;
        }
        private static void OpenConfigFolder()
        {
            string folder = Path.GetDirectoryName(ConfigPath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = ConfigPath,
                UseShellExecute = true
            });
        }
        private static void PlayWav() => _player?.Play();
        static readonly (byte r, byte g, byte b)[] GradientStops =
           {
            ( 80, 120, 255), // blue
            (200,  40, 255), // magenta (darker)
            (255,  80, 255), // magenta (lighter)
            ( 80, 255, 255), // cyan
            ( 40, 160, 160), // dark cyan
            };
        static readonly StringBuilder sb = new StringBuilder(0b1 << 13); // 8192
        static Program()
        {
            _ytHandler = new HttpClientHandler
            {
                AllowAutoRedirect = true
            };

            _ytClient = new HttpClient(_ytHandler);

            var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("BroadcasterIsLiveListener.success.wav");

            if (stream == null)
            {
                var wavPath = Path.Combine(AppContext.BaseDirectory, "success.wav");
                if (File.Exists(wavPath))
                    stream = File.OpenRead(wavPath);
            }

            if (stream != null)
                _player = new SoundPlayer(stream); // this is an embedded standard sound that has nothing to do with the (to be) per streamer soundfiles          

            string soundDir = Path.Combine(AppContext.BaseDirectory, "sounds");

            if (!Directory.Exists(soundDir))
                Directory.CreateDirectory(soundDir);

            foreach (var file in Directory.GetFiles(soundDir))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".wav" or ".mp3")
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    _streamerSoundPaths[name] = file;
                }
            }

            string exeDir = AppContext.BaseDirectory;
            string jsonName = ConfigFileName;

            ConfigPath = Path.Combine(exeDir, jsonName);
            bool continueConstruction = true;
            bool constructionComplete = false;
            if (!File.Exists(ConfigPath))
            {
                _configNotFound = true;
                continueConstruction = false;
            }
            if (continueConstruction)
            {
                while (!constructionComplete)
                {
                    try
                    {
                        Config = ConfigStore.Load() ?? null;
                    }
                    catch (JsonException ex)
                    {
                        Console.WriteLine("Your config file is invalid JSON.");
                        Console.WriteLine(ex.Message);
                        Console.WriteLine($"Delete {jsonName} and let the program recreate it.");
                        Console.WriteLine("Consider adjusting your .exe with params \"--config mycustomName.json\"");
                        OpenConfigFolder();
                        _configNotFound = false;
                        constructionComplete = true;
                        continueConstruction = false;
                        continue;
                    }

                    if (Config == null)
                    {
                        _configNotFound = true;
                        constructionComplete = true;
                        continueConstruction = false;
                        continue;
                    }
                    string clientId = Config.ClientId;
                    if (string.IsNullOrEmpty(clientId) || clientId == "your-client-id-here")
                    {
                        OpenConfigFolder();
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "https://dev.twitch.tv/console/apps",
                            UseShellExecute = true
                        });
                        Console.WriteLine(TUTORIAL);
                        _configNotFound = true;
                        constructionComplete = true;
                        continueConstruction = false;
                        continue;
                    }
                    string clientSecret = Config.ClientSecret;
                    if (string.IsNullOrEmpty(clientSecret) || clientSecret == "your-client-secret-here")
                    {
                        OpenConfigFolder();
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "https://dev.twitch.tv/console/apps",
                            UseShellExecute = true
                        });

                        Console.WriteLine(TUTORIAL);
                        _configNotFound = true;
                        constructionComplete = true;
                        continueConstruction = false;
                        continue;
                    }
                    clientId = Config.ClientId;
                    clientSecret = Config.ClientSecret;
                    twitch = new TwitchApi(clientId, clientSecret);
                    constructionComplete = twitch != null;
                    continueConstruction = !constructionComplete;
                    _configNotFound = constructionComplete;
                    break;
                }
            }
        }
        private static async Task Main(string[] args)
        {
            using var mutex = new Mutex(initiallyOwned: true, name: "Global\\broadcastListenerMutex", out bool isNew);
            if (exitToken.IsCancellationRequested || !isNew) return;

            Console.WriteLine("Success...");
            Console.CancelKeyPress += KeyPressHandler;

            var parsed = ParseArgs(args);
            Console.WriteLine("running args...");
            bool updateConfig = !await GetJsonFromArgs();

            if (updateConfig)
            {
                MigrationCheck().Wait(5000); // rewrites JSON if needed
                return;
            }
            Console.WriteLine("webhook args...");

            if (!string.IsNullOrWhiteSpace(parsed.WebhookKey) && parsed.WebhookKey != WebhookCancelled)
            {
                SelectedWebhookKey = parsed.WebhookKey;
                if (!Config.NotifyDiscord)
                {
                    updateConfig = true;
                    Config.NotifyDiscord = true;
                }
            }

            Console.WriteLine("Done...");
            if (parsed.Streamers.Count > 0)
            {
                updateConfig = await NormalizeStreamerIDsAsync(updateConfig, twitch);
                Console.WriteLine("Updating from parsed...");
            }
            Console.WriteLine("Done...");

            bool manualRun = !parsed.AutoRun;

            ConstructSoundPlayer();

            if (manualRun)
                await RunInteractiveSetup();

            AnsiSupport.Enable();
            Console.CursorVisible = false;
            Console.Clear();
            Console.WriteLine("\rRequesting OAuth token & starting watcher loop");

            await twitch.InitializeOAuthAsync();
            Console.WriteLine("Done...");

            bool idDemandsUpdate = await ResolveMyUserID(updateConfig, twitch);
            updateConfig = idDemandsUpdate || updateConfig;
            UpdateConfig = UpdateConfig || updateConfig;

            Console.WriteLine("");

            if (Config.RunRefresh)
            {
                Console.WriteLine("Clearing all subscriptions...");
                await TotalReset(twitch);
                Console.WriteLine("Done...");
                await Task.Delay(500, exitToken);
            }
            ConstructStringBuilder();

            if (UpdateConfig) await ConfigStore.SaveConfig(); // post Main setup

            int eventCounts = 0;
            foreach (var b in streamerSet.Values)
            {
                eventCounts += b.Events.Count;
            }
            LegacyPolling = LegacyPolling || eventCounts >= 27;

            Console.WriteLine("\rSetting up refresh-token...".PadRight(Console.WindowWidth));
            _ = Task.Run(() => twitch.CheckForRefresh(exitToken)); // background task that periodically renews tokens
            Console.WriteLine("\rDone...".PadRight(Console.WindowWidth));

            if (!LegacyPolling)
            {
                Console.WriteLine("Initializing subscriptions...");
                SessionManager.Initialize(twitch);
                await SessionManager.InitializeAsync();
                Console.WriteLine("\rDone...".PadRight(Console.WindowWidth));
                Console.WriteLine("\rSetting up subscriptions...".PadRight(Console.WindowWidth));
                await SetupSubscriptionsFromStreamerSet(); // the actual subscription
                Console.WriteLine("\rDone...".PadRight(Console.WindowWidth));
                Console.WriteLine("\rListing all subscriptions:".PadRight(Console.WindowWidth));
                await twitch.ListAllSubscriptionsAsync();
                Console.WriteLine("\rDone...".PadRight(Console.WindowWidth));
            }
            Console.Clear();
            try
            {
                Console.WriteLine("\rRunning watcherloop".PadRight(Console.WindowWidth));
                await RunWatcherLoop();
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("Shutting down...");
                if (UpdateConfig)
                {
                    await ConfigStore.SaveConfig(); // end-of-"life" cycle if prompted earlier
                    UpdateConfig = false;
                }
            }
            finally
            {
                PlayWav();
                if (twitch != null)
                {
                    await twitch.ClearAllSubscriptionsAsync();
                }
                Console.CancelKeyPress -= KeyPressHandler;
            }
        }
        private static void ConstructSoundPlayer()
        {
            var device = GetPlaybackDevice(Config.PreferredAudioDeviceName);
            foreach (var kvp in _streamerSoundPaths)
            {
                _streamerSounds[kvp.Key] = new AudioPlayer(kvp.Value, device);
            }
            _streamerSoundPaths = null;
        }
        private static async Task<bool> GetJsonFromArgs()
        {
            if (_configNotFound)
            {
                string jsonName = string.IsNullOrWhiteSpace(ConfigFileName)
                    ? "broadcastListener.json"
                    : ConfigFileName;

                string exeDir = AppContext.BaseDirectory;
                ConfigPath = Path.Combine(exeDir, jsonName);

                if (!File.Exists(ConfigPath))
                {
                    CreateNewJSON();
                    return false; // Now we really exit
                }

                // Config exists! Load it now
                try
                {
                    await MigrationCheck();
                    Config = ConfigStore.Load() ?? new AppConfig();
                    EnsureValidityJSON();
                }
                catch (JsonException ex)
                {
                    Console.WriteLine("Your config file is invalid JSON.");
                    Console.WriteLine(ex.Message);
                    return false;
                }

                string clientId = Config.ClientId;
                if (string.IsNullOrEmpty(clientId) || clientId == "your-client-id-here")
                {
                    Console.WriteLine(TUTORIAL);
                    OpenConfigFolder();
                    return false;
                }

                string clientSecret = Config.ClientSecret;
                if (string.IsNullOrEmpty(clientSecret) || clientSecret == "your-client-secret-here")
                {
                    Console.WriteLine(TUTORIAL);
                    OpenConfigFolder();
                    return false;
                }
                twitch = new TwitchApi(clientId, clientSecret);
            }
            return true;
        }
        private static async Task<bool> ResolveMyUserID(bool updateConfig, TwitchApi twitch)
        {
            if (string.IsNullOrWhiteSpace(Config.MyUserID) || Config.MyUserID.Contains("ignore", StringComparison.OrdinalIgnoreCase))
            {
                var maybeID = Config.MyUserID ?? "";
                var resolved = await twitch.ResolveUserIdFromToken();

                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    if (resolved != maybeID)
                    {
                        Config.MyUserID = resolved;
                        if (!updateConfig)
                            updateConfig = true;
                    }
                }
                else
                {
                    Console.WriteLine("⚠️ Failed to resolve MyUserID from token.");
                }
            }
            return updateConfig;
        }
        private static async Task TotalReset(TwitchApi twitch)
        {
            int retries = 60;
            Console.WriteLine("Clearing all subs...");
            await twitch.ClearAllSubscriptionsAsync();
            Console.WriteLine("Done!");
            await Task.Delay(2000, exitToken);
            Console.Clear();
            while (retries > 0 && !exitToken.IsCancellationRequested)
            {
                Console.Write($"\rcontinuing in {retries}".PadRight(Console.WindowWidth / 2));
                await Task.Delay(1001, exitToken);
                retries--;
            }
            Console.Clear();
        }
        private static void CreateNewJSON()
        {
            Console.WriteLine("No config file found. Creating a template...");
            Console.WriteLine("Please type in your preferred default profile\nto be monitoring ('the part after twitch.tv/')");
            string defaultStreamer = Console.ReadLine()?.Trim() ?? string.Empty;
            var template = new AppConfig
            {
                Streams = new Dictionary<string, StreamerConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    [defaultStreamer] = new StreamerConfig()
                }
            };

            string json = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);

            Console.WriteLine($"\nA new config file has been created at: {ConfigPath}");

            Console.WriteLine("\nPlease edit the file and enter your Twitch Client ID and Client Secret.");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
            OpenConfigFolder();
        }
        private static async Task<bool> NormalizeStreamerIDsAsync(bool updateConfig, TwitchApi twitch)
        {
            // Normalize keys first
            var normalized = new Dictionary<string, StreamerConfig>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in Config.Streams)
            {
                var key = kv.Key.ToLowerInvariant();

                if (!normalized.ContainsKey(key))
                    normalized[key] = kv.Value;
            }

            Config.Streams = normalized;
            streamerSet = normalized.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            updateConfig = true;

            var keysToRemove = new HashSet<string>();

            foreach (var name in streamerSet.Keys.ToList())
            {
                if (!Config.Streams.TryGetValue(name, out var cfg))
                {
                    // Should never happen, but future‑proofing
                    cfg = new StreamerConfig();
                    Config.Streams[name] = cfg;
                    updateConfig = true;
                }

                if (string.IsNullOrWhiteSpace(cfg.Id))
                {
                    var id = await twitch.ResolveUserId(name);
                    await Task.Delay(Config.DelayTwitchAPI, exitToken); // throttle

                    if (string.IsNullOrWhiteSpace(id))
                    {
                        Console.WriteLine($"Could not resolve Twitch ID for '{name}'. Removing.");
                        keysToRemove.Add(name);
                        continue;
                    }

                    cfg.Id = id;
                    updateConfig = true;
                }
            }

            foreach (var key in keysToRemove)
            {
                Config.Streams.Remove(key);
                streamerSet.Remove(key);
                updateConfig = true;
            }

            return updateConfig;
        }
        private static void EnsureValidityJSON()
        {
            Config.Streams ??= new(StringComparer.OrdinalIgnoreCase);
            Config.Youtube ??= new(StringComparer.OrdinalIgnoreCase);
            Config.DiscordWebhookDefault ??= string.Empty;

            if (string.IsNullOrWhiteSpace(Config.ClientId)) Config.ClientId = "your-client-id-here";

            if (string.IsNullOrWhiteSpace(Config.ClientSecret)) Config.ClientSecret = "your-client-secret-here";

            if (string.IsNullOrWhiteSpace(Config.PreferredAudioDeviceName)) Config.PreferredAudioDeviceName = "speaker";

            if (string.IsNullOrWhiteSpace(Config.NtfyTopic)) Config.NtfyTopic = string.Empty;

            if (double.IsNaN(Config.ColourfulArrayAnimationSpeed) || Config.ColourfulArrayAnimationSpeed <= 0) Config.ColourfulArrayAnimationSpeed = 0.1f;

            if (float.IsNaN(Config.MaxVolumeAllowed) || Config.MaxVolumeAllowed < .2f) Config.MaxVolumeAllowed = .2f;

            if (float.IsNaN(Config.MinVolumeAlert) || Config.MinVolumeAlert < .1f) Config.MinVolumeAlert = .1f;

            if (Config.LegacyPollingDelayMS < 3) Config.LegacyPollingDelayMS = 900;

            if (Config.DelayTwitchAPI < 3) Config.DelayTwitchAPI = 150;

            if (Config.TwitchAPIPollDelay < 5) Config.TwitchAPIPollDelay = 2000;

            if (Config.ColourfulArrayRefreshRate < 50) Config.ColourfulArrayRefreshRate = 100;

            foreach (var kvp in Config.Streams)
            {
                kvp.Value.Id ??= string.Empty;
                kvp.Value.DiscordWebhook ??= "NO_WEBHOOK";

                if (kvp.Value.Events == null || kvp.Value.Events.Count == 0)
                    kvp.Value.Events = new HashSet<string> { "stream.online" };
            }
        }
        private static async Task<bool> MigrationCheck()
        {
            try
            {
                var raw = File.ReadAllText(ConfigPath);
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (!root.TryGetProperty("Streams", out var streamsProp))
                    return false;

                bool needsMigration = streamsProp.EnumerateObject()
                    .Any(kvp => kvp.Value.ValueKind == JsonValueKind.String);

                if (!needsMigration)
                    return true;

                var migrated = new Dictionary<string, StreamerConfig>();

                foreach (var kvp in streamsProp.EnumerateObject())
                {
                    if (kvp.Value.ValueKind == JsonValueKind.String)
                    {
                        migrated[kvp.Name] = new StreamerConfig
                        {
                            Id = kvp.Value.GetString(),
                            Events = new HashSet<string> { "stream.online" },
                            DiscordWebhook = ""
                        };
                    }
                    else
                    {
                        migrated[kvp.Name] = kvp.Value.Deserialize<StreamerConfig>();
                    }
                }

                var newConfig = JsonSerializer.Deserialize<AppConfig>(raw) ?? new AppConfig();
                newConfig.Streams = migrated;

                File.WriteAllText(
                    ConfigPath,
                    JsonSerializer.Serialize(newConfig, new JsonSerializerOptions { WriteIndented = true })
                );

                await Task.Delay(5000);
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    var logPath = Path.Combine(
                        Path.GetDirectoryName(ConfigPath) ?? "",
                        "migration_error.log"
                    );

                    File.AppendAllText(
                        logPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Migration failed: {ex}\n"
                    );
                }
                catch
                { }
                return false;
            }
        }
        private static async Task RenderLoop()
        {
            double time = 0;
            string frame = _currentFrame;
            var sw = Stopwatch.StartNew();
            while (!exitToken.IsCancellationRequested)
            {
                while (!watcherActive)
                {
                    await Task.Delay(900, exitToken);
                }

                if (_layoutChanged)
                {
                    Console.Clear();
                    _layoutChanged = false;
                }
                Console.SetCursorPosition(0, 0);
                frame = _currentFrame;

                if (ColourfulArray)
                {
                    time += ColourfulArrayAnimation;
                    if (time > Math.PI * 2)
                        time -= Math.PI * 2;
                    Console.SetCursorPosition(0, 0);
                    WriteOscillatingGradient(frame, time);
                    Console.WriteLine();

                    var e = sw.Elapsed;

                    string uptime =
                        e.Days > 0
                            ? $"{e.Days} day{(e.Days > 1 ? "s" : "")} {e:hh\\:mm\\:ss}"
                            : $"{e:hh\\:mm\\:ss}";

                    WriteOscillatingGradient($"\rRuntime: {uptime}", time);
                }
                else Console.Write($"\r{frame}");
                await Task.Delay(Math.Max(5, ColourfulArrayUpdateMS), exitToken);
            }
        }
        private static async Task RunWatcherLoop()
        {
            var tmpList = new Dictionary<string, StreamerConfig>(streamerSet);
            string sb = _sb.ToString();
            int maxLineLength = sb
                 .Split('\n')
                 .Max(line => line.Length);

            int targetWidth = maxLineLength + 3;
            int targetHeight = sb.Split('\n').Count() + 12;

            // Width
            Console.BufferWidth = Math.Max(Console.BufferWidth, targetWidth);
            Console.WindowWidth = Math.Min(Console.LargestWindowWidth, targetWidth);

            // Height
            Console.BufferHeight = Math.Max(Console.BufferHeight, targetHeight);
            Console.WindowHeight = Math.Min(Console.LargestWindowHeight, targetHeight);

            int padWidth = Math.Min(Console.WindowWidth, maxLineLength);

            ConstructStringBuilderUI(tmpList);
            _currentFrame = _sb.ToString();

            _ = Task.Run(RenderLoop);

            while (!exitToken.IsCancellationRequested && tmpList.Count > 0)
            {
                string all_streamers = "?user_id=";

                foreach (var b in tmpList)
                    all_streamers += b.Value.Id + "&user_id=";
                if (all_streamers.EndsWith("&user_id="))
                    all_streamers = all_streamers.Remove(all_streamers.Length - "&user_id=".Length);

                JsonElement data = JsonDocument.Parse(await TwitchApi.GetCurrentlyLiveStreamers(all_streamers)).RootElement.GetProperty("data");

                foreach (var stream in data.EnumerateArray())
                {
                    string userId = stream.GetProperty("user_id").GetString();
                    string streamType = stream.GetProperty("type").GetString();

                    if (streamType.Contains("live", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = tmpList
                            .FirstOrDefault(x => x.Value.Id == userId)
                            .Key;
                        // should I make a queue object here to iterate through rather than use the "name" variable directly?
                        if (name != null)
                        {
                            tmpList.Remove(name);
                            await HandleStreamerLiveAsync(name);

                            // Start from the current buffer ONCE
                            ConstructStringBuilderUI(tmpList, name);

                            if (!_layoutChanged)
                                _layoutChanged = true;
                        }

                        if (Config.CloseAfterFirst)
                        {
                            return;
                        }
                    }
                }
                if (_layoutChanged)
                    _currentFrame = _sb.ToString();

                if (tmpList.Count < 1) return;
                await Task.Delay(Math.Max(Config.TwitchAPIPollDelay, 1500), exitToken);
            }
            Console.Clear();
            Console.Write("All done (press any key to exit, or wait 9 seconds)...");

            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalMilliseconds < 5000)
            {
                if (Console.KeyAvailable)
                {
                    Console.ReadKey(true); // consume key
                    break;
                }
                await Task.Delay(80); // small sleep to avoid busy-waiting
            }
        }
        private static void ConstructStringBuilderUI(Dictionary<string, StreamerConfig> tmpList, string name = "")
        {
            var sb = new StringBuilder();
            sb.Append(_sb);
            _sb.Clear();
            var allLines = sb.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();

            // Remove the streamer and unwanted lines
            if (!string.IsNullOrWhiteSpace(name))
            allLines.RemoveAll(line => line.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            allLines.RemoveAll(line => line.Contains("-webhook not found in config", StringComparison.OrdinalIgnoreCase));

            // System lines: Discord, Channel, Using
            var systemLines = allLines
                .Where(line =>
                    line.StartsWith("Discord", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Channel", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Using", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Streamer lines = everything else
            var names = tmpList.Keys.ToArray();

            var streamerLines = allLines
                .Where(line => names.Any(n =>
                    line.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
                .ToList();


            // Exponential ordering based on name length
            var sorted = streamerLines
                .Select(line =>
                {
                    string key = line.Split(' ')[0];
                    int len = key.Length;
                    return new { line, keyValue = Math.Exp(len * 0.15) };
                })
                .OrderBy(x => x.keyValue)
                .Select(x => x.line)
                .ToList();

            // Rebuild the buffer
            sb.Clear();
            sb.AppendJoin(Environment.NewLine, sorted);
            sb.AppendLine();

            if (systemLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendJoin(Environment.NewLine, systemLines);
            }
            _sb = sb;
        }
        private static async Task HandleDiscordCalls(string username)
        {
            // Final validation happens in SendAsync with the https:// check
            if (!Config.NotifyDiscord) return;

            string webhookUrl = Config?.DiscordWebhookDefault ?? string.Empty;

            streamerSet.TryGetValue(username, out var streamer);

            if (streamer == null) return;

            // 1. CLI override
            if (!string.IsNullOrWhiteSpace(SelectedWebhookKey))
            {
                webhookUrl = SelectedWebhookKey;
            }
            else
            {
                // 2. Per-streamer webhook
                if (!string.IsNullOrWhiteSpace(streamer.DiscordWebhook))
                {
                    if (streamer.DiscordWebhook == "NO_WEBHOOK")
                    {
                        return; // Don't notify
                    }
                    else if (streamer.DiscordWebhook == "DEFAULT")
                    {
                        // Use global default
                        webhookUrl = Config?.DiscordWebhookDefault ?? string.Empty;
                    }
                    else
                    {
                        webhookUrl = streamer.DiscordWebhook;
                    }
                }
            }
            string notifyMessage = string.Format(STREAMER_LIVE, username);

            if (string.IsNullOrWhiteSpace(webhookUrl)) return;
            // Final validation happens in SendAsync with the https:// check
            await DiscordNotifier.SendAsync(webhookUrl, notifyMessage);
        }
        private static async Task RunInteractiveSetup(bool fromCtrlC = false)
        {
            Console.Clear();
            string currentCollection = "";
            foreach (var b in Config.Streams)
                currentCollection += b.Key + '\n';
            if (currentCollection.Length > 0)
            {
                Console.WriteLine($"Current Collection:\n{currentCollection}");
            }
            Console.Write("Enter streamer to monitor ('Spacebar'-separated, blank for default): ");
            string input = Console.ReadLine()?.Trim() ?? string.Empty;
            if (!fromCtrlC)
                streamerSet.Clear();
            bool forceUpdate = false;
            if (string.IsNullOrEmpty(input))
            {
                streamerSet = new Dictionary<string, StreamerConfig>(Config.Streams);
            }
            else
            {
                foreach (var s in input.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string name = s.Trim();

                    if (!Config.Streams.TryGetValue(name, out var streamer))
                    {
                        streamer = new StreamerConfig();

                        Config.Streams.Add(name, streamer);
                        if (!forceUpdate)
                            forceUpdate = true;
                    }
                    if (fromCtrlC)
                    {
                        var streamerID = streamer?.Id ?? string.Empty;
                        if (twitch != null && !string.IsNullOrWhiteSpace(streamerID) && streamerSet.ContainsKey(name))
                        {
                            await twitch.UnsubscribeSingleAsync(streamerID);
                            streamerSet.Remove(name);
                        }
                    }
                    else
                    if (!streamerSet.TryGetValue(name, out var streamerTwo))
                    {
                        streamerTwo = new StreamerConfig();

                        streamerSet.Add(name, streamerTwo);
                        if (!forceUpdate)
                            forceUpdate = true;
                    }
                }
            }
            if (streamerSet.Count < 1)
            {
                Console.WriteLine("Streams dict empty, there's nothing to survey");
                if (AskYesNo("Open folder?"))
                    OpenConfigFolder();
                await Task.Delay(200, exitToken);
                Environment.Exit(0);
            }
            if (!fromCtrlC)
            {
                var labels = new[] { "Online Listener", "Close after First", "Update Config", "Include youtube", "Ping Discord", "Open browser", "Refresh webSockets", "Colourful Array", "Run sounds", "Notify phone", "LEGACY STYLE" };
                var values = new[]
                 {
                watchOnline,
                Config.CloseAfterFirst,
                UpdateConfig,
                Config.RunYoutubeCheck,
                Config.NotifyDiscord,
                Config.OpenBrowser,
                Config.RunRefresh,
                Config.ColourfulArray,
                Config.UseSoundFolder,
                Config.NotifyPhone,
                false
            };
                SelectionArray(labels, values);

                watchOnline = values[0];
                Config.CloseAfterFirst = values[1];
                UpdateConfig = forceUpdate || values[2];
                Config.RunYoutubeCheck = values[3];
                Config.NotifyDiscord = values[4];
                Config.OpenBrowser = values[5];
                Config.RunRefresh = values[6];
                Config.ColourfulArray = values[7];
                Config.UseSoundFolder = values[8];
                Config.NotifyPhone = values[9];
                LegacyPolling = !values[6] && values[10];

                if (values[4])
                {
                    SelectedWebhookKey = string.Empty;
                }
            }
            if (fromCtrlC || forceUpdate || UpdateConfig)
            {
                if (twitch != null) await NormalizeStreamerIDsAsync(UpdateConfig, twitch);
                await ConfigStore.SaveConfig(); // during run, kind of important.
            }
        }
        private static void LaunchBrowser(string url)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        public static async Task SendNtfyAsync(string message)
        {
            if (string.IsNullOrWhiteSpace(Config.NtfyTopic))
                return;

            var content = new StringContent(message, Encoding.UTF8, "text/plain");
            await _ytClient.PostAsync(Config.NtfyTopic, content);
        }
        public static async Task HandleStreamerLiveAsync(string username = "")
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username cannot be null or empty.", nameof(username));

            string nameOnline = $"Channel {username} SEEN LIVE AT {DateTime.Now:HH:mm}";

            _sb.AppendLine($"\n\r{nameOnline}");

            if (Config.OpenBrowser)
            {
                LaunchBrowser($"https://twitch.tv/{username}");
            }

            if (Config.NotifyDiscord)
                await HandleDiscordCalls(username);

            if (Config.NotifyPhone)
                await SendNtfyAsync(nameOnline);

            if (Config.UseSoundFolder && _streamerSounds.TryGetValue(username, out var player))
            {
                try
                {
                    await player.PlayAsync();
                }
                catch { }
            }

            if (Config.CloseAfterFirst || streamerSet.Count == 0 || TwitchWentLive.Count == streamerSet.Count)
                exitCts.Cancel();
        }
        public static void SelectionArray(string[] labels, bool[] values)
        {
            var options = labels
                .Select((label, i) => new Option(label, values[i]))
                .ToArray();

            ConsoleUI.SelectionArray(options);

            for (int i = 0; i < values.Length; i++)
                values[i] = options[i].Value;
        }
        public static async Task SetupSubscriptionsFromStreamerSet()
        {
            int remaining = 1;
            foreach (var kvp in streamerSet)
            {
                string username = kvp.Key;
                StreamerConfig streamer = kvp.Value;

                // Ensure we have a valid ID
                string id = streamer.Id;
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = await twitch.ResolveUserId(username);
                    await Task.Delay(Config.DelayTwitchAPI, exitToken);
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        Console.WriteLine($"Skipping invalid streamer: {username}");
                        Config.Streams.Remove(username);
                        continue;
                    }
                    // Save resolved ID back into config
                    streamer.Id = id;
                    if (string.IsNullOrWhiteSpace(Config.Streams[username].Id))
                        Config.Streams[username].Id = id;
                    UpdateConfig = true;
                }

                // Check if streamer is live
                bool isLive = await TwitchApi.IsStreamerLiveAsync(username);
                await Task.Delay(Config.DelayTwitchAPI, exitToken);
                if (isLive)
                {
                    await HandleStreamerLiveAsync(username);
                    continue;
                }

                await Task.Delay(Config.DelayTwitchAPI, exitToken); // throttle because IsHypeTrainActiveAsync() is about to call twitch API
                // Check hype train (only for your own channel)
                if (streamer.Events.Any(e => e.StartsWith("channel.hype_train.")) &&
                         Config.MyUserID == id &&
                         await twitch.IsHypeTrainActiveAsync(id))
                {
                    PlayWav();
                    Console.WriteLine($"🚂 Active Hype Train detected for {username}!");
                    continue;
                }
                Console.WriteLine($"\r{remaining}/ {streamerSet.Count}");
                remaining++;
                // Subscribe to events defined for this streamer
                foreach (var eventType in streamer.Events)
                {
                    await SessionManager.SubscribeAsync(id, eventType);
                    await Task.Delay(501, exitToken);
                }
            }

            // YouTube watcher
            if (Config.RunYoutubeCheck)
            {
                youtubeSet.UnionWith(Config.Youtube);
                if (youtubeSet.Count > 0)
                    _ = StartYouTubeWatcherAsync();
            }
        }
        public static void ConstructStringBuilder()
        {
            _sb.Clear();

            int longestName = 0;
            int longestWebhook = 0;

            // First pass: compute max widths
            foreach (var (name, info) in streamerSet)
            {
                longestName = Math.Max(longestName, name.Length);

                // Only count webhook length if it's shown
                if (info.DiscordWebhook is "NO_WEBHOOK" or "DEFAULT")
                    longestWebhook = Math.Max(longestWebhook, info.DiscordWebhook.Length);
            }

            // Second pass: build output
            foreach (var (name, info) in streamerSet)
            {
                var events = string.Join(", ", info.Events);

                string webhookPart =
                info.DiscordWebhook is "NO_WEBHOOK" or "DEFAULT"
                    ? info.DiscordWebhook
                    : "DESIGNATED";

                _sb.AppendLine(
                    $"{name.PadRight(longestName)}  {webhookPart.PadRight(longestWebhook)}  - [{events}]"
                );
            }
        }
        public static async Task<(bool isLive, string finalUrl)> IsYouTubeChannelLiveAsync(string channelUrl)
        {
            var request = new HttpRequestMessage(HttpMethod.Head, channelUrl);

            var response = await _ytClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead
            );

            var finalUrl = response?.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
            var isLive = finalUrl.Contains("/watch?v=", StringComparison.OrdinalIgnoreCase);

            return (isLive, finalUrl);
        }
        public static Task StartYouTubeWatcherAsync()
        {
            youtubeList.Clear();
            foreach (var b in Config.Youtube)
                youtubeSet.Add(b);
            foreach (var b in youtubeSet)
                youtubeList.Add(b);
            var tmpList = youtubeSet.ToList();
            return Task.Run(async () =>
            {
                while (!exitToken.IsCancellationRequested && tmpList.Count > 0)
                {
                    for (int i = tmpList.Count - 1; i >= 0; i--)
                    {
                        var channel = tmpList[i];
                        var (live, finalUrl) = await IsYouTubeChannelLiveAsync(channel);

                        if (live)
                        {
                            LaunchBrowser(finalUrl);
                            tmpList.RemoveAt(i);
                            continue;
                        }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(15), exitToken);
                }
            });
        }
        public static void WriteOscillatingGradient(string text, double time)
        {
            sb.Clear();
            sb.Append("\u001b[0m");

            for (int i = 0; i < text.Length; i++)
            {
                double t = (Math.Sin(time + i * 0.10) + 1) / 2;

                double scaled = t * (GradientStops.Length - 1);
                int idx = (int)scaled;
                double frac = scaled - idx;

                var (r1, g1, b1) = GradientStops[idx];
                var (r2, g2, b2) = GradientStops[(idx + 1) % GradientStops.Length];

                byte r = (byte)(r1 + (r2 - r1) * frac);
                byte g = (byte)(g1 + (g2 - g1) * frac);
                byte b = (byte)(b1 + (b2 - b1) * frac);

                // ANSI 24-bit colour code
                //   sb.Append($"\u001b[38;2;{r};{g};{b}m");

                sb.Append("\u001b[38;2;");
                sb.Append(r);
                sb.Append(';');
                sb.Append(g);
                sb.Append(';');
                sb.Append(b);
                sb.Append('m');

                sb.Append(text[i]);
            }

            sb.Append("\u001b[0m"); // reset at end

            Console.Write(sb.ToString());
        }
    }
    static public   class     ConsoleUI
    {
        private const string ENABLED = "[ X ]";
        private const string DISABLE = "[   ]";
        public static void SelectionArray(Option[] options)
        {
            int selectedRow = 0;
            int optionStartLine = Console.CursorTop;
            if (options.Length == 0)
            {
                Console.Write($"Error: {nameof(options)}-array empty!");
                return;
            }

            while (true)
            {
                // Draw all options
                for (int i = 0; i < options.Length; i++)
                {
                    Console.SetCursorPosition(0, optionStartLine + i);

                    Console.ForegroundColor = (i == selectedRow)
                        ? ConsoleColor.Green
                        : ConsoleColor.Gray;

                    string checkbox = options[i].Value ? ENABLED : DISABLE;
                    Console.Write($"{checkbox} {options[i].Label}".PadRight(Console.WindowWidth - 2));
                }

                Console.ResetColor();

                var key = Console.ReadKey(true).Key;

                switch (key)
                {
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.W:
                        selectedRow = (selectedRow - 1 + options.Length) % options.Length;
                        break;

                    case ConsoleKey.DownArrow:
                    case ConsoleKey.S:
                        selectedRow = (selectedRow + 1) % options.Length;
                        break;

                    case ConsoleKey.D:
                    case ConsoleKey.RightArrow:
                        options[selectedRow].Value = true;
                        break;

                    case ConsoleKey.A:
                    case ConsoleKey.LeftArrow:
                        options[selectedRow].Value = false;
                        break;

                    case ConsoleKey.Spacebar:
                    case ConsoleKey.Tab:
                        options[selectedRow].Value = !options[selectedRow].Value;
                        break;

                    case ConsoleKey.Enter:
                        return;

                    default:
                        int index = -1;

                        if (key >= ConsoleKey.D1 && key <= ConsoleKey.D9)
                            index = key - ConsoleKey.D1;
                        else if (key >= ConsoleKey.NumPad1 && key <= ConsoleKey.NumPad9)
                            index = key - ConsoleKey.NumPad1;

                        if (index >= 0 && index < options.Length)
                        {
                            options[index].Value = !options[index].Value;
                        }
                        break;
                }
            }
        }
    }
    static public   class     SessionManager
    {
        private static TwitchApi _api;
        public static void Initialize(TwitchApi api)
        {
            _api = api;
        }
        public static List<EventSubSession> Sessions => _sessions;
        private static readonly List<EventSubSession> _sessions = new();
        private static EventSubSession _current;

        private const int MaxCost = 10;
        private const int ThrottleDelayMs = 700;
        public static TaskCompletionSource<bool> ReadyTcs = new();
        public static void OnKeepaliveReceived()
        {
            if (!ReadyTcs.Task.IsCompleted)
                ReadyTcs.SetResult(true);
        }
        public static async Task InitializeAsync()
        {
            _current = await CreateNewSessionAsync();
            _sessions.Add(_current);
            if (CurrentFrame.Length == 0)
                Console.WriteLine($"\r\n[SessionManager] Created initial session: {_current.SessionId}");
        }
        public static async Task SubscribeAsync(string broadcasterId, string eventType)
        {
            int cost = GetEventCost(eventType);

            if (_current == null || _current.CurrentCost + cost > MaxCost)
            {
                _current = await CreateNewSessionAsync();
                _sessions.Add(_current);
                Console.WriteLine($"[SessionManager] New session: {_current.SessionId}");
            }

            // Hard stop — if this prints, the session handshake never finished
            if (string.IsNullOrEmpty(_current?.SessionId))
            {
                Console.WriteLine($"[SessionManager] ❌ SessionId empty, skipping {broadcasterId}");
                return;
            }

            bool ok = await SubscribeEvent(_current.SessionId, broadcasterId, eventType);
            if (ok)
                _current.CurrentCost += cost;
            else
            {
                Console.WriteLine($"[SessionManager] ⚠️ Failed: {broadcasterId} / {eventType}");
                await _api.ListAllSubscriptionsAsync(); // add this — dump what Twitch thinks we have
            }

            await Task.Delay(ThrottleDelayMs, exitToken);
        }
        // These three methods are placeholders — you already have them in TwitchApi
        private static Task<EventSubSession> CreateNewSessionAsync() => _api.CreateNewSessionAsync();
        private static Task<bool> SubscribeEvent(string sessionId, string broadcasterId, string eventType) => _api.SubscribeEvent(sessionId, broadcasterId, eventType);
        private static int GetEventCost(string eventType) => _api.GetEventCost(eventType);
    }
    static public   class     DiscordNotifier
    {
        private static readonly HttpClient _http = new HttpClient();
        public static async Task SendAsync(string webhookUrl, string message)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl) ||
                !webhookUrl.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var payload = new { content = message };
            var json = JsonSerializer.Serialize(payload);

            var response = await _http.PostAsync(
                webhookUrl,
                new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            );

            response.EnsureSuccessStatusCode();
        }
    }
    static public   class     ConfigStore
    {
        public static async Task SaveConfig()
        {
            await _saveConfigSemaphore.WaitAsync();
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                await File.WriteAllTextAsync(
                    ConfigPath,
                    JsonSerializer.Serialize(Config, options)
                );
            }
            finally
            {
                _saveConfigSemaphore.Release();
            }
        }
        public static AppConfig Load(string? path = "")
        {
            if (string.IsNullOrWhiteSpace(path)) path = ConfigPath;
            string json = File.ReadAllText(path);

            return JsonSerializer.Deserialize<AppConfig>(json);
        }
    }
           public   interface IAlertSoundPlayer
    {
        Task PlayAsync();
        TimeSpan Duration { get; }
    }
           public   class     AudioPlayer : IAlertSoundPlayer
    {
        private readonly string _path;
        private readonly MMDevice _device;
        public TimeSpan Duration { get; }
        public AudioPlayer(string path, MMDevice device)
        {
            _path = path;
            _device = device;
            using var reader = new AudioFileReader(path);
            Duration = reader.TotalTime;
        }
        public async Task PlayAsync()
        {
            await Task.Run(() =>
            {
                using var audio = new AudioFileReader(_path);
                using var output = new WasapiOut(_device, AudioClientShareMode.Shared, false, 200);
                var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                float lastVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar;

                device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Min(Config.MaxVolumeAllowed, Math.Max(Config.MinVolumeAlert, 2 * lastVolume));

                output.Init(audio);
                output.Play();

                while (output.PlaybackState == PlaybackState.Playing)
                    Thread.Sleep(50);
                device.AudioEndpointVolume.MasterVolumeLevelScalar = lastVolume;
            });
        }
    }
           public   class     StreamerConfig
    {
        public string Id { get; set; } = string.Empty;
        public HashSet<string> Events { get; set; } = new() { "stream.online" };
        public string DiscordWebhook { get; set; } = "NO_WEBHOOK";
    }
           public   class     AuthToken
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public int expires_in { get; set; }
        public string token_type { get; set; }
    }
           public   class     TwitchApi
    {
        public class EventSubSession
        {
            public ClientWebSocket Socket { get; set; } = new();
            public string SessionId { get; set; } = "";
            public int CurrentCost { get; set; } = 0;
            public TaskCompletionSource<bool> Ready { get; } = new();
            public TaskCompletionSource<bool> KeepaliveReceived { get; } = new(); // add this
            public bool ReadyForPurge = false;
        }
        #region fieldsTwitch
        private bool NeedsRotation = false;
        public static HashSet<string> TwitchWentLive { get; private set; } = new();
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly HttpClient _http = new HttpClient();
        private string _accessToken;
        private static readonly SemaphoreSlim _twitchRateLimit = new(1, 1);
        private static DateTime _lastCall = DateTime.MinValue;
        public string RedirectUrl { get; set; } = @"http://localhost:8080";
        #endregion
        public record PublicHypeTrainStatus(
            bool IsActive,
            int? Level,
            double? ProgressPercent,
            DateTimeOffset? EndsAt
        );
        public TwitchApi(string clientId, string clientSecret)
        {
            _clientId = clientId;
            _clientSecret = clientSecret;
        }
        public async Task ClearAllSubscriptionsAsync()
        {
            string cursor = null;
            int totalDeleted = 0;

            do
            {
                var url = "https://api.twitch.tv/helix/eventsub/subscriptions";
                if (cursor != null)
                    url += $"?after={cursor}";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Client-ID", _clientId);
                request.Headers.Add("Authorization", $"Bearer {_accessToken}");

                var response = await TwitchThrottledCall(() => _http.SendAsync(request));
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");

                Console.WriteLine($"Found {data.GetArrayLength()} subscriptions on this page, deleting...");

                foreach (var sub in data.EnumerateArray())
                {
                    var id = sub.GetProperty("id").GetString();
                    var status = sub.GetProperty("status").GetString();
                    var deleteRequest = new HttpRequestMessage(
                        HttpMethod.Delete,
                        $"https://api.twitch.tv/helix/eventsub/subscriptions?id={id}"
                    );
                    deleteRequest.Headers.Add("Client-ID", _clientId);
                    deleteRequest.Headers.Add("Authorization", $"Bearer {_accessToken}");
                    var deleteResponse = await TwitchThrottledCall(() => _http.SendAsync(deleteRequest));
                    Console.WriteLine($"Deleted [{status}] {id}: {deleteResponse.StatusCode}");
                    totalDeleted++;
                }

                // handle pagination
                cursor = null;
                if (doc.RootElement.TryGetProperty("pagination", out var pagination) &&
                    pagination.TryGetProperty("cursor", out var cursorEl))
                {
                    cursor = cursorEl.GetString();
                }

            } while (cursor != null);

            Console.WriteLine($"[ClearAll] Done. Deleted {totalDeleted} subscriptions.");
        }
        private async Task<T> TwitchThrottledCall<T>(Func<Task<T>> action)
        {
            await _twitchRateLimit.WaitAsync();
            try
            {
                var now = DateTime.UtcNow;
                var diff = now - _lastCall;

                if (diff.TotalMilliseconds < 120)
                    await Task.Delay(120 - (int)diff.TotalMilliseconds, exitToken);

                var result = await action();
                _lastCall = DateTime.UtcNow;
                return result;
            }
            finally
            {
                _twitchRateLimit.Release();
            }
        }
        public async Task ListAllSubscriptionsAsync()
        {
            int retries = 0;
            while (retries < 5)
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://api.twitch.tv/helix/eventsub/subscriptions"
                );
                request.Headers.Add("Client-ID", _clientId);
                request.Headers.Add("Authorization", $"Bearer {_accessToken}");

                var response = await TwitchThrottledCall(() => _http.SendAsync(request));

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    retries++;
                    int delay = 10_000;
                    if (response.Headers.TryGetValues("Retry-After", out var values))
                        delay = int.Parse(values.First()) * 1000;
                    Console.WriteLine($"[ListAllSubscriptions] 429 hit, backing off {delay / 1000}s... (attempt {retries}/5)");
                    await Task.Delay(delay, exitToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Failed to fetch subscriptions: {response.StatusCode}");
                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                Console.WriteLine($"\nTotal subscriptions: {data.GetArrayLength()}");
                foreach (var sub in data.EnumerateArray())
                {
                    var type = sub.GetProperty("type").GetString();
                    var status = sub.GetProperty("status").GetString();
                    var condition = sub.GetProperty("condition");
                    var broadcasterId = condition.GetProperty("broadcaster_user_id").GetString();
                    Console.WriteLine($"  - {type} for broadcaster {broadcasterId} (status: {status})");
                }
                return; // success, exit loop
            }

            Console.WriteLine("[ListAllSubscriptions] Gave up after 5 retries.");
        }
        public async Task UnsubscribeSingleAsync(string broadcasterUserId)
        {
            // 1. Fetch all subscriptions
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.twitch.tv/helix/eventsub/subscriptions"
            );

            request.Headers.Add("Client-ID", _clientId);
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");

            var response = await TwitchThrottledCall(() => _http.SendAsync(request));

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to fetch subscriptions: {response.StatusCode}");
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            // 2. Find matching subscriptions
            foreach (var sub in data.EnumerateArray())
            {
                var condition = sub.GetProperty("condition");
                var id = sub.GetProperty("id").GetString();

                if (condition.TryGetProperty("broadcaster_user_id", out var uidProp))
                {
                    var uid = uidProp.GetString();

                    if (uid == broadcasterUserId)
                    {
                        // 3. Delete only this subscription
                        var deleteRequest = new HttpRequestMessage(
                            HttpMethod.Delete,
                            $"https://api.twitch.tv/helix/eventsub/subscriptions?id={id}"
                        );

                        deleteRequest.Headers.Add("Client-ID", _clientId);
                        deleteRequest.Headers.Add("Authorization", $"Bearer {_accessToken}");

                        await TwitchThrottledCall(() => _http.SendAsync(deleteRequest));

                        Console.WriteLine($"🗑️ Unsubscribed from {broadcasterUserId}");
                        break;
                    }
                }
            }
        }
        public async Task InitializeOAuthAsync()
        {
            // 1. If we already have a valid token, use it
            if (!string.IsNullOrEmpty(Program.Config.AccessToken) &&
                Program.Config.AccessTokenExpiresAt.HasValue &&
                Program.Config.AccessTokenExpiresAt.Value > DateTime.UtcNow.AddMinutes(1))
            {
                _accessToken = Program.Config.AccessToken;
                return;
            }

            // 2. If we have a refresh token, try refreshing
            if (!string.IsNullOrEmpty(Program.Config.RefreshToken))
            {
                bool refreshed = await TryRefreshTokenAsync();
                if (refreshed)
                    return;
            }

            // 3. Otherwise, run full OAuth login
            await RunFullOAuthFlowAsync();
        }
        private async Task RunFullOAuthFlowAsync()
        {
            bool updateCallback =
            string.IsNullOrWhiteSpace(Program.Config.CallbackUrl)
            || !Uri.TryCreate(Program.Config.CallbackUrl, UriKind.Absolute, out _);

            Console.WriteLine("Opening browser for Twitch OAuth login...");
            RedirectUrl = (Program.Config.CallbackUrl ?? "http://localhost:8080")
                .TrimEnd('/') + "/";
            string url =
            $"https://id.twitch.tv/oauth2/authorize" +
            $"?client_id={_clientId}" +
            $"&redirect_uri={RedirectUrl}" +
            $"&response_type=code" +
            $"&scope=channel:read:hype_train";


            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

            var listener = new HttpListener();
            // Use the normalized value everywhere
            listener.Prefixes.Add(RedirectUrl);
            listener.Start();

            var context = await listener.GetContextAsync();
            string code = context.Request.QueryString["code"] ?? string.Empty;

            // Respond in browser
            var responseString = "<html><body>You may close this window.</body></html>";
            var buffer = Encoding.UTF8.GetBytes(responseString);
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
            listener.Stop();

            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://id.twitch.tv/oauth2/token")
            {
                Content = new FormUrlEncodedContent(new[]
                {
            new KeyValuePair<string,string>("client_id", _clientId),
            new KeyValuePair<string,string>("client_secret", _clientSecret),
            new KeyValuePair<string,string>("code", code),
            new KeyValuePair<string,string>("grant_type", "authorization_code"),
            new KeyValuePair<string,string>("redirect_uri", $"{RedirectUrl}")
        })
            };

            var response = await TwitchThrottledCall(() => _http.SendAsync(tokenRequest));
            var token = await response.Content.ReadFromJsonAsync<AuthToken>();

            _accessToken = token?.access_token ?? string.Empty;

            Program.Config.AccessToken = token?.access_token ?? string.Empty;
            Program.Config.RefreshToken = token?.refresh_token ?? string.Empty;
            Program.Config.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(token?.expires_in ?? 0);
            if (updateCallback)
                Program.Config.CallbackUrl = RedirectUrl;
            await ConfigStore.SaveConfig(); // imperative to call as it has new tokens

            Console.WriteLine("OAuth login complete; token saved.");
        }
        public async Task<bool> TryUpdateDateTimeToken()
        {
            if (await TryRefreshTokenAsync())
                return Program.Config.AccessTokenExpiresAt > DateTime.Now.AddMinutes(1);
            return false;
        }
        private async Task<bool> TryRefreshTokenAsync()
        {
            Console.WriteLine("Refreshing OAuth token...");

            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://id.twitch.tv/oauth2/token")
            {
                Content = new FormUrlEncodedContent(new[]
                {
            new KeyValuePair<string,string>("client_id", _clientId),
            new KeyValuePair<string,string>("client_secret", _clientSecret),
            new KeyValuePair<string,string>("grant_type", "refresh_token"),
            new KeyValuePair<string,string>("refresh_token", Program.Config.RefreshToken)
        })
            };

            var response = await TwitchThrottledCall(() => _http.SendAsync(tokenRequest));
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine("Refresh failed.");
                Console.WriteLine(await response.Content.ReadAsStringAsync());
                return false;
            }

            var token = await response.Content.ReadFromJsonAsync<AuthToken>();
            if (token == null || string.IsNullOrEmpty(token.access_token))
                return false;

            _accessToken = token.access_token;

            Program.Config.AccessToken = token.access_token;
            Program.Config.RefreshToken = token.refresh_token;
            Program.Config.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(token.expires_in);
            await ConfigStore.SaveConfig(); // imperative to update as to replace the new tokens.
            if (CurrentFrame.Length == 0)
                Console.WriteLine("Token refreshed.".PadRight(Console.WindowWidth));
            return true;
        }
        public async Task CheckForRefresh(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var expires = Program.Config.AccessTokenExpiresAt;

                // If missing, refresh immediately
                if (expires == null)
                {
                    if (CurrentFrame.Length == 0)
                        Console.WriteLine("\n\r🔄 Refreshing OAuth token...".PadRight(Console.WindowWidth));
                    await TryUpdateDateTimeToken();
                    await Task.Delay(TimeSpan.FromMinutes(1), token);
                    continue;
                }

                // Compute when to refresh (5 minutes before expiration)
                var refreshAt = expires.Value - TimeSpan.FromMinutes(5);
                var delay = refreshAt - DateTime.UtcNow;

                if (delay < TimeSpan.Zero)
                    delay = TimeSpan.Zero;

                // Sleep until refresh window
                await Task.Delay(delay, token);
                if (CurrentFrame.Length == 0)
                    Console.WriteLine("\n\r🔄 Refreshing OAuth token...".PadRight(Console.WindowWidth));
                var ok = await TryUpdateDateTimeToken();
                if (!ok && CurrentFrame.Length == 0)
                    Console.WriteLine("\n\r💥 Token refresh failed.".PadRight(Console.WindowWidth));

                // After refreshing, wait 1 minute before checking again
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
        }
        private async Task<string?> FetchUserDataAsync(string url, string errorContext)
        {
            if (string.IsNullOrEmpty(_accessToken))
            {
                Console.WriteLine("❌ No OAuth token available. Did you call InitializeOAuthAsync()?");
                return null;
            }

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Client-ID", _clientId);
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");

            var response = await TwitchThrottledCall(() => _http.SendAsync(request));
            var raw = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ Twitch API error ({errorContext}): {response.StatusCode}");
                Console.WriteLine(raw);
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var data = doc.RootElement.GetProperty("data");

                if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                {
                    Console.WriteLine($"❌ No user returned ({errorContext})");
                    return null;
                }

                return data[0].GetProperty("id").GetString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to parse Twitch response ({errorContext}): {ex.Message}");
                Console.WriteLine(raw);
                return null;
            }
        }
        public async Task<string?> ResolveUserIdFromToken()
        {
            return await FetchUserDataAsync(
                "https://api.twitch.tv/helix/users",
                "resolve user from token"
            );
        }
        public async Task<string?> ResolveUserId(string username)
        {
            return await FetchUserDataAsync(
                $"https://api.twitch.tv/helix/users?login={username}",
                $"resolve user '{username}'"
            );
        }
        public static async Task<string> GetCurrentlyLiveStreamers(string completeURL)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Client-ID", Program.Config.ClientId);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {Program.Config.AccessToken}");

            var url = $"https://api.twitch.tv/helix/streams{completeURL}";
            return await client.GetStringAsync(url);
        }
        public static async Task<bool> IsStreamerLiveAsync(string username)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Client-ID", Program.Config.ClientId);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {Program.Config.AccessToken}");

            var url = $"https://api.twitch.tv/helix/streams{username}";
            var response = await client.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            return data.GetArrayLength() > 0;
        }
        public async Task<bool> IsHypeTrainActiveAsync(string broadcasterId)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.twitch.tv/helix/hypetrain/events?broadcaster_id={broadcasterId}"
            );

            request.Headers.Add("Client-ID", _clientId);
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");

            var response = await TwitchThrottledCall(() => _http.SendAsync(request));
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // If Twitch returned an error object
            if (root.TryGetProperty("error", out var err))
            {
                Console.WriteLine($"⚠ HypeTrain API error: {err.GetString()} — {root.GetProperty("message").GetString()}");
                return false;
            }

            // If no "data" property exists
            if (!root.TryGetProperty("data", out var data))
                return false;

            return data.GetArrayLength() > 0;
        }
        public async Task<bool> SubscribeEvent(string sessionId, string broadcasterId, string eventType)
        {
            Console.WriteLine($"SubscribeEvent called: event={eventType}");

            Console.WriteLine($"[SubscribeEvent] Session ID: '{sessionId}'");

            if (string.IsNullOrEmpty(sessionId))
            {
                Console.WriteLine($"[DEBUG] ❌ SessionId is NULL or empty!");
                return false;
            }

            var body = new
            {
                type = eventType,
                version = "1",
                condition = new { broadcaster_user_id = broadcasterId },
                transport = new
                {
                    method = "websocket",
                    session_id = sessionId
                }
            };

            var json = JsonSerializer.Serialize(body);

            var request = new HttpRequestMessage(HttpMethod.Post,
                "https://api.twitch.tv/helix/eventsub/subscriptions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            request.Headers.Add("Client-ID", _clientId);
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");

            var response = await TwitchThrottledCall(() => _http.SendAsync(request));
            var raw = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Console.WriteLine("❌ 429 Too Many Requests");
                Console.WriteLine(raw);
                NeedsRotation = true;
                await Task.Delay(5500, exitToken);
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[SubscribeEvent] ❌ HTTP {(int)response.StatusCode} for {eventType}");
                Console.WriteLine($"[SubscribeEvent] Raw: {raw}");  // ← add this

                var doc = JsonDocument.Parse(raw);
            }

            if (!response.IsSuccessStatusCode)
            {
                var doc = JsonDocument.Parse(raw);

                if (doc.RootElement.TryGetProperty("error", out var err) &&
                    err.GetString() == "subscription_limit_exceeded")
                {
                    Console.WriteLine("RAW RESPONSE:");
                    Console.WriteLine(raw);
                    return false; // but mark session as full
                }
                NeedsRotation = true;
                await Task.Delay(500, exitToken);
                return false;
            }

            Console.WriteLine($"✅ Subscribed to {eventType}");
            await Task.Delay(100, exitToken);
            return true;
        }
        public async Task<EventSubSession> CreateNewSessionAsync()
        {
            Console.WriteLine($"[CreateNewSessionAsync] Called — total sessions so far: {SessionManager.Sessions.Count}");
            var session = new EventSubSession();
            session.Socket = new ClientWebSocket();
            await session.Socket.ConnectAsync(
                new Uri("wss://eventsub.wss.twitch.tv/ws"),
                CancellationToken.None
            );
            StartListening(session);
            var timeout = Task.Delay(15_000, exitToken);
            var completed = await Task.WhenAny(session.Ready.Task, timeout);
            if (completed == timeout)
                throw new Exception("EventSub session never received session_welcome");
            Console.WriteLine($"[CreateNewSessionAsync] Session ready: {session.SessionId}");
            return session;
        }
        private void StartListening(EventSubSession session)
        {
            _ = Task.Run(async () =>
            {
                var buffer = new byte[8192];
                uint keepAliveCount = 0;
                double time = 0;
                bool reset = true;
                Console.WriteLine($"[StartListening] Socket state: {session.Socket.State}, exitToken cancelled: {exitToken.IsCancellationRequested}");
                Console.WriteLine("Connected to Twitch EventSub WebSocket.");

                while (session.Socket.State == WebSocketState.Open && !exitToken.IsCancellationRequested)
                {
                    var result = await session.Socket.ReceiveAsync(buffer, CancellationToken.None);
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    var doc = JsonDocument.Parse(json);

                    if (!doc.RootElement.TryGetProperty("metadata", out var metadata))
                        continue;

                    var type = metadata.GetProperty("message_type").GetString();

                    // -------------------------
                    // KEEPALIVE HANDLING
                    // -------------------------
                    if (type == "session_keepalive")
                    {
                        session.KeepaliveReceived.TrySetResult(true); // add this
                        SessionManager.OnKeepaliveReceived();
                        LastKeepAlive = DateTime.UtcNow;
                        continue;
                    }

                    // -------------------------
                    // DEBUG LOGGING
                    // -------------------------
                    if (Program.debugMode)
                    {
                        Console.WriteLine("EventSub Message:");
                        Console.WriteLine(json);
                    }

                    // -------------------------
                    // SESSION WELCOME
                    // -------------------------
                    if (type == "session_welcome")
                    {
                        session.SessionId = doc.RootElement
                            .GetProperty("payload")
                            .GetProperty("session")
                            .GetProperty("id")
                            .GetString();

                        session.Ready.TrySetResult(true);
                        Console.WriteLine($"EventSub Session ID: {session.SessionId}");
                        continue;
                    }

                    if (type == "notification")
                    {
                        var subscriptionType = doc.RootElement
                            .GetProperty("payload")
                            .GetProperty("subscription")
                            .GetProperty("type")
                            .GetString();

                        if (subscriptionType == "stream.online")
                        {
                            session.ReadyForPurge = true;

                            var username = doc.RootElement
                                .GetProperty("payload")
                                .GetProperty("event")
                                .GetProperty("broadcaster_user_name")
                                .GetString();

                            var broadcasterId = doc.RootElement
                               .GetProperty("payload")
                               .GetProperty("event")
                               .GetProperty("broadcaster_user_id")
                               .GetString();

                            if (!string.IsNullOrWhiteSpace(username))
                            {
                                if (TwitchWentLive.Add(username))
                                {
                                    await Program.HandleStreamerLiveAsync(username);
                                    reset = true;
                                }
                            }
                        }
                        else if (subscriptionType == "stream.offline")
                        {
                            session.ReadyForPurge = true;

                            var username = doc.RootElement
                                .GetProperty("payload")
                                .GetProperty("event")
                                .GetProperty("broadcaster_user_name")
                                .GetString();

                            var broadcasterId = doc.RootElement
                               .GetProperty("payload")
                               .GetProperty("event")
                               .GetProperty("broadcaster_user_id")
                               .GetString();

                            if (!string.IsNullOrWhiteSpace(broadcasterId) && !string.IsNullOrWhiteSpace(username))
                            {
                                if (TwitchWentLive.Remove(username))
                                {
                                    if (streamerSet.ContainsKey(username))
                                    {
                                        streamerSet.Remove(username);
                                        Program.ConstructStringBuilder();
                                        await Task.Delay(200, exitToken);
                                    }
                                    await UnsubscribeSingleAsync(broadcasterId ?? string.Empty);
                                    reset = true;
                                }
                            }
                        }
                    }
                }
                // after the while loop ends
                Console.WriteLine($"[StartListening] Loop exited. Socket state: {session.Socket.State}, exitToken cancelled: {exitToken.IsCancellationRequested}");
            });
        }
        public int GetEventCost(string eventType)
        {
            return eventType switch
            {
                "stream.online" => 1,
                "stream.offline" => 1,
                "channel.update" => 1,
                "channel.follow" => 1,
                "channel.subscribe" => 1,
                "channel.cheer" => 1,
                "channel.raid" => 1,

                // If you add more event types later, put them here.

                _ => 1 // default cost
            };
        }
    }
           public   class     ParsedArgs
    {
        public bool AutoRun { get; set; }
        public Dictionary<string, StreamerConfig> Streamers { get; set; }
                                    = new(StringComparer.OrdinalIgnoreCase);
        public string WebhookKey { get; set; } = "NO_WEBHOOK";
    }
           public   class     AppConfig
    {
        public string MyUserID { get; set; } = @"auto-filled by twitch (please ignore this)";
        public string ClientId { get; set; } = @"your-client-id-here";
        public string ClientSecret { get; set; } = @"your-client-secret-here";
        public string CallbackUrl { get; set; } = @"http://localhost:8080";
        public Dictionary<string, StreamerConfig> Streams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool NotifyDiscord { get; set; } = false;
        public bool OpenBrowser { get; set; } = true;
        public bool CloseAfterFirst { get; set; } = false;
        public bool RunYoutubeCheck { get; set; } = false;
        public bool RunRefresh { get; set; } = true;
        public string DiscordWebhookDefault { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTime? AccessTokenExpiresAt
        {
            get; set;
        }
        public HashSet<string> Youtube { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int LegacyPollingDelayMS { get; set; } = 900;
        public int DelayTwitchAPI { get; set; } = 150;
        public int TwitchAPIPollDelay { get; set; } = 2000;
        public int ColourfulArrayRefreshRate { get; set; } = 100;
        public double ColourfulArrayAnimationSpeed { get; set; } = 0.1;
        public bool ColourfulArray { get; set; } = false;
        public bool UseSoundFolder { get; set; } = false;
        public bool NotifyPhone { get; set; } = false;
        public string PreferredAudioDeviceName { get; set; } = "speaker";
        public float MaxVolumeAllowed { get; set; } = .8f;
        public float MinVolumeAlert { get; set; } = .18f;
        public string NtfyTopic { get; set; } = string.Empty;
    }
           public   class     Option
    {
        public string Label { get; }
        public bool Value { get; set; }

        public Option(string label, bool value = false)
        {
            Label = label;
            Value = value;
        }
    }
}