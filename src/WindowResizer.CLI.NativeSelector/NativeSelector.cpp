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

static HANDLE gOriginalOut = INVALID_HANDLE_VALUE;
static HANDLE gVisibleOut = INVALID_HANDLE_VALUE;
static HANDLE gInput = INVALID_HANDLE_VALUE;
static DWORD gOriginalInputMode = 0;
static CONSOLE_CURSOR_INFO gOriginalCursor{};
static WORD gOriginalAttributes = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE;

static const int kHeaderLines = 3;
static const int kFooterLines = 1;

static int ClampInt(int value, int minValue, int maxValue)
{
    if (value < minValue) return minValue;
    if (value > maxValue) return maxValue;
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

static WORD ForegroundFromBackground(WORD attr)
{
    return static_cast<WORD>((attr >> 4) & 0x000F);
}

static WORD OriginalBackground()
{
    return static_cast<WORD>(gOriginalAttributes & 0x00F0);
}

static WORD OriginalForeground()
{
    WORD fg = static_cast<WORD>(gOriginalAttributes & 0x000F);
    if (fg == 0 && OriginalBackground() == 0)
        fg = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE;
    return fg;
}

static WORD NormalAttr()
{
    return static_cast<WORD>(OriginalBackground() | OriginalForeground());
}

static WORD HeaderAttr()
{
    WORD fg = OriginalForeground();
    return static_cast<WORD>(OriginalBackground() | fg | FOREGROUND_INTENSITY);
}

static WORD StatusAttr()
{
    WORD bg = BACKGROUND_BLUE;
    WORD fg = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY;
    return static_cast<WORD>(bg | fg);
}

static WORD SelectedAttr()
{
    // Grey selection background, with foreground taken from the terminal background.
    WORD greyBackground = BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE;
    WORD fgFromTerminalBackground = ForegroundFromBackground(gOriginalAttributes);
    return static_cast<WORD>(greyBackground | fgFromTerminalBackground);
}

static unsigned HashText(const std::wstring& text)
{
    unsigned hash = 2166136261u;
    for (wchar_t ch : text)
    {
        hash ^= static_cast<unsigned>(ch);
        hash *= 16777619u;
    }
    return hash;
}

static WORD RowAttr(const std::wstring& text, int index, bool selected)
{
    if (selected)
        return SelectedAttr();

    static const WORD palette[] =
    {
        FOREGROUND_GREEN | FOREGROUND_INTENSITY,
        FOREGROUND_BLUE | FOREGROUND_GREEN | FOREGROUND_INTENSITY,
        FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY,
        FOREGROUND_RED | FOREGROUND_BLUE | FOREGROUND_INTENSITY,
        FOREGROUND_RED | FOREGROUND_INTENSITY,
        FOREGROUND_BLUE | FOREGROUND_INTENSITY,
        FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE,
        FOREGROUND_GREEN | FOREGROUND_BLUE
    };

    unsigned h = HashText(text) + static_cast<unsigned>(index * 131u);
    return static_cast<WORD>(OriginalBackground() | palette[h % (sizeof(palette) / sizeof(palette[0]))]);
}

static CHAR_INFO MakeCell(wchar_t ch, WORD attr)
{
    CHAR_INFO cell{};
    cell.Char.UnicodeChar = ch;
    cell.Attributes = attr;
    return cell;
}

static bool GetConsoleSize(HANDLE output, int& width, int& height)
{
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (!GetConsoleScreenBufferInfo(output, &info))
        return false;

    width = RectWidth(info.srWindow);
    height = RectHeight(info.srWindow);

    if (width <= 0) width = static_cast<int>(info.dwSize.X);
    if (height <= 0) height = static_cast<int>(info.dwSize.Y);

    width = std::max(width, 1);
    height = std::max(height, 1);
    return true;
}

static void TrySetBufferSize(HANDLE output, int width, int height)
{
    if (width < 1 || height < 1)
        return;

    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (!GetConsoleScreenBufferInfo(output, &info))
        return;

    COORD size{};
    size.X = static_cast<SHORT>(std::max<int>(width, RectWidth(info.srWindow)));
    size.Y = static_cast<SHORT>(std::max<int>(height, RectHeight(info.srWindow)));
    SetConsoleScreenBufferSize(output, size);

    SMALL_RECT win{};
    win.Left = 0;
    win.Top = 0;
    win.Right = static_cast<SHORT>(std::max(1, width) - 1);
    win.Bottom = static_cast<SHORT>(std::max(1, height) - 1);
    SetConsoleWindowInfo(output, TRUE, &win);
}

static HANDLE CreateVisibleBuffer(int width, int height)
{
    HANDLE output = CreateConsoleScreenBuffer(
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        CONSOLE_TEXTMODE_BUFFER,
        nullptr);

    if (output == INVALID_HANDLE_VALUE)
        return output;

    TrySetBufferSize(output, std::max(width, 1), std::max(height, 1));
    return output;
}

static void HideCursor(HANDLE output)
{
    CONSOLE_CURSOR_INFO cursor{};
    if (GetConsoleCursorInfo(output, &cursor))
    {
        cursor.bVisible = FALSE;
        SetConsoleCursorInfo(output, &cursor);
    }
}

static void PutText(std::vector<CHAR_INFO>& frame, int width, int height, int x, int y, const std::wstring& text, WORD attr, int textOffset = 0)
{
    if (y < 0 || y >= height || width <= 0)
        return;

    if (textOffset < 0)
        textOffset = 0;

    int outX = x;
    for (int i = textOffset; i < static_cast<int>(text.size()) && outX < width; ++i)
    {
        if (outX >= 0)
        {
            size_t pos = static_cast<size_t>(y) * static_cast<size_t>(width) + static_cast<size_t>(outX);
            frame[pos].Char.UnicodeChar = text[static_cast<size_t>(i)];
            frame[pos].Attributes = attr;
        }
        ++outX;
    }
}

static wchar_t FirstJumpLetter(const std::wstring& text)
{
    for (wchar_t ch : text)
    {
        if (iswalpha(ch) || iswdigit(ch))
            return static_cast<wchar_t>(towupper(ch));
    }
    return L'\0';
}

static void JumpToLetter(const std::vector<std::wstring>& rows, wchar_t key, int& selectedIndex)
{
    if (rows.empty() || key == L'\0')
        return;

    key = static_cast<wchar_t>(towupper(key));
    int count = static_cast<int>(rows.size());
    int start = ClampInt(selectedIndex, 0, count - 1);

    for (int step = 1; step <= count; ++step)
    {
        int index = (start + step) % count;
        if (FirstJumpLetter(rows[static_cast<size_t>(index)]) == key)
        {
            selectedIndex = index;
            return;
        }
    }
}

static void Render(const std::vector<std::wstring>& rows, int selectedIndex, int top, int left, int width, int height)
{
    width = std::max(width, 1);
    height = std::max(height, 1);

    std::vector<CHAR_INFO> frame(static_cast<size_t>(width) * static_cast<size_t>(height), MakeCell(L' ', NormalAttr()));

    PutText(frame, width, height, 0, 0, L"WindowResizer NATIVE DLL selector", HeaderAttr());
    PutText(frame, width, height, 0, 1, L"Up/Down/PgUp/PgDn/Home/End move | letters jump | Left/Right pan | Enter accepts | Esc cancels", HeaderAttr());

    std::wstring countLine = L"rows=" + std::to_wstring(rows.size()) +
        L" selected=" + std::to_wstring(selectedIndex + 1) + L"/" + std::to_wstring(rows.size()) +
        L" top=" + std::to_wstring(top) + L" left=" + std::to_wstring(left);
    PutText(frame, width, height, 0, 2, countLine, HeaderAttr());

    int bodyTop = kHeaderLines;
    int bodyHeight = std::max(0, height - kHeaderLines - kFooterLines);

    for (int y = 0; y < bodyHeight; ++y)
    {
        int index = top + y;
        int screenY = bodyTop + y;
        if (index < 0 || index >= static_cast<int>(rows.size()))
            continue;

        bool selected = index == selectedIndex;
        WORD attr = RowAttr(rows[static_cast<size_t>(index)], index, selected);

        for (int x = 0; x < width; ++x)
            frame[static_cast<size_t>(screenY) * static_cast<size_t>(width) + static_cast<size_t>(x)] = MakeCell(L' ', attr);

        std::wstring prefix = selected ? L"> " : L"  ";
        std::wstring line = prefix + rows[static_cast<size_t>(index)];
        PutText(frame, width, height, 0, screenY, line, attr, left);
    }

    int footerY = height - 1;
    if (footerY >= 0)
    {
        for (int x = 0; x < width; ++x)
            frame[static_cast<size_t>(footerY) * static_cast<size_t>(width) + static_cast<size_t>(x)] = MakeCell(L' ', StatusAttr());

        PutText(frame, width, height, 0, footerY, L"NATIVE DLL selector | resize updates immediately | color rows | letter jump enabled", StatusAttr());
    }

    COORD bufferSize{};
    bufferSize.X = static_cast<SHORT>(width);
    bufferSize.Y = static_cast<SHORT>(height);
    COORD bufferCoord{0, 0};
    SMALL_RECT target{0, 0, static_cast<SHORT>(width - 1), static_cast<SHORT>(height - 1)};
    WriteConsoleOutputW(gVisibleOut, frame.data(), bufferSize, bufferCoord, &target);
}

static void SetupInput()
{
    if (gInput == INVALID_HANDLE_VALUE)
        return;

    GetConsoleMode(gInput, &gOriginalInputMode);

    DWORD mode = gOriginalInputMode;
    mode |= ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS;
    mode &= ~ENABLE_QUICK_EDIT_MODE;
    SetConsoleMode(gInput, mode);
}

static std::vector<std::wstring> ConvertRows(const NativeSelectorRow* nativeRows, int rowCount)
{
    std::vector<std::wstring> rows;
    if (!nativeRows || rowCount <= 0)
        return rows;

    rows.reserve(static_cast<size_t>(rowCount));
    for (int i = 0; i < rowCount; ++i)
    {
        const wchar_t* text = nativeRows[i].DisplayText;
        if (text && *text)
            rows.emplace_back(text);
        else
            rows.emplace_back(L"(empty row)");
    }
    return rows;
}

static int RunSelector(const std::vector<std::wstring>& rows, int initialIndex, int* selectedIndexOut)
{
    if (rows.empty())
        return -2;

    gOriginalOut = GetStdHandle(STD_OUTPUT_HANDLE);
    gInput = GetStdHandle(STD_INPUT_HANDLE);

    if (gOriginalOut == INVALID_HANDLE_VALUE || gInput == INVALID_HANDLE_VALUE)
        return -3;

    CONSOLE_SCREEN_BUFFER_INFO originalInfo{};
    if (GetConsoleScreenBufferInfo(gOriginalOut, &originalInfo))
        gOriginalAttributes = originalInfo.wAttributes;

    GetConsoleCursorInfo(gOriginalOut, &gOriginalCursor);
    SetupInput();

    int width = 80;
    int height = 25;
    GetConsoleSize(gOriginalOut, width, height);

    gVisibleOut = CreateVisibleBuffer(width, height);
    if (gVisibleOut == INVALID_HANDLE_VALUE)
        return -4;

    SetConsoleActiveScreenBuffer(gVisibleOut);
    HideCursor(gVisibleOut);

    int selectedIndex = ClampInt(initialIndex, 0, static_cast<int>(rows.size()) - 1);
    int top = 0;
    int left = 0;
    int lastWidth = -1;
    int lastHeight = -1;
    bool dirty = true;

    while (true)
    {
        int currentWidth = width;
        int currentHeight = height;
        if (GetConsoleSize(gVisibleOut, currentWidth, currentHeight))
        {
            if (currentWidth != lastWidth || currentHeight != lastHeight)
            {
                width = currentWidth;
                height = currentHeight;
                lastWidth = currentWidth;
                lastHeight = currentHeight;
                TrySetBufferSize(gVisibleOut, width, height);
                dirty = true;
            }
        }

        int bodyHeight = std::max(1, height - kHeaderLines - kFooterLines);
        int maxTop = std::max(0, static_cast<int>(rows.size()) - bodyHeight);
        if (selectedIndex < top)
            top = selectedIndex;
        if (selectedIndex >= top + bodyHeight)
            top = selectedIndex - bodyHeight + 1;
        top = ClampInt(top, 0, maxTop);
        left = std::max(0, left);

        if (dirty)
        {
            Render(rows, selectedIndex, top, left, width, height);
            HideCursor(gVisibleOut);
            dirty = false;
        }

        DWORD waitResult = WaitForSingleObject(gInput, 40);
        if (waitResult != WAIT_OBJECT_0)
        {
            // Polling size above lets height/width changes redraw even without a key/mouse event.
            continue;
        }

        DWORD eventCount = 0;
        if (!GetNumberOfConsoleInputEvents(gInput, &eventCount) || eventCount == 0)
            continue;

        std::vector<INPUT_RECORD> events(eventCount);
        DWORD eventsRead = 0;
        if (!ReadConsoleInputW(gInput, events.data(), eventCount, &eventsRead))
            continue;
        events.resize(eventsRead);

        for (const INPUT_RECORD& eventRecord : events)
        {
            if (eventRecord.EventType == WINDOW_BUFFER_SIZE_EVENT)
            {
                dirty = true;
                continue;
            }

            if (eventRecord.EventType == MOUSE_EVENT)
            {
                const MOUSE_EVENT_RECORD& mouse = eventRecord.Event.MouseEvent;
                if (mouse.dwEventFlags == MOUSE_WHEELED)
                {
                    short wheelDelta = static_cast<short>((mouse.dwButtonState >> 16) & 0xffff);
                    int steps = std::max(1, std::abs(static_cast<int>(wheelDelta)) / WHEEL_DELTA);
                    int old = selectedIndex;
                    if (wheelDelta > 0)
                        selectedIndex = std::max(0, selectedIndex - steps);
                    else
                        selectedIndex = std::min(static_cast<int>(rows.size()) - 1, selectedIndex + steps);
                    dirty = dirty || (old != selectedIndex);
                    continue;
                }

                bool leftDown = (mouse.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0;
                if (leftDown && (mouse.dwEventFlags == 0 || mouse.dwEventFlags == DOUBLE_CLICK))
                {
                    int row = mouse.dwMousePosition.Y - kHeaderLines;
                    int bodyHeightNow = std::max(1, height - kHeaderLines - kFooterLines);
                    if (row >= 0 && row < bodyHeightNow)
                    {
                        int index = top + row;
                        if (index >= 0 && index < static_cast<int>(rows.size()))
                        {
                            selectedIndex = index;
                            dirty = true;
                            if (mouse.dwEventFlags == DOUBLE_CLICK)
                            {
                                if (selectedIndexOut) *selectedIndexOut = selectedIndex;
                                return 1;
                            }
                        }
                    }
                }
                continue;
            }

            if (eventRecord.EventType != KEY_EVENT || !eventRecord.Event.KeyEvent.bKeyDown)
                continue;

            const KEY_EVENT_RECORD& key = eventRecord.Event.KeyEvent;
            int oldSelected = selectedIndex;

            switch (key.wVirtualKeyCode)
            {
            case VK_ESCAPE:
                return 0;

            case VK_RETURN:
                if (selectedIndexOut) *selectedIndexOut = selectedIndex;
                return 1;

            case VK_UP:
                selectedIndex = std::max(0, selectedIndex - 1);
                break;

            case VK_DOWN:
                selectedIndex = std::min(static_cast<int>(rows.size()) - 1, selectedIndex + 1);
                break;

            case VK_PRIOR:
                selectedIndex = std::max(0, selectedIndex - std::max(1, height - kHeaderLines - kFooterLines));
                break;

            case VK_NEXT:
                selectedIndex = std::min(static_cast<int>(rows.size()) - 1,
                    selectedIndex + std::max(1, height - kHeaderLines - kFooterLines));
                break;

            case VK_HOME:
                if (key.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED))
                    left = 0;
                else
                    selectedIndex = 0;
                break;

            case VK_END:
                selectedIndex = static_cast<int>(rows.size()) - 1;
                break;

            case VK_LEFT:
                left = std::max(0, left - 4);
                break;

            case VK_RIGHT:
                left += 4;
                break;

            default:
                if (key.uChar.UnicodeChar != L'\0' && (iswalpha(key.uChar.UnicodeChar) || iswdigit(key.uChar.UnicodeChar)))
                    JumpToLetter(rows, key.uChar.UnicodeChar, selectedIndex);
                break;
            }

            if (selectedIndex != oldSelected || key.wVirtualKeyCode == VK_LEFT || key.wVirtualKeyCode == VK_RIGHT ||
                key.wVirtualKeyCode == VK_HOME || key.wVirtualKeyCode == VK_END)
            {
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
        std::vector<std::wstring> rows = ConvertRows(nativeRows, rowCount);
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

    return result;
}
