using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using NAudio.Wave;
using System.Data;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ShutdownTimer
{
    public static partial class RegexLibrary
    {
        [GeneratedRegex(@"[^a-zA-Z0-9.]+")]
        public static partial Regex CountDownTimerSanitizer();

        [GeneratedRegex(@"[,\.\-;:_\s]+")]
        public static partial Regex DateTimeSanitizer();
    }
    partial class Program
    {
        #region Fields
        const bool debugging = false;
        static readonly
             Stopwatch
             DelaySW = new(),
             StopwatchUntilAction = new(),
             MaybeKillNow = new();

        const string
             VersionNumber = "1.2.1",
             MainMessage = "\nPress Ctrl-C to {0}\nPress Ctrl-Brk to end\n\n",
             SetupMessage = "\rSet shutdown {0}: ",
             AutoKillMsg = "to shutdown automatically,\ndo nothing for twenty seconds\n",
             ShutdownS = "s",
             PausedString = "PAUSED",
             ActiveString = "ACTIVE",
             ContinueString = "CONTINUE",
             StallString = "PAUSE",
             ProcessKillOnlyMsg = " Non-essential\n processes",
             ShutdownMsg = "Shutdown",
             TimerMessage = "\r{1} timer: {0}";

        static bool
            NonEssentialProcessKillOnly = false,
            SetTimeFormatDate = false;

        static TimeSpan durationUntilShutdownTimeSpan = TimeSpan.Zero;

        static DateTime setDateTimeObject = DateTime.Now;

        static readonly StringBuilder stringBuilder = new();
        
        static readonly bool DisableUACQuestion = config?.DisableUACFeature ?? true;
        #endregion
        static void KeyPressHandler(object sender, ConsoleCancelEventArgs e)
        {
            // Check which key was pressed
            if (e.SpecialKey == ConsoleSpecialKey.ControlC)
            {
                // Toggle the Paused flag
                if (StopwatchUntilAction.IsRunning)
                {
                    StopwatchUntilAction.Stop();
                    DelaySW.Restart();
                    DelaySW.Start();
                    UpdateTaskScheduler(null, null, true, false);
                }
                else
                {
                    DelaySW.Stop();
                    UpdateTaskScheduler(null, DateTime.Now.AddSeconds(DelaySW.Elapsed.TotalSeconds), true, true);
                    StopwatchUntilAction.Start();
                }
                ClearConsoleAndDisplayInfo(stringBuilder).Wait();
            }
            else if (e.SpecialKey == ConsoleSpecialKey.ControlBreak)
            {
                Process.Start("shutdown.exe", "-a").WaitForExit(500);
                System.Threading.Tasks.Task.Delay(500);
                UpdateTaskScheduler(false, DateTime.Now - TimeSpan.FromDays(1), false);
                Environment.Exit(0);
            }
            // Cancel the default behavior of terminating the program
            e.Cancel = true;
        }
        static void PauseTask(Microsoft.Win32.TaskScheduler.Task task)
        {
            if (task != null)
            {
                foreach (Trigger trigger in task.Definition.Triggers)
                {
                    if (trigger is TimeTrigger)
                    {
                        trigger.Enabled = false;
                        task.Enabled = false;
                        break;
                    }
                }
                task.TaskService.RootFolder.RegisterTaskDefinition(GetNextScheduledTask(), task.Definition);
            }
        }
        static void ResumeTask(Microsoft.Win32.TaskScheduler.Task task)
        {
            if (task != null)
            {
                foreach (Trigger trigger in task.Definition.Triggers)
                {
                    if (trigger is TimeTrigger timeTrigger)
                    {
                        trigger.Enabled = true;
                        timeTrigger.StartBoundary = timeTrigger.StartBoundary.AddSeconds(DelaySW.Elapsed.TotalSeconds);  // Example: set to 10 minutes from now.
                        task.Enabled = true;
                        break;
                    }
                }
                task.TaskService.RootFolder.RegisterTaskDefinition(GetNextScheduledTask(), task.Definition);
            }
        }
        static string GetNextScheduledTask()
        {
            using TaskService ts = new();

            var browserTask = ts.GetTask(@"\SHUTDOWN");
            var shutdownTask = ts.GetTask(@"\SHUTDOWN_PC");

            DateTime browserTime = browserTask?.NextRunTime ?? DateTime.MaxValue;
            DateTime shutdownTime = shutdownTask?.NextRunTime ?? DateTime.MaxValue;

            return browserTime <= shutdownTime ? @"\SHUTDOWN" : @"\SHUTDOWN_PC";
        }
        static async void UpdateTaskScheduler(bool? onlyKillBrowsers, DateTime? input, bool autoRun, bool? extendDuration = null)
        {
            if (debugging) return;

            string taskToUpdate = onlyKillBrowsers == true ? @"\SHUTDOWN" : onlyKillBrowsers == false ? @"\SHUTDOWN_PC" : GetNextScheduledTask();

            using TaskService ts = new();
            var task = ts.GetTask(taskToUpdate);

            if (task == null)
            {
                Console.Clear();
                Console.WriteLine("Shit's borken lmao");
                return;
            }

            foreach (var trigger in task.Definition.Triggers)
            {
                if (trigger is TimeTrigger timeTrigger)
                {
                    // Pause/resume logic
                    if (extendDuration != null)
                    {
                        if (extendDuration == true)
                            ResumeTask(task);
                        else
                            PauseTask(task);

                        return;
                    }

                    // Normal update
                    if (!autoRun)
                    {
                        // Ensure task isn't running
                        while (task.State == TaskState.Running)
                        {
                            task.Stop();
                            await System.Threading.Tasks.Task.Delay(1000);
                        }

                        // Ensure enabled
                        timeTrigger.Enabled = true;
                        task.Enabled = true;

                        // Update trigger time
                        timeTrigger.StartBoundary = input ?? DateTime.Now.AddDays(1);
                    }

                    break;
                }
            }
            task.TaskService.RootFolder.RegisterTaskDefinition(taskToUpdate, task.Definition);
        }
        static async System.Threading.Tasks.Task EnabledUAC()
        {
            if (DisableUACQuestion)
            {
                return;
            }
            if (IsUACEnabled())
            {
                if (ConsoleUI.AskYesNo("Disable UAC?", 1))
                    Process.Start("powershell.exe", "/C REG ADD \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v EnableLUA /t REG_DWORD /d 0 /f").WaitForExit(4000);
            }
            else
            {
                Console.WriteLine("\rPress any key to\nre-enable UAC.");
                if (await WaitForAbortAsync(TimeSpan.FromSeconds(3)))
                {
                    Process.Start("powershell.exe", "/C REG ADD \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v EnableLUA /t REG_DWORD /d 1 /f");
                    await System.Threading.Tasks.Task.Delay(4000);
                    Environment.Exit(0);
                }
            }
        }
        static bool IsUACEnabled()
        {
            using RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            if (key != null)
            {
                object? value = key.GetValue("EnableLUA") ?? 0;
                if (value != null)
                {
                    return (int)value == 1;
                }
            }
            return false; // Assume UAC is disabled if we can't read the registry key
        }
        static async Task<bool> WaitForAbortAsync(TimeSpan duration)
        {
            var keyTask = System.Threading.Tasks.Task.Run(() => Console.ReadKey(true));
            if (await System.Threading.Tasks.Task.WhenAny(keyTask, System.Threading.Tasks.Task.Delay(duration)) == keyTask)
            {
                return true;
            }
            return false;
        }//*/
        private static void KillNonEssentialProcesses(AppConfig config, ref bool input)
        {
            foreach (var (processName, enabled) in config.Targets)
            {
                if (!enabled)
                    continue;

                bool successfullyTerminated = false;

                foreach (var _ in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        successfullyTerminated |= ObliterateProcess(processName);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to kill {processName}: {ex.Message}");
                    }
                }

                if (!successfullyTerminated)
                {
                    Console.WriteLine($"Failed to kill: {processName}");
                }

                // Wait for termination (max 7 seconds)
                var sw = Stopwatch.StartNew();
                while (Process.GetProcessesByName(processName).Length != 0)
                {
                    if (sw.ElapsedMilliseconds > 7000)
                    {
                        Console.WriteLine($"Warning: {processName} processes still detected after timeout.");
                        break;
                    }
                    System.Threading.Tasks.Task.Delay(900).Wait();
                }

                Console.WriteLine($"All instances of {processName} terminated.");
                input = !successfullyTerminated;
            }
        }
        static bool ObliterateProcess(string processName)
        {
            Console.Clear();

            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /IM {processName}.exe",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                proc?.WaitForExit();

                if (proc?.ExitCode == 0)
                {
                    Console.WriteLine($"Successfully force-killed: {processName}");
                    return true;
                }
                else
                {
                    Console.WriteLine($"taskkill exited with code {proc.ExitCode} for {processName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error killing {processName}: {ex.Message}");
            }
            return false;
        }
        static WaveOutEvent? _tadaaOutput;
        static async System.Threading.Tasks.Task PlayEmbeddedTadaa()
        {
           
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "shutdown.tada.wav";

            foreach (var name in assembly.GetManifestResourceNames())
            {
                Console.WriteLine(name);
            }
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Console.WriteLine("Embedded sound not found.");
                return;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;

            var reader = new WaveFileReader(ms);
            Console.WriteLine(reader.WaveFormat);
            _tadaaOutput = new WaveOutEvent();
            _tadaaOutput.Init(reader);
            _tadaaOutput.Play();
        }
        private static readonly CancellationTokenSource exitCts = new CancellationTokenSource();
        public static readonly CancellationToken exitToken = exitCts.Token;
        static async System.Threading.Tasks.Task Main(string[] parameter)
        {
            await PlayEmbeddedTadaa();
            using var mutex = new Mutex(initiallyOwned: true, name: "Global\\ShutdownTimerMutex", out bool isNew); 
            if (!isNew && parameter.Length == 0 && !exitToken.IsCancellationRequested) 
            {
                return;
            }
            try
            {
            Console.Write("\r\n" + new string(' ', Console.WindowWidth - 1));
            Console.WindowHeight = 9;
            Console.WindowWidth = 39;
            bool
                killBrowser = false,
                cancelNow = false;

            int retryAttempts = 4;

            if (parameter.Length > 0)
            {
                (killBrowser, cancelNow, retryAttempts) = await RunAutorun(parameter, retryAttempts);
            }
            Console.CancelKeyPress += new ConsoleCancelEventHandler(KeyPressHandler);           
            Console.Clear();
            MaybeKillNow.Start();
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                while (MaybeKillNow.IsRunning && !exitToken.IsCancellationRequested)
                {
                    if (MaybeKillNow.ElapsedMilliseconds > 20000)
                    {
                        Console.Clear();
                        int retry = 4;

                        while (!DisplayConfigHelper.SetScreenToSingleScreen(config) && retry > 0 && config?.AdjustScreenConfiguration == true && !exitCts.IsCancellationRequested)
                        {
                            retry--;
                            await System.Threading.Tasks.Task.Delay(1000);
                        }
                        Environment.Exit(0);
                    }
                    await System.Threading.Tasks.Task.Delay(500);
                }
            });
            Console.WriteLine($"Version: {VersionNumber}\n");
            Console.WriteLine(AutoKillMsg);
                if (!killBrowser)
                {
                    bool[] choices = ConsoleUI.SelectionArray(
                        new[] { "Set shutdown by DateTime?", "Go for non-essentials-only?" },
                        new[] { SetTimeFormatDate, killBrowser }
                    );
                    killBrowser = choices[1];
                    SetTimeFormatDate = choices[0];
                    MaybeKillNow.Stop();
                }
            Console.Clear();
            stringBuilder.Clear();
            stringBuilder.Append(string.Format(SetupMessage, SetTimeFormatDate ? "Hour set" : "Timer"));
            Console.Write(stringBuilder.ToString());
            string?
                shutdownParams = Console.ReadLine() ?? null;

            if (SetTimeFormatDate)
            {
                setDateTimeObject = GetShutdownDateTime(shutdownParams);
                durationUntilShutdownTimeSpan = setDateTimeObject - DateTime.Now;
                UpdateTaskScheduler(killBrowser, setDateTimeObject, false);
                await System.Threading.Tasks.Task.Delay(600);
            }
            else
            {
                durationUntilShutdownTimeSpan = GetShutdownTimer(shutdownParams);
                ///*
                /// Below is testing
                ///*/
                setDateTimeObject = DateTime.Now.AddSeconds(durationUntilShutdownTimeSpan.TotalSeconds);
                UpdateTaskScheduler(killBrowser, setDateTimeObject, false);
                await System.Threading.Tasks.Task.Delay(600);
            }

            if (killBrowser)
            {
                shutdownParams = null;
            }

            Console.Clear();

            await EnabledUAC();

            Console.Clear();

            StopwatchUntilAction.Start();

            await ClearConsoleAndDisplayInfo(stringBuilder);

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(durationUntilShutdownTimeSpan.TotalSeconds+12));
                Environment.Exit(0);
            });

            while (StopwatchUntilAction.Elapsed.TotalSeconds < durationUntilShutdownTimeSpan.TotalSeconds && !exitToken.IsCancellationRequested)
            {
                if (StopwatchUntilAction.IsRunning)
                {
                    await ClearConsoleAndDisplayInfo();
                    await System.Threading.Tasks.Task.Delay(1100);
                }
                else
                {
                    await ClearConsoleAndDisplayInfo(stringBuilder);
                    await System.Threading.Tasks.Task.Delay(8000);
                }
            }
            await PlayEmbeddedTadaa();

                if (killBrowser)
                {
                    Console.Clear();
                    while (killBrowser && retryAttempts > 0)
                    {
                        KillNonEssentialProcesses(config, ref killBrowser);
                        await System.Threading.Tasks.Task.Delay(250);
                        retryAttempts--;
                    }
                } }
            catch{ }
        }
        private static async Task<(bool killBrowser, bool cancelNow, int retryAttempts)> RunAutorun(string[] parameter, int retryAttempts)
        {
            bool killBrowser, cancelNow;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(12));
                Environment.Exit(0);
            });

            cancelNow = parameter.Any(x => x.Contains("cancel", StringComparison.OrdinalIgnoreCase));
            killBrowser = parameter.Any(x => x.Contains("browser", StringComparison.OrdinalIgnoreCase));

            if (killBrowser)
                cancelNow = true;

            if (cancelNow)
            {
                Console.Clear();
                await PlayEmbeddedTadaa();
                while (killBrowser && retryAttempts > 0)
                {
                    KillNonEssentialProcesses(config, ref killBrowser);
                    await System.Threading.Tasks.Task.Delay(250);
                    retryAttempts--;
                }
                Process shutDown = Process.Start("shutdown.exe", "-a"); // ~~shutdown~~ the computer
                shutDown.WaitForExit(2000);
            }
            exitCts.Cancel();
            return (killBrowser, cancelNow, retryAttempts);
        }

        public static class ConsoleUI
        {
            const string YesLabel = "[ YES ]";
            const string NoLabel = "[ NO  ]";
            const string ENABLED = "[ X ]";
            const string DISABLED = "[   ]";

            public static bool[] SelectionArray(string[] labels, bool[] values) // 
            {
                int selectedRow = 0;
                int optionStartLine = Console.CursorTop;

                while (true)
                {
                    // Draw all options
                    for (int i = 0; i < labels.Length; i++)
                    {
                        Console.SetCursorPosition(0, optionStartLine + i);

                        // Highlight the selected row
                        Console.ForegroundColor = (i == selectedRow)
                            ? ConsoleColor.Green
                            : ConsoleColor.Gray;

                        string checkbox = values[i] ? ENABLED : DISABLED;
                        Console.Write($"{checkbox} {labels[i]}".PadRight(Console.WindowWidth));
                    }

                    Console.ResetColor();

                    // Read input
                    var key = Console.ReadKey(true).Key;

                    switch (key)
                    {
                        case ConsoleKey.UpArrow:
                        case ConsoleKey.W:
                            selectedRow = Math.Max(0, selectedRow - 1);
                            break;

                        case ConsoleKey.DownArrow:
                        case ConsoleKey.S:
                            selectedRow = Math.Min(labels.Length - 1, selectedRow + 1);
                            break;

                        case ConsoleKey.Spacebar:
                        case ConsoleKey.Tab:
                            values[selectedRow] = !values[selectedRow];
                            break;

                        case ConsoleKey.Enter:
                            return values;
                    }
                    if (MaybeKillNow.IsRunning)
                    MaybeKillNow.Stop();
                }
            }
            public static bool AskYesNo(string inputText, int startYes = 0)
            {
                Console.Clear();
                Console.WriteLine(inputText);
                Console.WriteLine();

                int selected = startYes;
                int optionStartLine = Console.CursorTop;

                while (true)
                {
                    Console.SetCursorPosition(0, optionStartLine);
                    Console.ForegroundColor = selected == 0 ? ConsoleColor.Green : ConsoleColor.DarkGray;
                    Console.Write(YesLabel);
                    Console.Write("  ");
                    Console.ForegroundColor = selected == 1 ? ConsoleColor.Green : ConsoleColor.DarkGray;
                    Console.Write(NoLabel);
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
                        case ConsoleKey.Spacebar:
                            selected = 1 - selected; // Toggle
                            break;

                        case ConsoleKey.Y:
                            return true;

                        case ConsoleKey.N:
                            return false;

                        case ConsoleKey.Enter:
                            return selected == 0;

                        case ConsoleKey.Escape:
                            return false; // Escape defaults to No
                    }
                }
            }
        }

        static readonly AppConfig config = AppConfig.LoadOrCreateConfig();
        private static async System.Threading.Tasks.Task ClearConsoleAndDisplayInfo(StringBuilder? inputSB = null)
        {
            if (inputSB == null)
            {
                Console.Write($"\r{FormatTimeRemaining()}");
                return;
            }

            Console.Clear();
            stringBuilder.Clear();

            stringBuilder.Append(string.Format(TimerMessage, !StopwatchUntilAction.IsRunning ? PausedString : ActiveString, NonEssentialProcessKillOnly ? ProcessKillOnlyMsg : ShutdownMsg));
            stringBuilder.Append(string.Format(MainMessage, !StopwatchUntilAction.IsRunning ? ContinueString : StallString));
            stringBuilder.Append($"\r{FormatTimeRemaining()}");
            Console.Write(stringBuilder.ToString());
        }
        static string FormatTimeRemaining()
        {
            return $"\r{(durationUntilShutdownTimeSpan - StopwatchUntilAction.Elapsed).ToString("hh\\:mm\\:ss") + " remaining"}";
        }
        static TimeSpan GetShutdownTimer(string? inputTime = null)
        {
            inputTime = inputTime.Replace("½", ".5");
            while (inputTime.Contains(".."))
            {
                inputTime = inputTime.Replace("..", ".");
            }
            inputTime = inputTime.Replace(',', '.').Trim();

            // Sanitize the input by replacing non-letter-non-digit characters with a single whitespace
            inputTime = RegexLibrary.CountDownTimerSanitizer().Replace(inputTime, " ");

            double totalSeconds = 0;

            /*/ Split the input string by units (e.g., "20m 11m 9m 49s 2h" becomes "20m", "11m", "9m", "49s", "2h")
            string[] parts = inputTime.Split([' ', ';', ':', '_', '-',], StringSplitOptions.RemoveEmptyEntries);//*/

            string[] parts = inputTime.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                double seconds = ParseTimePart(part);

                totalSeconds += seconds;
            }
            return TimeSpan.FromSeconds(totalSeconds);
        }
        static DateTime GetShutdownDateTime(string? inputDateTime = null)
        {
            if (string.IsNullOrEmpty(inputDateTime))
            {
                return DateTime.Now.AddMilliseconds(2);
            }
            const string msg = "{0}nter time (H:mm:ss): ", msg2 = "Invalid time format. Please e";

            string inputTime;
            // Keep prompting the user for input until they enter a time in the correct format
            while (true)
            {
            RetryHere:
                inputTime = RegexLibrary.DateTimeSanitizer().Replace(inputDateTime?.Trim(), ":");

                if (string.IsNullOrEmpty(inputTime))
                {
                    Console.Write($"\r{string.Format(msg, "E")}");
                    inputDateTime = Console.ReadLine();
                    goto RetryHere;
                }

                if (TimeSpan.TryParse(inputTime, out _))
                {
                    break;
                }
                else
                {
                    Console.Write($"\r{string.Format(msg, msg2)}");
                    inputDateTime = Console.ReadLine();
                    goto RetryHere;
                }
            }
            return IncreaseShutdownDateTimeTomorrow(inputTime);
        }
        static DateTime IncreaseShutdownDateTimeTomorrow(string inputTime)
        {
            // If the input is not in the correct format, use the default time
            if (!TimeSpan.TryParse(inputTime, out TimeSpan alarmTime))
            {
                return DateTime.Now.AddMilliseconds(2000);
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
            double totalSeconds = 0;
            part = part.ToLower();

            // Remove any non-letter-non-digit characters from the input part, except 'S', 'H', and 'M'
            string numericPart = new([.. part.Where(c => char.IsDigit(c) || c == '.' || c == 'S' || c == 'H' || c == 'M')]);

            // Determine the unit of time (hours, minutes, or seconds)
            char unit = part.LastOrDefault(c => c == 'h' || c == 'm' || c == 's');
            if (unit == default(char))
            {
                // Handle error or invalid input
                return 0;
            }

            // Parse the numeric part into a double
            if (double.TryParse(numericPart, out double quantity))
            {
                // Convert the quantity into seconds based on the unit
                switch (unit)
                {
                    case 'h':
                        totalSeconds += quantity * 3600;
                        break;
                    case 'm':
                        totalSeconds += quantity * 60;
                        break;
                    case 's':
                        totalSeconds += quantity;
                        break;
                }
            }
            return totalSeconds;
        }
        class DisplayConfigHelper
        {
            [DllImport("user32.dll")]
            private static extern int SetDisplayConfig(
                uint numPathArrayElements,
                IntPtr pathArray,
                uint numModeArrayElements,
                IntPtr modeArray,
                uint flags
            );

            const uint SDC_TOPOLOGY_INTERNAL = 0x00000001;
            const uint SDC_TOPOLOGY_CLONE = 0x00000002;
            const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
            const uint SDC_TOPOLOGY_EXTERNAL = 0x00000010;
            const uint SDC_APPLY = 0x00000080;

            private static uint GetTopologyFromConfig(int screenSelect)
            {
                return screenSelect switch { 1 => SDC_TOPOLOGY_INTERNAL, 2 => SDC_TOPOLOGY_EXTERNAL, 3 => SDC_TOPOLOGY_CLONE, 4 => SDC_TOPOLOGY_EXTEND, _ => SDC_TOPOLOGY_INTERNAL };
            }
            public static bool SetScreenToSingleScreen(AppConfig config)
            {
                if (config.AdjustScreenConfiguration == false) return true;

                uint selectedTopology = GetTopologyFromConfig(config.ScreenSelect);

                int result = SetDisplayConfig(
                    0, IntPtr.Zero,
                    0, IntPtr.Zero,
                    selectedTopology | SDC_APPLY
                );

                if (result != 0)
                {
                    Console.WriteLine($"SetDisplayConfig failed with error code: {result}");
                    return false;
                }
                return true;
            }
        }      
        public class ProcessTarget
        {
            public Dictionary<string, bool> Targets { get; set; } = new();
        }
        public class AppConfig
        {
            public bool AdjustScreenConfiguration { get; set; } = false;
            public int ScreenSelect { get; set; } = 1; // default: internal screen
            public Dictionary<string, bool> Targets { get; set; } = new();
            public bool DisableUACFeature { get; set; } = true;
            public static AppConfig LoadOrCreateConfig()
            {
                const string configPath = "processes.json";

                if (!File.Exists(configPath))
                {
                    var folder = AppContext.BaseDirectory;
                    Process.Start("explorer.exe", folder);

                    var defaultConfig = new AppConfig
                    {
                        AdjustScreenConfiguration = false,
                        ScreenSelect = 1,
                        DisableUACFeature = true,
                        Targets = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["chrome"] = true,
                            ["firefox"] = true,
                            ["opera"] = true,
                            ["msedge"] = false,
                            ["discord"] = true,
                            ["brave"] = false,
                            ["steam"] = true,
                            ["riot"] = true,
                            ["vgc"] = true,
                            ["bethesda.net_launcher"] = true,
                            ["epicgameslauncher"] = true
                        }
                    };
                    var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(configPath, json);
                    return defaultConfig;
                }
                var text = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize<AppConfig>(text)
                       ?? new AppConfig();
            }
        }
    }
}
