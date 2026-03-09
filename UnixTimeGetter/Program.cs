using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
partial class Program
{
    public class AppConfig
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public Dictionary<string, double?> Rates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public static AppConfig Load(string path)
        {
            string json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();

            // Ensure Rates is always a dictionary
            if (cfg.Rates == null)
                cfg.Rates = new Dictionary<string, double?>();

            return cfg;
        }
        public static void Save(string path, AppConfig config)
        {
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }
    public class Option
    {
        public string Label { get; }
        public bool Value { get; set; }

        public Option(string label, bool value = false)
        {
            Label = label;
            Value = value;
        }
    }
    public static class ConsoleUI
    {
        private const string ENABLED = "[ X ]";
        private const string DISABLED = "[   ]";

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
                ApplyRules(options);

                // Draw all options
                for (int i = 0; i < options.Length; i++)
                {
                    Console.SetCursorPosition(0, optionStartLine + i);

                    Console.ForegroundColor = (i == selectedRow)
                        ? ConsoleColor.Green
                        : ConsoleColor.Gray;

                    string checkbox = options[i].Value ? ENABLED : DISABLED;
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

                    case ConsoleKey.D1:
                        options[0].Value = true;
                        ApplyRules(options);
                        return;

                    default:
                        int index = -1;

                        if (key >= ConsoleKey.D1 && key <= ConsoleKey.D9)
                            index = key - ConsoleKey.D1;
                        else if (key >= ConsoleKey.NumPad1 && key <= ConsoleKey.NumPad9)
                            index = key - ConsoleKey.NumPad1;

                        if (index >= 0 && index < options.Length)
                        {
                            for (int i = 0; i < options.Length; i++)
                                options[i].Value = (i == index);
                        }
                        break;
                }
            }
            void ApplyRules(Option[] opts)
            {
                bool conversion = opts[0].Value;
                bool timestamp = !opts[0].Value;
                bool eggtimer = opts[2].Value;
                bool datetime = !opts[2].Value;

                if (conversion)
                {
                    opts[1].Value = false;
                    opts[2].Value = false;
                    opts[3].Value = false; // Egg‑timer off
                }
                else
                {
                    opts[1].Value = true;
                    if (eggtimer)
                    {
                        opts[2].Value = true;
                        opts[3].Value = false;
                    }
                    else
                    {
                        opts[2].Value = false;
                        opts[3].Value = true;
                    }
                }
            }
        }
    }
    public class RateEntry
    {
        public string VALUTA { get; set; } = string.Empty;
        public double? DNE { get; set; } // middle rate
    }
    public class RateResponse
    {
        public List<RateEntry> data { get; set; }

    }
    #region FIELDS
    private static string ConfigPath = string.Empty;
    private static DateTime targetDatetime; // make this a static field so handler can access it
    private static long UnixTimestamp = 0;
    private static string discordTag = "";
    private static bool restartRequested = false;
    const bool debugSet = false;
    const double MAX_TIMESPAN_SECONDS = 31557600000; // ~1000 years (exactly actually but whatever - veryveryvhighnumber lmao
    private static bool TimeSpanOverflow = false;
    private static double OverflowSeconds = 0;
    private static readonly DateTime UTCDateTime = DateTime.Now;
    private static TimeSpan timeSpan = TimeSpan.Zero;
    const string
        SupportedUnits =
     @"Supported units:

Length:
m, ft, in, yd, mi, nm, cm, mm, km, stt, spit, fur, chain, league

Speed:
mph, kph, mps, knot

Currency:
usd, eur, gbp, dkk, sek, cad, jpy, aud

Temperature:
c, f, k, r

Weight:
kg, lb, gr, oz, st, slug",
    EGG_TIMER = "Set timelapse (e.g. '2h')",
        DATETIME = "Set the time for timestamp",
        VERSION = "1.10.3",
        PATCHNOTES = "added sound file\ncan now Ctrl-C after setting a timestamp to increment by 24h per Ctrl-C\nTweaked conversion feature\nAdded feet/m/yard/miles\nmore errors.\nFixed errorFixed egg-timer mode.\nAdded stronger egg-timer features\nFixed formatting issues.\nMinor tweak\nSuper cool feature!";
    #endregion
    static readonly Dictionary<string, string[]> PainkillerAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            { "paracetamol", new[] { "paracetamol", "tylenol", "panodil", "pamol", "panadol", "crocin", "calonal" } },
            { "ibuprofen",   new[] { "ibuprofen", "advil", "motrin", "nurofen", "ipren", "brufen" } },        
        };
    static readonly Dictionary<string, string[]> UnitAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            // distance
            { "m",  new[] { "m", "meter", "meters", "metre", "metres", "met", "mets" } },
            { "ft", new[] { "ft", "foot", "feet", "'" } },
            { "in", new[] { "in", "inch", "inches", "\"" } },
            { "yd", new[] { "yd", "yard", "yards" } },
            { "mi", new[] { "mi", "mile", "miles" } },
            { "nm", new[] { "nm", "nmi", "nauticalmile", "nauticalmiles", "nautical mile", "nautical miles" } },
            { "cm", new[] { "cm", "centimeter", "centimeters", "centimetre", "centimetres" } },
            { "mm", new[] { "mm", "millimeter", "millimeters", "millimetre", "millimetres" } },
            { "km", new[] { "km", "kilometer", "kilometers", "kilometre", "kilometres" } },
            { "stt", new[] { "stt", "stonetoss", "stone-toss", "stone_toss", "stone toss", "toss", "throw" } },
            { "spit", new[] { "spit", "spits", "sp" } },
            { "fur", new[] { "fur", "furlong", "furlongs" } },
            { "chain", new[] { "chain", "chains", "chns", "ch", "chn" } },
            { "league", new[] { "league", "leagues", "lea", "leas" } },
            { "link", new[] { "link", "links", "li" } },
            { "rod", new[] { "rod", "rods", "pole", "poles", "perch", "perches" } },
            { "thou", new[] { "th", "thou", "thous" } },
            // distance future implementation maybe
            { "fathom", new[] { "fathom", "fathoms", "ftm" } },

            // currency
            { "usd", new[] { "usd", "$", "us$", "dollar", "dollars" } },
            { "eur", new[] { "eur", "€", "euro", "euros" } },
            { "gbp", new[] { "gbp", "£", "pound sterling" } },
            { "dkk", new[] { "dkk", "kr", "kroner" } },
            { "sek", new[] { "sek", "swe" } },
            { "cad", new[] { "cad", "c$", "can$", "canadian" } },
            { "jpy", new[] { "jpy", "¥", "yen" } },
            { "aud", new[] { "aud", "a$", "au$", "aud$", "australian", "aussie", "aussiedollar" } },
            
            // speed
            { "mps",  new[] { "mps", "meterpersecond", "meterspersecond" } },
            { "kph",  new[] { "kph", "kmh", "kilometerperhour", "kilometersperhour" } },
            { "mph",  new[] { "mph", "mileperhour", "milesperhour" } },
            { "knot", new[] { "knot", "knots", "kt", "kts" } },

            // temperature
            { "c", new[] { "c", "°c", "celsius" } },
            { "f", new[] { "f", "°f", "fahrenheit" } },
            { "k", new[] { "k", "kelvin" } },
            { "r", new[] { "r", "rankine" } },

            // weight 
            { "kg", new[] { "kg", "kilogram", "kilograms" } },
            { "g",  new[] { "g", "gram", "grams" } },
            { "lb", new[] { "lb", "lbs", "pound" } },
            { "oz", new[] { "oz", "ounce", "ounces" } },
            { "st", new[] { "st", "stone", "stones" } },
            { "gr", new[] { "gr", "grain", "grains" } },
            { "slug", new[] { "slug", "slugs" } },
        };    
    static string? NormalizeUnit(string rawUnit)
    {
        rawUnit = rawUnit.ToLowerInvariant();

        foreach (var kvp in UnitAliases)
        {
            if (kvp.Value.Contains(rawUnit))
                return kvp.Key;
        }

        return null;
    }
    static string? NormalizePainkiller(string raw)
    {
        raw = raw.ToLowerInvariant();

        foreach (var kvp in PainkillerAliases)
        {
            if (kvp.Value.Contains(raw))
                return kvp.Key;
        }
        return null;
    }
    static void PrintSupportedUnits() => WriteCentered(SupportedUnits);
    public static bool AskYesNo(string inputText, int modeSelected)
    {
        string actionSelect1 = "", actionSelect2 = "";
        switch (modeSelected)
        {
            case 1:
                actionSelect1 = "[ YES ]";
                actionSelect2 = "[ NO ]";
                break;
            case 0:
                actionSelect1 = "[ Conversion ]";
                actionSelect2 = "[ Timestamp ]";
                break;
        }

        if (!string.IsNullOrWhiteSpace(inputText))
        {
            WriteCentered(inputText);
            Console.WriteLine();
        }

        int selected = modeSelected;
        int optionStartLine = Console.CursorTop;

        while (true)
        {
            // Build the full option string with spacing
            string optionLine = $"{actionSelect1}   {actionSelect2}";

            // Center the whole line
            int left = Math.Max((Console.WindowWidth - optionLine.Length) / 2, 0);
            Console.SetCursorPosition(left, optionStartLine);

            // Render options with colors
            Console.ForegroundColor = selected == 0 ? ConsoleColor.Green : ConsoleColor.DarkGray;
            Console.Write(actionSelect1);
            Console.ResetColor();

            Console.Write("   ");

            Console.ForegroundColor = selected == 1 ? ConsoleColor.Green : ConsoleColor.DarkGray;
            Console.Write(actionSelect2);
            Console.ResetColor();

            // Clear any trailing characters
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
    }
    private static void OpenConfigFolder()
    {
        string exeDir = AppContext.BaseDirectory;
        string jsonName = "rates.json";
        ConfigPath = Path.Combine(exeDir, jsonName);

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
    static void KeyPressHandler(object sender, ConsoleCancelEventArgs e)
    {
        if (e.SpecialKey == ConsoleSpecialKey.ControlC)
        {
            if (AskYesNo("Open config?", 1))
                OpenConfigFolder();
            restartRequested = true;
            if (timeSpan != TimeSpan.Zero)
            timeSpan += TimeSpan.FromDays(1);
            if (UnixTimestamp == 0)
            {
                e.Cancel = true;
                return;
            }
            targetDatetime = targetDatetime.AddDays(1);            
            UnixTimestamp = ((DateTimeOffset)targetDatetime).ToUnixTimeSeconds();
            discordTag = $"Time set for: {targetDatetime} (<t:{UnixTimestamp}:t>)";
            EndGame(discordTag);
            e.Cancel = true;
        }
        else if (e.SpecialKey == ConsoleSpecialKey.ControlBreak)
        {
            if (AskYesNo("Open config?", 1))
                OpenConfigFolder();
            Environment.Exit(0);
        }
    }
    public static int AskOptions(string question, Option[] options)
    {
        if (!string.IsNullOrWhiteSpace(question))
        {
            WriteCentered(question);
            Console.WriteLine();
        }

        ConsoleUI.SelectionArray(options);

        for (int i = 0; i < options.Length; i++)
            if (options[i].Value)
                return i;

        return -1;
    }
    static void PlayEmbeddedWav()
    {
        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("UnixTimeGetter.incorrect.wav");

        if (stream == null)
        {
            Console.WriteLine("Sound resource not found.");
            return;
        }
        SoundPlayer player = new SoundPlayer(stream);
        player.Play();
    }
    [STAThread]
    static async Task Main()
    {
        await UnitConversion.InitializeRates();
        targetDatetime = UTCDateTime; // initialize once
        Console.CancelKeyPress += KeyPressHandler;
        Console.WindowWidth = Math.Min(80, Console.LargestWindowWidth);
        Console.BufferWidth = Math.Max(Console.WindowWidth, 100);

        Console.WindowHeight = Math.Min(25, Console.LargestWindowHeight);
        Console.BufferHeight = Math.Max(Console.WindowHeight, 200);

        string discordTag = "";
        TimeSpan addThis = TimeSpan.Zero;
        Console.WriteLine($"{VERSION}".PadLeft(Console.WindowWidth/2));
        var options = new[]
        {
            new Option("Conversion mode"),
            new Option("Timestamp mode", true),
            new Option("Egg‑timer mode"),
            new Option("Date‑time mode", true)
        };

        AskOptions("Settings", options);

        bool conversionMode = options[0].Value;
        bool timestampMode = options[1].Value;
        bool useEggtimer = options[2].Value;
        bool datetimeMode = options[3].Value;

        // ConsoleUI.SelectionArray(options);
        useEggtimer = options[2].Value;
           conversionMode = options[0].Value;
        

        while (true)
        {
            if (restartRequested)
            {
                restartRequested = false;

                TimeSpanOverflow = false;
                OverflowSeconds = 0;
                timeSpan = TimeSpan.Zero;
                UnixTimestamp = 0;

                continue;
            }

           // conversionMode = AskYesNo("Conversion or timestamp?", 0);
            if (conversionMode)
            {
                Console.Clear();
                PrintSupportedUnits();
                Console.WriteLine("");
                WriteCentered("Input the unit to be converted:");
                Console.Write("");
                Console.SetCursorPosition(Console.WindowWidth / 2, Console.CursorTop);
                discordTag = await UnitConversion.ConvertThis(Console.ReadLine());
                Console.Clear();
                goto EndofFile;
            }
            Console.Clear();
            //useEggtimer = AskYesNo("Use as egg-timer?", 1);
            Console.Clear();
            Console.WriteLine(useEggtimer ? EGG_TIMER : DATETIME);
            string userInput = Console.ReadLine() ?? "";
            if (debugSet)
                Console.WriteLine($"DEBUG: userInput = '{userInput}' (length: {userInput.Length})");
            if (string.IsNullOrEmpty(userInput))
            {
                Console.Clear();
                if (AskYesNo("userInput not recognised. Go again?", 1))
                    break;
                Environment.Exit(0);
            }
            if (!useEggtimer)
            {
                targetDatetime = GetShutdownDateTime(userInput);
                timeSpan = UTCDateTime - DateTime.Now;
                await Task.Delay(600);
            }
            else
            {
                timeSpan = GetShutdownTimer(userInput);
                if (!TimeSpanOverflow)
                {
                    targetDatetime = DateTime.Now.AddSeconds(timeSpan.TotalSeconds);
                    await Task.Delay(600);
                }
            }

            UnixTimestamp = ((DateTimeOffset)targetDatetime).ToUnixTimeSeconds();
            if (TimeSpanOverflow)
            {
                double years = OverflowSeconds / 31557600;
                double centuries = years / 100;
                double millennia = years / 1000;
                double aeons = years / 1000000000;

                if (aeons >= 1)
                    discordTag = $"⏰ {aeons:N0} aeons from now (Sol is dying too, so don't sweat it)";
                else if (millennia > 1)
                    discordTag = $"⏰ {millennia:N0} millennia from now (We're either at Mars or we're fucked;no in-between)";
                else if (centuries > 1)
                    discordTag = $"⏰ {centuries:N0} centuries from now (unless you're a vampire you shouldn't care)";
                else
                    discordTag = $"⏰ {years:N0} years from now (discord probably ain't here by then)";
            }
            else
                discordTag = useEggtimer ? $"<t:{UnixTimestamp}:R>" : $"SOON™ <t:{UnixTimestamp}:R>";
            EndofFile:
            EndGame(discordTag);

            if (!AskYesNo($"{(useEggtimer ? $"{targetDatetime}\nRestart?" : "Restart?")}", 1))
                break;
        }
    }
    private static void EndGame(string discordTag)
    {        
        OpenClipboard(IntPtr.Zero);
        EmptyClipboard();
        IntPtr hGlobal = Marshal.StringToHGlobalUni(discordTag);
        IntPtr result = SetClipboardData(13, hGlobal); // 13 = CF_UNICODETEXT
        CloseClipboard();
        if (result == IntPtr.Zero)
        {
            // Clipboard rejected the data — you must free the memory
            Marshal.FreeHGlobal(hGlobal);
        }
        Console.Clear();
        EnsureConsoleSize(discordTag);
        WriteCentered(discordTag);
        if (restartRequested)
            PlayEmbeddedWav();
    }
    static void EnsureConsoleSize(string text, int marginWidth = 4, int marginHeight = 2)
    {
        int longestLine = text.Split('\n').Max(line => line.Length);
        int lineCount = text.Split('\n').Length;

        int requiredWidth = longestLine + marginWidth;
        int requiredHeight = lineCount + marginHeight;

        if (requiredWidth > Console.WindowWidth)
        {
            Console.BufferWidth = Math.Max(Console.BufferWidth, requiredWidth);
            Console.WindowWidth = Math.Min(requiredWidth, Console.LargestWindowWidth);
        }

        if (requiredHeight > Console.WindowHeight)
        {
            Console.BufferHeight = Math.Max(Console.BufferHeight, requiredHeight);
            Console.WindowHeight = Math.Min(requiredHeight, Console.LargestWindowHeight);
        }
    }
    static void WriteCentered(string text)
    {
        string[] lines = text.Split('\n');

        foreach (var line in lines)
        {
            int left = Math.Max((Console.WindowWidth - line.Length) / 2, 0);
            Console.SetCursorPosition(left, Console.CursorTop);
            Console.WriteLine(line);
        }
    }
    static public class UnitConversion
    {
        static Dictionary<string, double?>? CachedRates = [];
        static DateTime? CachedAt = null;
        static async Task<Dictionary<string, double?>> FetchLiveRates()
        {
            using var client = new HttpClient();

            var payload = new
            {
                table = "DNVALD",
                format = "JSON",
                valuePresentation = "CodeAndValue",
                variables = new[]
                {
                    new { code = "Tid", values = new[] { "*" } },
                    new { code = "VALUTA", values = new[] { "*" } },
                    new { code = "KURTYP", values = new[] { "KBH" } }
                }
            };

            var responseMessage = await client.PostAsJsonAsync(
                "https://api.statbank.dk/v1/data",
                payload
            );

            string json = await responseMessage.Content.ReadAsStringAsync();

            var response = JsonSerializer.Deserialize<RateResponse>(json);

            if (response?.data == null || response.data.Count == 0)
            {
                Console.WriteLine("StatBank returned no data — using cached/default rates.");
                return new Dictionary<string, double?>();
            }

            foreach (var entry in response.data)
            {
                Console.WriteLine($"API: {entry.VALUTA} = {entry.DNE}");
            }

            return response.data.ToDictionary(
            x => x.VALUTA.ToLowerInvariant(),
            x => x.DNE
            );
        }
        static async Task<Dictionary<string, double?>?> GetRates()
        {
            if (CachedRates != null && CachedAt != null &&
                (DateTime.Now - CachedAt.Value).TotalHours < 12)
                return CachedRates;

            await InitializeRates(); // uses full fallback logic
            return CachedRates;
        }
        static Dictionary<string, double> DefaultRates() => new()
        {
            { "usd", 6.85 },
            { "eur", 7.45 },
            { "gbp", 8.65 },
            { "dkk", 1.0 },
            { "cad", 5.0 },
            { "jpy", 0.05 },
            { "aud", 4.5 }
        };
        public static async Task InitializeRates()
        {
            const string file = "rates.json";

            // 1. Load existing or fallback
            if (File.Exists(file))
            {
                try
                {
                    var config = AppConfig.Load(file) ?? new AppConfig();
                    CachedRates = config.Rates ?? DefaultRates().ToDictionary(k => k.Key, v => (double?)v.Value);
                    CachedAt = config.Timestamp;
                }
                catch
                {
                    Console.WriteLine("⚠️ Failed to load rates.json — using defaults.");
                    CachedRates = DefaultRates().ToDictionary(k => k.Key, v => (double?)v.Value);
                    CachedAt = DateTime.Now;
                }
            }
            else
            {
                CachedRates = DefaultRates().ToDictionary(k => k.Key, v => (double?)v.Value);
                CachedAt = DateTime.Now;
                AppConfig.Save(file, new AppConfig
                {
                    Timestamp = CachedAt.Value,
                    Rates = CachedRates
                });
            }

            // 2. Try to fetch live rates
            try
            {
                var live = await FetchLiveRates();

                if (live != null && live.Count > 0)
                {
                    foreach (var key in CachedRates.Keys)
                    {
                        if (live.TryGetValue(key, out double? newRate) && newRate.HasValue && newRate > 0)
                            CachedRates[key] = newRate;
                    }

                    CachedAt = DateTime.Now;

                    AppConfig.Save(file, new AppConfig
                    {
                        Timestamp = CachedAt.Value,
                        Rates = CachedRates
                    });
                }
                else
                {
                    Console.WriteLine("⚠️ Live rates were empty — keeping cached/default rates.");
                }

                foreach (var key in CachedRates.Keys)
                {
                    if (!live.ContainsKey(key))
                        Console.WriteLine($"Live data missing key: {key}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Live fetch failed: {ex.Message}");
            }
        }
        static string FormatImperialWeight(double kg)
        {
            double totalOunces = kg / 0.0283495231;

            int pounds = (int)(totalOunces / 16);
            totalOunces -= pounds * 16;

            double ounces = totalOunces;

            var parts = new List<string>();
            if (pounds > 0) parts.Add($"{pounds}lb");
            if (ounces > 0.01) parts.Add($"{ounces:0.##}oz");

            return parts.Count > 0 ? string.Join(" ", parts) : "0";
        }
        static string FormatMetricBreakdown(double kg)
        {
            int wholeKg = (int)kg;
            double grams = (kg - wholeKg) * 1000;

            int wholeGrams = (int)Math.Round(grams);

            // Handle rounding up to 1000g
            if (wholeGrams == 1000)
            {
                wholeKg++;
                wholeGrams = 0;
            }

            var parts = new List<string>();
            if (wholeKg > 0) parts.Add($"{wholeKg}kg");
            if (wholeGrams > 0) parts.Add($"{wholeGrams}g");

            return parts.Count > 0 ? string.Join(" ", parts) : "0";
        }
        static string FormatImperialBreakdown(double meters)
        {
            long totalMilliInches = (long)Math.Round(meters * 39370.07874015748);

            var parts = new List<string>();

            long leagues = totalMilliInches / 190080000;
            totalMilliInches %= 190080000;

            long miles = totalMilliInches / 63360000;
            totalMilliInches %= 63360000;

            long furlongs = totalMilliInches / 7920000;
            totalMilliInches %= 7920000;

            long chains = totalMilliInches / 792000;
            totalMilliInches %= 792000;

            long rods = totalMilliInches / 198000;   // 1 rod = 198,000 milli‑inches
            totalMilliInches %= 198000;

            long yards = totalMilliInches / 36000;
            totalMilliInches %= 36000;

            long feet = totalMilliInches / 12000;
            totalMilliInches %= 12000;

            long links = totalMilliInches / 7920;    // 1 link = 7,920 milli‑inches
            totalMilliInches %= 7920;

            long inchesWhole = totalMilliInches / 1000;
            totalMilliInches %= 1000;

            long thou = totalMilliInches;

            // Build output
            if (leagues > 0) parts.Add($"{leagues}lea");
            if (miles > 0) parts.Add($"{miles}mi");
            if (furlongs > 0) parts.Add($"{furlongs}fur");
            if (chains > 0) parts.Add($"{chains}ch");
            if (rods > 0) parts.Add($"{rods}rd");
            if (yards > 0) parts.Add($"{yards}yd");
            if (feet > 0) parts.Add($"{feet}ft");
            if (links > 0) parts.Add($"{links}li");
            if (inchesWhole > 0) parts.Add($"{inchesWhole}in");
            if (thou > 0) parts.Add($"{thou}thou");

            if (parts.Count == 0)
                return "0";

            return string.Join(" ", parts);
        }

        static string ConvertSpeed(double value, string unit)
        {
            // 1. Convert input to base unit: meters per second (m/s)
            double mps = unit switch
            {
                "mps" => value,
                "kph" => value / 3.6,
                "mph" => value * 0.44704,
                "knot" => value * 0.514444,
                _ => throw new Exception($"Unknown speed unit '{unit}'")
            };

            // 2. Convert from base to all supported units
            double out_mps = mps;
            double out_kph = mps * 3.6;
            double out_mph = mps / 0.44704;
            double out_knot = mps / 0.514444;

            // 3. Format output (same style as your other converters)
            return                
                $"{out_mps:0.##} m/s == " +
                $"{out_kph:0.#} km/h == " +
                $"{out_mph:0.#} mph == " +
                $"{out_knot:0.#} knots";
        }
        static string ConvertLength(double value, string unit)
        {
            int sttMeters = RandomStoneTossMeters();
            int spitMeters = RandomSpitMeters();

            double meters = unit switch
            {
                "m" => value,
                "cm" => value * .01,
                "mm" => value * .001,
                "fur" => value * 201.168,
                "ft" => value * 0.3048,
                "in" => value * 0.0254,
                "yd" => value * 0.9144,
                "mi" => value * 1609.344,
                "nm" => value * 1852,
                "km" => value * 1000,
                "stt" => value * sttMeters,
                "spit" => value * spitMeters,
                "chain" => value * 20.1168,
                "league" => value * 4828.032,
                "thou" => value * 0.0000254,
                _ => throw new Exception("Unknown length unit")
            };

            string metridBreakdown = FormatMetricBreakdownLength(meters);

            // Standalone conversions (not part of the imperial breakdown)
            double nautMil = meters / 1852;
            double stoneTosses = meters / sttMeters;
            double spits = meters / spitMeters;

            string imperialBreakdown = FormatImperialBreakdown(meters);

            return
                $"{metridBreakdown} == " +
                $"{imperialBreakdown} == " +
                $"{nautMil:0.######}naut. mile{(Math.Abs(nautMil - 1.0) < 0.0001 ? "" : "s")} ~ " +
                $"{stoneTosses:0.##}stone-toss ~ " +
                $"{spits:0.##}spit{(Math.Abs(spits - 1.0) < 0.0001 ? "" : "s")}";
        }

        private static string FormatMetricBreakdownLength(double meters)
        {
            long totalMillimeters = (long)Math.Round(meters * 1000);

            var parts = new List<string>();

            long km = totalMillimeters / 1_000_000;
            totalMillimeters %= 1_000_000;

            long m = totalMillimeters / 1000;
            totalMillimeters %= 1000;

            long cm = totalMillimeters / 10;
            totalMillimeters %= 10;

            long mm = totalMillimeters;

            // Build output
            if (km > 0) parts.Add($"{km}km");
            if (m > 0) parts.Add($"{m}m");
            if (cm > 0) parts.Add($"{cm}cm");
            if (mm > 0) parts.Add($"{mm}mm");

            if (parts.Count == 0)
                return "0";

            return string.Join(" ", parts);
        }
        static string ConvertTemperature(double value, string unit)
        {
            double c = unit switch
            {
                "c" => value,
                "f" => FahrenheitToCelsius(value),
                "r" => RankineToCelsius(value),
                "k" => KelvinToCelsius(value),
                _ => throw new Exception("Unknown temperature unit")
            };

            double f = CelsiusToFahrenheit(c);
            double k = CelsiusToKelvin(c);
            double r = CelsiusToRankine(c);

            return $"{c:0.#}°C == {f:0.#}°F == {k:0.#}K == {r:0.#}R";
        }
        static string ConvertWeight(double value, string unit)
        {
            // Convert everything to kilograms
            double kg = unit switch
            {
                "kg" => value,
                "g" => value / 1000,
                "lb" => value * 0.45359237,
                "oz" => value * 0.0283495231,
                "st" => value * 6.35029318,
                "gr" => value * 0.00006479891,
                "slug" => value * 14.5938999994935,
                _ => throw new Exception("Unknown weight unit")
            };

            // normalise 1700g to 1kg 700g and lb and oz etc?
            var grains = (kg / 0.00006479891);
            var imperial = FormatImperialWeight(kg);
            var kgs = FormatMetricBreakdown(kg);
            string grainsString = grains >= 1 ? ((int)grains).ToString() + "gr" : grains > 0 ? "<1" + "gr" : "";
            
            return
                $"{kgs} = " +
                $"{imperial} = " +
                $"{grainsString} = " +
                $"{kg / 14.5938999994935:0.####}slug";
        }
        static double? ParseCompoundLength(string input)
        {
            input = input.ToLowerInvariant();

            // Replace shorthand like 6'4" with 6 ft 4 in
            input = Regex.Replace(input, @"(\d+)'", "$1 ft ");
            input = Regex.Replace(input, @"(\d+)""", "$1 in ");

            string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // If there's only one part, it's NOT a compound
            if (parts.Length <= 1)
                return null;

            double totalMeters = 0;
            bool parsedAny = false;

            foreach (var part in parts)
            {
                var match = Regex.Match(part, @"^([0-9]*\.?[0-9]+)([a-zA-Z""']+)$");
                if (!match.Success)
                    return null;

                double value = double.Parse(match.Groups[1].Value);
                string rawUnit = match.Groups[2].Value;
                string? unit = NormalizeUnit(rawUnit);

                if (unit == null)
                    return null;

                double meters = unit switch
                {
                    "m" => value,
                    "cm" => value / 100,
                    "mm" => value / 1000,
                    "km" => value * 1000,
                    "ft" => value * 0.3048,
                    "in" => value * 0.0254,
                    "yd" => value * 0.9144,
                    "mi" => value * 1609.344,
                    "nm" => value * 1852,
                    "league" => value * 4828.032,

                    _ => throw new Exception("Unknown length unit")
                };

                totalMeters += meters;
                parsedAny = true;
            }
            return parsedAny ? totalMeters : null;
        }
        static (double value, string unit)? ParseSingleUnit(string input)
        {
            var match = Regex.Match(input, @"^\s*([+-]?[0-9]*\.?[0-9]+)\s*([a-zA-Z""']+)\s*$", RegexOptions.IgnoreCase);
            if (!match.Success)
                return null;

            double value = double.Parse(match.Groups[1].Value);
            string rawUnit = match.Groups[2].Value;
            string? unit = NormalizeUnit(rawUnit);

            if (unit == null)
                return null;

            return (value, unit);
        }        
        static readonly Dictionary<string, string> CurrencyCultures = new()
        {
            { "usd", "en-US" }, // $1,234.56
            { "eur", "fr-FR" }, // 1 234,56 €
            { "gbp", "en-GB" }, // £1,234.56
            { "dkk", "da-DK" }, // 1.234,56 kr.
            { "cad", "en-CA" }, // $1,234.56 (Canadian)
            { "jpy", "ja-JP" }, // ￥1,234
            { "aud", "en-AU" }  // A$1,234.56
        };
        static readonly Dictionary<string, string> CurrencySymbols = new()
        {
            { "usd", "$" },
            { "cad", "C$" },
            { "aud", "A$" },
            { "eur", "€" },
            { "gbp", "£" },
            { "dkk", "kr." },
            { "jpy", "¥" }
        };
        static string FormatCurrency(double value, string unit)
        {
            if (!CurrencyCultures.TryGetValue(unit, out string cultureName))
                return $"{value:0.##} {unit}";

            var culture = CultureInfo.GetCultureInfo(cultureName);

            // Use culture’s decimal rules
            var format = "C" + culture.NumberFormat.CurrencyDecimalDigits;

            string formatted = value.ToString(format, culture);

            // Override symbol if needed
            if (CurrencySymbols.TryGetValue(unit, out string symbol))
            {
                var nf = culture.NumberFormat;
                formatted = formatted.Replace(nf.CurrencySymbol, symbol);
            }

            return formatted;
        }
        static async Task<string> ConvertCurrency(double value, string unit)
        {
            var rates = await GetRates();

            // If the input currency has no rate, bail out
            if (!rates.TryGetValue(unit, out double? unitRate) || unitRate is null)
                return $"No exchange rate available for '{unit}'.";

            // Normalize to DKK
            double dkk = value * unitRate.Value;

            var results = new List<string>();

            foreach (var kvp in rates)
            {
                string target = kvp.Key;
                double? targetRate = kvp.Value;

                if (targetRate is null)
                {
                    results.Add($"{target.ToUpper()}: N/A");
                    continue;
                }

                double converted = target == "dkk"
                    ? dkk
                    : dkk / targetRate.Value;

                results.Add(FormatCurrency(converted, target));
            }

            return string.Join(" == ", results);
        }
        static public async Task<string> ConvertThis(string convertThis)
        {
            if (string.IsNullOrWhiteSpace(convertThis))
                return "No input provided.";

            string lowered = convertThis.Trim().ToLowerInvariant();
            string? pain = NormalizePainkiller(lowered);

            if (pain != null)
                return FormatPainkillerInfo(pain);

            string[] parts = convertThis.Split(',', StringSplitOptions.RemoveEmptyEntries);
            List<string> results = new();
            var rates = await GetRates(); // load once

            foreach (var raw in parts)
            {
                string b = raw.Trim();

                double? compoundMeters = ParseCompoundLength(b);

                if (compoundMeters != null)
                {
                    results.Add(ConvertLength(compoundMeters.Value, "m"));
                    continue;
                }

                var parsed = ParseSingleUnit(b);
                if (parsed == null)
                {
                    results.Add($"Invalid format in '{b}'");
                    continue;
                }

                double value = parsed.Value.value;
                string unit = parsed.Value.unit;

                // 3. Route by category
                switch (unit)
                {
                    // currency
                    case "usd":
                    case "eur":
                    case "gbp":
                    case "dkk":
                    case "cad":
                    case "jpy":
                    case "aud":
                        results.Add(await ConvertCurrency(value, unit));
                        break;

                    // Length
                    case "m":
                    case "ft":
                    case "in":
                    case "yd":
                    case "mi":
                    case "nm":
                    case "cm":
                    case "mm":
                    case "km":
                    case "stt":
                    case "spit":
                    case "fur":
                    case "chain":
                    case "league":
                    case "rod":
                    case "link":
                    case "thou":
                        results.Add(ConvertLength(value, unit));
                        break;
                    
                    // Speed
                    case "knot":
                    case "mph":
                    case "kph":
                    case "mps":
                        results.Add(ConvertSpeed(value, unit));
                        break;

                    // Weight
                    case "kg":
                    case "g":
                    case "lb":
                    case "oz":
                    case "st":
                    case "gr":
                    case "slug":
                        results.Add(ConvertWeight(value, unit));
                        break;

                    // Temperature
                    case "c":
                    case "f":
                    case "k":
                    case "r":
                        results.Add(ConvertTemperature(value, unit));
                        break;

                    default:
                        results.Add($"Unit '{unit}' is recognized but not implemented yet.");
                        break;
                }
            }

            return string.Join(Environment.NewLine, results);
        }
        static string FormatPainkillerInfo(string key)
        {
            return key switch
            {
                "paracetamol" =>
        @"
🇺🇸 USA → Tylenol
🇬🇧 UK → Panadol
🇩🇰 Denmark → Pamol / Panodil
🇩🇪 Germany → Paracetamol-ratiopharm
🇦🇺 Australia → Panadol
🇮🇳 India → Crocin
🇯🇵 Japan → Calonal
",

                "ibuprofen" =>
        @"
🇺🇸 USA → Advil / Motrin
🇬🇧 UK → Nurofen
🇩🇰 Denmark → Ipren
🇩🇪 Germany → Ibuprofen-ratiopharm
🇦🇺 Australia → Nurofen
🇯🇵 Japan → Brufen
",

                _ => "Unknown painkiller category."
            };
        }
        // --- Temperature conversions ---
        static double CelsiusToFahrenheit(double c) => (c * 9 / 5) + 32;
        static double FahrenheitToCelsius(double f) => (f - 32) * 5 / 9;
        static double RankineToCelsius(double r) => (r - 491.67) * 5 / 9;
        static double CelsiusToKelvin(double c) => c + 273.15;
        static double KelvinToCelsius(double k) => k - 273.15;
        static double CelsiusToRankine(double c) => (c + 273.15) * 9 / 5;
        // --- Length conversions (meters as reference) ---
        static int RandomStoneTossMeters() => Random.Shared.Next(30, 51); // upper bound exclusive
        static int RandomSpitMeters() => Random.Shared.Next(2, 5);
    }
    static DateTime GetShutdownDateTime(string? inputDateTime = null)
    {
        if (string.IsNullOrEmpty(inputDateTime))
        {
            return DateTime.Now.AddMilliseconds(2);
        }
        const string msg = "{0}nter time (H:mm:ss): ", msg2 = "Invalid time format. Please e";

        string inputTime;
        while (true)
        {
        RetryHere:
            inputTime = RegexLibrary.DateTimeSanitizer().Replace(inputDateTime?.Trim() ?? "", ":");

            Console.Clear();
            if (string.IsNullOrEmpty(inputTime))
            {
                Console.Write($"{string.Format(msg, "E")}");
                inputDateTime = Console.ReadLine();
                goto RetryHere;
            }

            if (TimeSpan.TryParse(inputTime, out _))
            {
                break;
            }
            else
            {
                Console.Write($"{string.Format(msg, msg2)}");
                inputDateTime = Console.ReadLine();
                goto RetryHere;
            }
        }
        return AdvanceOneDay(inputTime);
    }
    static DateTime AdvanceOneDay(string inputTime)
    {
        if (!TimeSpan.TryParse(inputTime, out TimeSpan alarmTime))
        {
            Console.Write("AdvanceOneDayError"); Console.ReadKey(); return DateTime.Now.AddMilliseconds(2000);
        }
        DateTime now = DateTime.Now, nextAlarm = now.Date + alarmTime;

        while (nextAlarm < now)
        {
            nextAlarm = nextAlarm.AddDays(1);
        }
        return nextAlarm;
    }
    static double ParseTimePart(string part)
    {
        double totalSeconds = 0.0;
        string numericPart = new string(part.Where(c => char.IsDigit(c) || c == '.').ToArray());
        char unit = part.LastOrDefault(c => c == 's' || c == 'h' || c == 'm' || c == 'H' || c == 'M' || c == 'S' || c == 'd' || c == 'D' || c == 'w' || c == 'W'
        || c == 'y' || c == 'Y' || c == 'e' || c == 'E' || c == 'c' || c == 'C' || c == 'a' || c == 'A');
        if (unit == default(char))
        {
            // ripbozo
            return double.TryParse(numericPart, out double q) ? q : 0;
        }
        if (double.TryParse(numericPart, out double quantity))
        {
            switch (unit)
            {
                case 'H':
                case 'h':
                    totalSeconds += quantity * 3600; // 1 hour
                    break;
                case 'M':
                case 'm':
                    totalSeconds += quantity * 60; // 1 minute
                    break;
                case 'S':
                case 's':
                    totalSeconds += quantity;
                    break;
                case 'd':
                case 'D':
                    totalSeconds += quantity * 86400; // one day (24 hour)
                    break;
                case 'w':
                case 'W':
                    totalSeconds += quantity * 604800; // 1 week
                    break;
                case 'y':
                case 'Y':
                    totalSeconds += quantity * 31557600; // 1 year
                    break;
                case 'c':
                case 'C':
                    totalSeconds += quantity * 3155760000; // 100 years
                    break;
                case 'a': 
                case 'A': 
                case 'e':
                case 'E':
                    totalSeconds += quantity * 31557600000000000; // 1 BILLION years
                    break;

            }
        }
        return totalSeconds;
    }
    static TimeSpan GetShutdownTimer(string inputTime)
    {
        if (debugSet)
            Console.WriteLine($"DEBUG GetShutdownTimer: inputTime = '{inputTime}'");
        var fractions = new Dictionary<string, string>
            {
                { "¼", ".25" }, { "⅓", ".3333" }, { "½", ".5" },
                { "⅔", ".6667" }, { "¾", ".75" }
            };
        inputTime = Regex.Replace(inputTime, "[¼⅓½⅔¾]", m => fractions[m.Value]);
        inputTime = RegexLibrary.CountDownTimerSanitizer().Replace(inputTime, " ");
        inputTime = inputTime.Replace(',', '.').Trim();
        inputTime = Regex.Replace(inputTime, @"\.{2,}", "."); 

        double totalSeconds = 0;
        string[] parts = inputTime.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            if (debugSet)
                Console.WriteLine($"DEBUG: About to call ParseTimePart with '{part}'");
            double result = ParseTimePart(part);
            if (debugSet)
                Console.WriteLine($"DEBUG: ParseTimePart returned {result}");
            totalSeconds += result;
        }
        if (debugSet)
        {
            Console.WriteLine($"DEBUG: After sanitize = '{inputTime}'");
            Console.WriteLine($"DEBUG: parts.Length = {parts.Length}");
            Console.WriteLine($"DEBUG: Final totalSeconds = {totalSeconds}");
        }
        if (totalSeconds > MAX_TIMESPAN_SECONDS)
        {
            TimeSpanOverflow = true;
            OverflowSeconds = totalSeconds;
            return TimeSpan.Zero;
        }
        return TimeSpan.FromSeconds(totalSeconds);
    }
    public static partial class RegexLibrary
    {
        [GeneratedRegex(@"[^a-zA-Z0-9.]+")]
        public static partial Regex CountDownTimerSanitizer();

        [GeneratedRegex(@"[,\.\-;:_\s]+")]
        public static partial Regex DateTimeSanitizer();
    }
    [DllImport("user32.dll")]
    static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")]
    static extern bool CloseClipboard();
    [DllImport("user32.dll")]
    static extern bool EmptyClipboard();
    [DllImport("user32.dll")]
    static extern IntPtr SetClipboardData(uint uFormat, IntPtr data);
}
