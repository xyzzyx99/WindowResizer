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
    int sequence = -1;
    int processId = 0;
    intptr_t hwnd = 0;
    bool top = false;
    std::wstring text;
};

static HANDLE gOriginalOut = INVALID_HANDLE_VALUE;
static HANDLE gVisibleOut = INVALID_HANDLE_VALUE;
static HANDLE gHiddenOut = INVALID_HANDLE_VALUE;
static HANDLE gInput = INVALID_HANDLE_VALUE;
static DWORD gOriginalInputMode = 0;
static CONSOLE_CURSOR_INFO gOriginalCursor{};
static bool gHaveOriginalCursor = false;

static const WORD FG_BLACK = 0;
static const WORD FG_BLUE = FOREGROUND_BLUE;
static const WORD FG_GREEN = FOREGROUND_GREEN;
static const WORD FG_CYAN = FOREGROUND_GREEN | FOREGROUND_BLUE;
static const WORD FG_RED = FOREGROUND_RED;
static const WORD FG_MAGENTA = FOREGROUND_RED | FOREGROUND_BLUE;
static const WORD FG_YELLOW = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY;
static const WORD FG_WHITE = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE;
static const WORD FG_BRIGHT_WHITE = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY;
static const WORD BG_GREY = BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE;

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

static WORD ForegroundPart(WORD attr)
{
    return attr & (FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY);
}

static WORD BackgroundPart(WORD attr)
{
    return attr & (BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE | BACKGROUND_INTENSITY);
}

static WORD BackgroundAsForeground(WORD attr)
{
    WORD bg = BackgroundPart(attr);
    WORD fg = 0;
    if (bg & BACKGROUND_RED) fg |= FOREGROUND_RED;
    if (bg & BACKGROUND_GREEN) fg |= FOREGROUND_GREEN;
    if (bg & BACKGROUND_BLUE) fg |= FOREGROUND_BLUE;
    if (bg & BACKGROUND_INTENSITY) fg |= FOREGROUND_INTENSITY;

    // If the terminal background is black/default, black on grey matches the
    // intended inverse-style look better than white on grey.
    return fg ? fg : FG_BLACK;
}

static bool GetConsoleInfo(HANDLE h, CONSOLE_SCREEN_BUFFER_INFO& info)
{
    return h != INVALID_HANDLE_VALUE && GetConsoleScreenBufferInfo(h, &info) != FALSE;
}

static void SetWholeLineAttr(HANDLE h, short y, short width, WORD attr)
{
    if (width <= 0) return;
    DWORD written = 0;
    COORD pos{0, y};
    FillConsoleOutputCharacterW(h, L' ', static_cast<DWORD>(width), pos, &written);
    FillConsoleOutputAttribute(h, attr, static_cast<DWORD>(width), pos, &written);
}

static void WriteAt(HANDLE h, short x, short y, const std::wstring& text, WORD attr)
{
    if (text.empty()) return;
    DWORD written = 0;
    COORD pos{x, y};
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

static void HideCursor(HANDLE h)
{
    CONSOLE_CURSOR_INFO cursor{};
    if (GetConsoleCursorInfo(h, &cursor))
    {
        cursor.bVisible = FALSE;
        SetConsoleCursorInfo(h, &cursor);
    }
}

static int BodyTop()
{
    return 3;
}

static int FooterRows()
{
    return 1;
}

static int BodyHeight(int height)
{
    return std::max(1, height - BodyTop() - FooterRows());
}

static bool ReadVisibleSize(int& width, int& height)
{
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (!GetConsoleInfo(gVisibleOut, info))
        return false;

    width = std::max(1, RectWidth(info.srWindow));
    height = std::max(1, RectHeight(info.srWindow));
    return true;
}

static bool EnsureBufferSize(HANDLE h, int width, int height)
{
    if (h == INVALID_HANDLE_VALUE || width <= 0 || height <= 0)
        return false;

    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (!GetConsoleInfo(h, info))
        return false;

    if (info.dwSize.X == width && info.dwSize.Y == height)
        return true;

    // Window must fit within the target buffer size before SetConsoleScreenBufferSize.
    SMALL_RECT smallWin{};
    smallWin.Left = 0;
    smallWin.Top = 0;
    smallWin.Right = static_cast<SHORT>(std::max(0, std::min<int>(width, RectWidth(info.srWindow)) - 1));
    smallWin.Bottom = static_cast<SHORT>(std::max(0, std::min<int>(height, RectHeight(info.srWindow)) - 1));
    SetConsoleWindowInfo(h, TRUE, &smallWin);

    COORD size{};
    size.X = static_cast<SHORT>(ClampInt(width, 1, 32766));
    size.Y = static_cast<SHORT>(ClampInt(height, 1, 32766));
    return SetConsoleScreenBufferSize(h, size) != FALSE;
}

static void ConfigureVisibleBuffer(int width, int height)
{
    EnsureBufferSize(gVisibleOut, width, height);

    SMALL_RECT win{};
    win.Left = 0;
    win.Top = 0;
    win.Right = static_cast<SHORT>(width - 1);
    win.Bottom = static_cast<SHORT>(height - 1);
    SetConsoleWindowInfo(gVisibleOut, TRUE, &win);
}

static int TextLengthOrFallback(const std::wstring& s)
{
    return static_cast<int>(std::max<size_t>(1, s.size()));
}

static int ComputeHiddenWidth(const std::vector<Row>& rows, int visibleWidth)
{
    int width = std::max(visibleWidth, 120);
    for (const Row& row : rows)
        width = std::max(width, TextLengthOrFallback(row.text) + 8);
    return ClampInt(width, 1, 32000);
}

static int ComputeHiddenHeight(const std::vector<Row>& rows, int visibleHeight)
{
    return ClampInt(std::max(visibleHeight, BodyTop() + static_cast<int>(rows.size()) + FooterRows()), 1, 32000);
}

static wchar_t FirstJumpChar(const std::wstring& text)
{
    for (wchar_t ch : text)
    {
        if (iswalnum(ch))
            return static_cast<wchar_t>(towupper(ch));
    }
    return L'\0';
}

static bool JumpToLetter(const std::vector<Row>& rows, wchar_t key, int& selected)
{
    if (rows.empty() || key == L'\0')
        return false;

    key = static_cast<wchar_t>(towupper(key));
    int count = static_cast<int>(rows.size());
    for (int step = 1; step <= count; ++step)
    {
        int index = (selected + step) % count;
        if (FirstJumpChar(rows[static_cast<size_t>(index)].text) == key)
        {
            if (selected != index)
            {
                selected = index;
                return true;
            }
            return false;
        }
    }
    return false;
}

static std::wstring ToHex(intptr_t value)
{
    const wchar_t* digits = L"0123456789ABCDEF";
    uintptr_t v = static_cast<uintptr_t>(value);
    if (v == 0) return L"0";

    std::wstring s;
    while (v)
    {
        s.push_back(digits[v & 0xF]);
        v >>= 4;
    }
    std::reverse(s.begin(), s.end());
    return s;
}

static int FindTopMarkerStart(const std::wstring& text)
{
    size_t pos = text.find(L"Top");
    if (pos == std::wstring::npos)
        return -1;
    return static_cast<int>(pos);
}

static int FindProcessNameEnd(const std::wstring& text)
{
    size_t pos = text.find(L" [");
    if (pos == std::wstring::npos)
        pos = text.find(L" |");
    if (pos == std::wstring::npos)
        pos = text.find(L' ');
    if (pos == std::wstring::npos)
        return static_cast<int>(text.size());
    return static_cast<int>(pos);
}

static void WriteTopProcessYellowParts(HANDLE h, short y, const Row& row, int xBase, WORD attr)
{
    if (!row.top || row.text.empty())
        return;

    WORD yellowAttr = (attr & (BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE | BACKGROUND_INTENSITY)) | FG_YELLOW;

    int processEnd = FindProcessNameEnd(row.text);
    if (processEnd > 0)
        WriteAt(h, static_cast<short>(xBase), y, row.text.substr(0, static_cast<size_t>(processEnd)), yellowAttr);

    int topStart = FindTopMarkerStart(row.text);
    if (topStart >= 0)
        WriteAt(h, static_cast<short>(xBase + topStart), y, L"Top", yellowAttr);
}

static bool RenderHidden(const std::vector<Row>& rows, int selected, int hiddenWidth, int hiddenHeight, WORD normalAttr, WORD selectedAttr)
{
    if (!EnsureBufferSize(gHiddenOut, hiddenWidth, hiddenHeight))
        return false;

    SMALL_RECT win{};
    win.Left = 0;
    win.Top = 0;
    win.Right = static_cast<SHORT>(std::min(hiddenWidth, 120) - 1);
    win.Bottom = static_cast<SHORT>(std::min(hiddenHeight, 40) - 1);
    SetConsoleWindowInfo(gHiddenOut, TRUE, &win);

    for (int y = 0; y < hiddenHeight; ++y)
        SetWholeLineAttr(gHiddenOut, static_cast<short>(y), static_cast<short>(hiddenWidth), normalAttr);

    WriteAt(gHiddenOut, 0, 0, L"WindowResizer native selector", normalAttr | FOREGROUND_INTENSITY);
    WriteAt(gHiddenOut, 0, 1, L"Up/Down, PgUp/PgDn, Home/End, Left/Right pan, letter jump, Enter accept, Esc cancel", normalAttr);

    int start = BodyTop();
    for (int i = 0; i < static_cast<int>(rows.size()); ++i)
    {
        int y = start + i;
        if (y >= hiddenHeight)
            break;

        bool isSelected = (i == selected);
        WORD rowAttr = isSelected ? selectedAttr : normalAttr;
        short yy = static_cast<short>(y);

        SetWholeLineAttr(gHiddenOut, yy, static_cast<short>(hiddenWidth), rowAttr);

        std::wstring marker = isSelected ? L"> " : L"  ";
        WriteAt(gHiddenOut, 0, yy, marker, rowAttr);
        WriteAt(gHiddenOut, 2, yy, rows[static_cast<size_t>(i)].text, rowAttr);

        // Match C# scheme: no rainbow rows. Only top process name and Top are yellow.
        WriteTopProcessYellowParts(gHiddenOut, yy, rows[static_cast<size_t>(i)], 2, rowAttr);
    }

    return true;
}

static bool CopyViewportFromHidden(int hiddenWidth, int hiddenHeight, int visibleWidth, int visibleHeight, int virtualLeft, int virtualTop, WORD statusAttr, int rowCount, int selected)
{
    if (visibleWidth <= 0 || visibleHeight <= 0)
        return false;

    std::vector<CHAR_INFO> frame(static_cast<size_t>(visibleWidth) * static_cast<size_t>(visibleHeight));
    for (size_t i = 0; i < frame.size(); ++i)
    {
        frame[i].Char.UnicodeChar = L' ';
        frame[i].Attributes = statusAttr;
    }

    // Copy fixed header rows.
    int headerRows = std::min(BodyTop(), visibleHeight);
    if (headerRows > 0)
    {
        COORD size{static_cast<SHORT>(visibleWidth), static_cast<SHORT>(headerRows)};
        COORD coord{0, 0};
        SMALL_RECT src{0, 0, static_cast<SHORT>(visibleWidth - 1), static_cast<SHORT>(headerRows - 1)};
        ReadConsoleOutputW(gHiddenOut, frame.data(), size, coord, &src);
    }

    // Copy body viewport from hidden rows.
    int bodyHeight = std::max(0, visibleHeight - BodyTop() - FooterRows());
    if (bodyHeight > 0)
    {
        std::vector<CHAR_INFO> body(static_cast<size_t>(visibleWidth) * static_cast<size_t>(bodyHeight));
        for (size_t i = 0; i < body.size(); ++i)
        {
            body[i].Char.UnicodeChar = L' ';
            body[i].Attributes = statusAttr;
        }

        int srcLeft = ClampInt(virtualLeft, 0, std::max(0, hiddenWidth - 1));
        int srcTop = BodyTop() + ClampInt(virtualTop, 0, std::max(0, hiddenHeight - BodyTop() - 1));
        int srcRight = std::min(hiddenWidth - 1, srcLeft + visibleWidth - 1);
        int srcBottom = std::min(hiddenHeight - 1, srcTop + bodyHeight - 1);

        if (srcRight >= srcLeft && srcBottom >= srcTop)
        {
            COORD size{static_cast<SHORT>(visibleWidth), static_cast<SHORT>(bodyHeight)};
            COORD coord{0, 0};
            SMALL_RECT src{static_cast<SHORT>(srcLeft), static_cast<SHORT>(srcTop), static_cast<SHORT>(srcRight), static_cast<SHORT>(srcBottom)};
            ReadConsoleOutputW(gHiddenOut, body.data(), size, coord, &src);
        }

        for (int y = 0; y < bodyHeight; ++y)
        {
            CHAR_INFO* dest = frame.data() + static_cast<size_t>(BodyTop() + y) * visibleWidth;
            CHAR_INFO* src = body.data() + static_cast<size_t>(y) * visibleWidth;
            std::copy(src, src + visibleWidth, dest);
        }
    }

    // Stable native marker footer. This should make fallback vs native obvious.
    int footerY = visibleHeight - 1;
    for (int x = 0; x < visibleWidth; ++x)
    {
        frame[static_cast<size_t>(footerY) * visibleWidth + x].Char.UnicodeChar = L' ';
        frame[static_cast<size_t>(footerY) * visibleWidth + x].Attributes = statusAttr;
    }

    std::wstring status = L"NATIVE DLL selector | rows=" + std::to_wstring(rowCount) +
        L" selected=" + std::to_wstring(selected) +
        L" | Enter accept, Esc cancel";

    for (int i = 0; i < static_cast<int>(status.size()) && i < visibleWidth; ++i)
    {
        frame[static_cast<size_t>(footerY) * visibleWidth + i].Char.UnicodeChar = status[static_cast<size_t>(i)];
        frame[static_cast<size_t>(footerY) * visibleWidth + i].Attributes = statusAttr;
    }

    COORD outSize{static_cast<SHORT>(visibleWidth), static_cast<SHORT>(visibleHeight)};
    COORD outCoord{0, 0};
    SMALL_RECT dest{0, 0, static_cast<SHORT>(visibleWidth - 1), static_cast<SHORT>(visibleHeight - 1)};
    return WriteConsoleOutputW(gVisibleOut, frame.data(), outSize, outCoord, &dest) != FALSE;
}

static bool SetupInput()
{
    if (gInput == INVALID_HANDLE_VALUE)
        return false;

    GetConsoleMode(gInput, &gOriginalInputMode);
    DWORD mode = gOriginalInputMode;
    mode |= ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS;
    mode &= ~ENABLE_QUICK_EDIT_MODE;
    return SetConsoleMode(gInput, mode) != FALSE;
}

static void Cleanup()
{
    if (gOriginalOut != INVALID_HANDLE_VALUE)
        SetConsoleActiveScreenBuffer(gOriginalOut);

    if (gHaveOriginalCursor && gOriginalOut != INVALID_HANDLE_VALUE)
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
}

static int RunSelector(const std::vector<Row>& rows, int initialIndex, int* selectedIndexOut)
{
    if (rows.empty())
        return -2;

    gOriginalOut = GetStdHandle(STD_OUTPUT_HANDLE);
    gInput = GetStdHandle(STD_INPUT_HANDLE);
    if (gOriginalOut == INVALID_HANDLE_VALUE || gInput == INVALID_HANDLE_VALUE)
        return -3;

    gHaveOriginalCursor = GetConsoleCursorInfo(gOriginalOut, &gOriginalCursor) != FALSE;

    CONSOLE_SCREEN_BUFFER_INFO originalInfo{};
    WORD normalAttr = FG_WHITE;
    if (GetConsoleInfo(gOriginalOut, originalInfo))
        normalAttr = originalInfo.wAttributes;

    WORD selectedAttr = BG_GREY | BackgroundAsForeground(normalAttr);
    WORD statusAttr = BACKGROUND_BLUE | FG_BRIGHT_WHITE;

    if (!SetupInput())
        return -4;

    gVisibleOut = CreateBuffer();
    gHiddenOut = CreateBuffer();
    if (gVisibleOut == INVALID_HANDLE_VALUE || gHiddenOut == INVALID_HANDLE_VALUE)
        return -5;

    int originalWidth = std::max(1, RectWidth(originalInfo.srWindow));
    int originalHeight = std::max(1, RectHeight(originalInfo.srWindow));
    ConfigureVisibleBuffer(originalWidth, originalHeight);

    SetConsoleActiveScreenBuffer(gVisibleOut);
    HideCursor(gVisibleOut);

    int selected = ClampInt(initialIndex, 0, static_cast<int>(rows.size()) - 1);
    int virtualTop = 0;
    int virtualLeft = 0;

    int visibleWidth = originalWidth;
    int visibleHeight = originalHeight;
    int lastVisibleWidth = -1;
    int lastVisibleHeight = -1;
    int hiddenWidth = 0;
    int hiddenHeight = 0;
    int lastSelectedRendered = -9999;
    int lastVirtualTopRendered = -9999;
    int lastVirtualLeftRendered = -9999;

    bool hiddenDirty = true;
    bool visibleDirty = true;

    while (true)
    {
        int currentWidth = visibleWidth;
        int currentHeight = visibleHeight;
        if (ReadVisibleSize(currentWidth, currentHeight))
        {
            if (currentWidth != visibleWidth || currentHeight != visibleHeight)
            {
                visibleWidth = currentWidth;
                visibleHeight = currentHeight;
                ConfigureVisibleBuffer(visibleWidth, visibleHeight);
                hiddenDirty = true;
                visibleDirty = true;
            }
        }

        int bodyHeight = BodyHeight(visibleHeight);
        int maxTop = std::max(0, static_cast<int>(rows.size()) - bodyHeight);
        if (selected < virtualTop)
            virtualTop = selected;
        if (selected >= virtualTop + bodyHeight)
            virtualTop = selected - bodyHeight + 1;
        virtualTop = ClampInt(virtualTop, 0, maxTop);

        int requiredHiddenWidth = ComputeHiddenWidth(rows, visibleWidth);
        int requiredHiddenHeight = ComputeHiddenHeight(rows, visibleHeight);
        if (requiredHiddenWidth != hiddenWidth || requiredHiddenHeight != hiddenHeight)
        {
            hiddenWidth = requiredHiddenWidth;
            hiddenHeight = requiredHiddenHeight;
            hiddenDirty = true;
            visibleDirty = true;
        }

        int maxLeft = std::max(0, hiddenWidth - visibleWidth);
        virtualLeft = ClampInt(virtualLeft, 0, maxLeft);

        if (hiddenDirty || selected != lastSelectedRendered)
        {
            RenderHidden(rows, selected, hiddenWidth, hiddenHeight, normalAttr, selectedAttr);
            hiddenDirty = false;
            visibleDirty = true;
            lastSelectedRendered = selected;
        }

        if (visibleDirty ||
            visibleWidth != lastVisibleWidth || visibleHeight != lastVisibleHeight ||
            virtualTop != lastVirtualTopRendered || virtualLeft != lastVirtualLeftRendered)
        {
            CopyViewportFromHidden(hiddenWidth, hiddenHeight, visibleWidth, visibleHeight,
                virtualLeft, virtualTop, statusAttr, static_cast<int>(rows.size()), selected);
            HideCursor(gVisibleOut);
            visibleDirty = false;
            lastVisibleWidth = visibleWidth;
            lastVisibleHeight = visibleHeight;
            lastVirtualTopRendered = virtualTop;
            lastVirtualLeftRendered = virtualLeft;
        }

        DWORD wait = WaitForSingleObject(gInput, 60);
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

        for (const INPUT_RECORD& e : events)
        {
            if (e.EventType == WINDOW_BUFFER_SIZE_EVENT)
            {
                visibleDirty = true;
                continue;
            }

            if (e.EventType == MOUSE_EVENT)
            {
                const MOUSE_EVENT_RECORD& m = e.Event.MouseEvent;
                if (m.dwEventFlags == MOUSE_WHEELED)
                {
                    short wheelDelta = static_cast<short>((m.dwButtonState >> 16) & 0xffff);
                    int old = selected;
                    selected = wheelDelta > 0 ? std::max(0, selected - 1) : std::min(static_cast<int>(rows.size()) - 1, selected + 1);
                    if (selected != old)
                    {
                        hiddenDirty = true;
                        visibleDirty = true;
                    }
                    continue;
                }

                if ((m.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) && (m.dwEventFlags == 0 || m.dwEventFlags == DOUBLE_CLICK))
                {
                    int clicked = virtualTop + (m.dwMousePosition.Y - BodyTop());
                    if (clicked >= 0 && clicked < static_cast<int>(rows.size()))
                    {
                        selected = clicked;
                        hiddenDirty = true;
                        visibleDirty = true;
                        if (m.dwEventFlags == DOUBLE_CLICK)
                        {
                            if (selectedIndexOut) *selectedIndexOut = rows[static_cast<size_t>(selected)].sequence;
                            return 1;
                        }
                    }
                    continue;
                }

                continue;
            }

            if (e.EventType != KEY_EVENT || !e.Event.KeyEvent.bKeyDown)
                continue;

            const KEY_EVENT_RECORD& k = e.Event.KeyEvent;
            int oldSelected = selected;
            int oldLeft = virtualLeft;

            switch (k.wVirtualKeyCode)
            {
            case VK_ESCAPE:
                return 0;
            case VK_RETURN:
                if (selectedIndexOut) *selectedIndexOut = rows[static_cast<size_t>(selected)].sequence;
                return 1;
            case VK_UP:
                selected = std::max(0, selected - 1);
                break;
            case VK_DOWN:
                selected = std::min(static_cast<int>(rows.size()) - 1, selected + 1);
                break;
            case VK_PRIOR:
                selected = std::max(0, selected - BodyHeight(visibleHeight));
                break;
            case VK_NEXT:
                selected = std::min(static_cast<int>(rows.size()) - 1, selected + BodyHeight(visibleHeight));
                break;
            case VK_HOME:
                if (k.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED))
                    virtualLeft = 0;
                else
                    selected = 0;
                break;
            case VK_END:
                if (k.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED))
                    virtualLeft = std::max(0, hiddenWidth - visibleWidth);
                else
                    selected = static_cast<int>(rows.size()) - 1;
                break;
            case VK_LEFT:
                virtualLeft = std::max(0, virtualLeft - 2);
                break;
            case VK_RIGHT:
                virtualLeft = std::min(std::max(0, hiddenWidth - visibleWidth), virtualLeft + 2);
                break;
            default:
                if (k.uChar.UnicodeChar != L'\0')
                    JumpToLetter(rows, k.uChar.UnicodeChar, selected);
                break;
            }

            if (selected != oldSelected)
            {
                hiddenDirty = true;
                visibleDirty = true;
            }
            if (virtualLeft != oldLeft)
                visibleDirty = true;
        }
    }
}

extern "C" __declspec(dllexport)
int __stdcall SelectWindowFromRows(const NativeSelectorRow* nativeRows, int rowCount, int initialIndex, int* selectedIndexOut)
{
    if (selectedIndexOut)
        *selectedIndexOut = 0;

    int result = -1;

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
                Row row;
                row.sequence = nativeRows[i].Sequence;
                row.processId = nativeRows[i].ProcessId;
                row.hwnd = nativeRows[i].WindowHandle;
                row.top = nativeRows[i].IsTopForProcess != 0;
                row.text = nativeRows[i].DisplayText ? nativeRows[i].DisplayText : L"";
                if (row.text.empty())
                {
                    row.text = L"PID " + std::to_wstring(row.processId) + L" 0x" + ToHex(row.hwnd);
                    if (row.top) row.text += L" Top";
                }
                rows.push_back(row);
            }

            result = RunSelector(rows, initialIndex, selectedIndexOut);
        }
    }
    catch (...)
    {
        result = -100;
    }

    Cleanup();
    return result;
}
