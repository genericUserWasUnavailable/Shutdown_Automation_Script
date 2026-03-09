using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using static discordBot.TwitchApi;

namespace discordBot
{
    internal class Program
    {
        private static TwitchApi twitch;
        private static readonly CancellationTokenSource exitCts = new CancellationTokenSource();
        public static readonly CancellationToken exitToken = exitCts.Token;
        public static AppConfig Config { get; set; }
        public static async Task AnnounceStreamerLiveAsync(string username, string broadcasterId)
        {
            string url = Config.DiscordWebhook;
            string message = $"🔴 **{username} is now LIVE!**\nhttps://twitch.tv/{username}";

            await DiscordNotifier.SendAsync(url, message);
        }
        public static async Task SaveConfig()
        {
            var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync("config.json", json);
        }
        public static AppConfig LoadConfig(string path = "config.json")
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json);
        }
        static async Task Main(string[] args)
        {
            // Load config (or hardcode it)
            Program.Config = LoadConfig();

            // Initialize Twitch API
            twitch = new TwitchApi(Config.ClientId, Config.ClientSecret);
            await twitch.InitializeOAuthAsync();

            // Initialize session manager
            SessionManager.Initialize(twitch);
            await SessionManager.InitializeAsync();

            // Subscribe to events
            foreach (var kv in Config.Streams)
            {
                var streamer = kv.Value;
                foreach (var evt in streamer.Events)
                    await SessionManager.SubscribeAsync(streamer.Id, evt);
            }

            // Keep the bot alive
            await Task.Delay(-1);
        }
    }
    public class StreamerConfig
    {
        public string Id { get; set; } = string.Empty;
        public HashSet<string> Events { get; set; } = new() { "stream.online", "stream.offline" };
    }
    public class AppConfig
    {
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
        public Dictionary<string, StreamerConfig> Streams { get; set; }
        public string DiscordWebhook { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTime? AccessTokenExpiresAt
        {
            get; set;
        }
    }
    public static class SessionManager
    {
        private static TwitchApi _api;
        public static void Initialize(TwitchApi api)
        {
            _api = api;
        }
        private static readonly List<EventSubSession> _sessions = new();
        private static EventSubSession _current;

        private const int MaxCost = 300;
        private const int ThrottleDelayMs = 120;
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
        }
        public static async Task SubscribeAsync(string broadcasterId, string eventType)
        {
            int cost = GetEventCost(eventType);

            // Need a new session?
            if (_current.CurrentCost + cost > MaxCost)
            {
                _current = await CreateNewSessionAsync();
                await Task.Delay(1200);
                _sessions.Add(_current);
                await Task.Delay(1200);
            }

            await SubscribeEvent(_current.SessionId, broadcasterId, eventType);
            _current.CurrentCost += cost;

            await Task.Delay(ThrottleDelayMs);
        }
        // These three methods are placeholders — you already have them in TwitchApi
        private static Task<EventSubSession> CreateNewSessionAsync() => _api.CreateNewSessionAsync();
        private static Task SubscribeEvent(string sessionId, string broadcasterId, string eventType) => _api.SubscribeEvent(sessionId, broadcasterId, eventType);
        private static int GetEventCost(string eventType) => _api.GetEventCost(eventType);
    }
    public class AuthToken
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public int expires_in { get; set; }
    }
    public class TwitchApi
    {
        public class EventSubSession
        {
            public ClientWebSocket Socket { get; set; } = new();
            public string SessionId { get; set; } = "";
            public int CurrentCost { get; set; } = 0;
            public TaskCompletionSource<bool> Ready { get; } = new();
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
        private async Task<T> TwitchThrottledCall<T>(Func<Task<T>> action)
        {
            await _twitchRateLimit.WaitAsync();
            try
            {
                var now = DateTime.UtcNow;
                var diff = now - _lastCall;

                if (diff.TotalMilliseconds < 120)
                    await Task.Delay(120 - (int)diff.TotalMilliseconds);

                var result = await action();
                _lastCall = DateTime.UtcNow;
                return result;
            }
            finally
            {
                _twitchRateLimit.Release();
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
            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://id.twitch.tv/oauth2/token")
            {
                Content = new FormUrlEncodedContent(new[]
                {
            new KeyValuePair<string,string>("client_id", _clientId),
            new KeyValuePair<string,string>("client_secret", _clientSecret),
            new KeyValuePair<string,string>("grant_type", "client_credentials"),
            new KeyValuePair<string,string>("scope", "channel:read:hype_train")
        })
            };

            var response = await TwitchThrottledCall(() => _http.SendAsync(tokenRequest));
            var token = await response.Content.ReadFromJsonAsync<AuthToken>();

            _accessToken = token?.access_token ?? string.Empty;

            Program.Config.AccessToken = token?.access_token ?? string.Empty;
            Program.Config.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(token?.expires_in ?? 0);

            // Client credentials flow does NOT return a refresh token
            Program.Config.RefreshToken = null;

            await Program.SaveConfig();
        }
        public async Task<bool> TryUpdateDateTimeToken()
        {
            if (await TryRefreshTokenAsync())
                return Program.Config.AccessTokenExpiresAt > DateTime.Now.AddMinutes(1);
            return false;
        }
        private async Task<bool> TryRefreshTokenAsync()
        {
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
                return false;
            }

            var token = await response.Content.ReadFromJsonAsync<AuthToken>();
            if (token == null || string.IsNullOrEmpty(token.access_token))
                return false;

            _accessToken = token.access_token;

            Program.Config.AccessToken = token.access_token;
            Program.Config.RefreshToken = token.refresh_token;
            Program.Config.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(token.expires_in);
            await Program.SaveConfig(); // imperative to update as to replace the new tokens.
            return true;
        }
        public async Task<bool> SubscribeEvent(string sessionId, string broadcasterId, string eventType)
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

            var response = await TwitchThrottledCall(() => _http.SendAsync(request));
            var raw = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                NeedsRotation = true;
                await Task.Delay(5500);
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                var doc = JsonDocument.Parse(raw);

                if (doc.RootElement.TryGetProperty("error", out var err) &&
                    err.GetString() == "subscription_limit_exceeded")
                {
                    return false; // but mark session as full
                }
                NeedsRotation = true;
                await Task.Delay(500);
                return false;
            }
            return true;
        }
        public async Task<EventSubSession> CreateNewSessionAsync()
        {
            var session = new EventSubSession();
            session.Socket = new ClientWebSocket();

            await session.Socket.ConnectAsync(
                new Uri("wss://eventsub.wss.twitch.tv/ws"),
                CancellationToken.None
            );

            StartListening(session);
            await session.Ready.Task;
            await Task.Delay(200);
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

                while (session.Socket.State == WebSocketState.Open)
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
                        SessionManager.OnKeepaliveReceived();
                        continue;
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
                                    await Program.AnnounceStreamerLiveAsync(username, broadcasterId ?? string.Empty); // FOUND LIVE 
                                    reset = true;
                                }
                            }
                        }
                    }
                }
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
    public static class DiscordNotifier
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
}
