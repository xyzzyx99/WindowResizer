using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
            var originalForeground = Console.ForegroundColor;
            var originalBackground = Console.BackgroundColor;
            var originalCursorVisible = Console.CursorVisible;
            var usingAlternateScreen = PrepareSelectorScreen();
            var startTop = 0;
            var pageSize = GetSelectorPageSize();
            var highlightBackground = GetDarkInvertedConsoleColor(originalBackground);
            var lastConsoleWidth = GetSafeConsoleWidth();
            var lastConsoleHeight = GetSafeConsoleHeight();

            try
            {
                HideSelectorCursor(usingAlternateScreen);
                while (true)
                {
                    pageSize = GetSelectorPageSize();
                    RenderTargetWindowSelector(targets, selectedIndex, ref offset, pageSize, startTop,
                        highlightBackground, originalForeground, originalBackground, usingAlternateScreen);

                    var key = ReadSelectorKey(targets, selectedIndex, ref offset, ref pageSize, startTop,
                        highlightBackground, originalForeground, originalBackground, usingAlternateScreen,
                        ref lastConsoleWidth, ref lastConsoleHeight);
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
                        default:
                            JumpToProcessGroupByKey(targets, key, ref selectedIndex);
                            break;
                    }
                }
            }
            finally
            {
                FinishSelectorScreen(usingAlternateScreen, originalCursorVisible);
                Console.ForegroundColor = originalForeground;
                Console.BackgroundColor = originalBackground;
                TrySetConsoleCursorVisible(originalCursorVisible);
                Console.CursorVisible = originalCursorVisible;
            }
        }

        private static ConsoleKeyInfo ReadSelectorKey(List<WindowCmd.TargetWindow> targets, int selectedIndex, ref int offset,
            ref int pageSize, int startTop, ConsoleColor highlightBackground,
            ConsoleColor originalForeground, ConsoleColor originalBackground, bool usingAlternateScreen,
            ref int lastConsoleWidth, ref int lastConsoleHeight)
        {
            while (true)
            {
                // Windows Terminal may briefly re-enable or show the text cursor
                // while the window is being resized. Keep hiding it even when
                // there is no key press and no redraw yet.
                HideSelectorCursor(usingAlternateScreen);

                if (Console.KeyAvailable)
                {
                    return Console.ReadKey(true);
                }

                if (HasConsoleSizeChanged(ref lastConsoleWidth, ref lastConsoleHeight))
                {
                    HideSelectorCursor(usingAlternateScreen);
                    TryClearSelectorScreen();
                    pageSize = GetSelectorPageSize();
                    RenderTargetWindowSelector(targets, selectedIndex, ref offset, pageSize, startTop,
                        highlightBackground, originalForeground, originalBackground, usingAlternateScreen);
                }

                Thread.Sleep(50);
            }
        }

        private static bool HasConsoleSizeChanged(ref int lastWidth, ref int lastHeight)
        {
            var currentWidth = GetSafeConsoleWidth();
            var currentHeight = GetSafeConsoleHeight();

            if (currentWidth == lastWidth && currentHeight == lastHeight)
            {
                return false;
            }

            lastWidth = currentWidth;
            lastHeight = currentHeight;
            return true;
        }

        private static int GetSafeConsoleWidth()
        {
            try
            {
                return Console.WindowWidth;
            }
            catch
            {
                return 0;
            }
        }

        private static int GetSafeConsoleHeight()
        {
            try
            {
                return Console.WindowHeight;
            }
            catch
            {
                return 0;
            }
        }

        private static void HideSelectorCursor(bool usingAlternateScreen)
        {
            TrySetConsoleCursorVisible(false);

            try
            {
                Console.CursorVisible = false;
            }
            catch
            {
                // Ignore consoles that do not allow cursor visibility changes.
            }

            if (usingAlternateScreen)
            {
                try
                {
                    Console.Write("\x1b[?25l");
                }
                catch
                {
                    // Ignore VT cleanup failures.
                }
            }
        }

        private static void TryClearSelectorScreen()
        {
            try
            {
                Console.Clear();
                Console.SetCursorPosition(0, 0);
            }
            catch
            {
                // Some redirected or unusual consoles may not allow cursor positioning.
            }
        }

        private static bool PrepareSelectorScreen()
        {
            var usingAlternateScreen = TryEnterAlternateScreen();

            // Use a full-screen selector area. When alternate screen support
            // is available, the original terminal contents are restored after
            // choosing a window or pressing Esc.
            TryClearSelectorScreen();

            return usingAlternateScreen;
        }

        private static void FinishSelectorScreen(bool usingAlternateScreen, bool originalCursorVisible)
        {
            if (usingAlternateScreen)
            {
                TryLeaveAlternateScreen(originalCursorVisible);
            }
        }

        private static bool TryEnterAlternateScreen()
        {
            try
            {
                if (!TryEnableVirtualTerminalProcessing())
                {
                    return false;
                }

                Console.Write("\x1b[?1049h\x1b[?25l");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryLeaveAlternateScreen(bool originalCursorVisible)
        {
            try
            {
                Console.Write("\x1b[0m\x1b[?1049l");
                Console.Write(originalCursorVisible ? "\x1b[?25h" : "\x1b[?25l");
            }
            catch
            {
                // Ignore console cleanup failures.
            }
        }

        private static bool TrySetConsoleCursorVisible(bool visible)
        {
            try
            {
                var handle = GetStdHandle(StdOutputHandle);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    return false;
                }

                ConsoleCursorInfo cursorInfo;
                if (!GetConsoleCursorInfo(handle, out cursorInfo))
                {
                    return false;
                }

                cursorInfo.Visible = visible;
                return SetConsoleCursorInfo(handle, ref cursorInfo);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryEnableVirtualTerminalProcessing()
        {
            try
            {
                var handle = GetStdHandle(StdOutputHandle);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    return false;
                }

                int mode;
                if (!GetConsoleMode(handle, out mode))
                {
                    return false;
                }

                return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
            }
            catch
            {
                return false;
            }
        }

        private static int GetSelectorPageSize()
        {
            const int headerLines = 2;
            const int footerLines = 1;
            const int minimumPageSize = 1;
            const int fallbackPageSize = 15;

            try
            {
                var pageSize = Console.WindowHeight - headerLines - footerLines;
                return Math.Max(minimumPageSize, pageSize);
            }
            catch
            {
                return fallbackPageSize;
            }
        }

        private static void JumpToProcessGroupByKey(List<WindowCmd.TargetWindow> targets, ConsoleKeyInfo key, ref int selectedIndex)
        {
            var keyChar = char.ToUpperInvariant(key.KeyChar);
            if (!char.IsLetterOrDigit(keyChar) || targets.Count == 0)
            {
                return;
            }

            var currentGroupStart = GetProcessGroupStartIndex(targets, selectedIndex);
            var nextIndex = FindNextProcessGroupStartingWith(targets, keyChar, currentGroupStart + 1);
            if (nextIndex < 0)
            {
                nextIndex = FindNextProcessGroupStartingWith(targets, keyChar, 0);
            }

            if (nextIndex >= 0)
            {
                selectedIndex = nextIndex;
            }
        }

        private static int GetProcessGroupStartIndex(List<WindowCmd.TargetWindow> targets, int selectedIndex)
        {
            var current = Math.Max(0, Math.Min(selectedIndex, targets.Count - 1));
            while (current > 0 && string.Equals(targets[current - 1].ProcessName, targets[current].ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                current--;
            }

            return current;
        }

        private static int FindNextProcessGroupStartingWith(List<WindowCmd.TargetWindow> targets, char keyChar, int startIndex)
        {
            for (var i = Math.Max(0, startIndex); i < targets.Count; i++)
            {
                if (!IsProcessGroupStart(targets, i))
                {
                    continue;
                }

                if (ProcessNameStartsWith(targets[i].ProcessName, keyChar))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsProcessGroupStart(List<WindowCmd.TargetWindow> targets, int index)
        {
            return index <= 0 || !string.Equals(targets[index - 1].ProcessName, targets[index].ProcessName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ProcessNameStartsWith(string processName, char keyChar)
        {
            return !string.IsNullOrWhiteSpace(processName)
                   && char.ToUpperInvariant(processName[0]) == keyChar;
        }

        private static int GetSafeSelectorWidth()
        {
            try
            {
                return Math.Max(1, Math.Min(Console.BufferWidth, Console.WindowWidth) - 1);
            }
            catch
            {
                return 79;
            }
        }

        private static void RenderTargetWindowSelector(List<WindowCmd.TargetWindow> targets, int selectedIndex, ref int offset,
            int pageSize, int startTop, ConsoleColor highlightBackground,
            ConsoleColor originalForeground, ConsoleColor originalBackground, bool useAnsiColors)
        {
            HideSelectorCursor(useAnsiColors);

            if (selectedIndex < offset)
            {
                offset = selectedIndex;
            }
            else if (selectedIndex >= offset + pageSize)
            {
                offset = selectedIndex - pageSize + 1;
            }

            var width = Math.Max(20, GetSafeSelectorWidth());
            var row = startTop;
            SetSelectorColors(originalForeground, originalBackground, useAnsiColors);
            WriteSelectorLine(row++, "Select a window/application:", width);
            SetSelectorColors(originalForeground, originalBackground, useAnsiColors);
            WriteSelectorLine(row++, "Use ↑/↓, PgUp/PgDn, Home/End, letter keys, Enter to choose, Esc to quit.", width);

            var visibleCount = Math.Min(pageSize, targets.Count - offset);
            for (var i = 0; i < pageSize; i++)
            {
                var targetIndex = offset + i;
                var selected = targetIndex == selectedIndex;
                var rowBackground = selected ? highlightBackground : originalBackground;

                if (!TrySetSelectorCursorPosition(0, row++))
                {
                    return;
                }
                SetSelectorColors(originalForeground, rowBackground, useAnsiColors);

                if (targetIndex < targets.Count)
                {
                    WriteTargetWindowSelectorLine(targets[targetIndex], width, selected, originalForeground, rowBackground, useAnsiColors);
                }
                else
                {
                    WriteSelectorLine(string.Empty, width);
                }
            }

            SetSelectorColors(originalForeground, originalBackground, useAnsiColors);
            var footer = targets.Count > visibleCount
                ? $"Showing {offset + 1}-{offset + visibleCount} of {targets.Count}."
                : $"Showing {targets.Count} window(s).";
            WriteSelectorLine(row, footer, width);

            // Leave the cursor in a harmless position and hide it again after
            // every paint. Resizing the terminal can temporarily reveal the
            // cursor at the last write position otherwise.
            TrySetSelectorCursorPosition(0, 0);
            HideSelectorCursor(useAnsiColors);
        }

        private static void FinishTargetWindowSelector(int startTop, int pageSize,
            ConsoleColor originalForeground, ConsoleColor originalBackground)
        {
            Console.ForegroundColor = originalForeground;
            Console.BackgroundColor = originalBackground;
        }

        private static bool TrySetSelectorCursorPosition(int left, int top)
        {
            try
            {
                Console.SetCursorPosition(left, top);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteSelectorLine(int row, string text, int width)
        {
            if (TrySetSelectorCursorPosition(0, row))
            {
                WriteSelectorLine(text, width);
            }
        }

        private static void WriteSelectorLine(string text, int width)
        {
            if (text.Length > width)
            {
                text = text.Substring(0, Math.Max(0, width - 1)) + "…";
            }

            Console.Write(text.PadRight(width));
        }

        private static void WriteTargetWindowSelectorLine(WindowCmd.TargetWindow target, int width, bool selected,
            ConsoleColor originalForeground, ConsoleColor background, bool useAnsiColors)
        {
            var title = string.IsNullOrWhiteSpace(target.Title) ? "(no title)" : target.Title;
            var used = 0;
            var processForeground = selected ? originalForeground : ConsoleColor.Green;
            var mutedForeground = selected ? originalForeground : ConsoleColor.DarkGray;
            var titleForeground = originalForeground;
            SetSelectorColors(titleForeground, background, useAnsiColors);
            used += WriteSelectorSegment(selected ? "> " : "  ", width - used, titleForeground, background, useAnsiColors);
            used += WriteSelectorSegment(target.ProcessName, width - used, processForeground, background, useAnsiColors);
            used += WriteProcessInfo(target, width - used, selected, processForeground, mutedForeground, background, useAnsiColors);

            used += WriteSelectorSegment(" | ", width - used, mutedForeground, background, useAnsiColors);
            used += WriteSelectorSegment(title, width - used, titleForeground, background, useAnsiColors);
            used += WriteSelectorSegment($" (0x{target.Handle.ToInt64():X})", width - used, mutedForeground, background, useAnsiColors);

            if (used < width)
            {
                SetSelectorColors(titleForeground, background, useAnsiColors);
                Console.Write(new string(' ', width - used));
            }

        }

        private static int WriteProcessInfo(WindowCmd.TargetWindow target, int availableWidth, bool selected,
            ConsoleColor topForeground, ConsoleColor mutedForeground, ConsoleColor background, bool useAnsiColors)
        {
            if (availableWidth <= 0 || (target.ProcessId <= 0 && !target.IsTopForProcess))
            {
                return 0;
            }

            var used = 0;
            var pidForeground = selected ? topForeground : mutedForeground;

            used += WriteSelectorSegment(" [", availableWidth - used, mutedForeground, background, useAnsiColors);

            if (target.ProcessId > 0)
            {
                used += WriteSelectorSegment(target.ProcessId.ToString(), availableWidth - used, pidForeground, background, useAnsiColors);

                if (target.IsTopForProcess)
                {
                    used += WriteSelectorSegment(" ", availableWidth - used, mutedForeground, background, useAnsiColors);
                }
            }

            if (target.IsTopForProcess)
            {
                used += WriteSelectorSegment("Top", availableWidth - used, topForeground, background, useAnsiColors);
            }

            used += WriteSelectorSegment("]", availableWidth - used, mutedForeground, background, useAnsiColors);
            return used;
        }

        private static int WriteSelectorSegment(string text, int availableWidth,
            ConsoleColor foreground, ConsoleColor background, bool useAnsiColors)
        {
            if (availableWidth <= 0)
            {
                return 0;
            }

            if (text.Length > availableWidth)
            {
                text = availableWidth == 1
                    ? "…"
                    : text.Substring(0, availableWidth - 1) + "…";
            }

            SetSelectorColors(foreground, background, useAnsiColors);
            Console.Write(text);
            return text.Length;
        }


        private static void SetSelectorColors(ConsoleColor foreground, ConsoleColor background, bool useAnsiColors)
        {
            Console.ForegroundColor = foreground;
            Console.BackgroundColor = background;

            if (useAnsiColors)
            {
                Console.Write(GetAnsiColorSequence(foreground, background));
            }
        }

        private static string GetAnsiColorSequence(ConsoleColor foreground, ConsoleColor background)
        {
            return $"\x1b[{GetAnsiForegroundCode(foreground)};{GetAnsiBackgroundCode(background)}m";
        }

        private static int GetAnsiForegroundCode(ConsoleColor color)
        {
            switch (color)
            {
                case ConsoleColor.Black: return 30;
                case ConsoleColor.DarkBlue: return 34;
                case ConsoleColor.DarkGreen: return 32;
                case ConsoleColor.DarkCyan: return 36;
                case ConsoleColor.DarkRed: return 31;
                case ConsoleColor.DarkMagenta: return 35;
                case ConsoleColor.DarkYellow: return 33;
                case ConsoleColor.Gray: return 37;
                case ConsoleColor.DarkGray: return 90;
                case ConsoleColor.Blue: return 94;
                case ConsoleColor.Green: return 92;
                case ConsoleColor.Cyan: return 96;
                case ConsoleColor.Red: return 91;
                case ConsoleColor.Magenta: return 95;
                case ConsoleColor.Yellow: return 93;
                case ConsoleColor.White: return 97;
                default: return 39;
            }
        }

        private static int GetAnsiBackgroundCode(ConsoleColor color)
        {
            switch (color)
            {
                case ConsoleColor.Black: return 40;
                case ConsoleColor.DarkBlue: return 44;
                case ConsoleColor.DarkGreen: return 42;
                case ConsoleColor.DarkCyan: return 46;
                case ConsoleColor.DarkRed: return 41;
                case ConsoleColor.DarkMagenta: return 45;
                case ConsoleColor.DarkYellow: return 43;
                case ConsoleColor.Gray: return 47;
                case ConsoleColor.DarkGray: return 100;
                case ConsoleColor.Blue: return 104;
                case ConsoleColor.Green: return 102;
                case ConsoleColor.Cyan: return 106;
                case ConsoleColor.Red: return 101;
                case ConsoleColor.Magenta: return 105;
                case ConsoleColor.Yellow: return 103;
                case ConsoleColor.White: return 107;
                default: return 49;
            }
        }


        private const int StdOutputHandle = -11;
        private const int EnableVirtualTerminalProcessing = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out int lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, int dwMode);

        [StructLayout(LayoutKind.Sequential)]
        private struct ConsoleCursorInfo
        {
            public int Size;

            [MarshalAs(UnmanagedType.Bool)]
            public bool Visible;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleCursorInfo(IntPtr hConsoleOutput, out ConsoleCursorInfo lpConsoleCursorInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCursorInfo(IntPtr hConsoleOutput, ref ConsoleCursorInfo lpConsoleCursorInfo);

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
