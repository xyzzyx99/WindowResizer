using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Threading.Tasks;
using Spectre.Console;
using WindowResizer.Base;
using WindowResizer.CLI.Utils;

namespace WindowResizer.CLI.Commands
{
    internal class ResizeCommand : Command
    {
        public ResizeCommand() : base("resize", "Resize window by process/title, use -w/--window for direct placement, or -i/--interactive to choose a window.")
        {
            var configOption = new ConfigOption();
            AddOption(configOption);
            var profileOption = new ProfileOption();
            AddOption(profileOption);
            var processOption = new ProcessOption();
            AddOption(processOption);
            var titleOption = new TitleOption();
            AddOption(titleOption);
            var windowOption = new WindowOption();
            AddOption(windowOption);
            var interactiveOption = new InteractiveOption();
            AddOption(interactiveOption);
            var verboseOption = new VerboseOption();
            AddOption(verboseOption);

            this.SetHandler((InvocationContext context) =>
            {
                var config = context.ParseResult.GetValueForOption(configOption);
                var profile = context.ParseResult.GetValueForOption(profileOption);
                var process = context.ParseResult.GetValueForOption(processOption);
                var title = context.ParseResult.GetValueForOption(titleOption);
                var verbose = context.ParseResult.GetValueForOption(verboseOption);
                var interactive = context.ParseResult.GetValueForOption(interactiveOption);
                var windowOptionWasUsed = context.ParseResult.FindResultFor(windowOption) != null;
                var windowArguments = context.ParseResult.GetValueForOption(windowOption) ?? new int[0];

                void VerboseInfo(List<WindowCmd.TargetWindow> lists)
                {
                    if (verbose)
                    {
                        Verbose(lists);
                    }
                }

                bool success;
                if (interactive)
                {
                    bool canceled;
                    var selectedWindow = ChooseTargetWindow(process, title, out canceled);
                    if (selectedWindow == null)
                    {
                        context.ExitCode = canceled ? 0 : 1;
                        return Task.CompletedTask;
                    }

                    success = windowOptionWasUsed
                        ? WindowCmd.ResizeDirect(selectedWindow, windowArguments, Output.Error, VerboseInfo)
                        : WindowCmd.ResizeSelected(config?.FullName, profile, selectedWindow, Output.Error, VerboseInfo);
                }
                else
                {
                    success = windowOptionWasUsed
                        ? WindowCmd.ResizeDirect(process, title, windowArguments, Output.Error, VerboseInfo)
                        : WindowCmd.Resize(config?.FullName, profile, process, title, Output.Error, VerboseInfo);
                }

                context.ExitCode = success ? 0 : 1;
                return Task.CompletedTask;
            });
        }

        private static WindowCmd.TargetWindow ChooseTargetWindow(string process, string title, out bool canceled)
        {
            canceled = false;

            if (Console.IsInputRedirected)
            {
                Output.Error("Interactive mode requires console input.");
                return null;
            }

            var targets = WindowCmd.GetSelectableTargets(process, title, Output.Error);
            if (!targets.Any())
            {
                Output.Error(string.IsNullOrWhiteSpace(process)
                    ? "No visible windowed applications found."
                    : $"No visible windows found for process <{process}>.");
                return null;
            }

            return SelectTargetWindow(targets, out canceled);
        }

        private static WindowCmd.TargetWindow SelectTargetWindow(List<WindowCmd.TargetWindow> targets, out bool canceled)
        {
            canceled = false;

            var selectedIndex = 0;
            var offset = 0;
            const int pageSize = 15;
            var startTop = Console.CursorTop;
            var originalForeground = Console.ForegroundColor;
            var originalBackground = Console.BackgroundColor;
            var originalCursorVisible = Console.CursorVisible;
            var highlightBackground = GetDarkInvertedConsoleColor(originalBackground);
            var highlightForeground = GetReadableForeground(highlightBackground);

            try
            {
                Console.CursorVisible = false;
                while (true)
                {
                    RenderTargetWindowSelector(targets, selectedIndex, ref offset, pageSize, startTop,
                        highlightForeground, highlightBackground, originalForeground, originalBackground);

                    var key = Console.ReadKey(true);
                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow:
                            if (selectedIndex > 0)
                            {
                                selectedIndex--;
                            }
                            break;
                        case ConsoleKey.DownArrow:
                            if (selectedIndex < targets.Count - 1)
                            {
                                selectedIndex++;
                            }
                            break;
                        case ConsoleKey.PageUp:
                            selectedIndex = Math.Max(0, selectedIndex - pageSize);
                            break;
                        case ConsoleKey.PageDown:
                            selectedIndex = Math.Min(targets.Count - 1, selectedIndex + pageSize);
                            break;
                        case ConsoleKey.Home:
                            selectedIndex = 0;
                            break;
                        case ConsoleKey.End:
                            selectedIndex = targets.Count - 1;
                            break;
                        case ConsoleKey.Enter:
                            FinishTargetWindowSelector(startTop, pageSize, originalForeground, originalBackground);
                            return targets[selectedIndex];
                        case ConsoleKey.Escape:
                            canceled = true;
                            FinishTargetWindowSelector(startTop, pageSize, originalForeground, originalBackground);
                            return null;
                    }
                }
            }
            finally
            {
                Console.ForegroundColor = originalForeground;
                Console.BackgroundColor = originalBackground;
                Console.CursorVisible = originalCursorVisible;
            }
        }

        private static void RenderTargetWindowSelector(List<WindowCmd.TargetWindow> targets, int selectedIndex, ref int offset,
            int pageSize, int startTop, ConsoleColor highlightForeground, ConsoleColor highlightBackground,
            ConsoleColor originalForeground, ConsoleColor originalBackground)
        {
            if (selectedIndex < offset)
            {
                offset = selectedIndex;
            }
            else if (selectedIndex >= offset + pageSize)
            {
                offset = selectedIndex - pageSize + 1;
            }

            var width = Math.Max(20, Console.BufferWidth - 1);
            Console.SetCursorPosition(0, startTop);
            Console.ForegroundColor = originalForeground;
            Console.BackgroundColor = originalBackground;
            WriteSelectorLine("Select a window/application:", width);
            WriteSelectorLine("Use ↑/↓, PgUp/PgDn, Home/End, Enter to choose, Esc to quit.", width);

            var visibleCount = Math.Min(pageSize, targets.Count - offset);
            for (var i = 0; i < pageSize; i++)
            {
                var targetIndex = offset + i;
                var selected = targetIndex == selectedIndex;

                Console.ForegroundColor = selected ? highlightForeground : originalForeground;
                Console.BackgroundColor = selected ? highlightBackground : originalBackground;

                var line = targetIndex < targets.Count
                    ? PlainFormatTargetWindow(targets[targetIndex])
                    : string.Empty;
                WriteSelectorLine(line, width);
            }

            Console.ForegroundColor = originalForeground;
            Console.BackgroundColor = originalBackground;
            var footer = targets.Count > visibleCount
                ? $"Showing {offset + 1}-{offset + visibleCount} of {targets.Count}."
                : $"Showing {targets.Count} window(s).";
            WriteSelectorLine(footer, width);
        }

        private static void FinishTargetWindowSelector(int startTop, int pageSize,
            ConsoleColor originalForeground, ConsoleColor originalBackground)
        {
            Console.ForegroundColor = originalForeground;
            Console.BackgroundColor = originalBackground;
            Console.SetCursorPosition(0, startTop + pageSize + 3);
            Console.WriteLine();
        }

        private static void WriteSelectorLine(string text, int width)
        {
            if (text.Length > width)
            {
                text = text.Substring(0, Math.Max(0, width - 1)) + "…";
            }

            Console.Write(text.PadRight(width));
            Console.WriteLine();
        }

        private static ConsoleColor GetDarkInvertedConsoleColor(ConsoleColor color)
        {
            switch (color)
            {
                case ConsoleColor.Black:
                    return ConsoleColor.DarkGray;
                case ConsoleColor.DarkBlue:
                    return ConsoleColor.DarkYellow;
                case ConsoleColor.DarkGreen:
                    return ConsoleColor.DarkMagenta;
                case ConsoleColor.DarkCyan:
                    return ConsoleColor.DarkRed;
                case ConsoleColor.DarkRed:
                    return ConsoleColor.DarkCyan;
                case ConsoleColor.DarkMagenta:
                    return ConsoleColor.DarkGreen;
                case ConsoleColor.DarkYellow:
                    return ConsoleColor.DarkBlue;
                case ConsoleColor.Gray:
                    return ConsoleColor.DarkBlue;
                case ConsoleColor.DarkGray:
                    return ConsoleColor.Black;
                case ConsoleColor.Blue:
                    return ConsoleColor.DarkYellow;
                case ConsoleColor.Green:
                    return ConsoleColor.DarkMagenta;
                case ConsoleColor.Cyan:
                    return ConsoleColor.DarkRed;
                case ConsoleColor.Red:
                    return ConsoleColor.DarkCyan;
                case ConsoleColor.Magenta:
                    return ConsoleColor.DarkGreen;
                case ConsoleColor.Yellow:
                    return ConsoleColor.DarkBlue;
                case ConsoleColor.White:
                    return ConsoleColor.Black;
                default:
                    return ConsoleColor.DarkGray;
            }
        }

        private static ConsoleColor GetReadableForeground(ConsoleColor background)
        {
            switch (background)
            {
                case ConsoleColor.Black:
                case ConsoleColor.DarkBlue:
                case ConsoleColor.DarkGreen:
                case ConsoleColor.DarkCyan:
                case ConsoleColor.DarkRed:
                case ConsoleColor.DarkMagenta:
                case ConsoleColor.DarkYellow:
                case ConsoleColor.DarkGray:
                    return ConsoleColor.White;
                default:
                    return ConsoleColor.Black;
            }
        }

        private static void GetConsoleRgb(ConsoleColor color, out byte r, out byte g, out byte b)
        {
            switch (color)
            {
                case ConsoleColor.Black:
                    r = 0; g = 0; b = 0;
                    return;
                case ConsoleColor.DarkBlue:
                    r = 0; g = 0; b = 128;
                    return;
                case ConsoleColor.DarkGreen:
                    r = 0; g = 128; b = 0;
                    return;
                case ConsoleColor.DarkCyan:
                    r = 0; g = 128; b = 128;
                    return;
                case ConsoleColor.DarkRed:
                    r = 128; g = 0; b = 0;
                    return;
                case ConsoleColor.DarkMagenta:
                    r = 128; g = 0; b = 128;
                    return;
                case ConsoleColor.DarkYellow:
                    r = 128; g = 128; b = 0;
                    return;
                case ConsoleColor.Gray:
                    r = 192; g = 192; b = 192;
                    return;
                case ConsoleColor.DarkGray:
                    r = 128; g = 128; b = 128;
                    return;
                case ConsoleColor.Blue:
                    r = 0; g = 0; b = 255;
                    return;
                case ConsoleColor.Green:
                    r = 0; g = 255; b = 0;
                    return;
                case ConsoleColor.Cyan:
                    r = 0; g = 255; b = 255;
                    return;
                case ConsoleColor.Red:
                    r = 255; g = 0; b = 0;
                    return;
                case ConsoleColor.Magenta:
                    r = 255; g = 0; b = 255;
                    return;
                case ConsoleColor.Yellow:
                    r = 255; g = 255; b = 0;
                    return;
                case ConsoleColor.White:
                    r = 255; g = 255; b = 255;
                    return;
                default:
                    r = 0; g = 0; b = 0;
                    return;
            }
        }

        private static string FormatTargetWindow(WindowCmd.TargetWindow target)
        {
            var title = string.IsNullOrWhiteSpace(target.Title) ? "(no title)" : target.Title;
            return $"[green]{EscapeMarkup(target.ProcessName)}[/] [grey]|[/] {EscapeMarkup(title)} [grey](0x{target.Handle.ToInt64():X})[/]";
        }

        private static string PlainFormatTargetWindow(WindowCmd.TargetWindow target)
        {
            var title = string.IsNullOrWhiteSpace(target.Title) ? "(no title)" : target.Title;
            return $"{target.ProcessName} | {title} (0x{target.Handle.ToInt64():X})";
        }

        private static string EscapeMarkup(string value)
        {
            return value.Replace("[", "[[").Replace("]", "]]");
        }

        private static void Verbose(List<WindowCmd.TargetWindow> lists)
        {
            if (!lists.Any())
            {
                Output.Echo("No windows resized.");
                return;
            }

            var table = new Table();
            table.AddColumn(new TableColumn("Handle"));
            table.AddColumn(new TableColumn("Process"));
            table.AddColumn(new TableColumn("Title"));
            table.AddColumn(new TableColumn("Success").Centered());
            table.AddColumn(new TableColumn("Error"));
            foreach (var item in lists)
            {
                var result = string.IsNullOrEmpty(item.Result) ? "[green]Y[/]" : "[red]N[/]";
                table.AddRow(item.Handle.ToString(), $"[green]{item.ProcessName}[/]", item.Title ?? string.Empty, result, $"[red]{item.Result}[/]");
            }

            table.Border(TableBorder.Square);
            table.Alignment(Justify.Left);
            AnsiConsole.Write(table);
        }
    }
}
