using System;
using System.Collections.Generic;
using System.Globalization;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
            int originalInputMode;
            var mouseInputEnabled = TryPrepareSelectorMouseInput(out originalInputMode);
            var usingAlternateScreen = PrepareSelectorScreen();
            var startTop = 0;
            var pageSize = GetSelectorPageSize();
            var highlightBackground = GetDarkInvertedConsoleColor(originalBackground);
            var lastConsoleWidth = GetSafeConsoleWidth();
            var lastConsoleHeight = GetSafeConsoleHeight();
            var renderState = new SelectorRenderState();

            try
            {
                HideSelectorCursor(usingAlternateScreen);
                while (true)
                {
                    pageSize = GetSelectorPageSize();
                    RenderTargetWindowSelector(targets, selectedIndex, ref offset, pageSize, startTop,
                        highlightBackground, originalForeground, originalBackground, usingAlternateScreen, renderState);

                    var key = ReadSelectorKey(targets, ref selectedIndex, ref offset, ref pageSize, startTop,
                        highlightBackground, originalForeground, originalBackground, usingAlternateScreen, renderState,
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
                RestoreSelectorMouseInput(mouseInputEnabled, originalInputMode);
                Console.ForegroundColor = originalForeground;
                Console.BackgroundColor = originalBackground;
                TrySetConsoleCursorVisible(originalCursorVisible);
                Console.CursorVisible = originalCursorVisible;
            }
        }

        private static ConsoleKeyInfo ReadSelectorKey(List<WindowCmd.TargetWindow> targets, ref int selectedIndex, ref int offset,
            ref int pageSize, int startTop, ConsoleColor highlightBackground,
            ConsoleColor originalForeground, ConsoleColor originalBackground, bool usingAlternateScreen,
            SelectorRenderState renderState, ref int lastConsoleWidth, ref int lastConsoleHeight)
        {
            while (true)
            {
                // Windows Terminal may briefly re-enable or show the text cursor
                // while the window is being resized. Keep hiding it even when
                // there is no key press and no redraw yet.
                HideSelectorCursor(usingAlternateScreen);

                ConsoleKeyInfo key;
                if (TryReadSelectorConsoleInput(targets, ref selectedIndex, offset, pageSize, startTop, out key))
                {
                    return key;
                }

                if (HasConsoleSizeChanged(ref lastConsoleWidth, ref lastConsoleHeight))
                {
                    HideSelectorCursor(usingAlternateScreen);
                    pageSize = GetSelectorPageSize();
                    RenderTargetWindowSelector(targets, selectedIndex, ref offset, pageSize, startTop,
                        highlightBackground, originalForeground, originalBackground, usingAlternateScreen, renderState);
                }

                HideSelectorCursor(usingAlternateScreen);
                Thread.Sleep(50);
            }
        }

        private static bool TryReadSelectorConsoleInput(List<WindowCmd.TargetWindow> targets, ref int selectedIndex,
            int offset, int pageSize, int startTop, out ConsoleKeyInfo key)
        {
            key = default(ConsoleKeyInfo);

            var inputHandle = GetStdHandle(StdInputHandle);
            if (inputHandle == IntPtr.Zero || inputHandle == new IntPtr(-1))
            {
                return TryReadLegacyKeyboardInput(out key);
            }

            uint eventCount;
            if (!GetNumberOfConsoleInputEvents(inputHandle, out eventCount))
            {
                return TryReadLegacyKeyboardInput(out key);
            }

            while (eventCount > 0)
            {
                INPUT_RECORD peekedRecord;
                uint recordsPeeked;
                if (!PeekConsoleInput(inputHandle, out peekedRecord, 1, out recordsPeeked) || recordsPeeked == 0)
                {
                    return TryReadLegacyKeyboardInput(out key);
                }

                if (peekedRecord.EventType == KeyEvent)
                {
                    if (!peekedRecord.Event.KeyEvent.bKeyDown)
                    {
                        if (!ReadAndDiscardConsoleInput(inputHandle))
                        {
                            return false;
                        }
                    }
                    else if (TryReadLegacyKeyboardInput(out key))
                    {
                        return true;
                    }
                    else if (TryReadRawConsoleKey(inputHandle, out key))
                    {
                        return true;
                    }
                }
                else if (peekedRecord.EventType == MouseEvent)
                {
                    INPUT_RECORD record;
                    uint recordsRead;
                    if (!ReadConsoleInput(inputHandle, out record, 1, out recordsRead) || recordsRead == 0)
                    {
                        return false;
                    }

                    if (TryHandleSelectorMouseEvent(record.Event.MouseEvent,
                            targets, ref selectedIndex, offset, pageSize, startTop, out key))
                    {
                        return true;
                    }
                }
                else if (!ReadAndDiscardConsoleInput(inputHandle))
                {
                    return false;
                }

                if (!GetNumberOfConsoleInputEvents(inputHandle, out eventCount))
                {
                    return false;
                }
            }

            return false;
        }

        private static bool ReadAndDiscardConsoleInput(IntPtr inputHandle)
        {
            INPUT_RECORD ignored;
            uint recordsRead;
            return ReadConsoleInput(inputHandle, out ignored, 1, out recordsRead) && recordsRead > 0;
        }

        private static bool TryReadRawConsoleKey(IntPtr inputHandle, out ConsoleKeyInfo key)
        {
            key = default(ConsoleKeyInfo);

            INPUT_RECORD record;
            uint recordsRead;
            if (!ReadConsoleInput(inputHandle, out record, 1, out recordsRead) || recordsRead == 0)
            {
                return false;
            }

            if (record.EventType != KeyEvent || !record.Event.KeyEvent.bKeyDown)
            {
                return false;
            }

            key = CreateConsoleKeyInfo(record.Event.KeyEvent);
            return true;
        }

        private static bool TryReadLegacyKeyboardInput(out ConsoleKeyInfo key)
        {
            key = default(ConsoleKeyInfo);

            try
            {
                if (!Console.KeyAvailable)
                {
                    return false;
                }

                key = Console.ReadKey(true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryHandleSelectorMouseEvent(MOUSE_EVENT_RECORD mouseEvent,
            List<WindowCmd.TargetWindow> targets, ref int selectedIndex, int offset, int pageSize, int startTop,
            out ConsoleKeyInfo key)
        {
            key = default(ConsoleKeyInfo);

            if (mouseEvent.dwEventFlags == MouseWheeled)
            {
                var wheelDelta = unchecked((short)((mouseEvent.dwButtonState >> 16) & 0xffff));
                if (wheelDelta == 0 || targets.Count == 0)
                {
                    return false;
                }

                var steps = Math.Max(1, Math.Abs(wheelDelta) / WheelDelta);
                selectedIndex = wheelDelta > 0
                    ? Math.Max(0, selectedIndex - steps)
                    : Math.Min(targets.Count - 1, selectedIndex + steps);
                key = CreateNoOpKey();
                return true;
            }

            var leftButtonDown = (mouseEvent.dwButtonState & LeftMostButtonPressed) != 0;
            if (!leftButtonDown || (mouseEvent.dwEventFlags != 0 && mouseEvent.dwEventFlags != DoubleClick))
            {
                return false;
            }

            var listRow = mouseEvent.dwMousePosition.Y - (startTop + SelectorHeaderLines);
            if (listRow < 0 || listRow >= pageSize)
            {
                return false;
            }

            var targetIndex = offset + listRow;
            if (targetIndex < 0 || targetIndex >= targets.Count)
            {
                return false;
            }

            selectedIndex = targetIndex;

            if (mouseEvent.dwEventFlags == DoubleClick)
            {
                key = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);
            }
            else
            {
                key = CreateNoOpKey();
            }

            return true;
        }

        private static ConsoleKeyInfo CreateNoOpKey()
        {
            return new ConsoleKeyInfo('\0', ConsoleKey.NoName, false, false, false);
        }

        private static ConsoleKeyInfo CreateConsoleKeyInfo(KEY_EVENT_RECORD keyEvent)
        {
            var modifiers = keyEvent.dwControlKeyState;
            var shift = (modifiers & ShiftPressed) != 0;
            var alt = (modifiers & (LeftAltPressed | RightAltPressed)) != 0;
            var control = (modifiers & (LeftCtrlPressed | RightCtrlPressed)) != 0;
            var consoleKey = (ConsoleKey)keyEvent.wVirtualKeyCode;
            var keyChar = keyEvent.UnicodeChar;

            if (keyChar == '\0')
            {
                keyChar = GetPrintableCharFromVirtualKey(consoleKey, shift);
            }

            return new ConsoleKeyInfo(keyChar, consoleKey, shift, alt, control);
        }

        private static char GetPrintableCharFromVirtualKey(ConsoleKey key, bool shift)
        {
            if (key >= ConsoleKey.A && key <= ConsoleKey.Z)
            {
                var ch = (char)('A' + ((int)key - (int)ConsoleKey.A));
                return shift ? ch : char.ToLowerInvariant(ch);
            }

            if (key >= ConsoleKey.D0 && key <= ConsoleKey.D9)
            {
                return (char)('0' + ((int)key - (int)ConsoleKey.D0));
            }

            if (key >= ConsoleKey.NumPad0 && key <= ConsoleKey.NumPad9)
            {
                return (char)('0' + ((int)key - (int)ConsoleKey.NumPad0));
            }

            return '\0';
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

        private static bool TryPrepareSelectorMouseInput(out int originalInputMode)
        {
            originalInputMode = 0;

            try
            {
                var handle = GetStdHandle(StdInputHandle);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    return false;
                }

                if (!GetConsoleMode(handle, out originalInputMode))
                {
                    return false;
                }

                var newMode = originalInputMode;
                newMode |= EnableExtendedFlags;
                newMode |= EnableMouseInput;
                newMode |= EnableWindowInput;
                newMode &= ~EnableQuickEditMode;

                return SetConsoleMode(handle, newMode);
            }
            catch
            {
                return false;
            }
        }

        private static void RestoreSelectorMouseInput(bool mouseInputEnabled, int originalInputMode)
        {
            if (!mouseInputEnabled)
            {
                return;
            }

            try
            {
                var handle = GetStdHandle(StdInputHandle);
                if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                {
                    SetConsoleMode(handle, originalInputMode);
                }
            }
            catch
            {
                // Ignore console cleanup failures.
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
            const int minimumPageSize = 1;
            const int fallbackPageSize = 15;

            try
            {
                var pageSize = Console.WindowHeight - SelectorHeaderLines - SelectorFooterLines;
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
                return Math.Max(1, Math.Min(Console.BufferWidth, Console.WindowWidth));
            }
            catch
            {
                return 80;
            }
        }

        private static void RenderTargetWindowSelector(List<WindowCmd.TargetWindow> targets, int selectedIndex, ref int offset,
            int pageSize, int startTop, ConsoleColor highlightBackground,
            ConsoleColor originalForeground, ConsoleColor originalBackground, bool useAnsiColors, SelectorRenderState renderState)
        {
            HideSelectorCursor(useAnsiColors);

            AdjustSelectorViewport(targets.Count, selectedIndex, pageSize, ref offset);

            var width = GetSafeSelectorWidth();
            var visibleCount = Math.Min(pageSize, Math.Max(0, targets.Count - offset));
            var rows = BuildSelectorRows(targets, selectedIndex, offset, pageSize, width, visibleCount,
                highlightBackground, originalForeground, originalBackground);

            if (!TryRenderSelectorRows(rows, startTop, width, renderState))
            {
                RenderTargetWindowSelectorWithConsoleWrites(targets, selectedIndex, offset, pageSize, startTop,
                    width, visibleCount, highlightBackground, originalForeground, originalBackground, useAnsiColors, renderState);
            }
            else
            {
                renderState.Update(selectedIndex, offset, pageSize, width, targets.Count, rows);
            }

            // Leave the cursor in a harmless position and hide it again after
            // every paint. Resizing the terminal can temporarily reveal the
            // cursor at the last write position otherwise.
            TrySetSelectorCursorPosition(0, 0);
            HideSelectorCursor(useAnsiColors);
        }

        private static void AdjustSelectorViewport(int targetCount, int selectedIndex, int pageSize, ref int offset)
        {
            if (targetCount <= 0)
            {
                offset = 0;
                return;
            }

            pageSize = Math.Max(1, pageSize);

            // Keep the full target list in memory and only remap the visible
            // slice. For pure height changes this keeps the same top row first,
            // adds/removes rows at the bottom, and only shifts the slice when the
            // selected item would leave the visible page or when we are already
            // at the end and need to keep a full page.
            if (selectedIndex < offset)
            {
                offset = selectedIndex;
            }
            else if (selectedIndex >= offset + pageSize)
            {
                offset = selectedIndex - pageSize + 1;
            }

            var maxOffset = Math.Max(0, targetCount - pageSize);
            offset = Math.Max(0, Math.Min(offset, maxOffset));
        }

        private static List<SelectorRowBuffer> BuildSelectorRows(List<WindowCmd.TargetWindow> targets, int selectedIndex,
            int offset, int pageSize, int width, int visibleCount, ConsoleColor highlightBackground,
            ConsoleColor originalForeground, ConsoleColor originalBackground)
        {
            var rows = new List<SelectorRowBuffer>(SelectorHeaderLines + pageSize + SelectorFooterLines);
            rows.Add(BuildSelectorTextRow("Select a window/application:", width, originalForeground, originalBackground));
            rows.Add(BuildSelectorTextRow("Use ↑/↓, PgUp/PgDn, Home/End, letter keys, mouse wheel, double-click, Enter, Esc.",
                width, originalForeground, originalBackground));

            for (var i = 0; i < pageSize; i++)
            {
                rows.Add(BuildSelectorListRow(targets, offset + i, selectedIndex, width,
                    highlightBackground, originalForeground, originalBackground));
            }

            var footer = targets.Count > visibleCount
                ? $"Showing {offset + 1}-{offset + visibleCount} of {targets.Count}."
                : $"Showing {targets.Count} window(s).";
            rows.Add(BuildSelectorTextRow(footer, width, originalForeground, originalBackground));
            return rows;
        }

        private static SelectorRowBuffer BuildSelectorTextRow(string text, int width,
            ConsoleColor foreground, ConsoleColor background)
        {
            var builder = new SelectorRowBuilder(width, foreground, background);
            builder.Append(text, foreground, background);
            return builder.ToRowBuffer();
        }

        private static SelectorRowBuffer BuildSelectorListRow(List<WindowCmd.TargetWindow> targets, int targetIndex,
            int selectedIndex, int width, ConsoleColor highlightBackground,
            ConsoleColor originalForeground, ConsoleColor originalBackground)
        {
            var selected = targetIndex == selectedIndex;
            var rowBackground = selected ? highlightBackground : originalBackground;
            var builder = new SelectorRowBuilder(width, originalForeground, rowBackground);

            if (targetIndex >= targets.Count)
            {
                return builder.ToRowBuffer();
            }

            AppendTargetWindowSelectorLine(builder, targets[targetIndex], selected, originalForeground, rowBackground);
            return builder.ToRowBuffer();
        }

        private static void AppendTargetWindowSelectorLine(SelectorRowBuilder builder, WindowCmd.TargetWindow target,
            bool selected, ConsoleColor originalForeground, ConsoleColor background)
        {
            var title = string.IsNullOrWhiteSpace(target.Title) ? "(no title)" : target.Title;
            var processForeground = selected ? originalForeground : ConsoleColor.Green;
            var mutedForeground = selected ? originalForeground : ConsoleColor.DarkGray;
            var titleForeground = originalForeground;

            builder.Append(selected ? "> " : "  ", titleForeground, background);
            builder.Append(target.ProcessName, processForeground, background);
            AppendProcessInfo(builder, target, selected, processForeground, mutedForeground, background);
            builder.Append(" | ", mutedForeground, background);
            builder.Append(title, titleForeground, background);
            builder.Append($" (0x{target.Handle.ToInt64():X})", mutedForeground, background);
        }

        private static void AppendProcessInfo(SelectorRowBuilder builder, WindowCmd.TargetWindow target, bool selected,
            ConsoleColor topForeground, ConsoleColor mutedForeground, ConsoleColor background)
        {
            if (builder.Remaining <= 0 || (target.ProcessId <= 0 && !target.IsTopForProcess))
            {
                return;
            }

            var pidForeground = selected ? topForeground : mutedForeground;
            builder.Append(" [", mutedForeground, background);

            if (target.ProcessId > 0)
            {
                builder.Append(target.ProcessId.ToString(), pidForeground, background);

                if (target.IsTopForProcess)
                {
                    builder.Append(" ", mutedForeground, background);
                }
            }

            if (target.IsTopForProcess)
            {
                builder.Append("Top", topForeground, background);
            }

            builder.Append("]", mutedForeground, background);
        }

        private static bool TryRenderSelectorRows(List<SelectorRowBuffer> rows, int startTop, int width,
            SelectorRenderState renderState)
        {
            var outputHandle = GetStdHandle(StdOutputHandle);
            if (outputHandle == IntPtr.Zero || outputHandle == new IntPtr(-1) || width <= 0)
            {
                return false;
            }

            var oldRows = renderState.LastRows;
            for (var i = 0; i < rows.Count; i++)
            {
                var oldRow = oldRows != null && i < oldRows.Count ? oldRows[i] : null;
                if (!renderState.IsInvalid && rows[i].EqualsCells(oldRow))
                {
                    continue;
                }

                if (!TryWriteSelectorRowBuffer(outputHandle, rows[i], startTop + i, width))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryWriteSelectorRowBuffer(IntPtr outputHandle, SelectorRowBuffer row, int top, int width)
        {
            if (top < 0 || width <= 0 || row == null || row.Cells.Length != width)
            {
                return false;
            }

            try
            {
                var bufferSize = new COORD { X = (short)width, Y = 1 };
                var bufferCoord = new COORD { X = 0, Y = 0 };
                var writeRegion = new SMALL_RECT
                {
                    Left = 0,
                    Top = (short)top,
                    Right = (short)(width - 1),
                    Bottom = (short)top
                };

                return WriteConsoleOutput(outputHandle, row.Cells, bufferSize, bufferCoord, ref writeRegion);
            }
            catch
            {
                return false;
            }
        }

        private static void RenderTargetWindowSelectorWithConsoleWrites(List<WindowCmd.TargetWindow> targets, int selectedIndex,
            int offset, int pageSize, int startTop, int width, int visibleCount, ConsoleColor highlightBackground,
            ConsoleColor originalForeground, ConsoleColor originalBackground, bool useAnsiColors, SelectorRenderState renderState)
        {
            var fullRedraw = renderState.IsInvalid
                             || renderState.LastOffset != offset
                             || renderState.LastPageSize != pageSize
                             || renderState.LastWidth != width
                             || renderState.LastTargetCount != targets.Count;

            if (fullRedraw)
            {
                RenderTargetWindowSelectorFull(targets, selectedIndex, offset, pageSize, startTop,
                    width, visibleCount, highlightBackground, originalForeground, originalBackground, useAnsiColors);
            }
            else if (renderState.LastSelectedIndex != selectedIndex)
            {
                RedrawSelectorRowIfVisible(targets, renderState.LastSelectedIndex, selectedIndex, offset, pageSize, startTop,
                    width, highlightBackground, originalForeground, originalBackground, useAnsiColors);
                RedrawSelectorRowIfVisible(targets, selectedIndex, selectedIndex, offset, pageSize, startTop,
                    width, highlightBackground, originalForeground, originalBackground, useAnsiColors);
            }

            renderState.Update(selectedIndex, offset, pageSize, width, targets.Count, null);
        }

        private sealed class SelectorRowBuilder
        {
            private readonly int width;
            private readonly CHAR_INFO[] cells;
            private int used;

            public SelectorRowBuilder(int width, ConsoleColor foreground, ConsoleColor background)
            {
                this.width = Math.Max(1, width);
                cells = new CHAR_INFO[this.width];
                var attribute = GetSelectorAttribute(foreground, background);
                for (var i = 0; i < cells.Length; i++)
                {
                    cells[i].UnicodeChar = ' ';
                    cells[i].Attributes = attribute;
                }
            }

            public int Remaining => width - used;

            public int Append(string text, ConsoleColor foreground, ConsoleColor background)
            {
                if (Remaining <= 0 || string.IsNullOrEmpty(text))
                {
                    return 0;
                }

                text = TrimSelectorTextToWidth(text, Remaining);
                var before = used;
                var attribute = GetSelectorAttribute(foreground, background);
                AppendTextCells(text, attribute);
                return used - before;
            }

            public SelectorRowBuffer ToRowBuffer()
            {
                return new SelectorRowBuffer(cells);
            }

            private void AppendTextCells(string text, short attribute)
            {
                text = NormalizeSelectorText(text);

                for (var i = 0; i < text.Length && used < width;)
                {
                    var charCount = GetSelectorCodePointLength(text, i);
                    var charWidth = GetSelectorCodePointWidth(text, i);
                    if (charWidth <= 0)
                    {
                        i += charCount;
                        continue;
                    }

                    if (used + charWidth > width)
                    {
                        break;
                    }

                    var ch = charCount == 1 ? text[i] : '?';
                    cells[used].UnicodeChar = ch;
                    cells[used].Attributes = attribute;
                    used++;

                    // When a code point is measured as occupying two terminal
                    // cells, reserve the second cell in the off-screen row. This
                    // keeps all later columns aligned and prevents the row from
                    // crossing the terminal edge when copied to the console
                    // buffer.
                    if (charWidth > 1 && used < width)
                    {
                        cells[used].UnicodeChar = ' ';
                        cells[used].Attributes = attribute;
                        used++;
                    }

                    i += charCount;
                }
            }
        }

        private sealed class SelectorRowBuffer
        {
            public SelectorRowBuffer(CHAR_INFO[] cells)
            {
                Cells = cells;
            }

            public CHAR_INFO[] Cells { get; }

            public bool EqualsCells(SelectorRowBuffer other)
            {
                if (other == null || other.Cells == null || other.Cells.Length != Cells.Length)
                {
                    return false;
                }

                for (var i = 0; i < Cells.Length; i++)
                {
                    if (Cells[i].UnicodeChar != other.Cells[i].UnicodeChar
                        || Cells[i].Attributes != other.Cells[i].Attributes)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private sealed class SelectorRenderState
        {
            public bool IsInvalid { get; private set; } = true;
            public int LastSelectedIndex { get; private set; } = -1;
            public int LastOffset { get; private set; } = -1;
            public int LastPageSize { get; private set; } = -1;
            public int LastWidth { get; private set; } = -1;
            public int LastTargetCount { get; private set; } = -1;
            public List<SelectorRowBuffer> LastRows { get; private set; }

            public void Update(int selectedIndex, int offset, int pageSize, int width, int targetCount,
                List<SelectorRowBuffer> rows)
            {
                IsInvalid = false;
                LastSelectedIndex = selectedIndex;
                LastOffset = offset;
                LastPageSize = pageSize;
                LastWidth = width;
                LastTargetCount = targetCount;
                LastRows = rows;
            }

            public void Invalidate()
            {
                IsInvalid = true;
                LastRows = null;
            }
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

        private static void WriteSelectorLine(int row, string text, int width, bool useAnsiColors)
        {
            if (TrySetSelectorCursorPosition(0, row))
            {
                WriteSelectorLine(text, width, useAnsiColors);
            }
        }

        private static void WriteSelectorLine(string text, int width, bool useAnsiColors)
        {
            ClearSelectorCurrentLine(useAnsiColors);

            text = TrimSelectorTextToWidth(text, width);
            Console.Write(text);

            var used = GetSelectorTextWidth(text);
            if (used < width)
            {
                Console.Write(new string(' ', width - used));
                ClearSelectorLineRemainder(useAnsiColors);
            }
        }

        private static void ClearSelectorCurrentLine(bool useAnsiColors)
        {
            if (!useAnsiColors)
            {
                return;
            }

            try
            {
                Console.Write("\x1b[2K");
            }
            catch
            {
                // Ignore consoles that do not support VT erase-line.
            }
        }

        private static void ClearSelectorLineRemainder(bool useAnsiColors)
        {
            if (!useAnsiColors)
            {
                return;
            }

            try
            {
                Console.Write("\x1b[K");
            }
            catch
            {
                // Ignore consoles that do not support VT erase-line.
            }
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
                ClearSelectorLineRemainder(useAnsiColors);
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

            text = TrimSelectorTextToWidth(text, availableWidth);

            SetSelectorColors(foreground, background, useAnsiColors);
            Console.Write(text);
            return GetSelectorTextWidth(text);
        }

        private static string TrimSelectorTextToWidth(string text, int maxWidth)
        {
            if (maxWidth <= 0 || string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            text = NormalizeSelectorText(text);

            if (GetSelectorTextWidth(text) <= maxWidth)
            {
                return text;
            }

            var marker = GetSelectorTextWidth("…") <= maxWidth ? "…" : ".";
            var markerWidth = GetSelectorTextWidth(marker);
            var allowedTextWidth = Math.Max(0, maxWidth - markerWidth);
            var builder = new StringBuilder();
            var used = 0;

            for (var i = 0; i < text.Length;)
            {
                var charCount = GetSelectorCodePointLength(text, i);
                var charWidth = GetSelectorCodePointWidth(text, i);

                if (used + charWidth > allowedTextWidth)
                {
                    break;
                }

                builder.Append(text, i, charCount);
                used += charWidth;
                i += charCount;
            }

            builder.Append(marker);
            return builder.ToString();
        }

        private static string NormalizeSelectorText(string text)
        {
            return text.Replace('\r', ' ').Replace('\n', ' ');
        }

        private static int GetSelectorTextWidth(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            var width = 0;
            for (var i = 0; i < text.Length;)
            {
                width += GetSelectorCodePointWidth(text, i);
                i += GetSelectorCodePointLength(text, i);
            }

            return width;
        }

        private static int GetSelectorCodePointLength(string text, int index)
        {
            return index + 1 < text.Length
                   && char.IsHighSurrogate(text[index])
                   && char.IsLowSurrogate(text[index + 1])
                ? 2
                : 1;
        }

        private static int GetSelectorCodePointWidth(string text, int index)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(text, index);
            if (category == UnicodeCategory.NonSpacingMark
                || category == UnicodeCategory.EnclosingMark
                || category == UnicodeCategory.Format
                || category == UnicodeCategory.Control)
            {
                return 0;
            }

            var codePoint = GetSelectorCodePoint(text, index);
            return IsSelectorWideCodePoint(codePoint) || IsSelectorAmbiguousWideCodePoint(codePoint) ? 2 : 1;
        }

        private static int GetSelectorCodePoint(string text, int index)
        {
            return GetSelectorCodePointLength(text, index) == 2
                ? char.ConvertToUtf32(text, index)
                : text[index];
        }

        private static bool IsSelectorWideCodePoint(int codePoint)
        {
            return (codePoint >= 0x1100 && codePoint <= 0x115F)
                   || codePoint == 0x2329
                   || codePoint == 0x232A
                   || (codePoint >= 0x2E80 && codePoint <= 0xA4CF)
                   || (codePoint >= 0xAC00 && codePoint <= 0xD7A3)
                   || (codePoint >= 0xF900 && codePoint <= 0xFAFF)
                   || (codePoint >= 0xFE10 && codePoint <= 0xFE19)
                   || (codePoint >= 0xFE30 && codePoint <= 0xFE6F)
                   || (codePoint >= 0xFF00 && codePoint <= 0xFF60)
                   || (codePoint >= 0xFFE0 && codePoint <= 0xFFE6)
                   || (codePoint >= 0x1F300 && codePoint <= 0x1FAFF)
                   || (codePoint >= 0x20000 && codePoint <= 0x3FFFD);
        }

        private static bool IsSelectorAmbiguousWideCodePoint(int codePoint)
        {
            return (codePoint >= 0x2010 && codePoint <= 0x2016) // hyphen through double vertical line, includes em dash
                   || (codePoint >= 0x2018 && codePoint <= 0x201F) // smart quotes
                   || (codePoint >= 0x2020 && codePoint <= 0x2027) // dagger, bullet, ellipsis
                   || (codePoint >= 0x2030 && codePoint <= 0x203E)
                   || (codePoint >= 0x2190 && codePoint <= 0x21FF) // arrows
                   || (codePoint >= 0x2200 && codePoint <= 0x22FF) // math symbols
                   || (codePoint >= 0x2460 && codePoint <= 0x24FF)
                   || (codePoint >= 0x2500 && codePoint <= 0x257F) // box drawing
                   || (codePoint >= 0x25A0 && codePoint <= 0x25FF)
                   || (codePoint >= 0x2600 && codePoint <= 0x27BF)
                   || codePoint == 0x00A1
                   || codePoint == 0x00A4
                   || codePoint == 0x00A7
                   || codePoint == 0x00A8
                   || codePoint == 0x00AA
                   || codePoint == 0x00AD
                   || codePoint == 0x00AE
                   || codePoint == 0x00B0
                   || codePoint == 0x00B1
                   || (codePoint >= 0x00B2 && codePoint <= 0x00B4)
                   || codePoint == 0x00B6
                   || codePoint == 0x00B7
                   || codePoint == 0x00B8
                   || codePoint == 0x00B9
                   || codePoint == 0x00BA
                   || (codePoint >= 0x00BC && codePoint <= 0x00BF)
                   || codePoint == 0x00C6
                   || codePoint == 0x00D0
                   || codePoint == 0x00D7
                   || codePoint == 0x00D8
                   || (codePoint >= 0x00DE && codePoint <= 0x00E1)
                   || codePoint == 0x00E6
                   || (codePoint >= 0x00E8 && codePoint <= 0x00EA)
                   || (codePoint >= 0x00EC && codePoint <= 0x00ED)
                   || codePoint == 0x00F0
                   || (codePoint >= 0x00F2 && codePoint <= 0x00F3)
                   || (codePoint >= 0x00F7 && codePoint <= 0x00FA)
                   || codePoint == 0x00FC
                   || codePoint == 0x00FE
                   || codePoint == 0x0101
                   || codePoint == 0x0111
                   || codePoint == 0x0113
                   || codePoint == 0x011B
                   || (codePoint >= 0x0126 && codePoint <= 0x0127)
                   || codePoint == 0x012B
                   || (codePoint >= 0x0131 && codePoint <= 0x0133)
                   || codePoint == 0x0138
                   || (codePoint >= 0x013F && codePoint <= 0x0142)
                   || codePoint == 0x0144
                   || (codePoint >= 0x0148 && codePoint <= 0x014B)
                   || codePoint == 0x014D
                   || (codePoint >= 0x0152 && codePoint <= 0x0153)
                   || (codePoint >= 0x0166 && codePoint <= 0x0167)
                   || codePoint == 0x016B
                   || codePoint == 0x01CE
                   || codePoint == 0x01D0
                   || codePoint == 0x01D2
                   || codePoint == 0x01D4
                   || codePoint == 0x01D6
                   || codePoint == 0x01D8
                   || codePoint == 0x01DA
                   || codePoint == 0x01DC
                   || codePoint == 0x0251
                   || codePoint == 0x0261
                   || codePoint == 0x02C4
                   || codePoint == 0x02C7
                   || (codePoint >= 0x02C9 && codePoint <= 0x02CB)
                   || codePoint == 0x02CD
                   || codePoint == 0x02D0
                   || (codePoint >= 0x02D8 && codePoint <= 0x02DB)
                   || codePoint == 0x02DD
                   || codePoint == 0x02DF
                   || (codePoint >= 0x0391 && codePoint <= 0x03A9)
                   || (codePoint >= 0x03B1 && codePoint <= 0x03C1)
                   || (codePoint >= 0x03C3 && codePoint <= 0x03C9)
                   || codePoint == 0x0401
                   || (codePoint >= 0x0410 && codePoint <= 0x044F)
                   || codePoint == 0x0451;
        }


        private static short GetSelectorAttribute(ConsoleColor foreground, ConsoleColor background)
        {
            return (short)(((int)foreground & 0x0F) | (((int)background & 0x0F) << 4));
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


        private const int SelectorHeaderLines = 2;
        private const int SelectorFooterLines = 1;
        private const int StdInputHandle = -10;
        private const int StdOutputHandle = -11;
        private const int EnableVirtualTerminalProcessing = 0x0004;
        private const int EnableMouseInput = 0x0010;
        private const int EnableWindowInput = 0x0008;
        private const int EnableQuickEditMode = 0x0040;
        private const int EnableExtendedFlags = 0x0080;
        private const ushort KeyEvent = 0x0001;
        private const ushort MouseEvent = 0x0002;
        private const uint LeftMostButtonPressed = 0x0001;
        private const uint DoubleClick = 0x0002;
        private const uint MouseWheeled = 0x0004;
        private const int WheelDelta = 120;
        private const uint RightAltPressed = 0x0001;
        private const uint LeftAltPressed = 0x0002;
        private const uint RightCtrlPressed = 0x0004;
        private const uint LeftCtrlPressed = 0x0008;
        private const uint ShiftPressed = 0x0010;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out int lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, int dwMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNumberOfConsoleInputEvents(IntPtr hConsoleInput, out uint lpcNumberOfEvents);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadConsoleInput(IntPtr hConsoleInput, out INPUT_RECORD lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PeekConsoleInput(IntPtr hConsoleInput, out INPUT_RECORD lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool WriteConsoleOutput(IntPtr hConsoleOutput, CHAR_INFO[] lpBuffer,
            COORD dwBufferSize, COORD dwBufferCoord, ref SMALL_RECT lpWriteRegion);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT_RECORD
        {
            public ushort EventType;
            public INPUT_RECORD_UNION Event;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT_RECORD_UNION
        {
            [FieldOffset(0)]
            public KEY_EVENT_RECORD KeyEvent;

            [FieldOffset(0)]
            public MOUSE_EVENT_RECORD MouseEvent;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEY_EVENT_RECORD
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool bKeyDown;
            public ushort wRepeatCount;
            public ushort wVirtualKeyCode;
            public ushort wVirtualScanCode;
            public char UnicodeChar;
            public uint dwControlKeyState;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSE_EVENT_RECORD
        {
            public COORD dwMousePosition;
            public uint dwButtonState;
            public uint dwControlKeyState;
            public uint dwEventFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        private struct CHAR_INFO
        {
            [FieldOffset(0)]
            public char UnicodeChar;

            [FieldOffset(2)]
            public short Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SMALL_RECT
        {
            public short Left;
            public short Top;
            public short Right;
            public short Bottom;
        }

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
