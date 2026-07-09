#define UNICODE
#define _UNICODE
#define NOMINMAX

#include <windows.h>
#include <algorithm>
#include <cwctype>
#include <cstdint>
#include <string>
#include <utility>
#include <vector>

#ifndef ENABLE_VIRTUAL_TERMINAL_PROCESSING
#define ENABLE_VIRTUAL_TERMINAL_PROCESSING 0x0004
#endif

#ifndef DISABLE_NEWLINE_AUTO_RETURN
#define DISABLE_NEWLINE_AUTO_RETURN 0x0008
#endif

#ifndef ENABLE_QUICK_EDIT_MODE
#define ENABLE_QUICK_EDIT_MODE 0x0040
#endif

#ifndef ENABLE_EXTENDED_FLAGS
#define ENABLE_EXTENDED_FLAGS 0x0080
#endif

#ifndef ENABLE_MOUSE_INPUT
#define ENABLE_MOUSE_INPUT 0x0010
#endif

struct NativeSelectorRow
{
    int Sequence;
    int ProcessId;
    intptr_t WindowHandle;
    int IsTopForProcess;
    const wchar_t* DisplayText;
};

namespace
{
    enum class Style
    {
        Normal,
        Header,
        Selected,
        SelectedMarker,
        ProcessName,
        ProcessNameSelected,
        TopProcess,
        TopProcessSelected,
        Handle,
        HandleSelected,
        Status
    };

    struct Row
    {
        int sequence = 0;
        int processId = 0;
        intptr_t windowHandle = 0;
        bool isTop = false;
        std::wstring text;
    };

    struct Segment
    {
        std::wstring text;
        Style style = Style::Normal;
    };

    struct ConsoleState
    {
        HANDLE out = INVALID_HANDLE_VALUE;
        HANDLE in = INVALID_HANDLE_VALUE;
        DWORD originalOutMode = 0;
        DWORD originalInMode = 0;
        CONSOLE_CURSOR_INFO originalCursor{};
        bool haveOriginalCursor = false;
        bool vtEnabled = false;
        bool altScreen = false;
    };

    static int ClampInt(int value, int lo, int hi)
    {
        if (value < lo) return lo;
        if (value > hi) return hi;
        return value;
    }

    static int RectWidth(const SMALL_RECT& rect)
    {
        return rect.Right - rect.Left + 1;
    }

    static int RectHeight(const SMALL_RECT& rect)
    {
        return rect.Bottom - rect.Top + 1;
    }

    static bool IsHighSurrogate(wchar_t ch)
    {
        return ch >= 0xD800 && ch <= 0xDBFF;
    }

    static bool IsLowSurrogate(wchar_t ch)
    {
        return ch >= 0xDC00 && ch <= 0xDFFF;
    }

    static unsigned int DecodeCodePoint(const std::wstring& text, size_t index, size_t& units)
    {
        units = 1;
        unsigned int cp = static_cast<unsigned int>(text[index]);

        if (IsHighSurrogate(text[index]) && index + 1 < text.size() && IsLowSurrogate(text[index + 1]))
        {
            unsigned int high = static_cast<unsigned int>(text[index]) - 0xD800;
            unsigned int low = static_cast<unsigned int>(text[index + 1]) - 0xDC00;
            cp = 0x10000 + ((high << 10) | low);
            units = 2;
        }

        return cp;
    }

    static bool IsCombiningMark(unsigned int cp)
    {
        return
            (cp >= 0x0300 && cp <= 0x036F) ||
            (cp >= 0x1AB0 && cp <= 0x1AFF) ||
            (cp >= 0x1DC0 && cp <= 0x1DFF) ||
            (cp >= 0x20D0 && cp <= 0x20FF) ||
            (cp >= 0xFE20 && cp <= 0xFE2F) ||
            (cp >= 0xFE00 && cp <= 0xFE0F) ||
            cp == 0x200D;
    }

    static bool IsWideCodePoint(unsigned int cp)
    {
        return
            (cp >= 0x1100 && cp <= 0x115F) ||
            (cp >= 0x2329 && cp <= 0x232A) ||
            (cp >= 0x2E80 && cp <= 0xA4CF) ||
            (cp >= 0xAC00 && cp <= 0xD7A3) ||
            (cp >= 0xF900 && cp <= 0xFAFF) ||
            (cp >= 0xFE10 && cp <= 0xFE19) ||
            (cp >= 0xFE30 && cp <= 0xFE6F) ||
            (cp >= 0xFF00 && cp <= 0xFF60) ||
            (cp >= 0xFFE0 && cp <= 0xFFE6) ||
            (cp >= 0x1F300 && cp <= 0x1FAFF) ||
            (cp >= 0x20000 && cp <= 0x3FFFD);
    }

    static int CodePointCellWidth(unsigned int cp)
    {
        if (cp == 0 || cp == L'\r' || cp == L'\n')
            return 0;
        if (IsCombiningMark(cp))
            return 0;
        if (cp < 0x20 || (cp >= 0x7F && cp < 0xA0))
            return 0;
        return IsWideCodePoint(cp) ? 2 : 1;
    }

    static int CellWidth(const std::wstring& text)
    {
        int cells = 0;
        for (size_t i = 0; i < text.size(); )
        {
            size_t units = 1;
            unsigned int cp = DecodeCodePoint(text, i, units);
            cells += CodePointCellWidth(cp);
            i += units;
        }
        return cells;
    }

    static std::wstring SliceByCells(const std::wstring& text, int startCell, int maxCells)
    {
        if (maxCells <= 0)
            return L"";

        std::wstring result;
        int currentCell = 0;
        int usedCells = 0;
        bool started = false;

        for (size_t i = 0; i < text.size(); )
        {
            size_t units = 1;
            unsigned int cp = DecodeCodePoint(text, i, units);
            int width = CodePointCellWidth(cp);

            if (width == 0)
            {
                if (started)
                    result.append(text, i, units);
                i += units;
                continue;
            }

            int nextCell = currentCell + width;

            if (nextCell <= startCell)
            {
                currentCell = nextCell;
                i += units;
                continue;
            }

            if (currentCell < startCell && startCell < nextCell)
            {
                currentCell = nextCell;
                i += units;
                continue;
            }

            if (usedCells + width > maxCells)
                break;

            result.append(text, i, units);
            started = true;
            usedCells += width;
            currentCell = nextCell;
            i += units;
        }

        return result;
    }

    static int FirstAlnumCellCharLower(const std::wstring& text)
    {
        for (wchar_t ch : text)
        {
            if (std::iswalnum(ch))
                return static_cast<int>(std::towlower(ch));
        }
        return 0;
    }

    static void WriteWide(HANDLE out, const std::wstring& text)
    {
        DWORD written = 0;
        if (!text.empty())
            WriteConsoleW(out, text.c_str(), static_cast<DWORD>(text.size()), &written, nullptr);
    }

    static std::wstring Esc(const wchar_t* suffix)
    {
        std::wstring s;
        s.push_back(0x1B);
        s += suffix;
        return s;
    }

    static std::wstring MoveTo(int row0, int col0)
    {
        return Esc((L"[" + std::to_wstring(row0 + 1) + L";" + std::to_wstring(col0 + 1) + L"H").c_str());
    }

    static const wchar_t* StyleCode(Style style)
    {
        switch (style)
        {
        case Style::Header:
            return L"\x1b[0;1m";
        case Style::Selected:
            return L"\x1b[30;47m";
        case Style::SelectedMarker:
            return L"\x1b[97;47m";
        case Style::ProcessName:
            return L"\x1b[32m";
        case Style::ProcessNameSelected:
            return L"\x1b[32;47m";
        case Style::TopProcess:
            return L"\x1b[33m";
        case Style::TopProcessSelected:
            return L"\x1b[33;47m";
        case Style::Handle:
            return L"\x1b[90m";
        case Style::HandleSelected:
            return L"\x1b[90;47m";
        case Style::Status:
            return L"\x1b[37;44m";
        case Style::Normal:
        default:
            return L"\x1b[0m";
        }
    }

    static bool GetVisibleSize(HANDLE out, int& width, int& height)
    {
        CONSOLE_SCREEN_BUFFER_INFO info{};
        if (!GetConsoleScreenBufferInfo(out, &info))
            return false;

        width = RectWidth(info.srWindow);
        height = RectHeight(info.srWindow);

        if (width < 1) width = 1;
        if (height < 1) height = 1;
        return true;
    }

    static void HideCharacterCursor(ConsoleState& state)
    {
        CONSOLE_CURSOR_INFO cursor{};
        if (GetConsoleCursorInfo(state.out, &cursor))
        {
            if (!state.haveOriginalCursor)
            {
                state.originalCursor = cursor;
                state.haveOriginalCursor = true;
            }

            cursor.bVisible = FALSE;
            if (cursor.dwSize < 1)
                cursor.dwSize = 1;
            SetConsoleCursorInfo(state.out, &cursor);
        }
    }

    static void ForceHideCursor(ConsoleState& state)
    {
        if (state.out != INVALID_HANDLE_VALUE)
            WriteWide(state.out, Esc(L"[?25l"));
        HideCharacterCursor(state);
    }

    static void RestoreCharacterCursor(ConsoleState& state)
    {
        if (state.haveOriginalCursor)
            SetConsoleCursorInfo(state.out, &state.originalCursor);
    }

    static bool EnableVirtualTerminal(ConsoleState& state)
    {
        state.out = GetStdHandle(STD_OUTPUT_HANDLE);
        state.in = GetStdHandle(STD_INPUT_HANDLE);

        if (state.out == INVALID_HANDLE_VALUE || state.in == INVALID_HANDLE_VALUE)
            return false;

        if (!GetConsoleMode(state.out, &state.originalOutMode))
            return false;

        if (!GetConsoleMode(state.in, &state.originalInMode))
            return false;

        DWORD outMode = state.originalOutMode;
        outMode |= ENABLE_PROCESSED_OUTPUT;
        outMode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;

        if (!SetConsoleMode(state.out, outMode | DISABLE_NEWLINE_AUTO_RETURN))
        {
            if (!SetConsoleMode(state.out, outMode))
                return false;
        }

        DWORD inMode = state.originalInMode;
        inMode |= ENABLE_WINDOW_INPUT;
        inMode |= ENABLE_MOUSE_INPUT;
        inMode |= ENABLE_EXTENDED_FLAGS;
        inMode &= ~ENABLE_QUICK_EDIT_MODE;
        SetConsoleMode(state.in, inMode);

        state.vtEnabled = true;
        return true;
    }

    static void EnterVirtualScreen(ConsoleState& state)
    {
        WriteWide(state.out, Esc(L"[?1049h")); // alternate screen buffer
        ForceHideCursor(state);                 // hide VT and Win32 console cursor
        WriteWide(state.out, Esc(L"[?7l"));    // disable auto-wrap; prevents row joining in Windows Terminal
        WriteWide(state.out, Esc(L"[2J"));     // clear screen
        WriteWide(state.out, Esc(L"[H"));      // home
        state.altScreen = true;
    }

    static void LeaveVirtualScreen(ConsoleState& state)
    {
        if (state.out != INVALID_HANDLE_VALUE)
        {
            WriteWide(state.out, Esc(L"[0m"));
            WriteWide(state.out, Esc(L"[?7h"));
            RestoreCharacterCursor(state);
            WriteWide(state.out, Esc(L"[?25h"));
            if (state.altScreen)
                WriteWide(state.out, Esc(L"[?1049l"));
        }

        if (state.vtEnabled)
        {
            SetConsoleMode(state.out, state.originalOutMode);
            SetConsoleMode(state.in, state.originalInMode);
        }
    }

    static int HeaderRows()
    {
        return 3;
    }

    static int StatusRows()
    {
        return 1;
    }

    static std::wstring CleanOneLine(const wchar_t* text)
    {
        if (text == nullptr)
            return L"";

        std::wstring result(text);
        for (wchar_t& ch : result)
        {
            if (ch == L'\r' || ch == L'\n' || ch == L'\t')
                ch = L' ';
        }
        return result;
    }

    static std::vector<Row> BuildRows(const NativeSelectorRow* nativeRows, int rowCount)
    {
        std::vector<Row> rows;
        rows.reserve(static_cast<size_t>(std::max(0, rowCount)));

        for (int i = 0; i < rowCount; ++i)
        {
            Row row;
            row.sequence = nativeRows[i].Sequence;
            row.processId = nativeRows[i].ProcessId;
            row.windowHandle = nativeRows[i].WindowHandle;
            row.isTop = nativeRows[i].IsTopForProcess != 0;
            row.text = CleanOneLine(nativeRows[i].DisplayText);
            rows.push_back(std::move(row));
        }

        return rows;
    }

    static int MaxRenderedCellWidth(const std::vector<Row>& rows)
    {
        int maxWidth = CellWidth(L"NATIVE DLL selector VT virtual buffer");
        maxWidth = std::max(maxWidth, CellWidth(L"Up/Down move  PgUp/PgDn  Home/End  Left/Right pan  Enter/double-click select  Esc cancel"));

        for (const Row& row : rows)
        {
            int width = CellWidth(L"> ") + CellWidth(row.text);
            maxWidth = std::max(maxWidth, width);
        }

        return std::min(32000, maxWidth + 8);
    }

    static size_t FindProcessEnd(const std::wstring& text)
    {
        size_t processEnd = text.find(L" [");
        size_t bar = text.find(L" | ");

        if (processEnd == std::wstring::npos || (bar != std::wstring::npos && processEnd > bar))
            processEnd = (bar == std::wstring::npos) ? text.size() : bar;

        return processEnd;
    }

    static std::wstring LowerString(std::wstring value)
    {
        for (wchar_t& ch : value)
            ch = static_cast<wchar_t>(std::towlower(ch));
        return value;
    }

    static std::wstring ProcessNameKey(const Row& row)
    {
        size_t end = FindProcessEnd(row.text);
        return LowerString(row.text.substr(0, end));
    }

    static int FirstAlnumProcessCharLower(const Row& row)
    {
        std::wstring process = row.text.substr(0, FindProcessEnd(row.text));
        return FirstAlnumCellCharLower(process);
    }

    static size_t FindHandleStart(const std::wstring& text)
    {
        size_t pos = text.rfind(L" (0x");
        if (pos != std::wstring::npos)
            return pos + 1; // include the bracket, but not the preceding separator space

        pos = text.rfind(L" [0x");
        if (pos != std::wstring::npos)
            return pos + 1; // include the bracket, but not the preceding separator space

        pos = text.rfind(L" | hwnd ");
        if (pos != std::wstring::npos)
            return pos + 3; // keep the column separator normal; gray the handle column

        pos = text.rfind(L" hwnd 0x");
        if (pos != std::wstring::npos)
            return pos + 1;

        return std::wstring::npos;
    }

    static std::vector<Segment> BuildRowSegments(const Row& row, bool selected)
    {
        std::vector<Segment> segments;
        segments.push_back({ selected ? L"> " : L"  ", selected ? Style::SelectedMarker : Style::Normal });

        Style normal = selected ? Style::Selected : Style::Normal;
        Style process = selected ? Style::ProcessNameSelected : Style::ProcessName;
        Style top = selected ? Style::TopProcessSelected : Style::TopProcess;
        Style handle = selected ? Style::HandleSelected : Style::Handle;

        size_t handleStart = FindHandleStart(row.text);
        std::wstring body = row.text;
        std::wstring handleText;

        if (handleStart != std::wstring::npos && handleStart < row.text.size())
        {
            body = row.text.substr(0, handleStart);
            handleText = row.text.substr(handleStart);
        }

        size_t processEnd = FindProcessEnd(body);
        if (processEnd > body.size())
            processEnd = body.size();

        if (processEnd > 0)
            segments.push_back({ body.substr(0, processEnd), row.isTop ? top : process });

        size_t pos = processEnd;

        if (row.isTop)
        {
            size_t topPos = body.find(L"Top", pos);
            if (topPos != std::wstring::npos)
            {
                if (topPos > pos)
                    segments.push_back({ body.substr(pos, topPos - pos), normal });
                segments.push_back({ body.substr(topPos, 3), top });
                pos = topPos + 3;
            }
        }

        if (pos < body.size())
            segments.push_back({ body.substr(pos), normal });

        if (!handleText.empty())
            segments.push_back({ handleText, handle });

        return segments;
    }

    static void AppendMoveTo(std::wstring& batch, int row0, int col0)
    {
        batch.push_back(0x1B);
        batch += L"[";
        batch += std::to_wstring(row0 + 1);
        batch += L";";
        batch += std::to_wstring(col0 + 1);
        batch += L"H";
    }

    static void AppendClearLine(std::wstring& batch)
    {
        batch += L"\x1b[2K";
    }

    static void AppendStyle(std::wstring& batch, Style style)
    {
        batch += StyleCode(style);
    }

    static void AppendSpaces(std::wstring& batch, int count)
    {
        if (count > 0)
            batch.append(static_cast<size_t>(count), L' ');
    }

    static void AppendStyledLine(std::wstring& batch, int row0, int width, const std::vector<Segment>& segments, int virtualLeft, bool selected)
    {
        AppendMoveTo(batch, row0, 0);
        AppendClearLine(batch);

        int globalCell = 0;
        int usedCells = 0;
        Style currentStyle = Style::Normal;
        bool hasStyle = false;

        for (const Segment& segment : segments)
        {
            int segmentCells = CellWidth(segment.text);
            int segmentStart = globalCell;
            int segmentEnd = segmentStart + segmentCells;

            if (segmentEnd <= virtualLeft)
            {
                globalCell = segmentEnd;
                continue;
            }

            if (usedCells >= width)
                break;

            int localStart = std::max(0, virtualLeft - segmentStart);
            int available = width - usedCells;
            std::wstring part = SliceByCells(segment.text, localStart, available);

            if (!part.empty())
            {
                if (!hasStyle || currentStyle != segment.style)
                {
                    AppendStyle(batch, segment.style);
                    currentStyle = segment.style;
                    hasStyle = true;
                }

                batch += part;
                usedCells += CellWidth(part);
            }

            globalCell = segmentEnd;
        }

        if (selected && usedCells < width)
        {
            if (!hasStyle || currentStyle != Style::Selected)
                AppendStyle(batch, Style::Selected);
            AppendSpaces(batch, width - usedCells);
        }

        batch += L"\x1b[0m";
    }

    static void AppendPlainLine(std::wstring& batch, int row0, int width, const std::wstring& text, Style style, bool fillLine)
    {
        AppendMoveTo(batch, row0, 0);
        AppendClearLine(batch);
        AppendStyle(batch, style);

        std::wstring part = SliceByCells(text, 0, width);
        batch += part;

        if (fillLine)
        {
            int used = CellWidth(part);
            if (used < width)
                AppendSpaces(batch, width - used);
        }

        batch += L"\x1b[0m";
    }

    static void AppendClearOnlyLine(std::wstring& batch, int row0)
    {
        AppendMoveTo(batch, row0, 0);
        AppendClearLine(batch);
        batch += L"\x1b[0m";
    }

    static void FlushBatch(ConsoleState& state, const std::wstring& batch)
    {
        WriteWide(state.out, batch);
        ForceHideCursor(state);
    }

    static std::wstring BuildStatusText(
        const std::vector<Row>& rows,
        int selectedIndex,
        int virtualTop,
        int virtualLeft,
        int visibleWidth,
        int visibleHeight,
        int hiddenWidth,
        int hiddenHeight)
    {
        return
            L" NATIVE DLL selector VT partial+mouse | rows=" + std::to_wstring(rows.size()) +
            L" | visible=" + std::to_wstring(visibleWidth) + L"x" + std::to_wstring(visibleHeight) +
            L" | hidden dwSize=" + std::to_wstring(hiddenWidth) + L"x" + std::to_wstring(hiddenHeight) +
            L" | left=" + std::to_wstring(virtualLeft) +
            L" top=" + std::to_wstring(virtualTop) +
            L" selected=" + std::to_wstring(selectedIndex);
    }

    static void AppendRowBySourceIndex(
        std::wstring& batch,
        const std::vector<Row>& rows,
        int sourceRow,
        int virtualTop,
        int virtualLeft,
        int visibleWidth,
        int visibleHeight,
        int selectedIndex)
    {
        int listHeight = std::max(0, visibleHeight - HeaderRows() - StatusRows());
        if (sourceRow < virtualTop || sourceRow >= virtualTop + listHeight)
            return;

        int screenRow = HeaderRows() + sourceRow - virtualTop;
        if (screenRow < HeaderRows() || screenRow >= visibleHeight - StatusRows())
            return;

        if (sourceRow >= 0 && sourceRow < static_cast<int>(rows.size()))
        {
            bool selected = sourceRow == selectedIndex;
            AppendStyledLine(batch, screenRow, visibleWidth, BuildRowSegments(rows[static_cast<size_t>(sourceRow)], selected), virtualLeft, selected);
        }
        else
        {
            AppendClearOnlyLine(batch, screenRow);
        }
    }

    static void AppendStatusLine(
        std::wstring& batch,
        const std::vector<Row>& rows,
        int selectedIndex,
        int virtualTop,
        int virtualLeft,
        int visibleWidth,
        int visibleHeight,
        int hiddenWidth,
        int hiddenHeight)
    {
        if (visibleHeight <= 0)
            return;

        int statusY = visibleHeight - 1;
        AppendPlainLine(
            batch,
            statusY,
            visibleWidth,
            BuildStatusText(rows, selectedIndex, virtualTop, virtualLeft, visibleWidth, visibleHeight, hiddenWidth, hiddenHeight),
            Style::Status,
            true);
    }

    static void RenderVirtualBuffer(
        ConsoleState& state,
        const std::vector<Row>& rows,
        int selectedIndex,
        int virtualTop,
        int virtualLeft,
        int visibleWidth,
        int visibleHeight,
        int hiddenWidth,
        int hiddenHeight)
    {
        if (visibleWidth <= 0 || visibleHeight <= 0)
            return;

        std::wstring batch;
        batch.reserve(static_cast<size_t>(visibleWidth) * static_cast<size_t>(std::max(visibleHeight, 1)) * 2);

        AppendPlainLine(batch, 0, visibleWidth, L"NATIVE DLL selector VT virtual buffer", Style::Header, false);
        AppendPlainLine(batch, 1, visibleWidth, L"Up/Down move  PgUp/PgDn  Home/End  Left/Right pan  Enter/double-click select  Esc cancel", Style::Header, false);

        std::wstring ruler;
        for (int i = 0; i < visibleWidth; ++i)
            ruler.push_back(static_cast<wchar_t>(L'0' + ((virtualLeft + i) % 10)));
        AppendPlainLine(batch, 2, visibleWidth, ruler, Style::Header, false);

        int listTop = HeaderRows();
        int listHeight = std::max(0, visibleHeight - HeaderRows() - StatusRows());

        for (int screenRow = 0; screenRow < listHeight; ++screenRow)
        {
            int sourceRow = virtualTop + screenRow;
            int y = listTop + screenRow;
            if (sourceRow >= 0 && sourceRow < static_cast<int>(rows.size()))
            {
                bool selected = sourceRow == selectedIndex;
                AppendStyledLine(batch, y, visibleWidth, BuildRowSegments(rows[static_cast<size_t>(sourceRow)], selected), virtualLeft, selected);
            }
            else
            {
                AppendClearOnlyLine(batch, y);
            }
        }

        AppendStatusLine(batch, rows, selectedIndex, virtualTop, virtualLeft, visibleWidth, visibleHeight, hiddenWidth, hiddenHeight);
        AppendMoveTo(batch, std::max(0, visibleHeight - 1), 0);
        FlushBatch(state, batch);
    }

    static void RenderSelectionDelta(
        ConsoleState& state,
        const std::vector<Row>& rows,
        int oldSelectedIndex,
        int selectedIndex,
        int virtualTop,
        int virtualLeft,
        int visibleWidth,
        int visibleHeight,
        int hiddenWidth,
        int hiddenHeight)
    {
        if (visibleWidth <= 0 || visibleHeight <= 0)
            return;

        std::wstring batch;
        batch.reserve(static_cast<size_t>(visibleWidth) * 6);

        if (oldSelectedIndex != selectedIndex)
            AppendRowBySourceIndex(batch, rows, oldSelectedIndex, virtualTop, virtualLeft, visibleWidth, visibleHeight, selectedIndex);

        AppendRowBySourceIndex(batch, rows, selectedIndex, virtualTop, virtualLeft, visibleWidth, visibleHeight, selectedIndex);
        AppendStatusLine(batch, rows, selectedIndex, virtualTop, virtualLeft, visibleWidth, visibleHeight, hiddenWidth, hiddenHeight);
        AppendMoveTo(batch, std::max(0, visibleHeight - 1), 0);
        FlushBatch(state, batch);
    }

    static void ReadAllInput(ConsoleState& state, std::vector<INPUT_RECORD>& events)
    {
        DWORD count = 0;
        if (!GetNumberOfConsoleInputEvents(state.in, &count) || count == 0)
            return;

        events.resize(count);
        DWORD read = 0;
        if (!ReadConsoleInputW(state.in, events.data(), count, &read))
        {
            events.clear();
            return;
        }
        events.resize(read);
    }

    static void JumpToProcessLetter(const std::vector<Row>& rows, int& selectedIndex, wchar_t typed)
    {
        if (rows.empty())
            return;

        wchar_t target = static_cast<wchar_t>(std::towlower(typed));
        if (!std::iswalnum(target))
            return;

        int count = static_cast<int>(rows.size());
        int start = (selectedIndex + 1) % count;
        std::wstring currentProcess = ProcessNameKey(rows[static_cast<size_t>(ClampInt(selectedIndex, 0, count - 1))]);

        // Cycle by process group, not by every window row of the same process.
        for (int offset = 0; offset < count; ++offset)
        {
            int index = (start + offset) % count;
            const Row& candidate = rows[static_cast<size_t>(index)];
            if (FirstAlnumProcessCharLower(candidate) != target)
                continue;

            if (ProcessNameKey(candidate) == currentProcess)
                continue;

            selectedIndex = index;
            return;
        }

        // If the current row is not a process beginning with the typed letter, still allow
        // jumping into the first matching process group.
        if (FirstAlnumProcessCharLower(rows[static_cast<size_t>(ClampInt(selectedIndex, 0, count - 1))]) != target)
        {
            for (int offset = 0; offset < count; ++offset)
            {
                int index = (start + offset) % count;
                if (FirstAlnumProcessCharLower(rows[static_cast<size_t>(index)]) == target)
                {
                    selectedIndex = index;
                    return;
                }
            }
        }
    }

    static bool TryGetListRowFromMouse(int mouseY, int virtualTop, int visibleHeight, int rowCount, int& sourceRow)
    {
        int listHeight = std::max(0, visibleHeight - HeaderRows() - StatusRows());
        if (mouseY < HeaderRows() || mouseY >= HeaderRows() + listHeight)
            return false;

        int index = virtualTop + (mouseY - HeaderRows());
        if (index < 0 || index >= rowCount)
            return false;

        sourceRow = index;
        return true;
    }

    static int SelectWithVirtualTerminal(const NativeSelectorRow* nativeRows, int rowCount, int initialIndex, int* selectedIndexOut)
    {
        if (nativeRows == nullptr || rowCount <= 0 || selectedIndexOut == nullptr)
            return -2;

        ConsoleState state;
        if (!EnableVirtualTerminal(state))
            return -10;

        std::vector<Row> rows = BuildRows(nativeRows, rowCount);
        if (rows.empty())
            return -2;

        int selectedIndex = ClampInt(initialIndex, 0, static_cast<int>(rows.size()) - 1);
        int visibleWidth = 80;
        int visibleHeight = 25;
        GetVisibleSize(state.out, visibleWidth, visibleHeight);

        int hiddenWidth = std::max(visibleWidth, MaxRenderedCellWidth(rows));
        int hiddenHeight = std::max(visibleHeight, HeaderRows() + static_cast<int>(rows.size()) + StatusRows());

        int virtualTop = 0;
        int virtualLeft = 0;
        int lastWidth = -1;
        int lastHeight = -1;
        int lastVirtualTop = -1;
        int lastVirtualLeft = -1;
        int lastSelectedIndex = -1;
        bool fullDirty = true;
        bool selectionDirty = false;
        bool running = true;
        bool accepted = false;

        EnterVirtualScreen(state);

        while (running)
        {
            int w = 0;
            int h = 0;
            if (GetVisibleSize(state.out, w, h))
            {
                visibleWidth = w;
                visibleHeight = h;
                if (visibleWidth != lastWidth || visibleHeight != lastHeight)
                {
                    lastWidth = visibleWidth;
                    lastHeight = visibleHeight;
                    hiddenWidth = std::max(hiddenWidth, visibleWidth);
                    hiddenHeight = std::max(hiddenHeight, visibleHeight);
                    fullDirty = true;
                }
            }

            int listHeight = std::max(1, visibleHeight - HeaderRows() - StatusRows());
            int maxTop = std::max(0, static_cast<int>(rows.size()) - listHeight);
            int maxLeft = std::max(0, hiddenWidth - visibleWidth);

            selectedIndex = ClampInt(selectedIndex, 0, static_cast<int>(rows.size()) - 1);
            if (selectedIndex < virtualTop)
                virtualTop = selectedIndex;
            if (selectedIndex >= virtualTop + listHeight)
                virtualTop = selectedIndex - listHeight + 1;

            virtualTop = ClampInt(virtualTop, 0, maxTop);
            virtualLeft = ClampInt(virtualLeft, 0, maxLeft);

            if (virtualTop != lastVirtualTop || virtualLeft != lastVirtualLeft)
                fullDirty = true;

            if (fullDirty)
            {
                RenderVirtualBuffer(state, rows, selectedIndex, virtualTop, virtualLeft, visibleWidth, visibleHeight, hiddenWidth, hiddenHeight);
                fullDirty = false;
                selectionDirty = false;
            }
            else if (selectionDirty || selectedIndex != lastSelectedIndex)
            {
                RenderSelectionDelta(state, rows, lastSelectedIndex, selectedIndex, virtualTop, virtualLeft, visibleWidth, visibleHeight, hiddenWidth, hiddenHeight);
                selectionDirty = false;
            }

            ForceHideCursor(state);

            lastVirtualTop = virtualTop;
            lastVirtualLeft = virtualLeft;
            lastSelectedIndex = selectedIndex;

            DWORD waitResult = WaitForSingleObject(state.in, 40);
            if (waitResult != WAIT_OBJECT_0)
                continue;

            std::vector<INPUT_RECORD> events;
            ReadAllInput(state, events);

            for (const INPUT_RECORD& eventRecord : events)
            {
                if (eventRecord.EventType == WINDOW_BUFFER_SIZE_EVENT)
                {
                    fullDirty = true;
                    ForceHideCursor(state);
                    continue;
                }

                if (eventRecord.EventType == MOUSE_EVENT)
                {
                    const MOUSE_EVENT_RECORD& mouse = eventRecord.Event.MouseEvent;
                    int sourceRow = -1;
                    if (TryGetListRowFromMouse(mouse.dwMousePosition.Y, virtualTop, visibleHeight, static_cast<int>(rows.size()), sourceRow))
                    {
                        bool leftButton = (mouse.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0;
                        if (leftButton && mouse.dwEventFlags == DOUBLE_CLICK)
                        {
                            selectedIndex = sourceRow;
                            accepted = true;
                            running = false;
                            break;
                        }

                        if (leftButton && mouse.dwEventFlags == 0)
                        {
                            if (selectedIndex != sourceRow)
                            {
                                selectedIndex = sourceRow;
                                selectionDirty = true;
                            }
                        }
                    }
                    continue;
                }

                if (eventRecord.EventType != KEY_EVENT)
                    continue;

                const KEY_EVENT_RECORD& key = eventRecord.Event.KeyEvent;
                if (!key.bKeyDown)
                    continue;

                switch (key.wVirtualKeyCode)
                {
                case VK_ESCAPE:
                    running = false;
                    break;

                case VK_RETURN:
                    accepted = true;
                    running = false;
                    break;

                case VK_UP:
                    if (selectedIndex > 0)
                    {
                        --selectedIndex;
                        selectionDirty = true;
                    }
                    break;

                case VK_DOWN:
                    if (selectedIndex < static_cast<int>(rows.size()) - 1)
                    {
                        ++selectedIndex;
                        selectionDirty = true;
                    }
                    break;

                case VK_PRIOR:
                    selectedIndex = std::max(0, selectedIndex - std::max(1, listHeight));
                    selectionDirty = true;
                    break;

                case VK_NEXT:
                    selectedIndex = std::min(static_cast<int>(rows.size()) - 1, selectedIndex + std::max(1, listHeight));
                    selectionDirty = true;
                    break;

                case VK_HOME:
                    if (key.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED))
                        virtualLeft = 0;
                    else
                    {
                        selectedIndex = 0;
                        selectionDirty = true;
                    }
                    fullDirty = true;
                    break;

                case VK_END:
                    if (key.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED))
                        virtualLeft = maxLeft;
                    else
                    {
                        selectedIndex = static_cast<int>(rows.size()) - 1;
                        selectionDirty = true;
                    }
                    fullDirty = true;
                    break;

                case VK_LEFT:
                    virtualLeft = std::max(0, virtualLeft - 4);
                    fullDirty = true;
                    break;

                case VK_RIGHT:
                    virtualLeft = std::min(maxLeft, virtualLeft + 4);
                    fullDirty = true;
                    break;

                default:
                    if (key.uChar.UnicodeChar != 0)
                    {
                        int beforeJump = selectedIndex;
                        JumpToProcessLetter(rows, selectedIndex, key.uChar.UnicodeChar);
                        if (selectedIndex != beforeJump)
                            selectionDirty = true;
                    }
                    break;
                }
            }
        }

        LeaveVirtualScreen(state);

        if (accepted)
        {
            *selectedIndexOut = selectedIndex;
            return 1;
        }

        *selectedIndexOut = initialIndex;
        return 0;
    }
}

extern "C" __declspec(dllexport)
int __stdcall SelectWindowFromRows(const NativeSelectorRow* rows, int rowCount, int initialIndex, int* selectedIndexOut)
{
    try
    {
        return SelectWithVirtualTerminal(rows, rowCount, initialIndex, selectedIndexOut);
    }
    catch (...)
    {
        if (selectedIndexOut != nullptr)
            *selectedIndexOut = initialIndex;
        return -1;
    }
}

