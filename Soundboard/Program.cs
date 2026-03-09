using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Media;

namespace Soundboard
{
    internal class Program
    {
        static readonly List<string> soundList =
            [
                
            ];
        static readonly List<string> sounds;
        static readonly SoundPlayer player;
        static Program()
        {
            player = new();
            EnsureSoundFolder();
            sounds = LoadSounds();
        }
        static void Main()
        {
            SoundboardMenu(sounds);
        }
        static void SoundboardMenu(List<string> sounds)
        {
            const int columns = 3;
            int rows = (int)Math.Ceiling(sounds.Count / (double)columns);

            int row = 0;
            int col = 0;

            while (true)
            {
                Console.Clear();

                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < columns; c++)
                    {
                        int index = r * columns + c;

                        string label = index < sounds.Count
                            ? Path.GetFileName(sounds[index]).PadRight(12)
                            : "".PadRight(12);

                        if (r == row && c == col)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.Write($"> {label}");
                            Console.ResetColor();
                        }
                        else
                        {
                            Console.Write($"  {label}");
                        }
                    }
                    Console.WriteLine();
                }

                var key = Console.ReadKey(true).Key;

                switch (key)
                {
                    case ConsoleKey.LeftArrow:
                        col = (col - 1 + columns) % columns;
                        break;

                    case ConsoleKey.RightArrow:
                        col = (col + 1) % columns;
                        break;

                    case ConsoleKey.UpArrow:
                        row = (row - 1 + rows) % rows;
                        break;

                    case ConsoleKey.DownArrow:
                        row = (row + 1) % rows;
                        break;

                    case ConsoleKey.Enter:
                        int index = row * columns + col;
                        if (index < sounds.Count)
                            PlaySound(sounds[index]);
                        break;

                    case ConsoleKey.Escape:
                        return;
                }
            }
        }
        static List<string> LoadSounds()
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "sounds");
            return [.. Directory.GetFiles(folder, "*.wav")];
        }
        static void EnsureSoundFolder()
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "sounds");
            Directory.CreateDirectory(folder);

            ExtractIfMissing("error.wav", folder);
            ExtractIfMissing("success.wav", folder);
            ExtractIfMissing("alert.wav", folder);
        }
        static void ExtractIfMissing(string fileName, string folder)
        {
            string path = Path.Combine(folder, fileName);
            if (File.Exists(path))
                return;

            using Stream? resource = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream($"Soundboard.{fileName}");

            if (resource != null)
            {
                using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
                resource.CopyTo(fs);
            }
        }
        static void PlaySound(string filePath)
        {
            if (!File.Exists(filePath))
                return;

            player.SoundLocation = filePath;
            player.Load();
            player.PlaySync();
        }
    }
}
