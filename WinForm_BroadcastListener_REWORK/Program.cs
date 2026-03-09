using System.Diagnostics;
using System.IO;
using System.Media;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace WinForm_BroadcastListener_REWORK
{
    internal static class Program
    {
        public static class JsonConfigLoader
        {
            public static AppConfig Load(string path)
            {
                string json = System.IO.File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppConfig>(json);
            }
        }
        public class AppConfig
        {
            public string ClientId { get; set; }
            public string ClientSecret { get; set; }
            public HashSet<string> Streamer { get; set; } = new();
            public bool NotifyDiscord { get; set; }
            public bool OpenBrowser { get; set; }
            public string DiscordUserID { get; set; } = "";
            public Dictionary<string, string> DiscordWebhooks { get; set; } = new();
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
            public DateTime? AccessTokenExpiresAt
            {
                get; set;
            }
        }
        const string STREAMER_LIVE = "🔴 **{0} is now LIVE!**\nhttps://twitch.tv/{0} ", DISCORD_PING = "<@{0}> You're up!";
        public const bool debugMode = false;      
        public static class Prompt
        {
            public static string ShowDialog(string text, string caption)
            {
                Form prompt = new Form()
                {
                    Width = 400,
                    Height = 150,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = caption,
                    StartPosition = FormStartPosition.CenterScreen
                };

                Label textLabel = new Label() { Left = 20, Top = 20, Text = text, AutoSize = true };
                TextBox inputBox = new TextBox() { Left = 20, Top = 50, Width = 340 };
                Button confirmation = new Button() { Text = "OK", Left = 270, Width = 90, Top = 80, DialogResult = DialogResult.OK };

                prompt.Controls.Add(textLabel);
                prompt.Controls.Add(inputBox);
                prompt.Controls.Add(confirmation);
                prompt.AcceptButton = confirmation;

                return prompt.ShowDialog() == DialogResult.OK ? inputBox.Text : null;
            }
        }

        public static async Task HandleKeyPressAsync(Keys key)
        {
            if (key == Keys.F12)
            {
                watcherActive = false;

                OpenConfigFolder();
                Config.AccessTokenExpiresAt = null;
                Config.AccessToken = null;                
            }
            else
                watcherActive = !watcherActive;

            if (watcherActive)
            {               
                string input = Prompt.ShowDialog("Enter streamer to add/remove:", "Streamer Input");

                if (!string.IsNullOrWhiteSpace(input))
                {
                    if (streamerSet.Contains(input))
                    {
                        streamerSet.Remove(input);
                        MessageBox.Show($"Removed: {input}", "Streamer Updated");                        
                    }
                    else
                    {
                        streamerSet.Add(input);
                        MessageBox.Show($"Added: {input}", "Streamer Updated");
                    }
                    Config.Streamer = streamerSet;
                }
                else
                {
                    MessageBox.Show($"No changes made.", "Streamer Updated");
                }
            }

            await SaveConfig();
            PlayWav();
            await Task.Delay(8000);
            if (key == Keys.F12) Environment.Exit(0);
        }
        public static async Task SaveConfig()
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            await System.IO.File.WriteAllTextAsync(pathOfJSON, JsonSerializer.Serialize(Config, options));
        }
        static void OpenConfigFolder()
        {
            string folder = Path.GetDirectoryName(pathOfJSON);
            if (!string.IsNullOrEmpty(folder))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
        }
        public static TwitchApi twitch = null;
        static bool watcherActive = true;
        public static string SelectedWebhookKey = "";
        static HashSet<string> streamerSet = new(StringComparer.OrdinalIgnoreCase);
        public static string pathOfJSON = "";
        public static AppConfig Config { get; set; }
        public class ParsedArgs
        {
            public bool AutoRun { get; set; }
            public List<string> Streamers { get; set; } = new();
            public string WebhookKey { get; set; } = null;
        }
        static ParsedArgs ParseArgs(string[] args)
        {
            var parsed = new ParsedArgs();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--autorun":
                    case "-a":
                    case "aa":
                        parsed.AutoRun = true;
                        break;

                    case "--webhook":
                    case "-w":
                        if (i + 1 < args.Length)
                            parsed.WebhookKey = args[++i];
                        break;

                    case "--streamer":
                    case "-s":
                        if (i + 1 < args.Length)
                            parsed.Streamers.Add(args[++i]);
                        break;

                    default:
                        // Legacy behavior: treat unknown args as streamer names
                        parsed.Streamers.Add(args[i]);
                        break;
                }
            }

            return parsed;
        }
        static void PlayWav()
        {
            var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("WinForm_BroadcastListener_REWORK.success.wav");

            if (stream == null)
            {
                Program.status.StatusLabel.Text = ("Sound resource not found.");
                return;
            }
            SoundPlayer player = new SoundPlayer(stream);
            player.Play();
        }
        public static StatusForm status = new StatusForm();
        [STAThread]
        static async Task Main(string[] args)
        {
            ApplicationConfiguration.Initialize();
            status.Show();
            string exeDir = AppContext.BaseDirectory;
            string configPath = Path.Combine(exeDir, "broadcastListener.json");
            bool NotifyDiscord = true;
            bool OpenBrowser = true;
            pathOfJSON = configPath;
            if (!File.Exists(configPath))
            {
                string streamer = Prompt.ShowDialog(
     "No config file found.\nA template will be created.\n\nEnter default streamer:",
     "Setup"
 );

                var template = new AppConfig
                {
                    ClientId = "your-client-id-here",
                    ClientSecret = "your-client-secret-here",
                    Streamer = new HashSet<string> { streamer },
                    NotifyDiscord = true,
                    OpenBrowser = true,
                    DiscordUserID = "target-userID-here",
                    DiscordWebhooks = new Dictionary<string, string>
                    {
                        { "default-template-rename-these-as-you-find-they-fit", "your-discord-webhook-url-here" }
                    },
                    AccessToken = null,
                    RefreshToken = null,
                    AccessTokenExpiresAt = null
                };
                string json = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
                MessageBox.Show(
                "A new config file has been created at:\n" +
                configPath +
                "\n\nPlease edit the file and enter your Twitch Client ID and Client Secret.\n" +
                "Press OK to open the folder.",
                "Config Created"
                );
                OpenConfigFolder();
                return;
            }
            AppConfig appConfig = null;
            try
            {
                appConfig = JsonConfigLoader.Load(configPath);
                Config = appConfig;
                status.StatusLabel.Text = "Updating config with new OAuth fields...";
                await SaveConfig();
            }
            catch (JsonException ex)
            {
                status.StatusLabel.Text = ("Your config file is invalid JSON.");
                status.StatusLabel.Text = (ex.Message);
                status.StatusLabel.Text = ("Delete broadcastListener.json and let the program recreate it.");               
                OpenConfigFolder();
                return;
            }
            string clientId = appConfig.ClientId;
            if (string.IsNullOrEmpty(clientId) || clientId == "your-client-id-here")
            {
                OpenConfigFolder();

                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://dev.twitch.tv/console/apps",
                    UseShellExecute = true
                });

                status.StatusLabel.Text = ("shit's bork'd lmao\n fill out clientID");
                return;
            }
            string clientSecret = appConfig.ClientSecret;
            if (string.IsNullOrEmpty(clientSecret) || clientSecret == "your-client-secret-here")
            {
                OpenConfigFolder();

                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://dev.twitch.tv/console/apps",
                    UseShellExecute = true
                });

                status.StatusLabel.Text = ("shit's bork'd lmao\nfill out clientSecret");
                
                return;
            }
            var parsed = ParseArgs(args);

            if (parsed.WebhookKey != null &&
                Config.DiscordWebhooks.ContainsKey(parsed.WebhookKey))
            {
                SelectedWebhookKey = parsed.WebhookKey;
            }
            bool manualRun = !parsed.AutoRun;
            bool watchOnline = true;

            clientId = appConfig.ClientId;
            clientSecret = appConfig.ClientSecret;
            twitch = new TwitchApi(clientId, clientSecret);
            if (manualRun)
            {
                string currentCollection = "";
                foreach (var b in appConfig.Streamer) currentCollection += b + '\n';
                if (currentCollection.Length > 0)
                {
                    status.StatusLabel.Text = ($"Current Collection:\n{currentCollection}");
                }
                watchOnline = MessageBox.Show("Watch for streamer going LIVE<=>HYPE", "Select") ==  DialogResult.Yes;
                
                status.StatusLabel.Text = "Enter streamer to monitor (or remove) (comma-separated, blank for default):";
                string input = Prompt.ShowDialog(
                    "Enter streamer to monitor (or remove)\n(comma-separated, blank for default):",
                    "Streamer Input"
                )?.Trim();


                if (!string.IsNullOrEmpty(input))
                {
                    foreach (var s in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!appConfig.Streamer.Contains(s))
                            streamerSet.Add(s.Trim());
                    }
                }
                else
                {
                    foreach (var s in appConfig.Streamer)
                        streamerSet.Add(s);
                }
            }
            else
            {
                foreach (var arg in args)
                {
                    if (string.IsNullOrWhiteSpace(arg) || arg.Equals("aa", StringComparison.OrdinalIgnoreCase))
                        continue;
                    else
                    {
                        streamerSet.Add(arg);
                    }
                }
            }
            if (streamerSet.Count == 0 || !manualRun)
            {
                foreach (var s in appConfig.Streamer)
                    streamerSet.Add(s);
            }
            List<string> eventTypes = new();

            if (watchOnline)
            {
                eventTypes.Add("stream.online");
            }
            else
            {
                eventTypes.Add("channel.hype_train.begin");
                eventTypes.Add("channel.hype_train.progress");
                eventTypes.Add("channel.hype_train.end");
            }
            string myUserId = await twitch.ResolveUserIdFromToken();
            bool canSubscribeToHypeTrain = true;

            foreach (var streamer in streamerSet)
            {
                var id = await twitch.ResolveUserId(streamer);
                if (id != myUserId)
                {
                    canSubscribeToHypeTrain = false;
                    break;
                }
            }

            if (!canSubscribeToHypeTrain)
            {
                status.StatusLabel.Text = ("⚠️ Cannot subscribe to hype-train events for channels you don't own.");
                status.StatusLabel.Text = ("Falling back to stream.online mode.");

                watchOnline = true;
                eventTypes.Clear();
                eventTypes.Add("stream.online");
            }

            await SaveConfig();
            
            status.StatusLabel.Text = ("Requesting OAuth token...");

            status.StatusLabel.Text = ("\rStarting watcher loop...       ");

            int lastLength = 0;

            NotifyDiscord = appConfig.NotifyDiscord;
            OpenBrowser = appConfig.OpenBrowser;

            await twitch.InitializeOAuthAsync();
            await twitch.ClearAllSubscriptionsAsync();
            await twitch.InitializeEventSubWebSocketAsync(Config.NotifyDiscord, Config.OpenBrowser);

            // Wait until session ID is available
            while (twitch.SessionId == null)
                await Task.Delay(100);

            // For each streamer, resolve their user ID and subscribe
            foreach (var streamer in streamerSet)
            {
                var id = await twitch.ResolveUserId(streamer);
                if (id == null)
                {
                    status.StatusLabel.Text = ($"Skipping invalid streamer: {streamer}");
                    continue;
                }

                if (watchOnline)
                {
                    bool isLive = await TwitchApi.IsStreamerLiveAsync(streamer);

                    if (isLive)
                    {
                        PlayWav();
                        await HandleStreamerLiveAsync(streamer, NotifyDiscord, OpenBrowser);
                        continue;
                    }
                }
                else
                {
                    // Hype-train mode: check if a hype train is already active
                    bool hypeTrainActive = await twitch.IsHypeTrainActiveAsync(id);

                    if (hypeTrainActive)
                    {
                        PlayWav();
                        status.StatusLabel.Text = ($"🚂 Active Hype Train detected for {streamer}!");
                        continue;
                    }

                    //      _ = MonitorPublicHypeTrainAsync(streamer, NotifyDiscord, OpenBrowser, CancellationToken.None);

                    bool isLive = await TwitchApi.IsStreamerLiveAsync(streamer);

                    if (isLive) // this part is never run if the hypetrain activity is detected, because of the continue?
                    {
                        PlayWav();
                        await HandleStreamerLiveAsync(streamer, NotifyDiscord, OpenBrowser);
                        continue;
                    }
                }

                // Subscribe to the selected event types
                foreach (var eventType in eventTypes)
                {
                    await twitch.SubscribeEvent(twitch.SessionId, id, eventType);
                }
            }

            status.StatusLabel.Text = ("Listening for stream.online events...");
            foreach (var b in streamerSet)
                status.StatusLabel.Text = (b);
            if (manualRun)
            Application.Run(new Form1());
            else
                await Task.Delay(Timeout.Infinite);
        }
       
        public static async Task HandleStreamerLiveAsync(string username, bool notifyDiscord = true, bool openBrowser = true)
        {
            streamerSet.Remove(username);
            await SaveConfig();

            string pingedUser = "";

            if (notifyDiscord)
            {
                string notifyMessage = string.Format(STREAMER_LIVE, username);
                if (!string.IsNullOrWhiteSpace(Config.DiscordUserID))
                    pingedUser = string.Format(DISCORD_PING, Config.DiscordUserID);

                string webhookUrl = null;

                // 1. CLI override
                if (!string.IsNullOrWhiteSpace(SelectedWebhookKey) &&
                    Config.DiscordWebhooks.TryGetValue(SelectedWebhookKey, out var explicitUrl))
                {
                    webhookUrl = explicitUrl;
                    status.StatusLabel.Text = ($"Using webhook override: {SelectedWebhookKey}");
                }
                else
                {
                    // 2. Alarm name (same as streamer name unless you define differently)
                    string alarmKey = username.ToLower();

                    if (Config.DiscordWebhooks.TryGetValue(alarmKey, out var alarmUrl))
                    {
                        webhookUrl = alarmUrl;
                        status.StatusLabel.Text = ($"Using webhook for alarm '{alarmKey}'");
                    }
                    else
                    {
                        // 3. Fallback to first webhook
                        webhookUrl = Config.DiscordWebhooks.Values.First();
                        status.StatusLabel.Text = ("No matching webhook found; using first entry in config.");
                    }
                }
                await DiscordNotifier.SendAsync(webhookUrl, notifyMessage + pingedUser);
            }

            if (openBrowser)
            {
                status.StatusLabel.Text = ($"{username} is LIVE! Launching browser...");
                LaunchBrowser($"https://twitch.tv/{username}");
            }
        }
        static void LaunchBrowser(string url)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
    }
    public class AuthToken
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public int expires_in { get; set; }
        public string token_type { get; set; }
    }

    public class StreamResponse
    {
        public StreamData[] data { get; set; }
    }

    public class StreamData
    {
        public string id { get; set; }
        public string user_name { get; set; }
        public string type { get; set; }
    }
    public class TwitchApi
    {
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly HttpClient _http = new HttpClient();
        private string _accessToken;
        public string SessionId => _sessionId;
        /*/ public async Task<PublicHypeTrainStatus?> PublicHypeTrainProbe(string streamerLogin)
         {
             return new PublicHypeTrainStatus { bool, int , double, DateTime };
         }*/
        public record PublicHypeTrainStatus(
            bool IsActive,
            int? Level,
            double? ProgressPercent,
            DateTimeOffset? EndsAt
        );

        public async Task<string> ResolveUserIdFromToken()
        {
            if (string.IsNullOrEmpty(_accessToken))
            {
                Program.status.StatusLabel.Text = ("❌ No OAuth token available. Did you call InitializeOAuthAsync()?");
                return null;
            }

            var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.twitch.tv/helix/users"
            );

            request.Headers.Add("Client-ID", _clientId);
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");

            var response = await _http.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Program.status.StatusLabel.Text = ($"❌ Failed to resolve user from token: {response.StatusCode}");
                Program.status.StatusLabel.Text = (raw);
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var data = doc.RootElement.GetProperty("data");

                if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                {
                    Program.status.StatusLabel.Text = ("❌ No user returned for this token.");
                    return null;
                }

                var user = data[0];
                return user.GetProperty("id").GetString();
            }
            catch (Exception ex)
            {
                Program.status.StatusLabel.Text = ($"❌ Failed to parse user-from-token response: {ex.Message}");
                Program.status.StatusLabel.Text = (raw);
                return null;
            }
        }

        public TwitchApi(string clientId, string clientSecret)
        {
            _clientId = clientId;
            _clientSecret = clientSecret;
        }
        public async Task ClearAllSubscriptionsAsync()
        {
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.twitch.tv/helix/eventsub/subscriptions"
            );

            request.Headers.Add("Client-ID", _clientId);
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");

            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            foreach (var sub in data.EnumerateArray())
            {
                var id = sub.GetProperty("id").GetString();

                var deleteRequest = new HttpRequestMessage(
                    HttpMethod.Delete,
                    $"https://api.twitch.tv/helix/eventsub/subscriptions?id={id}"
                );

                deleteRequest.Headers.Add("Client-ID", _clientId);
                deleteRequest.Headers.Add("Authorization", $"Bearer {_accessToken}");

                await _http.SendAsync(deleteRequest);
            }

            Program.status.StatusLabel.Text = ("🧹 Cleared all existing EventSub subscriptions.");
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
            Program.status.StatusLabel.Text = ("Opening browser for Twitch OAuth login...");

            string url =
            $"https://id.twitch.tv/oauth2/authorize" +
            $"?client_id={_clientId}" +
            $"&redirect_uri=http://localhost:8080/" +
            $"&response_type=code" +
            $"&scope=channel:read:hype_train";


            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

            var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:8080/");
            listener.Start();

            var context = await listener.GetContextAsync();
            string code = context.Request.QueryString["code"];

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
            new KeyValuePair<string,string>("redirect_uri", "http://localhost:8080/")
        })
            };

            var response = await _http.SendAsync(tokenRequest);
            var token = await response.Content.ReadFromJsonAsync<AuthToken>();

            _accessToken = token.access_token;

            Program.Config.AccessToken = token.access_token;
            Program.Config.RefreshToken = token.refresh_token;
            Program.Config.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(token.expires_in);

            await Program.SaveConfig();

            Program.status.StatusLabel.Text = ("OAuth login complete; token saved.");
        }

        private async Task<bool> TryRefreshTokenAsync()
        {
            Program.status.StatusLabel.Text = ("Refreshing OAuth token...");

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

            var response = await _http.SendAsync(tokenRequest);
            if (!response.IsSuccessStatusCode)
            {
                Program.status.StatusLabel.Text = ("Refresh failed.");
                Program.status.StatusLabel.Text = (await response.Content.ReadAsStringAsync());
                return false;
            }

            var token = await response.Content.ReadFromJsonAsync<AuthToken>();
            if (token == null || string.IsNullOrEmpty(token.access_token))
                return false;

            _accessToken = token.access_token;

            Program.Config.AccessToken = token.access_token;
            Program.Config.RefreshToken = token.refresh_token;
            Program.Config.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(token.expires_in);

            await Program.SaveConfig();

            Program.status.StatusLabel.Text = ("Token refreshed.");
            return true;
        }

        public async Task<string> ResolveUserId(string username)
        {
            if (string.IsNullOrEmpty(_accessToken))
            {
                Program.status.StatusLabel.Text = ("❌ No OAuth token available. Did you call InitializeOAuthAsync()?");
                return null;
            }

            var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.twitch.tv/helix/users?login={username}"
            );

            request.Headers.Add("Client-ID", _clientId);
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");

            var response = await _http.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Program.status.StatusLabel.Text = ($"❌ Twitch API error for '{username}': {response.StatusCode}");
                Program.status.StatusLabel.Text = (raw);
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var data = doc.RootElement.GetProperty("data");

                if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                {
                    Program.status.StatusLabel.Text = ($"❌ No Twitch user found for '{username}'");
                    return null;
                }

                var user = data[0];
                if (!user.TryGetProperty("id", out var idProp))
                {
                    Program.status.StatusLabel.Text = ($"❌ Twitch user object missing 'id' for '{username}'");
                    return null;
                }

                return idProp.GetString();
            }
            catch (Exception ex)
            {
                Program.status.StatusLabel.Text = ($"❌ Failed to parse Twitch user response for '{username}': {ex.Message}");
                Program.status.StatusLabel.Text = (raw);
                return null;
            }
        }
        private ClientWebSocket _webSocket;
        private string _sessionId;
        public async Task InitializeEventSubWebSocketAsync(bool notifyDiscord = false, bool openBrowser = false)
        {
            try
            {
                _webSocket = new ClientWebSocket();

                // Connect to Twitch EventSub WebSocket endpoint
                await _webSocket.ConnectAsync(
                    new Uri("wss://eventsub.wss.twitch.tv/ws"),
                    CancellationToken.None
                );

                Program.status.StatusLabel.Text = ("Connected to Twitch EventSub WebSocket.");

                // Start listening for messages
                _ = Task.Run(async () =>
                {
                    var buffer = new byte[8192];
                    uint keepAliveCount = 0;
                    while (_webSocket.State == WebSocketState.Open)
                    {
                        var result = await _webSocket.ReceiveAsync(buffer, CancellationToken.None);
                        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

                        var doc = JsonDocument.Parse(json);

                        if (!doc.RootElement.TryGetProperty("metadata", out var metadata))
                            continue;

                        var type = metadata.GetProperty("message_type").GetString();

                        if (type == "session_keepalive")
                        {
                            keepAliveCount++;
                            Program.status.StatusLabel.Text = ($"\rStill connected — ping: {keepAliveCount}/ {uint.MaxValue}");
                            continue;
                        }
                        Program.status.StatusLabel.Text = ("");
                        if (Program.debugMode)
                        {
                            Program.status.StatusLabel.Text = ("EventSub Message:");
                            Program.status.StatusLabel.Text = (json);
                        }
                        if (type == "session_welcome")
                        {
                            TryExtractSessionId(json);
                        }
                        else if (type == "notification")
                        {
                            var subscriptionType = doc.RootElement
                                .GetProperty("payload")
                                .GetProperty("subscription")
                                .GetProperty("type")
                                .GetString();

                            if (subscriptionType == "stream.online")
                            {
                                var username = doc.RootElement
                                    .GetProperty("payload")
                                    .GetProperty("event")
                                    .GetProperty("broadcaster_user_name")
                                    .GetString();

                                await Program.HandleStreamerLiveAsync(username, notifyDiscord, openBrowser);
                            }
                        }
                    }

                });
            }
            catch (Exception ex)
            {
                Program.status.StatusLabel.Text = ($"EventSub WebSocket error: {ex.Message}");
            }
        }
        public static async Task<bool> IsStreamerLiveAsync(string username)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Client-ID", Program.Config.ClientId);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {Program.Config.AccessToken}");

            var url = $"https://api.twitch.tv/helix/streams?user_login={username}";
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

            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // If Twitch returned an error object
            if (root.TryGetProperty("error", out var err))
            {
                Program.status.StatusLabel.Text = ($"⚠ HypeTrain API error: {err.GetString()} — {root.GetProperty("message").GetString()}");
                return false;
            }

            // If no "data" property exists
            if (!root.TryGetProperty("data", out var data))
                return false;

            return data.GetArrayLength() > 0;
        }


        public async Task SubscribeEvent(string sessionId, string broadcasterId, string eventType)
        {
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

            var response = await _http.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Program.status.StatusLabel.Text = ($"❌ Failed to subscribe to {eventType}: {response.StatusCode}");
                Program.status.StatusLabel.Text = (raw);
            }
            else
            {
                Program.status.StatusLabel.Text = ($"✅ Subscribed to {eventType}");
            }
        }

        private void TryExtractSessionId(string json)
        {
            try
            {
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("metadata", out var metadata))
                {
                    if (metadata.TryGetProperty("message_type", out var type) &&
                        type.GetString() == "session_welcome")
                    {
                        var sessionId = doc.RootElement
                            .GetProperty("payload")
                            .GetProperty("session")
                            .GetProperty("id")
                            .GetString();

                        _sessionId = sessionId;
                        Program.status.StatusLabel.Text = ($"EventSub Session ID: {_sessionId}");
                    }
                }
            }
            catch (Exception ex)
            {
                Program.status.StatusLabel.Text = ($"Failed to parse session ID: {ex.Message}");
            }
        }
    }
    public static class DiscordNotifier
    {
        private static readonly HttpClient _http = new HttpClient();

        public static async Task SendAsync(string webhookUrl, string message)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl))
                return;

            var payload = new { content = message };
            var json = JsonSerializer.Serialize(payload);

            var response = await _http.PostAsync(
                webhookUrl,
                new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            );

            response.EnsureSuccessStatusCode();
        }
    }
}