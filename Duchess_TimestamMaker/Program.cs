using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text.RegularExpressions;
partial class Program
{
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
    EGG_TIMER = "Set timelapse (e.g. '2h')",
        DATETIME = "Set the time for timestamp",
        VERSION = "1.10.3",
        PATCHNOTES = "added sound file\ncan now Ctrl-C after setting a timestamp to increment by 24h per Ctrl-C\nTweaked conversion feature\nAdded feet/m/yard/miles\nmore errors.\nFixed errorFixed egg-timer mode.\nAdded stronger egg-timer features\nFixed formatting issues.\nMinor tweak\nSuper cool feature!";
    #endregion
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
    static void KeyPressHandler(object sender, ConsoleCancelEventArgs e)
    {
        if (e.SpecialKey == ConsoleSpecialKey.ControlC)
        {
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
    }
    [STAThread]
    static async Task Main()
    {
        targetDatetime = UTCDateTime; // initialize once
        Console.CancelKeyPress += KeyPressHandler;
        Console.WindowWidth = Math.Min(80, Console.LargestWindowWidth);
        Console.BufferWidth = Math.Max(Console.WindowWidth, 100);

        Console.WindowHeight = Math.Min(25, Console.LargestWindowHeight);
        Console.BufferHeight = Math.Max(Console.WindowHeight, 200);

        string discordTag = "";
        TimeSpan addThis = TimeSpan.Zero;
        Console.WriteLine($"{VERSION}".PadLeft(Console.WindowWidth / 2));
        
        // ConsoleUI.SelectionArray(options);
        var useEggtimer = AskYesNo("Use as Egg-timer?", 1);

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
            Console.Clear();
            //useEggtimer = AskYesNo("Use as egg-timer?", 1);
            Console.Clear();
            Console.WriteLine(useEggtimer ? EGG_TIMER : DATETIME);
            string userInput = Console.ReadLine() ?? "";            
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
    public static DateTime GetShutdownDateTime(string? inputDateTime = null)
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
            inputTime = Regex.Replace(inputDateTime, @"[,\.\-;:_\s]+", ":");

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
        inputTime = Regex.Replace(inputTime, @"[^a-zA-Z0-9.]+", " ");
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
    [DllImport("user32.dll")]
    static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")]
    static extern bool CloseClipboard();
    [DllImport("user32.dll")]
    static extern bool EmptyClipboard();
    [DllImport("user32.dll")]
    static extern IntPtr SetClipboardData(uint uFormat, IntPtr data);
}