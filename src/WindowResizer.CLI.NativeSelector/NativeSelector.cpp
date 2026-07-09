#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>
#include <algorithm>
#include <cstdint>
#include <cwctype>
#include <string>
#include <vector>

struct NativeSelectorRow
{
    int Sequence;
    int ProcessId;
    intptr_t WindowHandle;
    int IsTopForProcess;
    const wchar_t* DisplayText;
};

struct Row
{
    int sequence = 0;
    bool isTop = false;
    std::wstring text;
};

struct HiddenCache
{
    int width = 0;
    int height = 0;
    int selectedIndex = -1;
    std::vector<CHAR_INFO> cells;
};

static HANDLE gOriginalOut = INVALID_HANDLE_VALUE;
static HANDLE gVisibleOut = INVALID_HANDLE_VALUE;
static HANDLE gHiddenOut = INVALID_HANDLE_VALUE;
static HANDLE gInput = INVALID_HANDLE_VALUE;
static DWORD gOriginalInputMode = 0;
static CONSOLE_CURSOR_INFO gOriginalCursor{};
static WORD gNormalAttr = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE;

static const int kHeaderLines = 3;

static int ClampInt(int v, int lo, int hi)
{
    return std::max(lo, std::min(v, hi));
}

static int RectWidth(const SMALL_RECT& r)
{
    return r.Right - r.Left + 1;
}

static int RectHeight(const SMALL_RECT& r)
{
    return r.Bottom - r.Top + 1;
}

static CHAR_INFO MakeCell(wchar_t ch, WORD attr)
{
    CHAR_INFO c{};
    c.Char.UnicodeChar = ch;
    c.Attributes = attr;
    return c;
}

static bool IsHighSurrogate(wchar_t ch)
{
    return ch >= 0xD800 && ch <= 0xDBFF;
}

static bool IsLowSurrogate(wchar_t ch)
{
    return ch >= 0xDC00 && ch <= 0xDFFF;
}

static bool IsCombiningMark(unsigned int cp)
{
    return
        (cp >= 0x0300 && cp <= 0x036F) ||
        (cp >= 0x1AB0 && cp <= 0x1AFF) ||
        (cp >= 0x1DC0 && cp <= 0x1DFF) ||
        (cp >= 0x20D0 && cp <= 0x20FF) ||
        (cp >= 0xFE20 && cp <= 0xFE2F);
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
        (cp >= 0xFFE0 && cp <= 0xFFE6);
}

static int EstimatedCells(const std::wstring& s)
{
    int cells = 0;
    for (size_t i = 0; i < s.size(); ++i)
    {
        unsigned int cp = static_cast<unsigned int>(s[i]);
        if (IsHighSurrogate(s[i]) && i + 1 < s.size() && IsLowSurrogate(s[i + 1]))
        {
            cells += 2;
            ++i;
            continue;
        }
        if (IsLowSurrogate(s[i]))
            continue;
        if (IsCombiningMark(cp))
            continue;
        cells += IsWideCodePoint(cp) ? 2 : 1;
    }
    return cells;
}

static WORD ForegroundFromBackground(WORD attr)
{
    return static_cast<WORD>((attr & 0x00F0) >> 4);
}

static WORD WithBackground(WORD foreground, WORD backgroundSource)
{
    return static_cast<WORD>((backgroundSource & 0x00F0) | (foreground & 0x000F));
}

static WORD SelectedAttr()
{
    WORD fg = ForegroundFromBackground(gNormalAttr);
    return static_cast<WORD>(BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE | fg);
}

static WORD HeaderAttr()
{
    return static_cast<WORD>((gNormalAttr & 0x00F0) | FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY);
}

static WORD YellowAttr()
{
    return static_cast<WORD>((gNormalAttr & 0x00F0) | FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY);
}

static void FillLine(HANDLE h, int y, int width, WORD attr)
{
    if (width <= 0)
        return;
    DWORD written = 0;
    COORD pos{0, static_cast<SHORT>(y)};
    FillConsoleOutputCharacterW(h, L' ', static_cast<DWORD>(width), pos, &written);
    FillConsoleOutputAttribute(h, attr, static_cast<DWORD>(width), pos, &written);
}

static void WriteAt(HANDLE h, int x, int y, const std::wstring& text, WORD attr)
{
    if (text.empty())
        return;
    COORD pos{static_cast<SHORT>(x), static_cast<SHORT>(y)};
    DWORD written = 0;
    SetConsoleCursorPosition(h, pos);
    SetConsoleTextAttribute(h, attr);
    WriteConsoleW(h, text.c_str(), static_cast<DWORD>(text.size()), &written, nullptr);
}

static void PutAscii(std::vector<CHAR_INFO>& frame, int width, int height, int x, int y, const std::wstring& text, WORD attr)
{
    if (y < 0 || y >= height)
        return;
    for (int i = 0; i < static_cast<int>(text.size()); ++i)
    {
        int xx = x + i;
        if (xx < 0 || xx >= width)
            continue;
        frame[static_cast<size_t>(y) * width + xx] = MakeCell(text[static_cast<size_t>(i)], attr);
    }
}

static CHAR_INFO CellAt(const HiddenCache& cache, int x, int y)
{
    if (x < 0 || y < 0 || x >= cache.width || y >= cache.height)
        return MakeCell(L' ', gNormalAttr);
    return cache.cells[static_cast<size_t>(y) * cache.width + x];
}

static HANDLE CreateBuffer()
{
    return CreateConsoleScreenBuffer(
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        CONSOLE_TEXTMODE_BUFFER,
        nullptr);
}

static void SetupInput()
{
    GetConsoleMode(gInput, &gOriginalInputMode);
    DWORD mode = gOriginalInputMode;
    mode |= ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS;
    mode &= ~ENABLE_QUICK_EDIT_MODE;
    SetConsoleMode(gInput, mode);
}

static void HideCursor(HANDLE h)
{
    CONSOLE_CURSOR_INFO cursor{};
    if (GetConsoleCursorInfo(h, &cursor))
    {
        cursor.bVisible = FALSE;
        SetConsoleCursorInfo(h, &cursor);
    }
}

static bool GetVisibleSize(int& width, int& height)
{
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (!GetConsoleScreenBufferInfo(gVisibleOut, &info))
        return false;

    width = RectWidth(info.srWindow);
    height = RectHeight(info.srWindow);
    if (width < 1) width = 1;
    if (height < 1) height = 1;

    if (info.dwSize.X < width || info.dwSize.Y < height)
    {
        COORD size{};
        size.X = static_cast<SHORT>(std::max<int>(info.dwSize.X, width));
        size.Y = static_cast<SHORT>(std::max<int>(info.dwSize.Y, height));
        SetConsoleScreenBufferSize(gVisibleOut, size);
    }
    return true;
}

static int RequiredHiddenWidth(const std::vector<Row>& rows, int visibleWidth)
{
    int width = std::max(visibleWidth, 120);
    width = std::max(width, EstimatedCells(L"WindowResizer NATIVE DLL selector") + 8);
    width = std::max(width, EstimatedCells(L"Up/Down PgUp/PgDn Home/End Left/Right pan Letters jump Enter accept Esc cancel") + 8);
    for (const auto& row : rows)
        width = std::max(width, 2 + EstimatedCells(row.text) + 8);
    return ClampInt(width, 120, 4096);
}

static void DrawTopMarkers(HANDLE h, int y, const Row& row, WORD attr)
{
    size_t pipe = row.text.find(L" | ");
    size_t bracket = row.text.find(L" [");
    size_t processEnd = row.text.size();
    if (pipe != std::wstring::npos) processEnd = std::min(processEnd, pipe);
    if (bracket != std::wstring::npos) processEnd = std::min(processEnd, bracket);
    if (processEnd > 0)
        WriteAt(h, 2, y, row.text.substr(0, processEnd), attr);

    if (pipe != std::wstring::npos)
    {
        size_t top = row.text.rfind(L"Top", pipe);
        if (top != std::wstring::npos)
            WriteAt(h, 2 + static_cast<int>(top), y, L"Top", attr);
    }
}

static void DrawHidden(HANDLE hidden, int width, int height, const std::vector<Row>& rows, int selectedIndex)
{
    for (int y = 0; y < height; ++y)
        FillLine(hidden, y, width, gNormalAttr);

    WriteAt(hidden, 0, 0, L"WindowResizer NATIVE DLL selector", HeaderAttr());
    WriteAt(hidden, 0, 1, L"Up/Down PgUp/PgDn Home/End select | Left/Right pan | Letters jump | Enter accept | Esc cancel", gNormalAttr);
    FillLine(hidden, 2, width, gNormalAttr);

    for (int i = 0; i < static_cast<int>(rows.size()); ++i)
    {
        int y = kHeaderLines + i;
        if (y >= height)
            break;

        bool selected = i == selectedIndex;
        WORD attr = selected ? SelectedAttr() : gNormalAttr;
        FillLine(hidden, y, width, attr);

        std::wstring line = selected ? L"> " : L"  ";
        line += rows[static_cast<size_t>(i)].text;
        WriteAt(hidden, 0, y, line, attr);

        if (rows[static_cast<size_t>(i)].isTop && !selected)
            DrawTopMarkers(hidden, y, rows[static_cast<size_t>(i)], YellowAttr());
    }
}

static HiddenCache BuildHiddenCache(HANDLE hidden, int hiddenWidth, int hiddenHeight, const std::vector<Row>& rows, int selectedIndex)
{
    HiddenCache cache;
    cache.width = hiddenWidth;
    cache.height = hiddenHeight;
    cache.selectedIndex = selectedIndex;
    cache.cells.resize(static_cast<size_t>(hiddenWidth) * static_cast<size_t>(hiddenHeight));

    COORD size{static_cast<SHORT>(hiddenWidth), static_cast<SHORT>(hiddenHeight)};
    SetConsoleScreenBufferSize(hidden, size);
    DrawHidden(hidden, hiddenWidth, hiddenHeight, rows, selectedIndex);

    COORD bufferSize{static_cast<SHORT>(hiddenWidth), static_cast<SHORT>(hiddenHeight)};
    COORD bufferCoord{0, 0};
    SMALL_RECT source{0, 0, static_cast<SHORT>(hiddenWidth - 1), static_cast<SHORT>(hiddenHeight - 1)};
    ReadConsoleOutputW(hidden, cache.cells.data(), bufferSize, bufferCoord, &source);

    return cache;
}

static void RenderVisible(const HiddenCache& cache, int virtualLeft, int virtualTop, int selectedIndex, int rowCount, int visibleWidth, int visibleHeight)
{
    if (visibleWidth <= 0 || visibleHeight <= 0)
        return;

    std::vector<CHAR_INFO> frame(static_cast<size_t>(visibleWidth) * static_cast<size_t>(visibleHeight), MakeCell(L' ', gNormalAttr));

    int fixedHeader = std::min(kHeaderLines, visibleHeight);
    for (int y = 0; y < fixedHeader; ++y)
        for (int x = 0; x < visibleWidth; ++x)
            frame[static_cast<size_t>(y) * visibleWidth + x] = CellAt(cache, x, y);

    int footerRow = visibleHeight - 1;
    int bodyHeight = std::max(0, visibleHeight - kHeaderLines - 1);
    for (int y = 0; y < bodyHeight; ++y)
    {
        int sourceY = kHeaderLines + virtualTop + y;
        int screenY = kHeaderLines + y;
        for (int x = 0; x < visibleWidth; ++x)
            frame[static_cast<size_t>(screenY) * visibleWidth + x] = CellAt(cache, virtualLeft + x, sourceY);
    }

    WORD statusAttr = gNormalAttr;
    for (int x = 0; x < visibleWidth; ++x)
        frame[static_cast<size_t>(footerRow) * visibleWidth + x] = MakeCell(L' ', statusAttr);

    std::wstring status = L"NATIVE DLL selector | rows=" + std::to_wstring(rowCount) +
        L" selected=" + std::to_wstring(selectedIndex + 1) + L"/" + std::to_wstring(rowCount) +
        L" left=" + std::to_wstring(virtualLeft) + L" top=" + std::to_wstring(virtualTop) +
        L" size=" + std::to_wstring(visibleWidth) + L"x" + std::to_wstring(visibleHeight);
    PutAscii(frame, visibleWidth, visibleHeight, 0, footerRow, status, statusAttr);

    COORD bufferSize{static_cast<SHORT>(visibleWidth), static_cast<SHORT>(visibleHeight)};
    COORD bufferCoord{0, 0};
    SMALL_RECT dest{0, 0, static_cast<SHORT>(visibleWidth - 1), static_cast<SHORT>(visibleHeight - 1)};
    WriteConsoleOutputW(gVisibleOut, frame.data(), bufferSize, bufferCoord, &dest);
}

static wchar_t FirstJumpLetter(const std::wstring& text)
{
    for (wchar_t ch : text)
    {
        if (iswalnum(ch))
            return static_cast<wchar_t>(towupper(ch));
    }
    return L'\0';
}

static void JumpToLetter(const std::vector<Row>& rows, wchar_t key, int& selectedIndex)
{
    if (rows.empty() || key == L'\0')
        return;
    key = static_cast<wchar_t>(towupper(key));
    int count = static_cast<int>(rows.size());
    for (int step = 1; step <= count; ++step)
    {
        int idx = (selectedIndex + step) % count;
        if (FirstJumpLetter(rows[static_cast<size_t>(idx)].text) == key)
        {
            selectedIndex = idx;
            return;
        }
    }
}

static int RunSelector(const std::vector<Row>& rows, int initialIndex, int* selectedIndexOut)
{
    if (rows.empty())
        return -2;

    gOriginalOut = GetStdHandle(STD_OUTPUT_HANDLE);
    gInput = GetStdHandle(STD_INPUT_HANDLE);
    if (gOriginalOut == INVALID_HANDLE_VALUE || gInput == INVALID_HANDLE_VALUE)
        return -3;

    CONSOLE_SCREEN_BUFFER_INFO originalInfo{};
    if (GetConsoleScreenBufferInfo(gOriginalOut, &originalInfo))
        gNormalAttr = originalInfo.wAttributes;
    GetConsoleCursorInfo(gOriginalOut, &gOriginalCursor);
    SetupInput();

    gVisibleOut = CreateBuffer();
    gHiddenOut = CreateBuffer();
    if (gVisibleOut == INVALID_HANDLE_VALUE || gHiddenOut == INVALID_HANDLE_VALUE)
        return -4;

    int originalWidth = RectWidth(originalInfo.srWindow);
    int originalHeight = RectHeight(originalInfo.srWindow);
    if (originalWidth < 80) originalWidth = 80;
    if (originalHeight < 25) originalHeight = 25;
    COORD visibleSize{static_cast<SHORT>(originalWidth), static_cast<SHORT>(originalHeight)};
    SetConsoleScreenBufferSize(gVisibleOut, visibleSize);

    SetConsoleActiveScreenBuffer(gVisibleOut);
    HideCursor(gVisibleOut);

    int visibleWidth = originalWidth;
    int visibleHeight = originalHeight;
    GetVisibleSize(visibleWidth, visibleHeight);

    int selectedIndex = ClampInt(initialIndex, 0, static_cast<int>(rows.size()) - 1);
    int virtualTop = 0;
    int virtualLeft = 0;
    int lastWidth = -1;
    int lastHeight = -1;
    int hiddenWidth = 0;
    int hiddenHeight = 0;
    bool rebuild = true;
    bool dirty = true;
    HiddenCache cache;

    while (true)
    {
        int currentWidth = visibleWidth;
        int currentHeight = visibleHeight;
        if (GetVisibleSize(currentWidth, currentHeight))
        {
            if (currentWidth != lastWidth || currentHeight != lastHeight)
            {
                visibleWidth = currentWidth;
                visibleHeight = currentHeight;
                lastWidth = currentWidth;
                lastHeight = currentHeight;
                rebuild = true;
                dirty = true;
            }
        }

        int bodyHeight = std::max(1, visibleHeight - kHeaderLines - 1);
        int maxTop = std::max(0, static_cast<int>(rows.size()) - bodyHeight);
        if (selectedIndex < virtualTop)
            virtualTop = selectedIndex;
        if (selectedIndex >= virtualTop + bodyHeight)
            virtualTop = selectedIndex - bodyHeight + 1;
        virtualTop = ClampInt(virtualTop, 0, maxTop);

        int neededHiddenWidth = RequiredHiddenWidth(rows, visibleWidth);
        int neededHiddenHeight = std::max(std::max(25, visibleHeight), kHeaderLines + static_cast<int>(rows.size()) + 1);
        if (neededHiddenWidth != hiddenWidth || neededHiddenHeight != hiddenHeight)
        {
            hiddenWidth = neededHiddenWidth;
            hiddenHeight = neededHiddenHeight;
            rebuild = true;
        }

        int maxLeft = std::max(0, hiddenWidth - visibleWidth);
        virtualLeft = ClampInt(virtualLeft, 0, maxLeft);

        if (rebuild || cache.cells.empty() || cache.selectedIndex != selectedIndex)
        {
            cache = BuildHiddenCache(gHiddenOut, hiddenWidth, hiddenHeight, rows, selectedIndex);
            rebuild = false;
            dirty = true;
        }

        if (dirty)
        {
            RenderVisible(cache, virtualLeft, virtualTop, selectedIndex, static_cast<int>(rows.size()), visibleWidth, visibleHeight);
            HideCursor(gVisibleOut);
            dirty = false;
        }

        DWORD wait = WaitForSingleObject(gInput, 25);
        if (wait != WAIT_OBJECT_0)
            continue;

        DWORD count = 0;
        if (!GetNumberOfConsoleInputEvents(gInput, &count) || count == 0)
            continue;

        std::vector<INPUT_RECORD> events(count);
        DWORD read = 0;
        if (!ReadConsoleInputW(gInput, events.data(), count, &read))
            continue;
        events.resize(read);

        for (const auto& e : events)
        {
            if (e.EventType == WINDOW_BUFFER_SIZE_EVENT)
            {
                dirty = true;
                rebuild = true;
                continue;
            }

            if (e.EventType == MOUSE_EVENT)
            {
                const MOUSE_EVENT_RECORD& m = e.Event.MouseEvent;
                if (m.dwEventFlags == MOUSE_WHEELED)
                {
                    short delta = static_cast<short>((m.dwButtonState >> 16) & 0xffff);
                    int steps = std::max(1, std::abs(static_cast<int>(delta)) / WHEEL_DELTA);
                    int old = selectedIndex;
                    selectedIndex = delta > 0
                        ? std::max(0, selectedIndex - steps)
                        : std::min(static_cast<int>(rows.size()) - 1, selectedIndex + steps);
                    if (old != selectedIndex)
                    {
                        rebuild = true;
                        dirty = true;
                    }
                    continue;
                }

                bool leftDown = (m.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0;
                if (leftDown && (m.dwEventFlags == 0 || m.dwEventFlags == DOUBLE_CLICK))
                {
                    int bodyRow = m.dwMousePosition.Y - kHeaderLines;
                    if (bodyRow >= 0 && bodyRow < bodyHeight)
                    {
                        int idx = virtualTop + bodyRow;
                        if (idx >= 0 && idx < static_cast<int>(rows.size()))
                        {
                            selectedIndex = idx;
                            rebuild = true;
                            dirty = true;
                            if (m.dwEventFlags == DOUBLE_CLICK)
                            {
                                if (selectedIndexOut) *selectedIndexOut = rows[static_cast<size_t>(selectedIndex)].sequence;
                                return 1;
                            }
                        }
                    }
                }
                continue;
            }

            if (e.EventType != KEY_EVENT || !e.Event.KeyEvent.bKeyDown)
                continue;

            const KEY_EVENT_RECORD& k = e.Event.KeyEvent;
            int old = selectedIndex;

            switch (k.wVirtualKeyCode)
            {
            case VK_ESCAPE:
                return 0;
            case VK_RETURN:
                if (selectedIndexOut) *selectedIndexOut = rows[static_cast<size_t>(selectedIndex)].sequence;
                return 1;
            case VK_UP:
                selectedIndex = std::max(0, selectedIndex - 1);
                break;
            case VK_DOWN:
                selectedIndex = std::min(static_cast<int>(rows.size()) - 1, selectedIndex + 1);
                break;
            case VK_PRIOR:
                selectedIndex = std::max(0, selectedIndex - bodyHeight);
                break;
            case VK_NEXT:
                selectedIndex = std::min(static_cast<int>(rows.size()) - 1, selectedIndex + bodyHeight);
                break;
            case VK_HOME:
                if (k.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED))
                    virtualLeft = 0;
                else
                    selectedIndex = 0;
                dirty = true;
                break;
            case VK_END:
                if (k.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED))
                    virtualLeft = maxLeft;
                else
                    selectedIndex = static_cast<int>(rows.size()) - 1;
                dirty = true;
                break;
            case VK_LEFT:
                virtualLeft = std::max(0, virtualLeft - 1);
                dirty = true;
                break;
            case VK_RIGHT:
                virtualLeft = std::min(maxLeft, virtualLeft + 1);
                dirty = true;
                break;
            default:
                if (k.uChar.UnicodeChar != L'\0')
                    JumpToLetter(rows, k.uChar.UnicodeChar, selectedIndex);
                break;
            }

            if (old != selectedIndex)
            {
                rebuild = true;
                dirty = true;
            }
        }
    }
}

extern "C" __declspec(dllexport)
int __stdcall SelectWindowFromRows(const NativeSelectorRow* nativeRows, int rowCount, int initialIndex, int* selectedIndexOut)
{
    int result = -1;
    if (selectedIndexOut)
        *selectedIndexOut = 0;
    try
{
        if (!nativeRows || rowCount <= 0)
            result = -2;
        else
        {
            std::vector<Row> rows;
            rows.reserve(static_cast<size_t>(rowCount));
            for (int i = 0; i < rowCount; ++i)
            {
                Row r;
                r.sequence = nativeRows[i].Sequence;
                r.isTop = nativeRows[i].IsTopForProcess != 0;
                r.text = nativeRows[i].DisplayText ? nativeRows[i].DisplayText : L"";
                if (!r.text.empty())
                    rows.push_back(std::move(r));
            }
            result = RunSelector(rows, initialIndex, selectedIndexOut);
        }
    }
    catch (...)
{
        result = -101;
    }

    if (gOriginalOut != INVALID_HANDLE_VALUE)
        SetConsoleActiveScreenBuffer(gOriginalOut);
    if (gOriginalOut != INVALID_HANDLE_VALUE)
        SetConsoleCursorInfo(gOriginalOut, &gOriginalCursor);
    if (gInput != INVALID_HANDLE_VALUE && gOriginalInputMode != 0)
        SetConsoleMode(gInput, gOriginalInputMode);
    if (gVisibleOut != INVALID_HANDLE_VALUE)
    {
        CloseHandle(gVisibleOut);
        gVisibleOut = INVALID_HANDLE_VALUE;
    }
    if (gHiddenOut != INVALID_HANDLE_VALUE)
    {
        CloseHandle(gHiddenOut);
        gHiddenOut = INVALID_HANDLE_VALUE;
    }

    return result;
}


