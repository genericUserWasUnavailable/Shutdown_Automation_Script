using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Transactions;

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

        const
             string
             VersionNumber = "1.2.2",
             MainMessage = "\nPress Ctrl-C to {0}\nPress Ctrl-Brk to end\n\n",
             SetupMessage = "\rSet shutdown {0}: ",
             AutoKillMsg = "to shutdown automatically,\ndo nothing for twenty seconds\n",
             PausedString = "PAUSED",
             ActiveString = "ACTIVE",
             ContinueString = "CONTINUE",
             StallString = "PAUSE",
             ProcessKillOnlyMsg = " Non-essential\n processes",
             ShutdownMsg = "Shutdown",
             TimerMessage = "\r{1} timer: {0}";

        static
            bool
            instantKill = false,
            NonEssentialProcessKillOnly = false,
            SetTimeFormatDate = false;
        
        static
            TimeSpan
            durationUntilShutdownTimeSpan = TimeSpan.Zero;

        static
            DateTime
            setDateTimeObject = DateTime.Now;

        static readonly
            StringBuilder stringBuilder = new();

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
                    UpdateTaskScheduler(NonEssentialProcessKillOnly, null, true, false);
                }
                else
                {
                    DelaySW.Stop();
                    UpdateTaskScheduler(NonEssentialProcessKillOnly, DateTime.Now.AddSeconds(DelaySW.Elapsed.TotalSeconds), true, true);
                    StopwatchUntilAction.Start();
                }
                ClearConsoleAndDisplayInfo(stringBuilder).Wait();
            }
            else if (e.SpecialKey == ConsoleSpecialKey.ControlBreak)
            {
                Process.Start("shutdown.exe", "-a").WaitForExit(500);
                System.Threading.Tasks.Task.Delay(500).Wait();
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
                task.TaskService.RootFolder.RegisterTaskDefinition(@"\SHUTDOWN", task.Definition);
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
                        double delay = Math.Max(DelaySW.Elapsed.TotalSeconds, 1);
                        timeTrigger.StartBoundary = timeTrigger.StartBoundary.AddSeconds(delay);
                        task.Enabled = true;
                        break;
                    }
                }
                task.TaskService.RootFolder.RegisterTaskDefinition(@"\SHUTDOWN", task.Definition);
            }
        }
        static async void UpdateTaskScheduler(bool onlyKillBrowsers, DateTime? input, bool autoRun, bool? extendDuration = null)
        {
            if (debugging) return;

            NonEssentialProcessKillOnly = onlyKillBrowsers;

            using TaskService ts = new();
            Microsoft.Win32.TaskScheduler.Task task = ts.GetTask(@"\SHUTDOWN");
            if (task != null)
            {
                foreach (Trigger trigger in task.Definition.Triggers)
                {
                    if (trigger != null)
                    {
                        if (trigger is TimeTrigger timeTrigger)
                        {
                            if (!autoRun && extendDuration == null)
                            {
                                while (task.State == TaskState.Running)
                                {
                                    task.Stop();
                                    System.Threading.Tasks.Task.Delay(1000).Wait();
                                }
                                if (!timeTrigger.Enabled || !task.Enabled)
                                {
                                    timeTrigger.Enabled = true;
                                    task.Enabled = true;
                                    System.Threading.Tasks.Task.Delay(900).Wait();
                                }
                                Console.Write(input.ToString());
                                {
                                    timeTrigger.StartBoundary = input ?? DateTime.Now.AddDays(1);
                                    if (onlyKillBrowsers)
                                    {
                                        var action = task.Definition.Actions.OfType<ExecAction>().FirstOrDefault();
                                        if (action != null)
                                        {
                                            action.Arguments = "/onlyKillBrowsers";
                                        }
                                    }
                                    ts.RootFolder.RegisterTaskDefinition(@"\SHUTDOWN", task.Definition);
                                }
                            }
                            else
                                if (extendDuration != null)
                            {
                                if (extendDuration == true)
                                {
                                    ResumeTask(task);
                                }
                                else
                                {
                                    PauseTask(task);
                                }
                                return;
                            }

                            break;
                        }
                    }
                }
                return;
            }
            Console.Clear();
            Console.WriteLine("Shit's borken lmao");
        }
        static async System.Threading.Tasks.Task EnabledUAC()
        {
            if (IsUACEnabled())
            {
                Console.WriteLine("Disable UAC?\n              Y/N?");
                if (Console.ReadKey(true).Key == ConsoleKey.Y)
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
                object value = key.GetValue("EnableLUA") ?? 0;
                    return (int)value == 1;
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
        private static void KillNonEssentialProcesses(ref bool input)
        {
            string[]
            processNames =
                [
                    "chrome", "firefox", "opera", "msedge", "iexplore", "brave", "vivaldi", // browsers
                    "discord", "steam", "riotclient", "epicgameslauncher", "origin", "uplay" // clients
                ];

            foreach (string runningProcess in processNames)
            {
                bool successfullyTerminated = false;

                foreach (Process process in Process.GetProcessesByName(runningProcess))
                {
                    try
                    {
                        //process.Kill(); // leaving this for future rollback/ reminding me what I originally meant to do.
                        successfullyTerminated = ObliterateProcess(runningProcess);
                        //Console.WriteLine($"Killed browser: {runningProcess} (PID: {process.Id})"); // redundant log
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to kill {runningProcess}: {ex.Message}"); // important to note.
                    }
                }

                if (!successfullyTerminated)
                {
                    Console.WriteLine($"Failed to kill: {runningProcess}");
                }
                // Actively wait for the process to fully terminate (Max wait: 7 seconds)
                Stopwatch sw = Stopwatch.StartNew();
                sw.Start();
                while (Process.GetProcessesByName(runningProcess).Length != 0)
                {
                    if (sw.ElapsedMilliseconds > 7000) // Hard limit of 7 seconds
                    {
                        Console.WriteLine($"Warning: {runningProcess} processes still detected after timeout.");
                        break;
                    }
                    System.Threading.Tasks.Task.Delay(250).Wait(); // Check every 250ms
                }
                Console.WriteLine($"All instances of {runningProcess} terminated.");
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
        static async System.Threading.Tasks.Task Main(string[] parameter)
        {
            bool
                killBrowser = false,
                cancelNow = false;
            
            int
                retryAttempts = 4;

            if (parameter.Any(x => x?.Length > 0))
            {
                cancelNow = parameter.Any(x => x.Contains("cancel", StringComparison.OrdinalIgnoreCase));
                killBrowser = parameter.Any(x => x.Contains("browser", StringComparison.OrdinalIgnoreCase));

                if (killBrowser)
                    cancelNow = true;

                if (cancelNow)
                {
                    Console.Clear();

                    while (killBrowser && retryAttempts > 0)
                    {
                        KillNonEssentialProcesses(ref killBrowser);
                        await System.Threading.Tasks.Task.Delay(250);
                        retryAttempts--;
                    }

                    Process shutDown = Process.Start("shutdown.exe", "-a"); // ~~shutdown~~ the computer
                    shutDown.WaitForExit(2000);
                    Environment.Exit(0);
                }
                instantKill = true;
                MaybeKillNow.Start();
                await ShutDownComplete("s");

                Environment.Exit(0);
            }

            Console.CancelKeyPress += new ConsoleCancelEventHandler(KeyPressHandler);

            Console.Write("\r\n" + new string(' ', Console.WindowWidth - 1));
            Console.WindowHeight = 9;
            Console.WindowWidth = 29;
            Console.Clear();           
            MaybeKillNow.Start();
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                while (MaybeKillNow.IsRunning)
                {
                    if (MaybeKillNow.ElapsedMilliseconds > 20000)
                    {
                        Console.Clear();
                        int retry = 4;

                        while (retry * DisplayConfigHelper.SetScreenToSingleScreen() > 0)
                        {
                            retry--;
                            await System.Threading.Tasks.Task.Delay(1000);
                        }
                        /*
                        while (WrongScreenDetectedAndFixing() && retry > 0)
                        {
                            await System.Threading.Tasks.Task.Delay(1000);
                            retry--;
                        }//*/

                        await ShutDownComplete("s");
                        Environment.Exit(0);
                    }
                    await System.Threading.Tasks.Task.Delay(500);
                }
            });
            Console.WriteLine($"Version: {VersionNumber}\n");
            Console.WriteLine(AutoKillMsg);
            Console.Write("\rTo set shutdown by DateTime?\n              Y/N?");
            SetTimeFormatDate = Console.ReadKey(true).Key == ConsoleKey.Y;
            MaybeKillNow.Stop();
            Console.Clear();
            Console.Write("\rGo for non-essentials-only?\n                     Y/N?");
            killBrowser = Console.ReadKey(true).Key == ConsoleKey.Y;
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

            ClearConsoleAndDisplayInfo(stringBuilder).Wait();

            while (StopwatchUntilAction.Elapsed.TotalSeconds < durationUntilShutdownTimeSpan.TotalSeconds)
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

            if (killBrowser)
            {
                Console.Clear();

                while (killBrowser && retryAttempts > 0)
                {
                    KillNonEssentialProcesses(ref killBrowser);
                    await System.Threading.Tasks.Task.Delay(250);
                    retryAttempts--;
                }
            }

            await ShutDownComplete(DecipherConsoleReadLine(shutdownParams));
        }
        static string DecipherConsoleReadLine(string? shutdownParams = "")
        {
            return string.IsNullOrEmpty(shutdownParams) || shutdownParams.Contains("br", StringComparison.OrdinalIgnoreCase) ? "a" : shutdownParams.Contains('r', StringComparison.OrdinalIgnoreCase) ? "r" : "s";
        }       
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
            stringBuilder.Append(FormatTimeRemaining());
            Console.Write(stringBuilder.ToString());
        }
        static string FormatTimeRemaining()
        {
            return $"\r{(durationUntilShutdownTimeSpan - StopwatchUntilAction.Elapsed).ToString("hh\\:mm\\:ss") + " remaining"}";
        }
        static TimeSpan GetShutdownTimer(string? inputTime = null)
        {
            inputTime = inputTime
            .Replace(',', '.')
            .Replace("¼", ".25")
            .Replace("⅓", ".3333")
            .Replace("½", ".5")
            .Replace("⅔", ".6667")
            .Replace("¾", ".75");
            // Sanitize the input by replacing non-letter-non-digit characters with a single whitespace
            inputTime = RegexLibrary.CountDownTimerSanitizer().Replace(inputTime, " ");
            inputTime = inputTime.Replace(',', '.').Trim();
            inputTime = Regex.Replace(inputTime, @"\.{2,}", ".");

            double totalSeconds = 0;
            string[] parts = inputTime.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                totalSeconds += ParseTimePart(part);
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
        static async System.Threading.Tasks.Task ShutDownComplete(string? setShutdownParams = null)
        {
            Console.Clear();

            string arguments = setShutdownParams?.ToLower() switch
            {
                "r" => "-f -r -t 00",
                "s" => "-f -s -t 00",
                _ => "-a"
            };

            string message = setShutdownParams?.ToLower() switch
            {
                "r" => "Rebooting. See you soon",
                "s" => "Terminating.",
                _ => "Action aborted!"
            };

            Console.WriteLine(message);

            int retry = 4;
            while (retry * DisplayConfigHelper.SetScreenToSingleScreen() != 0)
            {
                retry--;
                await System.Threading.Tasks.Task.Delay(1000);
            }
           
            if (debugging)
            {
                Process.Start("shutdown.exe", "-a")?.Dispose(); // debugging
                Console.Clear();
                Console.Write("DEBUGGING - CHANGE LINE 23 to false");
            }
            else
                Process.Start("shutdown.exe", arguments)?.Dispose();

            await System.Threading.Tasks.Task.Delay(1000);
            Environment.Exit(0);
        }
        public class DisplayConfigHelper
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
            const uint SDC_APPLY = 0x00000080;

            public static int SetScreenToSingleScreen()
            {
                int result = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero,
                    SDC_TOPOLOGY_INTERNAL | SDC_APPLY);

                if (result != 0)
                {
                    Console.WriteLine($"\rSetDisplayConfig failed with error code: {result}");
                }
                return result; // 0 = success, otherwise error code
            }
        }     
    }
}
