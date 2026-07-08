
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif#include <windows.h>
#include <algorithm>
#include <cwctype>
#include <sstream>
#include <string>
#include <vector>

static const WORD kNormalAttr = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE;
static const WORD kHeaderAttr = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY;
static const WORD kStatusAttr = BACKGROUND_BLUE | FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY;
static const WORD kSelectedAttr = BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE;
static const WORD kSelectedMarkerAttr = BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE |
    FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY;

static HANDLE gOriginalOut = INVALID_HANDLE_VALUE;
static HANDLE gVisibleOut = INVALID_HANDLE_VALUE;
static HANDLE gHiddenOut = INVALID_HANDLE_VALUE;
static HANDLE gInput = INVALID_HANDLE_VALUE;
static DWORD gOriginalInputMode = 0;
static CONSOLE_CURSOR_INFO gOriginalCursor{};

struct HiddenCache
{
    int width = 0;
    int height = 0;
    int selectedIndex = -1;
    std::vector<CHAR_INFO> cells;
};

static int BodyTop()
{
    return 4;
}

static int HiddenStartRow()
{
    return 4;
}

static int ClampInt(int v, int lo, int hi)
{
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
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

static int EstimatedConsoleCellWidth(const std::wstring& s)
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

        cells += cp < 0x80 ? 1 : 2;
    }

    return cells;
}

static std::vector<std::wstring> SplitRows(const wchar_t* rowsText)
{
    std::vector<std::wstring> rows;

    if (!rowsText || !*rowsText)
        return rows;

    const wchar_t* start = rowsText;
    const wchar_t* p = rowsText;

    while (*p)
    {
        if (*p == L'\n')
        {
            std::wstring row(start, p - start);
            if (!row.empty() && row.back() == L'\r')
                row.pop_back();
            rows.push_back(row);
            start = p + 1;
        }
        ++p;
    }

    std::wstring row(start, p - start);
    if (!row.empty() && row.back() == L'\r')
        row.pop_back();
    if (!row.empty())
        rows.push_back(row);

    return rows;
}

static int RequiredHiddenWidth(const std::vector<std::wstring>& rows, int visibleWidth)
{
    int width = std::max(visibleWidth, 120);
    width = std::max(width, EstimatedConsoleCellWidth(L"WindowResizer native selector") + 8);
    width = std::max(width, EstimatedConsoleCellWidth(L"Arrows move, PgUp/PgDn, Home/End, Left/Right pan, mouse wheel/click, Enter, Esc") + 8);

    for (const auto& row : rows)
        width = std::max(width, EstimatedConsoleCellWidth(L"> ") + EstimatedConsoleCellWidth(row) + 16);

    return std::min(width, 32000);
}

static bool GetInfo(HANDLE h, CONSOLE_SCREEN_BUFFER_INFO& info)
{
    return GetConsoleScreenBufferInfo(h, &info) != FALSE;
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

static void FillLine(HANDLE h, short y, short width, WORD attr)
{
    DWORD written = 0;
    COORD pos{0, y};
    FillConsoleOutputCharacterW(h, L' ', width, pos, &written);
    FillConsoleOutputAttribute(h, attr, width, pos, &written);
}

static void WriteAt(HANDLE h, short x, short y, const std::wstring& text, WORD attr)
{
    COORD pos{x, y};
    DWORD written = 0;
    SetConsoleCursorPosition(h, pos);
    SetConsoleTextAttribute(h, attr);
    WriteConsoleW(h, text.c_str(), static_cast<DWORD>(text.size()), &written, nullptr);
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

static bool NormalizeVisibleBufferToWindow(int& visibleWidth, int& visibleHeight)
{
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (!GetInfo(gVisibleOut, info))
        return false;

    visibleWidth = RectWidth(info.srWindow);
    visibleHeight = RectHeight(info.srWindow);

    if (visibleWidth < 1) visibleWidth = 1;
    if (visibleHeight < 1) visibleHeight = 1;

    if (info.dwSize.X < visibleWidth || info.dwSize.Y < visibleHeight)
    {
        COORD grow{};
        grow.X = static_cast<SHORT>(std::max<int>(info.dwSize.X, visibleWidth));
        grow.Y = static_cast<SHORT>(std::max<int>(info.dwSize.Y, visibleHeight));
        SetConsoleScreenBufferSize(gVisibleOut, grow);
    }

    SMALL_RECT win{};
    win.Left = 0;
    win.Top = 0;
    win.Right = static_cast<SHORT>(visibleWidth - 1);
    win.Bottom = static_cast<SHORT>(visibleHeight - 1);
    SetConsoleWindowInfo(gVisibleOut, TRUE, &win);

    COORD exact{};
    exact.X = static_cast<SHORT>(visibleWidth);
    exact.Y = static_cast<SHORT>(visibleHeight);
    SetConsoleScreenBufferSize(gVisibleOut, exact);

    CONSOLE_SCREEN_BUFFER_INFO after{};
    if (!GetInfo(gVisibleOut, after))
        return false;

    visibleWidth = RectWidth(after.srWindow);
    visibleHeight = RectHeight(after.srWindow);
    return true;
}

static void SetupInput()
{
    GetConsoleMode(gInput, &gOriginalInputMode);

    DWORD mode = gOriginalInputMode;
    mode |= ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS;
    mode &= ~ENABLE_QUICK_EDIT_MODE;
    SetConsoleMode(gInput, mode);
}

static void DrawHiddenBuffer(HANDLE hidden, int hiddenWidth, int hiddenHeight, const std::vector<std::wstring>& rows, int selectedIndex)
{
    for (int y = 0; y < hiddenHeight; ++y)
        FillLine(hidden, static_cast<short>(y), static_cast<short>(hiddenWidth), kNormalAttr);

    WriteAt(hidden, 0, 0, L"WindowResizer native selector", kHeaderAttr);
    WriteAt(hidden, 0, 1, L"Up/Down select, PgUp/PgDn, Home/End, Left/Right pan, mouse wheel/click, Enter accepts, Esc cancels", kHeaderAttr);

    std::wstring ruler;
    for (int i = 0; i < hiddenWidth; ++i)
        ruler += wchar_t(L'0' + (i % 10));
    WriteAt(hidden, 0, 2, ruler, kHeaderAttr);

    const int start = HiddenStartRow();
    for (int i = 0; i < static_cast<int>(rows.size()) && start + i < hiddenHeight; ++i)
    {
        bool selected = i == selectedIndex;
        WORD attr = selected ? kSelectedAttr : kNormalAttr;
        short y = static_cast<short>(start + i);

        FillLine(hidden, y, static_cast<short>(hiddenWidth), attr);

        std::wstring line = selected ? L"> " : L"  ";
        line += rows[static_cast<size_t>(i)];
        WriteAt(hidden, 0, y, line, attr);

        if (selected)
            WriteAt(hidden, 0, y, L">", kSelectedMarkerAttr);
    }
}

static HiddenCache BuildHiddenCache(HANDLE hidden, int hiddenWidth, int hiddenHeight, const std::vector<std::wstring>& rows, int selectedIndex)
{
    HiddenCache cache;
    cache.width = hiddenWidth;
    cache.height = hiddenHeight;
    cache.selectedIndex = selectedIndex;
    cache.cells.resize(static_cast<size_t>(hiddenWidth) * hiddenHeight);

    COORD size{};
    size.X = static_cast<SHORT>(hiddenWidth);
    size.Y = static_cast<SHORT>(hiddenHeight);
    SetConsoleScreenBufferSize(hidden, size);

    SMALL_RECT win{};
    win.Left = 0;
    win.Top = 0;
    win.Right = static_cast<SHORT>(std::min(hiddenWidth, 120) - 1);
    win.Bottom = static_cast<SHORT>(std::min(hiddenHeight, 30) - 1);
    SetConsoleWindowInfo(hidden, TRUE, &win);

    DrawHiddenBuffer(hidden, hiddenWidth, hiddenHeight, rows, selectedIndex);

    COORD bufferSize{static_cast<SHORT>(hiddenWidth), static_cast<SHORT>(hiddenHeight)};
    COORD bufferCoord{0, 0};
    SMALL_RECT source{0, 0, static_cast<SHORT>(hiddenWidth - 1), static_cast<SHORT>(hiddenHeight - 1)};
    ReadConsoleOutputW(hidden, cache.cells.data(), bufferSize, bufferCoord, &source);

    return cache;
}

static void PutText(std::vector<CHAR_INFO>& frame, int width, int height, int x, int y, const std::wstring& text, WORD attr)
{
    if (y < 0 || y >= height)
        return;

    for (int i = 0; i < static_cast<int>(text.size()); ++i)
    {
        int xx = x + i;
        if (xx < 0 || xx >= width)
            continue;

        frame[static_cast<size_t>(y) * width + xx].Char.UnicodeChar = text[static_cast<size_t>(i)];
        frame[static_cast<size_t>(y) * width + xx].Attributes = attr;
    }
}

static CHAR_INFO CellAt(const HiddenCache& cache, int x, int y)
{
    if (x < 0 || y < 0 || x >= cache.width || y >= cache.height)
        return MakeCell(L' ', kNormalAttr);
    return cache.cells[static_cast<size_t>(y) * cache.width + x];
}

static void RenderVisibleFromCache(const HiddenCache& cache, int virtualLeft, int virtualTop, int selectedIndex, int visibleWidth, int visibleHeight)
{
    if (visibleWidth <= 0 || visibleHeight <= 0)
        return;

    std::vector<CHAR_INFO> frame(static_cast<size_t>(visibleWidth) * visibleHeight, MakeCell(L' ', kNormalAttr));

    // Header is fixed. Body is horizontally and vertically panned from the hidden rendered cells.
    for (int y = 0; y < std::min(BodyTop(), visibleHeight); ++y)
    {
        for (int x = 0; x < visibleWidth; ++x)
            frame[static_cast<size_t>(y) * visibleWidth + x] = CellAt(cache, x, y);
    }

    int footerRow = visibleHeight - 1;
    int bodyHeight = std::max(0, visibleHeight - BodyTop() - 1);

    for (int y = 0; y < bodyHeight; ++y)
    {
        int sourceY = HiddenStartRow() + virtualTop + y;
        int screenY = BodyTop() + y;

        for (int x = 0; x < visibleWidth; ++x)
            frame[static_cast<size_t>(screenY) * visibleWidth + x] = CellAt(cache, virtualLeft + x, sourceY);
    }

    std::wstring status =
        L" width=" + std::to_wstring(visibleWidth) +
        L" height=" + std::to_wstring(visibleHeight) +
        L" left=" + std::to_wstring(virtualLeft) +
        L" top=" + std::to_wstring(virtualTop) +
        L" selected=" + std::to_wstring(selectedIndex) +
        L" hidden=" + std::to_wstring(cache.width) + L"x" + std::to_wstring(cache.height) + L" ";

    for (int x = 0; x < visibleWidth; ++x)
        frame[static_cast<size_t>(footerRow) * visibleWidth + x] = MakeCell(L' ', kStatusAttr);
    PutText(frame, visibleWidth, visibleHeight, 0, footerRow, status, kStatusAttr);

    COORD bufferSize{static_cast<SHORT>(visibleWidth), static_cast<SHORT>(visibleHeight)};
    COORD bufferCoord{0, 0};
    SMALL_RECT dest{0, 0, static_cast<SHORT>(visibleWidth - 1), static_cast<SHORT>(visibleHeight - 1)};
    WriteConsoleOutputW(gVisibleOut, frame.data(), bufferSize, bufferCoord, &dest);
}

static wchar_t FirstProcessLetter(const std::wstring& row)
{
    for (wchar_t ch : row)
    {
        if (iswalpha(ch) || iswdigit(ch))
            return static_cast<wchar_t>(towupper(ch));
    }
    return L'\0';
}

static void JumpToProcessLetter(const std::vector<std::wstring>& rows, wchar_t key, int& selectedIndex)
{
    if (rows.empty() || key == L'\0')
        return;

    key = static_cast<wchar_t>(towupper(key));
    int count = static_cast<int>(rows.size());

    for (int step = 1; step <= count; ++step)
    {
        int index = (selectedIndex + step) % count;
        if (FirstProcessLetter(rows[static_cast<size_t>(index)]) == key)
        {
            selectedIndex = index;
            return;
        }
    }
}

static bool ReadInputEvents(std::vector<INPUT_RECORD>& events)
{
    DWORD count = 0;
    if (!GetNumberOfConsoleInputEvents(gInput, &count))
        return false;

    if (count == 0)
        return true;

    events.resize(count);
    DWORD read = 0;
    if (!ReadConsoleInputW(gInput, events.data(), count, &read))
        return false;

    events.resize(read);
    return true;
}

static int RunSelector(const std::vector<std::wstring>& rows, int initialIndex, int* selectedIndexOut)
{
    if (rows.empty())
        return -2;

    gOriginalOut = GetStdHandle(STD_OUTPUT_HANDLE);
    gInput = GetStdHandle(STD_INPUT_HANDLE);

    if (gOriginalOut == INVALID_HANDLE_VALUE || gInput == INVALID_HANDLE_VALUE)
        return -3;

    GetConsoleCursorInfo(gOriginalOut, &gOriginalCursor);
    SetupInput();

    gVisibleOut = CreateBuffer();
    gHiddenOut = CreateBuffer();

    if (gVisibleOut == INVALID_HANDLE_VALUE || gHiddenOut == INVALID_HANDLE_VALUE)
        return -4;

    int visibleWidth = 0;
    int visibleHeight = 0;

    SetConsoleActiveScreenBuffer(gVisibleOut);
    HideCursor(gVisibleOut);
    NormalizeVisibleBufferToWindow(visibleWidth, visibleHeight);

    int selectedIndex = ClampInt(initialIndex, 0, static_cast<int>(rows.size()) - 1);
    int virtualTop = 0;
    int virtualLeft = 0;
    int lastWidth = -1;
    int lastHeight = -1;
    bool dirty = true;
    bool rebuildHidden = true;

    int hiddenWidth = RequiredHiddenWidth(rows, visibleWidth);
    int hiddenHeight = std::max(visibleHeight, HiddenStartRow() + static_cast<int>(rows.size()) + 2);
    HiddenCache cache;

    while (true)
    {
        int currentWidth = 0;
        int currentHeight = 0;
        if (NormalizeVisibleBufferToWindow(currentWidth, currentHeight))
        {
            visibleWidth = currentWidth;
            visibleHeight = currentHeight;

            if (visibleWidth != lastWidth || visibleHeight != lastHeight)
            {
                lastWidth = visibleWidth;
                lastHeight = visibleHeight;
                hiddenWidth = std::max(hiddenWidth, RequiredHiddenWidth(rows, visibleWidth));
                hiddenHeight = std::max(visibleHeight, HiddenStartRow() + static_cast<int>(rows.size()) + 2);
                dirty = true;
            }
        }

        if (rebuildHidden || cache.cells.empty() || cache.width != hiddenWidth || cache.height != hiddenHeight || cache.selectedIndex != selectedIndex)
        {
            cache = BuildHiddenCache(gHiddenOut, hiddenWidth, hiddenHeight, rows, selectedIndex);
            rebuildHidden = false;
            dirty = true;
        }

        int bodyHeight = std::max(1, visibleHeight - BodyTop() - 1);
        int maxTop = std::max(0, static_cast<int>(rows.size()) - bodyHeight);
        int maxLeft = std::max(0, hiddenWidth - visibleWidth);

        if (selectedIndex < virtualTop)
            virtualTop = selectedIndex;
        if (selectedIndex >= virtualTop + bodyHeight)
            virtualTop = selectedIndex - bodyHeight + 1;

        virtualTop = ClampInt(virtualTop, 0, maxTop);
        virtualLeft = ClampInt(virtualLeft, 0, maxLeft);

        if (dirty)
        {
            RenderVisibleFromCache(cache, virtualLeft, virtualTop, selectedIndex, visibleWidth, visibleHeight);
            HideCursor(gVisibleOut);
            dirty = false;
        }

        DWORD wait = WaitForSingleObject(gInput, 40);
        if (wait != WAIT_OBJECT_0)
            continue;

        std::vector<INPUT_RECORD> events;
        if (!ReadInputEvents(events))
            continue;

        for (const auto& e : events)
        {
            if (e.EventType == WINDOW_BUFFER_SIZE_EVENT)
            {
                dirty = true;
                continue;
            }

            if (e.EventType == MOUSE_EVENT)
            {
                const MOUSE_EVENT_RECORD& m = e.Event.MouseEvent;

                if (m.dwEventFlags == MOUSE_WHEELED)
                {
                    short wheelDelta = static_cast<short>((m.dwButtonState >> 16) & 0xffff);
                    int wheelAbs = wheelDelta < 0 ? -static_cast<int>(wheelDelta) : static_cast<int>(wheelDelta);
                    int steps = std::max(1, wheelAbs / WHEEL_DELTA);
                    int old = selectedIndex;
                    selectedIndex = wheelDelta > 0
                        ? std::max(0, selectedIndex - steps)
                        : std::min(static_cast<int>(rows.size()) - 1, selectedIndex + steps);
                    if (selectedIndex != old)
                    {
                        rebuildHidden = true;
                        dirty = true;
                    }
                    continue;
                }

                bool leftDown = (m.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0;
                if (leftDown && (m.dwEventFlags == 0 || m.dwEventFlags == DOUBLE_CLICK))
                {
                    int row = m.dwMousePosition.Y - BodyTop();
                    if (row >= 0 && row < bodyHeight)
                    {
                        int index = virtualTop + row;
                        if (index >= 0 && index < static_cast<int>(rows.size()))
                        {
                            selectedIndex = index;
                            rebuildHidden = true;
                            dirty = true;

                            if (m.dwEventFlags == DOUBLE_CLICK)
                            {
                                if (selectedIndexOut) *selectedIndexOut = selectedIndex;
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
            int oldSelected = selectedIndex;
            bool selectionChanged = false;

            switch (k.wVirtualKeyCode)
            {
            case VK_ESCAPE:
                return 0;

            case VK_RETURN:
                if (selectedIndexOut) *selectedIndexOut = selectedIndex;
                return 1;

            case VK_UP:
                selectedIndex = std::max(0, selectedIndex - 1);
                selectionChanged = selectedIndex != oldSelected;
                break;

            case VK_DOWN:
                selectedIndex = std::min(static_cast<int>(rows.size()) - 1, selectedIndex + 1);
                selectionChanged = selectedIndex != oldSelected;
                break;

            case VK_PRIOR:
                selectedIndex = std::max(0, selectedIndex - bodyHeight);
                selectionChanged = selectedIndex != oldSelected;
                break;

            case VK_NEXT:
                selectedIndex = std::min(static_cast<int>(rows.size()) - 1, selectedIndex + bodyHeight);
                selectionChanged = selectedIndex != oldSelected;
                break;

            case VK_HOME:
                if (k.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED))
                    virtualLeft = 0;
                else
                    selectedIndex = 0;
                selectionChanged = selectedIndex != oldSelected;
                dirty = true;
                break;

            case VK_END:
                if (k.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED))
                    virtualLeft = maxLeft;
                else
                    selectedIndex = static_cast<int>(rows.size()) - 1;
                selectionChanged = selectedIndex != oldSelected;
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
                {
                    JumpToProcessLetter(rows, k.uChar.UnicodeChar, selectedIndex);
                    selectionChanged = selectedIndex != oldSelected;
                    dirty = true;
                }
                break;
            }

            if (selectionChanged)
            {
                rebuildHidden = true;
                dirty = true;
            }
        }
    }
}

extern "C" __declspec(dllexport) int __stdcall SelectWindowFromRows(const wchar_t* rowsText, int initialIndex, int* selectedIndexOut)
{
    int result = -1;

    try
    {
        std::vector<std::wstring> rows = SplitRows(rowsText);
        result = RunSelector(rows, initialIndex, selectedIndexOut);
    }
    catch (...)
    {
        result = -100;
    }

    if (gOriginalOut != INVALID_HANDLE_VALUE)
        SetConsoleActiveScreenBuffer(gOriginalOut);

    if (gOriginalOut != INVALID_HANDLE_VALUE)
        SetConsoleCursorInfo(gOriginalOut, &gOriginalCursor);

    if (gInput != INVALID_HANDLE_VALUE)
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

