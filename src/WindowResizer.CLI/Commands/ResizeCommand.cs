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
        private const int SelectorResizePollMilliseconds = 50;
        private const int SelectorResizeStableMilliseconds = 200;

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
                renderState.Dispose();
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
            var resizePending = false;
            var pendingConsoleWidth = lastConsoleWidth;
            var pendingConsoleHeight = lastConsoleHeight;
            var pendingResizeTick = 0;

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

                var currentWidth = GetSafeConsoleWidth();
                var currentHeight = GetSafeConsoleHeight();
                if (currentWidth != lastConsoleWidth || currentHeight != lastConsoleHeight)
                {
                    var now = Environment.TickCount;

                    if (!resizePending || currentWidth != pendingConsoleWidth || currentHeight != pendingConsoleHeight)
                    {
                        pendingConsoleWidth = currentWidth;
                        pendingConsoleHeight = currentHeight;
                        pendingResizeTick = now;
                        resizePending = true;
                    }

                    // Do not redraw while the terminal border is still moving.
                    // Redraw once only after the reported size has remained the
                    // same for about 0.2 seconds.
                    if (unchecked(now - pendingResizeTick) < SelectorResizeStableMilliseconds)
                    {
                        HideSelectorCursor(usingAlternateScreen);
                        Thread.Sleep(SelectorResizePollMilliseconds);
                        continue;
                    }

                    lastConsoleWidth = pendingConsoleWidth;
                    lastConsoleHeight = pendingConsoleHeight;
                    resizePending = false;
                    HideSelectorCursor(usingAlternateScreen);
                    pageSize = GetSelectorPageSize();
                    RenderTargetWindowSelector(targets, selectedIndex, ref offset, pageSize, startTop,
                        highlightBackground, originalForeground, originalBackground, usingAlternateScreen, renderState);
                }
                else
                {
                    resizePending = false;
                }

                HideSelectorCursor(usingAlternateScreen);
                Thread.Sleep(SelectorResizePollMilliseconds);
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

            var viewportTop = GetSelectorViewportTop();
            var listRow = mouseEvent.dwMousePosition.Y - (viewportTop + startTop + SelectorHeaderLines);
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

        private static bool TryGetConsoleSizeChange(int lastWidth, int lastHeight, out int currentWidth, out int currentHeight)
        {
            currentWidth = GetSafeConsoleWidth();
            currentHeight = GetSafeConsoleHeight();

            return currentWidth != lastWidth || currentHeight != lastHeight;
        }

        private static int GetSafeConsoleWidth()
        {
            int left;
            int top;
            int width;
            int height;
            if (TryGetSelectorViewport(out left, out top, out width, out height))
            {
                return width;
            }

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
            int left;
            int top;
            int width;
            int height;
            if (TryGetSelectorViewport(out left, out top, out width, out height))
            {
                return height;
            }

            try
            {
                return Console.WindowHeight;
            }
            catch
            {
                return 0;
            }
        }

        private static int GetSelectorViewportTop()
        {
            int left;
            int top;
            int width;
            int height;
            return TryGetSelectorViewport(out left, out top, out width, out height) ? top : 0;
        }

        private static bool TryGetSelectorViewport(out int left, out int top, out int width, out int height)
        {
            left = 0;
            top = 0;
            width = 0;
            height = 0;

            try
            {
                var outputHandle = GetStdHandle(StdOutputHandle);
                if (outputHandle == IntPtr.Zero || outputHandle == new IntPtr(-1))
                {
                    return false;
                }

                CONSOLE_SCREEN_BUFFER_INFO info;
                if (!GetConsoleScreenBufferInfo(outputHandle, out info))
                {
                    return false;
                }

                left = info.srWindow.Left;
                top = info.srWindow.Top;
                width = info.srWindow.Right - info.srWindow.Left + 1;
                height = info.srWindow.Bottom - info.srWindow.Top + 1;
                return width > 0 && height > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryAnchorSelectorViewportToOrigin()
        {
            try
            {
                var outputHandle = GetStdHandle(StdOutputHandle);
                if (outputHandle == IntPtr.Zero || outputHandle == new IntPtr(-1))
                {
                    return false;
                }

                CONSOLE_SCREEN_BUFFER_INFO info;
                if (!GetConsoleScreenBufferInfo(outputHandle, out info))
                {
                    return false;
                }

                var width = info.srWindow.Right - info.srWindow.Left + 1;
                var height = info.srWindow.Bottom - info.srWindow.Top + 1;
                if (width <= 0 || height <= 0)
                {
                    return false;
                }

                // Windows Terminal/conhost can keep a larger screen buffer than
                // the visible terminal height. When the terminal is shrunk, the
                // viewport can slide down to old buffer rows, which makes stale
                // footer lines such as several old "Showing ..." messages appear.
                // Keep the alternate-screen selector viewport anchored at the
                // top-left of the screen buffer so rows are added/removed from the
                // bottom of the visible page instead of exposing old buffer content.
                if (info.srWindow.Left == 0 && info.srWindow.Top == 0)
                {
                    return true;
                }

                if (width > info.dwSize.X || height > info.dwSize.Y)
                {
                    return false;
                }

                var rect = new SMALL_RECT
                {
                    Left = 0,
                    Top = 0,
                    Right = (short)(width - 1),
                    Bottom = (short)(height - 1)
                };

                return SetConsoleWindowInfo(outputHandle, true, ref rect);
            }
            catch
            {
                return false;
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
                var pageSize = GetSafeConsoleHeight() - SelectorHeaderLines - SelectorFooterLines;
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
            TryAnchorSelectorViewportToOrigin();

            AdjustSelectorViewport(targets.Count, selectedIndex, pageSize, ref offset);

            var width = GetSafeSelectorWidth();
            var visibleCount = Math.Min(pageSize, Math.Max(0, targets.Count - offset));

            if (!renderState.HiddenRenderer.TryRender(targets, selectedIndex, offset, pageSize, startTop, width,
                    visibleCount, highlightBackground, originalForeground, originalBackground, useAnsiColors, renderState))
            {
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

        private static SelectorRowBuffer BuildSelectorBlankRow(int width, List<SelectorRowBuffer> currentRows)
        {
            var attribute = currentRows != null && currentRows.Count > 0 && currentRows[0].Cells.Length > 0
                ? currentRows[0].Cells[0].Attributes
                : GetSelectorAttribute(Console.ForegroundColor, Console.BackgroundColor);
            var cells = new CHAR_INFO[Math.Max(1, width)];
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i].UnicodeChar = ' ';
                cells[i].Attributes = attribute;
            }

            return new SelectorRowBuffer(cells);
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

        private static bool TryNormalizeSelectorVisibleBufferSize(IntPtr outputHandle)
        {
            if (outputHandle == IntPtr.Zero || outputHandle == new IntPtr(-1))
            {
                return false;
            }

            try
            {
                CONSOLE_SCREEN_BUFFER_INFO info;
                if (!GetConsoleScreenBufferInfo(outputHandle, out info))
                {
                    return false;
                }

                var width = info.srWindow.Right - info.srWindow.Left + 1;
                var height = info.srWindow.Bottom - info.srWindow.Top + 1;
                if (width <= 0 || height <= 0 || width > short.MaxValue || height > short.MaxValue)
                {
                    return false;
                }

                if (info.srWindow.Left != 0 || info.srWindow.Top != 0)
                {
                    var rect = new SMALL_RECT
                    {
                        Left = 0,
                        Top = 0,
                        Right = (short)(width - 1),
                        Bottom = (short)(height - 1)
                    };
                    SetConsoleWindowInfo(outputHandle, true, ref rect);
                }

                if (info.dwSize.X != width || info.dwSize.Y != height)
                {
                    var size = new COORD { X = (short)width, Y = (short)height };
                    SetConsoleScreenBufferSize(outputHandle, size);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWriteSelectorFrameCells(IntPtr outputHandle, CHAR_INFO[] cells, int startTop, int width, int rowCount)
        {
            if (cells == null || rowCount <= 0 || width <= 0 || cells.Length < width * rowCount)
            {
                return false;
            }

            try
            {
                int viewportLeft;
                int viewportTop;
                int viewportWidth;
                int viewportHeight;
                if (TryGetSelectorViewport(out viewportLeft, out viewportTop, out viewportWidth, out viewportHeight))
                {
                    if (startTop >= viewportHeight || width > viewportWidth)
                    {
                        return true;
                    }
                }
                else
                {
                    viewportLeft = 0;
                    viewportTop = 0;
                    viewportHeight = GetSafeConsoleHeight();
                }

                var writableRows = Math.Min(rowCount, Math.Max(0, viewportHeight - startTop));
                if (writableRows <= 0)
                {
                    return true;
                }

                var frameCells = cells;
                if (writableRows != rowCount)
                {
                    frameCells = new CHAR_INFO[width * writableRows];
                    Array.Copy(cells, frameCells, frameCells.Length);
                }

                var absoluteTop = viewportTop + startTop;
                var bufferSize = new COORD { X = (short)width, Y = (short)writableRows };
                var bufferCoord = new COORD { X = 0, Y = 0 };
                var writeRegion = new SMALL_RECT
                {
                    Left = (short)viewportLeft,
                    Top = (short)absoluteTop,
                    Right = (short)(viewportLeft + width - 1),
                    Bottom = (short)(absoluteTop + writableRows - 1)
                };

                return WriteConsoleOutput(outputHandle, frameCells, bufferSize, bufferCoord, ref writeRegion);
            }
            catch
            {
                return false;
            }
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

            // If the selector geometry changed, copy one complete frame, not a
            // collection of individually changed rows. This frame includes the
            // header, all visible list rows, and the footer/status message. It
            // prevents stale footer rows such as multiple old "Showing ..." lines
            // from surviving terminal height changes.
            var geometryChanged = renderState.IsInvalid
                                  || oldRows == null
                                  || oldRows.Count != rows.Count
                                  || renderState.LastWidth != width;
            if (geometryChanged)
            {
                return TryWriteSelectorFrameBuffer(outputHandle, rows, startTop, width);
            }

            var maxWritableRows = GetSafeConsoleHeight();
            for (var i = 0; i < rows.Count; i++)
            {
                var oldRow = i < oldRows.Count ? oldRows[i] : null;
                if (rows[i].EqualsCells(oldRow))
                {
                    continue;
                }

                if (startTop + i >= maxWritableRows)
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

        private static bool TryWriteSelectorFrameBuffer(IntPtr outputHandle, List<SelectorRowBuffer> rows, int startTop, int width)
        {
            if (rows == null || rows.Count == 0 || startTop < 0 || width <= 0)
            {
                return false;
            }

            try
            {
                int viewportLeft;
                int viewportTop;
                int viewportWidth;
                int viewportHeight;
                if (TryGetSelectorViewport(out viewportLeft, out viewportTop, out viewportWidth, out viewportHeight))
                {
                    if (startTop >= viewportHeight || width > viewportWidth)
                    {
                        return true;
                    }
                }
                else
                {
                    viewportLeft = 0;
                    viewportTop = 0;
                    viewportHeight = GetSafeConsoleHeight();
                }

                var writableRows = Math.Min(rows.Count, Math.Max(0, viewportHeight - startTop));
                if (writableRows <= 0)
                {
                    return true;
                }

                var cells = new CHAR_INFO[width * writableRows];
                for (var row = 0; row < writableRows; row++)
                {
                    var source = rows[row].Cells;
                    Array.Copy(source, 0, cells, row * width, width);
                }

                var absoluteTop = viewportTop + startTop;
                var bufferSize = new COORD { X = (short)width, Y = (short)writableRows };
                var bufferCoord = new COORD { X = 0, Y = 0 };
                var writeRegion = new SMALL_RECT
                {
                    Left = (short)viewportLeft,
                    Top = (short)absoluteTop,
                    Right = (short)(viewportLeft + width - 1),
                    Bottom = (short)(absoluteTop + writableRows - 1)
                };

                return WriteConsoleOutput(outputHandle, cells, bufferSize, bufferCoord, ref writeRegion);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWriteSelectorRowBuffer(IntPtr outputHandle, SelectorRowBuffer row, int top, int width)
        {
            if (top < 0 || width <= 0 || row == null || row.Cells.Length != width)
            {
                return false;
            }

            try
            {
                int viewportLeft;
                int viewportTop;
                int viewportWidth;
                int viewportHeight;
                if (TryGetSelectorViewport(out viewportLeft, out viewportTop, out viewportWidth, out viewportHeight))
                {
                    if (top >= viewportHeight || width > viewportWidth)
                    {
                        return true;
                    }
                }
                else
                {
                    viewportLeft = 0;
                    viewportTop = 0;
                }

                var absoluteTop = viewportTop + top;
                var bufferSize = new COORD { X = (short)width, Y = 1 };
                var bufferCoord = new COORD { X = 0, Y = 0 };
                var writeRegion = new SMALL_RECT
                {
                    Left = (short)viewportLeft,
                    Top = (short)absoluteTop,
                    Right = (short)(viewportLeft + width - 1),
                    Bottom = (short)absoluteTop
                };

                return WriteConsoleOutput(outputHandle, row.Cells, bufferSize, bufferCoord, ref writeRegion);
            }
            catch
            {
                return false;
            }
        }

        private static void RenderTargetWindowSelectorFull(List<WindowCmd.TargetWindow> targets, int selectedIndex, int offset,
            int pageSize, int startTop, int width, int visibleCount, ConsoleColor highlightBackground,
            ConsoleColor originalForeground, ConsoleColor originalBackground, bool useAnsiColors)
        {
            var row = startTop;
            SetSelectorColors(originalForeground, originalBackground, useAnsiColors);
            WriteSelectorLine(row++, "Select a window/application:", width, useAnsiColors);
            SetSelectorColors(originalForeground, originalBackground, useAnsiColors);
            WriteSelectorLine(row++, "Use ↑/↓, PgUp/PgDn, Home/End, letter keys, mouse wheel, double-click, Enter, Esc.", width, useAnsiColors);

            for (var i = 0; i < pageSize; i++)
            {
                RedrawSelectorListRow(targets, offset + i, selectedIndex, row++, width,
                    highlightBackground, originalForeground, originalBackground, useAnsiColors);
            }

            SetSelectorColors(originalForeground, originalBackground, useAnsiColors);
            var footer = targets.Count > visibleCount
                ? $"Showing {offset + 1}-{offset + visibleCount} of {targets.Count}."
                : $"Showing {targets.Count} window(s).";
            WriteSelectorLine(row, footer, width, useAnsiColors);
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
                var previousRowCount = renderState.LastPageSize > 0
                    ? SelectorHeaderLines + renderState.LastPageSize + SelectorFooterLines
                    : 0;
                var currentRowCount = SelectorHeaderLines + pageSize + SelectorFooterLines;

                RenderTargetWindowSelectorFull(targets, selectedIndex, offset, pageSize, startTop,
                    width, visibleCount, highlightBackground, originalForeground, originalBackground, useAnsiColors);

                if (previousRowCount > currentRowCount)
                {
                    ClearSelectorRows(startTop + currentRowCount, previousRowCount - currentRowCount,
                        width, originalForeground, originalBackground, useAnsiColors);
                }
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

        private static void RedrawSelectorRowIfVisible(List<WindowCmd.TargetWindow> targets, int targetIndex, int selectedIndex,
            int offset, int pageSize, int startTop, int width, ConsoleColor highlightBackground,
            ConsoleColor originalForeground, ConsoleColor originalBackground, bool useAnsiColors)
        {
            if (targetIndex < offset || targetIndex >= offset + pageSize)
            {
                return;
            }

            var row = startTop + SelectorHeaderLines + (targetIndex - offset);
            RedrawSelectorListRow(targets, targetIndex, selectedIndex, row, width,
                highlightBackground, originalForeground, originalBackground, useAnsiColors);
        }

        private static void RedrawSelectorListRow(List<WindowCmd.TargetWindow> targets, int targetIndex, int selectedIndex,
            int row, int width, ConsoleColor highlightBackground, ConsoleColor originalForeground,
            ConsoleColor originalBackground, bool useAnsiColors)
        {
            if (!TrySetSelectorCursorPosition(0, row))
            {
                return;
            }

            var selected = targetIndex == selectedIndex;
            var rowBackground = selected ? highlightBackground : originalBackground;
            SetSelectorColors(originalForeground, rowBackground, useAnsiColors);
            ClearSelectorCurrentLine(useAnsiColors);

            if (targetIndex < targets.Count)
            {
                WriteTargetWindowSelectorLine(targets[targetIndex], width, selected, originalForeground, rowBackground, useAnsiColors);
            }
            else
            {
                WriteSelectorLine(string.Empty, width, useAnsiColors);
            }
        }

        private sealed class SelectorHiddenBufferRenderer : IDisposable
        {
            private const int InvalidSelectedIndex = -1;

            private IntPtr hiddenHandle = IntPtr.Zero;
            private CHAR_INFO[] hiddenCache;
            private int hiddenWidth;
            private int hiddenHeight;
            private int cachedSelectedIndex = InvalidSelectedIndex;
            private ConsoleColor cachedHighlightBackground;
            private ConsoleColor cachedOriginalForeground;
            private ConsoleColor cachedOriginalBackground;
            private bool cacheValid;

            public bool TryRender(List<WindowCmd.TargetWindow> targets, int selectedIndex, int offset, int pageSize,
                int startTop, int width, int visibleCount, ConsoleColor highlightBackground,
                ConsoleColor originalForeground, ConsoleColor originalBackground, bool usingAlternateScreen,
                SelectorRenderState renderState)
            {
                if (!usingAlternateScreen || targets == null || targets.Count == 0 || width <= 0 || pageSize <= 0)
                {
                    return false;
                }

                var outputHandle = GetStdHandle(StdOutputHandle);
                if (outputHandle == IntPtr.Zero || outputHandle == new IntPtr(-1))
                {
                    return false;
                }

                try
                {
                    TryNormalizeSelectorVisibleBufferSize(outputHandle);
                    width = GetSafeSelectorWidth();
                    if (width <= 0)
                    {
                        return false;
                    }

                    if (!EnsureHiddenCache(targets, selectedIndex, width, highlightBackground,
                            originalForeground, originalBackground))
                    {
                        return false;
                    }

                    var previousOffset = renderState.LastOffset;
                    var previousSelectedIndex = renderState.LastSelectedIndex;
                    var geometryChanged = renderState.IsInvalid
                                          || renderState.LastWidth != width
                                          || renderState.LastPageSize != pageSize
                                          || renderState.LastTargetCount != targets.Count;

                    var rendered = false;
                    if (!geometryChanged && previousOffset == offset)
                    {
                        rendered = TryPatchChangedSelectionRows(outputHandle, targets.Count, previousSelectedIndex,
                            selectedIndex, offset, pageSize, startTop, width);
                    }
                    else if (!geometryChanged && previousOffset + 1 == offset)
                    {
                        rendered = TryScrollVisibleBody(outputHandle, startTop, width, pageSize, -1)
                                   && TryWriteHiddenListRowToVisible(outputHandle, offset + pageSize - 1,
                                       startTop + SelectorHeaderLines + pageSize - 1, width)
                                   && TryPatchChangedSelectionRows(outputHandle, targets.Count, previousSelectedIndex,
                                       selectedIndex, offset, pageSize, startTop, width)
                                   && TryWriteSelectorFooter(outputHandle, targets.Count, offset, visibleCount, pageSize,
                                       startTop, width, originalForeground, originalBackground);
                    }
                    else if (!geometryChanged && previousOffset - 1 == offset)
                    {
                        rendered = TryScrollVisibleBody(outputHandle, startTop, width, pageSize, 1)
                                   && TryWriteHiddenListRowToVisible(outputHandle, offset,
                                       startTop + SelectorHeaderLines, width)
                                   && TryPatchChangedSelectionRows(outputHandle, targets.Count, previousSelectedIndex,
                                       selectedIndex, offset, pageSize, startTop, width)
                                   && TryWriteSelectorFooter(outputHandle, targets.Count, offset, visibleCount, pageSize,
                                       startTop, width, originalForeground, originalBackground);
                    }

                    if (!rendered)
                    {
                        rendered = TryWriteFullFrameFromHiddenCache(outputHandle, targets.Count, offset, pageSize,
                            startTop, width, visibleCount, originalForeground, originalBackground);
                    }

                    if (rendered)
                    {
                        renderState.Update(selectedIndex, offset, pageSize, width, targets.Count, null);
                    }

                    return rendered;
                }
                catch
                {
                    return false;
                }
            }

            public void Dispose()
            {
                if (hiddenHandle != IntPtr.Zero && hiddenHandle != new IntPtr(-1))
                {
                    CloseHandle(hiddenHandle);
                }

                hiddenHandle = IntPtr.Zero;
                hiddenCache = null;
                cacheValid = false;
            }

            private bool EnsureHiddenCache(List<WindowCmd.TargetWindow> targets, int selectedIndex, int width,
                ConsoleColor highlightBackground, ConsoleColor originalForeground, ConsoleColor originalBackground)
            {
                var requiredHeight = Math.Max(1, SelectorHeaderLines + targets.Count);
                var colorsChanged = cachedHighlightBackground != highlightBackground
                                    || cachedOriginalForeground != originalForeground
                                    || cachedOriginalBackground != originalBackground;

                if (!cacheValid || hiddenHandle == IntPtr.Zero || hiddenHandle == new IntPtr(-1)
                    || hiddenWidth != width || hiddenHeight != requiredHeight || colorsChanged)
                {
                    return RebuildHiddenCache(targets, selectedIndex, width, requiredHeight,
                        highlightBackground, originalForeground, originalBackground);
                }

                if (cachedSelectedIndex != selectedIndex)
                {
                    var oldSelectedIndex = cachedSelectedIndex;
                    cachedSelectedIndex = selectedIndex;

                    if (oldSelectedIndex >= 0 && oldSelectedIndex < targets.Count)
                    {
                        if (!RewriteHiddenListRowAndCache(targets, oldSelectedIndex, selectedIndex,
                                highlightBackground, originalForeground, originalBackground))
                        {
                            return false;
                        }
                    }

                    if (selectedIndex >= 0 && selectedIndex < targets.Count)
                    {
                        if (!RewriteHiddenListRowAndCache(targets, selectedIndex, selectedIndex,
                                highlightBackground, originalForeground, originalBackground))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            private bool RebuildHiddenCache(List<WindowCmd.TargetWindow> targets, int selectedIndex, int width, int height,
                ConsoleColor highlightBackground, ConsoleColor originalForeground, ConsoleColor originalBackground)
            {
                Dispose();

                hiddenHandle = CreateConsoleScreenBuffer(GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                    IntPtr.Zero, ConsoleTextmodeBuffer, IntPtr.Zero);
                if (hiddenHandle == IntPtr.Zero || hiddenHandle == new IntPtr(-1))
                {
                    return false;
                }

                int mode;
                if (GetConsoleMode(hiddenHandle, out mode))
                {
                    SetConsoleMode(hiddenHandle, mode & ~EnableWrapAtEolOutput);
                }

                var size = new COORD { X = (short)Math.Min(short.MaxValue, Math.Max(1, width)), Y = (short)Math.Min(short.MaxValue, Math.Max(1, height)) };
                if (!SetConsoleScreenBufferSize(hiddenHandle, size))
                {
                    Dispose();
                    return false;
                }

                hiddenWidth = size.X;
                hiddenHeight = size.Y;
                cachedSelectedIndex = selectedIndex;
                cachedHighlightBackground = highlightBackground;
                cachedOriginalForeground = originalForeground;
                cachedOriginalBackground = originalBackground;

                WriteHiddenTextRow(0, "Select a window/application:", originalForeground, originalBackground);
                WriteHiddenTextRow(1, "Use ↑/↓, PgUp/PgDn, Home/End, letter keys, mouse wheel, double-click, Enter, Esc.",
                    originalForeground, originalBackground);

                for (var i = 0; i < targets.Count; i++)
                {
                    WriteHiddenListRow(targets[i], i, selectedIndex, highlightBackground,
                        originalForeground, originalBackground);
                }

                if (!ReadWholeHiddenCache())
                {
                    Dispose();
                    return false;
                }

                NormalizeWholeHiddenCache(targets, selectedIndex, highlightBackground,
                    originalForeground, originalBackground);

                cacheValid = true;
                return true;
            }

            private bool RewriteHiddenListRowAndCache(List<WindowCmd.TargetWindow> targets, int targetIndex, int selectedIndex,
                ConsoleColor highlightBackground, ConsoleColor originalForeground, ConsoleColor originalBackground)
            {
                if (targetIndex < 0 || targetIndex >= targets.Count)
                {
                    return true;
                }

                WriteHiddenListRow(targets[targetIndex], targetIndex, selectedIndex, highlightBackground,
                    originalForeground, originalBackground);

                if (!ReadHiddenCacheRows(SelectorHeaderLines + targetIndex, 1))
                {
                    return false;
                }

                NormalizeHiddenCacheListRow(targets, targetIndex, selectedIndex, highlightBackground,
                    originalForeground, originalBackground);
                return true;
            }

            private void WriteHiddenTextRow(int row, string text, ConsoleColor foreground, ConsoleColor background)
            {
                ClearHiddenRow(row, foreground, background);
                SetHiddenCursor(0, row);
                WriteHiddenSegment(text, foreground, background);
            }

            private void WriteHiddenListRow(WindowCmd.TargetWindow target, int targetIndex, int selectedIndex,
                ConsoleColor highlightBackground, ConsoleColor originalForeground, ConsoleColor originalBackground)
            {
                var selected = targetIndex == selectedIndex;
                var rowBackground = selected ? highlightBackground : originalBackground;
                var processForeground = selected ? originalForeground : ConsoleColor.Green;
                var mutedForeground = selected ? originalForeground : ConsoleColor.DarkGray;
                var titleForeground = originalForeground;
                var title = string.IsNullOrWhiteSpace(target.Title) ? "(no title)" : target.Title;
                var row = SelectorHeaderLines + targetIndex;

                ClearHiddenRow(row, originalForeground, rowBackground);
                SetHiddenCursor(0, row);

                WriteHiddenSegment(selected ? "> " : "  ", titleForeground, rowBackground);
                WriteHiddenSegment(target.ProcessName, processForeground, rowBackground);
                WriteHiddenProcessInfo(target, selected, processForeground, mutedForeground, rowBackground);
                WriteHiddenSegment(" | ", mutedForeground, rowBackground);
                WriteHiddenSegment(title, titleForeground, rowBackground);
                WriteHiddenSegment($" (0x{target.Handle.ToInt64():X})", mutedForeground, rowBackground);
            }

            private void WriteHiddenProcessInfo(WindowCmd.TargetWindow target, bool selected,
                ConsoleColor topForeground, ConsoleColor mutedForeground, ConsoleColor background)
            {
                if (target.ProcessId <= 0 && !target.IsTopForProcess)
                {
                    return;
                }

                var pidForeground = selected ? topForeground : mutedForeground;
                WriteHiddenSegment(" [", mutedForeground, background);

                if (target.ProcessId > 0)
                {
                    WriteHiddenSegment(target.ProcessId.ToString(), pidForeground, background);

                    if (target.IsTopForProcess)
                    {
                        WriteHiddenSegment(" ", mutedForeground, background);
                    }
                }

                if (target.IsTopForProcess)
                {
                    WriteHiddenSegment("Top", topForeground, background);
                }

                WriteHiddenSegment("]", mutedForeground, background);
            }

            private void ClearHiddenRow(int row, ConsoleColor foreground, ConsoleColor background)
            {
                if (row < 0 || row >= hiddenHeight)
                {
                    return;
                }

                var coord = new COORD { X = 0, Y = (short)row };
                uint written;
                FillConsoleOutputCharacter(hiddenHandle, ' ', (uint)hiddenWidth, coord, out written);
                FillConsoleOutputAttribute(hiddenHandle, (ushort)GetSelectorAttribute(foreground, background),
                    (uint)hiddenWidth, coord, out written);
            }

            private void SetHiddenCursor(int left, int top)
            {
                var coord = new COORD { X = (short)Math.Max(0, left), Y = (short)Math.Max(0, top) };
                SetConsoleCursorPosition(hiddenHandle, coord);
            }

            private void WriteHiddenSegment(string text, ConsoleColor foreground, ConsoleColor background)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                text = NormalizeSelectorText(text);
                SetConsoleTextAttribute(hiddenHandle, (ushort)GetSelectorAttribute(foreground, background));
                uint written;
                WriteConsole(hiddenHandle, text, (uint)text.Length, out written, IntPtr.Zero);
            }

            private bool ReadWholeHiddenCache()
            {
                hiddenCache = new CHAR_INFO[hiddenWidth * hiddenHeight];
                return ReadHiddenCacheRows(0, hiddenHeight);
            }

            private bool ReadHiddenCacheRows(int startRow, int count)
            {
                if (hiddenCache == null || startRow < 0 || count <= 0 || startRow + count > hiddenHeight)
                {
                    return false;
                }

                var temp = new CHAR_INFO[hiddenWidth * count];
                var bufferSize = new COORD { X = (short)hiddenWidth, Y = (short)count };
                var bufferCoord = new COORD { X = 0, Y = 0 };
                var readRegion = new SMALL_RECT
                {
                    Left = 0,
                    Top = (short)startRow,
                    Right = (short)(hiddenWidth - 1),
                    Bottom = (short)(startRow + count - 1)
                };

                if (!ReadConsoleOutput(hiddenHandle, temp, bufferSize, bufferCoord, ref readRegion))
                {
                    return false;
                }

                Array.Copy(temp, 0, hiddenCache, startRow * hiddenWidth, temp.Length);
                return true;
            }

            private void NormalizeWholeHiddenCache(List<WindowCmd.TargetWindow> targets, int selectedIndex,
                ConsoleColor highlightBackground, ConsoleColor originalForeground, ConsoleColor originalBackground)
            {
                NormalizeHiddenCacheRow(0, BuildSelectorTextRow("Select a window/application:", hiddenWidth,
                    originalForeground, originalBackground));
                NormalizeHiddenCacheRow(1, BuildSelectorTextRow(
                    "Use ↑/↓, PgUp/PgDn, Home/End, letter keys, mouse wheel, double-click, Enter, Esc.",
                    hiddenWidth, originalForeground, originalBackground));

                for (var i = 0; i < targets.Count; i++)
                {
                    NormalizeHiddenCacheListRow(targets, i, selectedIndex, highlightBackground,
                        originalForeground, originalBackground);
                }
            }

            private void NormalizeHiddenCacheListRow(List<WindowCmd.TargetWindow> targets, int targetIndex, int selectedIndex,
                ConsoleColor highlightBackground, ConsoleColor originalForeground, ConsoleColor originalBackground)
            {
                NormalizeHiddenCacheRow(SelectorHeaderLines + targetIndex,
                    BuildSelectorListRow(targets, targetIndex, selectedIndex, hiddenWidth, highlightBackground,
                        originalForeground, originalBackground));
            }

            private void NormalizeHiddenCacheRow(int hiddenRow, SelectorRowBuffer manualRow)
            {
                if (hiddenCache == null || manualRow == null || manualRow.Cells == null
                    || hiddenRow < 0 || hiddenRow >= hiddenHeight)
                {
                    return;
                }

                var baseIndex = hiddenRow * hiddenWidth;
                var width = Math.Min(hiddenWidth, manualRow.Cells.Length);
                var hiddenHasText = false;

                for (var i = 0; i < hiddenWidth; i++)
                {
                    var ch = hiddenCache[baseIndex + i].UnicodeChar;
                    if (ch != '\0' && ch != ' ')
                    {
                        hiddenHasText = true;
                        break;
                    }
                }

                for (var i = 0; i < width; i++)
                {
                    var index = baseIndex + i;

                    // The hidden buffer gives us the console-rendered character cells,
                    // which fixes the old Unicode overlap problem.  Some classic
                    // conhost combinations, however, can return invisible attributes
                    // from a non-active screen buffer.  Keep the hidden-buffer
                    // characters, but use the selector's known attributes.  If the
                    // hidden row came back empty, fall back to the manual row text
                    // rather than leaving the selector blank.
                    if (!hiddenHasText || hiddenCache[index].UnicodeChar == '\0')
                    {
                        hiddenCache[index].UnicodeChar = manualRow.Cells[i].UnicodeChar;
                    }

                    if (hiddenCache[index].UnicodeChar == '\0')
                    {
                        hiddenCache[index].UnicodeChar = ' ';
                    }

                    hiddenCache[index].Attributes = manualRow.Cells[i].Attributes;
                }

                for (var i = width; i < hiddenWidth; i++)
                {
                    var index = baseIndex + i;
                    if (hiddenCache[index].UnicodeChar == '\0')
                    {
                        hiddenCache[index].UnicodeChar = ' ';
                    }
                }
            }

            private bool TryWriteFullFrameFromHiddenCache(IntPtr outputHandle, int targetCount, int offset, int pageSize,
                int startTop, int width, int visibleCount, ConsoleColor originalForeground, ConsoleColor originalBackground)
            {
                var totalRows = SelectorHeaderLines + pageSize + SelectorFooterLines;
                var frame = new CHAR_INFO[Math.Max(1, width * totalRows)];
                var blankAttribute = GetSelectorAttribute(originalForeground, originalBackground);

                for (var i = 0; i < frame.Length; i++)
                {
                    frame[i].UnicodeChar = ' ';
                    frame[i].Attributes = blankAttribute;
                }

                CopyHiddenRowToFrame(0, frame, 0, width);
                CopyHiddenRowToFrame(1, frame, 1, width);

                for (var i = 0; i < pageSize; i++)
                {
                    var targetIndex = offset + i;
                    if (targetIndex >= 0 && targetIndex < targetCount)
                    {
                        CopyHiddenRowToFrame(SelectorHeaderLines + targetIndex, frame, SelectorHeaderLines + i, width);
                    }
                }

                var footer = BuildSelectorFooterRow(targetCount, offset, visibleCount, width,
                    originalForeground, originalBackground);
                Array.Copy(footer.Cells, 0, frame, (SelectorHeaderLines + pageSize) * width, width);

                return TryWriteSelectorFrameCells(outputHandle, frame, startTop, width, totalRows);
            }

            private void CopyHiddenRowToFrame(int hiddenRow, CHAR_INFO[] frame, int frameRow, int width)
            {
                if (hiddenCache == null || hiddenRow < 0 || hiddenRow >= hiddenHeight || frameRow < 0)
                {
                    return;
                }

                Array.Copy(hiddenCache, hiddenRow * hiddenWidth, frame, frameRow * width, Math.Min(width, hiddenWidth));
            }

            private bool TryPatchChangedSelectionRows(IntPtr outputHandle, int targetCount, int oldSelectedIndex,
                int selectedIndex, int offset, int pageSize, int startTop, int width)
            {
                var ok = true;
                ok &= TryWriteVisibleListRowIfInPage(outputHandle, targetCount, oldSelectedIndex, offset, pageSize, startTop, width);
                if (selectedIndex != oldSelectedIndex)
                {
                    ok &= TryWriteVisibleListRowIfInPage(outputHandle, targetCount, selectedIndex, offset, pageSize, startTop, width);
                }

                return ok;
            }

            private bool TryWriteVisibleListRowIfInPage(IntPtr outputHandle, int targetCount, int targetIndex,
                int offset, int pageSize, int startTop, int width)
            {
                if (targetIndex < offset || targetIndex >= offset + pageSize || targetIndex < 0 || targetIndex >= targetCount)
                {
                    return true;
                }

                return TryWriteHiddenListRowToVisible(outputHandle, targetIndex,
                    startTop + SelectorHeaderLines + targetIndex - offset, width);
            }

            private bool TryWriteHiddenListRowToVisible(IntPtr outputHandle, int targetIndex, int visibleRow, int width)
            {
                var hiddenRow = SelectorHeaderLines + targetIndex;
                if (hiddenCache == null || hiddenRow < 0 || hiddenRow >= hiddenHeight)
                {
                    return true;
                }

                var cells = new CHAR_INFO[width];
                Array.Copy(hiddenCache, hiddenRow * hiddenWidth, cells, 0, Math.Min(width, hiddenWidth));
                return TryWriteSelectorRowBuffer(outputHandle, new SelectorRowBuffer(cells), visibleRow, width);
            }

            private bool TryWriteSelectorFooter(IntPtr outputHandle, int targetCount, int offset, int visibleCount,
                int pageSize, int startTop, int width, ConsoleColor originalForeground, ConsoleColor originalBackground)
            {
                var footer = BuildSelectorFooterRow(targetCount, offset, visibleCount, width,
                    originalForeground, originalBackground);
                return TryWriteSelectorRowBuffer(outputHandle, footer, startTop + SelectorHeaderLines + pageSize, width);
            }

            private SelectorRowBuffer BuildSelectorFooterRow(int targetCount, int offset, int visibleCount, int width,
                ConsoleColor originalForeground, ConsoleColor originalBackground)
            {
                var footer = targetCount > visibleCount
                    ? $"Showing {offset + 1}-{offset + visibleCount} of {targetCount}."
                    : $"Showing {targetCount} window(s).";
                return BuildSelectorTextRow(footer, width, originalForeground, originalBackground);
            }

            private bool TryScrollVisibleBody(IntPtr outputHandle, int startTop, int width, int pageSize, int direction)
            {
                if (pageSize <= 1 || width <= 0)
                {
                    return true;
                }

                int viewportLeft;
                int viewportTop;
                int viewportWidth;
                int viewportHeight;
                if (!TryGetSelectorViewport(out viewportLeft, out viewportTop, out viewportWidth, out viewportHeight))
                {
                    viewportLeft = 0;
                    viewportTop = 0;
                }

                var bodyTop = viewportTop + startTop + SelectorHeaderLines;
                var bodyBottom = bodyTop + pageSize - 1;
                var bodyLeft = viewportLeft;
                var bodyRight = viewportLeft + width - 1;

                SMALL_RECT source;
                COORD destination;

                if (direction < 0)
                {
                    source = new SMALL_RECT
                    {
                        Left = (short)bodyLeft,
                        Top = (short)(bodyTop + 1),
                        Right = (short)bodyRight,
                        Bottom = (short)bodyBottom
                    };
                    destination = new COORD { X = (short)bodyLeft, Y = (short)bodyTop };
                }
                else
                {
                    source = new SMALL_RECT
                    {
                        Left = (short)bodyLeft,
                        Top = (short)bodyTop,
                        Right = (short)bodyRight,
                        Bottom = (short)(bodyBottom - 1)
                    };
                    destination = new COORD { X = (short)bodyLeft, Y = (short)(bodyTop + 1) };
                }

                var fill = new CHAR_INFO
                {
                    UnicodeChar = ' ',
                    Attributes = GetSelectorAttribute(cachedOriginalForeground, cachedOriginalBackground)
                };

                return ScrollConsoleScreenBuffer(outputHandle, ref source, IntPtr.Zero, destination, ref fill);
            }
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

        private sealed class SelectorRenderState : IDisposable
        {
            public SelectorHiddenBufferRenderer HiddenRenderer { get; } = new SelectorHiddenBufferRenderer();

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

            public void Dispose()
            {
                HiddenRenderer.Dispose();
            }
        }

        private static void ClearSelectorRows(int startRow, int count, int width,
            ConsoleColor foreground, ConsoleColor background, bool useAnsiColors)
        {
            if (count <= 0 || width <= 0)
            {
                return;
            }

            SetSelectorColors(foreground, background, useAnsiColors);
            var maxRows = GetSafeConsoleHeight();
            for (var i = 0; i < count; i++)
            {
                var row = startRow + i;
                if (row < 0 || row >= maxRows)
                {
                    continue;
                }

                WriteSelectorLine(row, string.Empty, width, useAnsiColors);
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
                int viewportLeft;
                int viewportTop;
                int viewportWidth;
                int viewportHeight;
                if (TryGetSelectorViewport(out viewportLeft, out viewportTop, out viewportWidth, out viewportHeight))
                {
                    left += viewportLeft;
                    top += viewportTop;
                }

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
        private const int EnableWrapAtEolOutput = 0x0002;
        private const int EnableExtendedFlags = 0x0080;
        private const ushort KeyEvent = 0x0001;
        private const ushort MouseEvent = 0x0002;
        private const uint LeftMostButtonPressed = 0x0001;
        private const uint DoubleClick = 0x0002;
        private const uint MouseWheeled = 0x0004;
        private const int WheelDelta = 120;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint ConsoleTextmodeBuffer = 0x00000001;
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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ReadConsoleOutput(IntPtr hConsoleOutput, CHAR_INFO[] lpBuffer,
            COORD dwBufferSize, COORD dwBufferCoord, ref SMALL_RECT lpReadRegion);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WriteConsoleW")]
        private static extern bool WriteConsole(IntPtr hConsoleOutput, string lpBuffer,
            uint nNumberOfCharsToWrite, out uint lpNumberOfCharsWritten, IntPtr lpReserved);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleTextAttribute(IntPtr hConsoleOutput, ushort wAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCursorPosition(IntPtr hConsoleOutput, COORD dwCursorPosition);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool FillConsoleOutputCharacter(IntPtr hConsoleOutput, char cCharacter,
            uint nLength, COORD dwWriteCoord, out uint lpNumberOfCharsWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FillConsoleOutputAttribute(IntPtr hConsoleOutput, ushort wAttribute,
            uint nLength, COORD dwWriteCoord, out uint lpNumberOfAttrsWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateConsoleScreenBuffer(uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwFlags, IntPtr lpScreenBufferData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleScreenBufferSize(IntPtr hConsoleOutput, COORD dwSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ScrollConsoleScreenBuffer(IntPtr hConsoleOutput, ref SMALL_RECT lpScrollRectangle,
            IntPtr lpClipRectangle, COORD dwDestinationOrigin, ref CHAR_INFO lpFill);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleWindowInfo(IntPtr hConsoleOutput, bool bAbsolute, ref SMALL_RECT lpConsoleWindow);

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

        [StructLayout(LayoutKind.Sequential)]
        private struct CONSOLE_SCREEN_BUFFER_INFO
        {
            public COORD dwSize;
            public COORD dwCursorPosition;
            public short wAttributes;
            public SMALL_RECT srWindow;
            public COORD dwMaximumWindowSize;
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
