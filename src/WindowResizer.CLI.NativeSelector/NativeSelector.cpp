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
    int Sequence = 0;
    int ProcessId = 0;
    intptr_t WindowHandle = 0;
    bool IsTopForProcess = false;
    std::wstring Text;
};

static HANDLE gOriginalOut = INVALID_HANDLE_VALUE;
static HANDLE gVisibleOut = INVALID_HANDLE_VALUE;
static HANDLE gHiddenOut = INVALID_HANDLE_VALUE;
static HANDLE gInput = INVALID_HANDLE_VALUE;
static DWORD gOriginalInputMode = 0;
static CONSOLE_CURSOR_INFO gOriginalCursor{};
static WORD gNormalAttr = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE;
static WORD gSelectedAttr = BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE;
static WORD gTopAttr = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY;
static COORD gVisibleFixedSize{0, 0};

static const int kHeaderRows = 3;
static const int kBodyTop = 3;
static const int kFooterRows = 1;
static const int kMinVisibleWidth = 1;
static const int kMinVisibleHeight = 5;

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

static bool GetInfo(HANDLE h, CONSOLE_SCREEN_BUFFER_INFO& info)
{
    return GetConsoleScreenBufferInfo(h, &info) != FALSE;
}

static WORD BackgroundBits(WORD attr)
{
    return attr & (BACKGROUND_BLUE | BACKGROUND_GREEN | BACKGROUND_RED | BACKGROUND_INTENSITY);
}

static WORD ForegroundFromBackground(WORD attr)
{
    WORD bg = BackgroundBits(attr);
    WORD fg = 0;
    if (bg & BACKGROUND_BLUE) fg |= FOREGROUND_BLUE;
    if (bg & BACKGROUND_GREEN) fg |= FOREGROUND_GREEN;
    if (bg & BACKGROUND_RED) fg |= FOREGROUND_RED;
    if (bg & BACKGROUND_INTENSITY) fg |= FOREGROUND_INTENSITY;
    return fg;
}

static void SetupAttributes()
{
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (GetInfo(gOriginalOut, info))
        gNormalAttr = info.wAttributes;

    WORD selectedFg = ForegroundFromBackground(gNormalAttr);
    // If the terminal background is black, use black text on grey, matching the intended inverse-like C# look.
    gSelectedAttr = (BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE) | selectedFg;
    gTopAttr = BackgroundBits(gNormalAttr) | FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY;
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
        (cp >= 0xFFE0 && cp <= 0xFFE6) ||
        (cp >= 0x1F300 && cp <= 0x1FAFF);
}

static int EstimatedCellWidth(const std::wstring& s)
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

static void FillLine(HANDLE h, short y, short width, WORD attr)
{
    if (width <= 0)
        return;
    DWORD written = 0;
    COORD pos{0, y};
    FillConsoleOutputCharacterW(h, L' ', width, pos, &written);
    FillConsoleOutputAttribute(h, attr, width, pos, &written);
}

static void WriteAt(HANDLE h, short x, short y, const std::wstring& text, WORD attr)
{
    if (text.empty())
        return;
    DWORD written = 0;
    COORD pos{x, y};
    SetConsoleCursorPosition(h, pos);
    SetConsoleTextAttribute(h, attr);
    WriteConsoleW(h, text.c_str(), static_cast<DWORD>(text.size()), &written, nullptr);
}

static size_t ProcessNameEnd(const std::wstring& s, size_t start)
{
    size_t end = s.find(L" [", start);
    size_t pipe = s.find(L" |", start);
    size_t space = s.find(L' ', start);
    size_t best = std::wstring::npos;
    if (end != std::wstring::npos) best = end;
    if (pipe != std::wstring::npos) best = std::min(best, pipe);
    if (space != std::wstring::npos) best = std::min(best, space);
    if (best == std::wstring::npos || best <= start)
        best = s.size();
    return best;
}

static void WriteRowWithTopHighlight(HANDLE h, short y, int hiddenWidth, const Row& row, bool selected)
{
    WORD baseAttr = selected ? gSelectedAttr : gNormalAttr;
    FillLine(h, y, static_cast<short>(hiddenWidth), baseAttr);

    std::wstring line = selected ? L"> " : L"  ";
    line += row.Text;
    WriteAt(h, 0, y, line, baseAttr);

    if (!selected && row.IsTopForProcess)
    {
        size_t nameStart = 2;
        size_t nameEnd = ProcessNameEnd(line, nameStart);
        if (nameEnd > nameStart)
            WriteAt(h, static_cast<short>(nameStart), y, line.substr(nameStart, nameEnd - nameStart), gTopAttr);

        size_t top = line.find(L"Top");
        if (top != std::wstring::npos)
            WriteAt(h, static_cast<short>(top), y, L"Top", gTopAttr);
    }
}

static wchar_t FirstJumpLetter(const Row& row)
{
    for (wchar_t ch : row.Text)
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
        int index = (selectedIndex + step) % count;
        if (FirstJumpLetter(rows[static_cast<size_t>(index)]) == key)
        {
            selectedIndex = index;
            return;
        }
    }
}

static int RequiredHiddenWidth(const std::vector<Row>& rows, int visibleWidth)
{
    int width = std::max(visibleWidth, 160);
    width = std::max(width, EstimatedCellWidth(L"WindowResizer native selector") + 8);
    width = std::max(width, EstimatedCellWidth(L"NATIVE DLL selector | arrows/PgUp/PgDn/Home/End/letters, Enter accepts, Esc cancels") + 8);
    for (const Row& row : rows)
        width = std::max(width, EstimatedCellWidth(row.Text) + 8);
    return ClampInt(width, 160, 32000);
}

static int RequiredHiddenHeight(const std::vector<Row>& rows, int visibleHeight)
{
    return std::max(visibleHeight, kBodyTop + static_cast<int>(rows.size()) + kFooterRows + 1);
}

static bool SetBufferSizeAtLeast(HANDLE h, int width, int height)
{
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (!GetInfo(h, info))
        return false;

    int newWidth = std::max<int>(info.dwSize.X, width);
    int newHeight = std::max<int>(info.dwSize.Y, height);
    COORD size{static_cast<SHORT>(newWidth), static_cast<SHORT>(newHeight)};
    if (info.dwSize.X == size.X && info.dwSize.Y == size.Y)
        return true;
    return SetConsoleScreenBufferSize(h, size) != FALSE;
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

static void SetupInput()
{
    GetConsoleMode(gInput, &gOriginalInputMode);
    DWORD mode = gOriginalInputMode;
    mode |= ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS;
    mode &= ~ENABLE_QUICK_EDIT_MODE;
    SetConsoleMode(gInput, mode);
}

static void RenderHidden(HANDLE hidden, int hiddenWidth, int hiddenHeight, const std::vector<Row>& rows, int selectedIndex)
{
    for (int y = 0; y < hiddenHeight; ++y)
        FillLine(hidden, static_cast<short>(y), static_cast<short>(hiddenWidth), gNormalAttr);

    WriteAt(hidden, 0, 0, L"WindowResizer native selector", gNormalAttr | FOREGROUND_INTENSITY);
    WriteAt(hidden, 0, 1, L"Arrows/PgUp/PgDn/Home/End select, letters jump, Enter accepts, Esc cancels", gNormalAttr);
    FillLine(hidden, 2, static_cast<short>(hiddenWidth), gNormalAttr);

    for (int i = 0; i < static_cast<int>(rows.size()); ++i)
    {
        int y = kBodyTop + i;
        if (y >= hiddenHeight - 1)
            break;
        WriteRowWithTopHighlight(hidden, static_cast<short>(y), hiddenWidth, rows[static_cast<size_t>(i)], i == selectedIndex);
    }
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
        CHAR_INFO& c = frame[static_cast<size_t>(y) * width + xx];
        c.Char.UnicodeChar = text[static_cast<size_t>(i)];
        c.Attributes = attr;
    }
}

static CHAR_INFO BlankCell(WORD attr)
{
    CHAR_INFO c{};
    c.Char.UnicodeChar = L' ';
    c.Attributes = attr;
    return c;
}

static bool ReadHiddenRect(HANDLE hidden, std::vector<CHAR_INFO>& target, int targetWidth, int targetHeight,
    int destX, int destY, int readWidth, int readHeight, int sourceX, int sourceY)
{
    if (readWidth <= 0 || readHeight <= 0)
        return true;

    std::vector<CHAR_INFO> temp(static_cast<size_t>(readWidth) * readHeight, BlankCell(gNormalAttr));
    COORD size{static_cast<SHORT>(readWidth), static_cast<SHORT>(readHeight)};
    COORD coord{0, 0};
    SMALL_RECT src{
        static_cast<SHORT>(sourceX),
        static_cast<SHORT>(sourceY),
        static_cast<SHORT>(sourceX + readWidth - 1),
        static_cast<SHORT>(sourceY + readHeight - 1)};

    if (!ReadConsoleOutputW(hidden, temp.data(), size, coord, &src))
        return false;

    for (int y = 0; y < readHeight; ++y)
    {
        int ty = destY + y;
        if (ty < 0 || ty >= targetHeight)
            continue;
        for (int x = 0; x < readWidth; ++x)
        {
            int tx = destX + x;
            if (tx < 0 || tx >= targetWidth)
                continue;
            target[static_cast<size_t>(ty) * targetWidth + tx] = temp[static_cast<size_t>(y) * readWidth + x];
        }
    }

    return true;
}

static bool CopyViewport(HANDLE hidden, HANDLE visible, int hiddenWidth, int hiddenHeight,
    int virtualLeft, int virtualTop, int selectedIndex, int rowCount)
{
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (!GetInfo(visible, info))
        return false;

    int visibleWidth = std::max(kMinVisibleWidth, RectWidth(info.srWindow));
    int visibleHeight = std::max(kMinVisibleHeight, RectHeight(info.srWindow));
    if (visibleWidth <= 0 || visibleHeight <= 0)
        return false;

    std::vector<CHAR_INFO> frame(static_cast<size_t>(visibleWidth) * visibleHeight, BlankCell(gNormalAttr));

    int headerRows = std::min(kHeaderRows, visibleHeight);
    ReadHiddenRect(hidden, frame, visibleWidth, visibleHeight, 0, 0, visibleWidth, headerRows, 0, 0);

    int footerRow = visibleHeight - 1;
    int bodyHeight = std::max(0, visibleHeight - kBodyTop - 1);
    if (bodyHeight > 0)
    {
        int maxReadHeight = std::max(0, std::min(bodyHeight, hiddenHeight - (kBodyTop + virtualTop)));
        ReadHiddenRect(hidden, frame, visibleWidth, visibleHeight, 0, kBodyTop,
            visibleWidth, maxReadHeight, virtualLeft, kBodyTop + virtualTop);
    }

    WORD footerAttr = BackgroundBits(gNormalAttr) | FOREGROUND_GREEN | FOREGROUND_INTENSITY;
    for (int x = 0; x < visibleWidth; ++x)
        frame[static_cast<size_t>(footerRow) * visibleWidth + x] = BlankCell(footerAttr);

    std::wstring footer = L"NATIVE DLL selector | rows=" + std::to_wstring(rowCount) +
        L" selected=" + std::to_wstring(selectedIndex) +
        L" left=" + std::to_wstring(virtualLeft) +
        L" top=" + std::to_wstring(virtualTop);
    PutText(frame, visibleWidth, visibleHeight, 0, footerRow, footer, footerAttr);

    COORD size{static_cast<SHORT>(visibleWidth), static_cast<SHORT>(visibleHeight)};
    COORD coord{0, 0};
    SMALL_RECT dest = info.srWindow;
    dest.Right = static_cast<SHORT>(dest.Left + visibleWidth - 1);
    dest.Bottom = static_cast<SHORT>(dest.Top + visibleHeight - 1);
    return WriteConsoleOutputW(visible, frame.data(), size, coord, &dest) != FALSE;
}

static int RunSelector(const std::vector<Row>& rows, int initialIndex, int* selectedIndexOut)
{
    if (rows.empty())
        return -2;

    gOriginalOut = GetStdHandle(STD_OUTPUT_HANDLE);
    gInput = GetStdHandle(STD_INPUT_HANDLE);
    if (gOriginalOut == INVALID_HANDLE_VALUE || gOriginalOut == nullptr || gInput == INVALID_HANDLE_VALUE || gInput == nullptr)
        return -3;

    GetConsoleCursorInfo(gOriginalOut, &gOriginalCursor);
    SetupAttributes();
    SetupInput();

    CONSOLE_SCREEN_BUFFER_INFO originalInfo{};
    if (!GetInfo(gOriginalOut, originalInfo))
        return -4;
    int originalVisibleWidth = std::max(kMinVisibleWidth, RectWidth(originalInfo.srWindow));
    int originalVisibleHeight = std::max(kMinVisibleHeight, RectHeight(originalInfo.srWindow));

    int selectedIndex = ClampInt(initialIndex, 0, static_cast<int>(rows.size()) - 1);
    int virtualTop = 0;
    int virtualLeft = 0;

    int hiddenWidth = RequiredHiddenWidth(rows, originalVisibleWidth);
    int hiddenHeight = RequiredHiddenHeight(rows, originalVisibleHeight);

    // The key anti-flicker change: size the active visible buffer once, generously,
    // before activating it. Do not grow dwSize while the user drags the terminal size.
    int visibleFixedWidth = ClampInt(std::max({originalVisibleWidth, originalInfo.dwSize.X, hiddenWidth, 512}), originalVisibleWidth, 32000);
    int visibleFixedHeight = ClampInt(std::max({originalVisibleHeight, originalInfo.dwSize.Y, hiddenHeight, 300}), originalVisibleHeight, 2000);

    gVisibleOut = CreateBuffer();
    gHiddenOut = CreateBuffer();
    if (gVisibleOut == INVALID_HANDLE_VALUE || gHiddenOut == INVALID_HANDLE_VALUE)
        return -5;

    SMALL_RECT initialWin{0, 0, static_cast<SHORT>(originalVisibleWidth - 1), static_cast<SHORT>(originalVisibleHeight - 1)};
    COORD visibleSize{static_cast<SHORT>(visibleFixedWidth), static_cast<SHORT>(visibleFixedHeight)};
    SetConsoleScreenBufferSize(gVisibleOut, visibleSize);
    SetConsoleWindowInfo(gVisibleOut, TRUE, &initialWin);
    SetConsoleScreenBufferSize(gVisibleOut, visibleSize);
    gVisibleFixedSize = visibleSize;

    COORD hiddenSize{static_cast<SHORT>(hiddenWidth), static_cast<SHORT>(hiddenHeight)};
    SetConsoleScreenBufferSize(gHiddenOut, hiddenSize);
    SMALL_RECT hiddenWin{0, 0, static_cast<SHORT>(std::min(hiddenWidth, originalVisibleWidth) - 1), static_cast<SHORT>(std::min(hiddenHeight, originalVisibleHeight) - 1)};
    SetConsoleWindowInfo(gHiddenOut, TRUE, &hiddenWin);

    RenderHidden(gHiddenOut, hiddenWidth, hiddenHeight, rows, selectedIndex);

    if (!SetConsoleActiveScreenBuffer(gVisibleOut))
        return -6;
    HideCursor(gVisibleOut);

    int lastVisibleWidth = -1;
    int lastVisibleHeight = -1;
    int lastSelectedIndex = -1;
    int lastVirtualTop = -1;
    int lastVirtualLeft = -1;
    bool hiddenDirty = false;
    bool visibleDirty = true;

    while (true)
    {
        CONSOLE_SCREEN_BUFFER_INFO visibleInfo{};
        if (GetInfo(gVisibleOut, visibleInfo))
        {
            int currentWidth = std::max(kMinVisibleWidth, RectWidth(visibleInfo.srWindow));
            int currentHeight = std::max(kMinVisibleHeight, RectHeight(visibleInfo.srWindow));

            int bodyHeight = std::max(1, currentHeight - kBodyTop - 1);
            int maxTop = std::max(0, static_cast<int>(rows.size()) - bodyHeight);
            int maxLeft = std::max(0, hiddenWidth - currentWidth);

            if (selectedIndex < virtualTop)
                virtualTop = selectedIndex;
            if (selectedIndex >= virtualTop + bodyHeight)
                virtualTop = selectedIndex - bodyHeight + 1;
            virtualTop = ClampInt(virtualTop, 0, maxTop);
            virtualLeft = ClampInt(virtualLeft, 0, maxLeft);

            if (currentWidth != lastVisibleWidth || currentHeight != lastVisibleHeight ||
                selectedIndex != lastSelectedIndex || virtualTop != lastVirtualTop || virtualLeft != lastVirtualLeft)
            {
                visibleDirty = true;
                lastVisibleWidth = currentWidth;
                lastVisibleHeight = currentHeight;
                lastSelectedIndex = selectedIndex;
                lastVirtualTop = virtualTop;
                lastVirtualLeft = virtualLeft;
            }
        }

        if (hiddenDirty)
        {
            RenderHidden(gHiddenOut, hiddenWidth, hiddenHeight, rows, selectedIndex);
            hiddenDirty = false;
            visibleDirty = true;
        }

        if (visibleDirty)
        {
            CopyViewport(gHiddenOut, gVisibleOut, hiddenWidth, hiddenHeight, virtualLeft, virtualTop, selectedIndex, static_cast<int>(rows.size()));
            HideCursor(gVisibleOut);
            visibleDirty = false;
        }

        DWORD wait = WaitForSingleObject(gInput, 40);
        if (wait != WAIT_OBJECT_0)
            continue;

        DWORD eventCount = 0;
        if (!GetNumberOfConsoleInputEvents(gInput, &eventCount) || eventCount == 0)
            continue;

        std::vector<INPUT_RECORD> events(eventCount);
        DWORD read = 0;
        if (!ReadConsoleInputW(gInput, events.data(), eventCount, &read))
            continue;
        events.resize(read);

        for (const INPUT_RECORD& e : events)
        {
            if (e.EventType == WINDOW_BUFFER_SIZE_EVENT)
            {
                visibleDirty = true;
                continue;
            }

            CONSOLE_SCREEN_BUFFER_INFO vi{};
            GetInfo(gVisibleOut, vi);
            int currentHeight = std::max(kMinVisibleHeight, RectHeight(vi.srWindow));
            int bodyHeight = std::max(1, currentHeight - kBodyTop - 1);
            int maxLeft = std::max(0, hiddenWidth - std::max(kMinVisibleWidth, RectWidth(vi.srWindow)));

            if (e.EventType == MOUSE_EVENT)
            {
                const MOUSE_EVENT_RECORD& m = e.Event.MouseEvent;
                if (m.dwEventFlags == MOUSE_WHEELED)
                {
                    short wheelDelta = static_cast<short>((m.dwButtonState >> 16) & 0xffff);
                    int steps = std::max(1, std::abs(static_cast<int>(wheelDelta)) / WHEEL_DELTA);
                    int old = selectedIndex;
                    selectedIndex = wheelDelta > 0
                        ? std::max(0, selectedIndex - steps)
                        : std::min(static_cast<int>(rows.size()) - 1, selectedIndex + steps);
                    if (selectedIndex != old)
                    {
                        hiddenDirty = true;
                        visibleDirty = true;
                    }
                    continue;
                }

                bool leftDown = (m.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0;
                if (leftDown && (m.dwEventFlags == 0 || m.dwEventFlags == DOUBLE_CLICK))
                {
                    int y = m.dwMousePosition.Y - vi.srWindow.Top;
                    int row = y - kBodyTop;
                    if (row >= 0 && row < bodyHeight)
                    {
                        int index = virtualTop + row;
                        if (index >= 0 && index < static_cast<int>(rows.size()))
                        {
                            selectedIndex = index;
                            hiddenDirty = true;
                            visibleDirty = true;
                            if (m.dwEventFlags == DOUBLE_CLICK)
                            {
                                if (selectedIndexOut) *selectedIndexOut = rows[static_cast<size_t>(selectedIndex)].Sequence;
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
            int oldLeft = virtualLeft;

            switch (k.wVirtualKeyCode)
            {
            case VK_ESCAPE:
                return 0;
            case VK_RETURN:
                if (selectedIndexOut) *selectedIndexOut = rows[static_cast<size_t>(selectedIndex)].Sequence;
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
                break;
            case VK_END:
                if (k.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED))
                    virtualLeft = maxLeft;
                else
                    selectedIndex = static_cast<int>(rows.size()) - 1;
                break;
            case VK_LEFT:
                virtualLeft = std::max(0, virtualLeft - 1);
                break;
            case VK_RIGHT:
                virtualLeft = std::min(maxLeft, virtualLeft + 1);
                break;
            default:
                if (k.uChar.UnicodeChar != L'\0')
                    JumpToLetter(rows, k.uChar.UnicodeChar, selectedIndex);
                break;
            }

            if (selectedIndex != oldSelected)
                hiddenDirty = true;
            if (selectedIndex != oldSelected || virtualLeft != oldLeft)
                visibleDirty = true;
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
                Row row;
                row.Sequence = nativeRows[i].Sequence;
                row.ProcessId = nativeRows[i].ProcessId;
                row.WindowHandle = nativeRows[i].WindowHandle;
                row.IsTopForProcess = nativeRows[i].IsTopForProcess != 0;
                row.Text = nativeRows[i].DisplayText ? nativeRows[i].DisplayText : L"";
                if (!row.Text.empty())
                    rows.push_back(row);
            }
            result = RunSelector(rows, initialIndex, selectedIndexOut);
        }
    }
    catch (...)
    {
        result = -100;
    }

    if (gOriginalOut != INVALID_HANDLE_VALUE && gOriginalOut != nullptr)
        SetConsoleActiveScreenBuffer(gOriginalOut);
    if (gOriginalOut != INVALID_HANDLE_VALUE && gOriginalOut != nullptr)
        SetConsoleCursorInfo(gOriginalOut, &gOriginalCursor);
    if (gInput != INVALID_HANDLE_VALUE && gInput != nullptr)
        SetConsoleMode(gInput, gOriginalInputMode);
    if (gVisibleOut != INVALID_HANDLE_VALUE && gVisibleOut != nullptr)
    {
        CloseHandle(gVisibleOut);
        gVisibleOut = INVALID_HANDLE_VALUE;
    }
    if (gHiddenOut != INVALID_HANDLE_VALUE && gHiddenOut != nullptr)
    {
        CloseHandle(gHiddenOut);
        gHiddenOut = INVALID_HANDLE_VALUE;
    }

    return result;
}
