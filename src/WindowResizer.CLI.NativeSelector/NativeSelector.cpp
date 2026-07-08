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
#include <string>
#include <vector>

// Must exactly match ResizeCommand.NativeConsoleSelector.NativeSelectorRow:
//   int Sequence;
//   int ProcessId;
//   IntPtr WindowHandle;
//   int IsTopForProcess;
//   LPWStr DisplayText;
struct NativeSelectorRow
{
    int Sequence;
    int ProcessId;
    intptr_t WindowHandle;
    int IsTopForProcess;
    const wchar_t* DisplayText;
};

static const WORD kNormalAttr = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE;
static const WORD kHeaderAttr = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY;
static const WORD kSelectedAttr = BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE;
static const WORD kSelectedTextAttr = BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE |
    FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY;
static const WORD kStatusAttr = BACKGROUND_BLUE | FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY;

static int RectWidth(const SMALL_RECT& r)
{
    return r.Right - r.Left + 1;
}

static int RectHeight(const SMALL_RECT& r)
{
    return r.Bottom - r.Top + 1;
}

static int ClampInt(int value, int low, int high)
{
    if (value < low) return low;
    if (value > high) return high;
    return value;
}

static CHAR_INFO MakeCell(wchar_t ch, WORD attr)
{
    CHAR_INFO cell{};
    cell.Char.UnicodeChar = ch;
    cell.Attributes = attr;
    return cell;
}

static std::wstring SafeString(const wchar_t* text)
{
    return text ? std::wstring(text) : std::wstring();
}

static void PutText(std::vector<CHAR_INFO>& frame, int width, int height, int x, int y, const std::wstring& text, WORD attr)
{
    if (width <= 0 || height <= 0 || y < 0 || y >= height)
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

static bool ReadVisibleWindow(HANDLE out, CONSOLE_SCREEN_BUFFER_INFO& info, std::vector<CHAR_INFO>& saved)
{
    if (!GetConsoleScreenBufferInfo(out, &info))
        return false;

    int width = RectWidth(info.srWindow);
    int height = RectHeight(info.srWindow);
    if (width <= 0 || height <= 0)
        return false;

    saved.assign(static_cast<size_t>(width) * height, MakeCell(L' ', kNormalAttr));

    COORD bufferSize{ static_cast<SHORT>(width), static_cast<SHORT>(height) };
    COORD bufferCoord{ 0, 0 };
    SMALL_RECT readRect = info.srWindow;
    return ReadConsoleOutputW(out, saved.data(), bufferSize, bufferCoord, &readRect) != FALSE;
}

static bool WriteVisibleWindow(HANDLE out, const CONSOLE_SCREEN_BUFFER_INFO& info, const std::vector<CHAR_INFO>& frame)
{
    int width = RectWidth(info.srWindow);
    int height = RectHeight(info.srWindow);
    if (width <= 0 || height <= 0 || frame.size() < static_cast<size_t>(width) * height)
        return false;

    COORD bufferSize{ static_cast<SHORT>(width), static_cast<SHORT>(height) };
    COORD bufferCoord{ 0, 0 };
    SMALL_RECT dest = info.srWindow;
    return WriteConsoleOutputW(out, frame.data(), bufferSize, bufferCoord, &dest) != FALSE;
}

static void RenderSelector(HANDLE out, const NativeSelectorRow* rows, int rowCount, int selected, int offset)
{
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (!GetConsoleScreenBufferInfo(out, &info))
        return;

    int width = RectWidth(info.srWindow);
    int height = RectHeight(info.srWindow);
    if (width <= 0 || height <= 0)
        return;

    std::vector<CHAR_INFO> frame(static_cast<size_t>(width) * height, MakeCell(L' ', kNormalAttr));

    for (int x = 0; x < width; ++x)
    {
        frame[static_cast<size_t>(x)] = MakeCell(L' ', kHeaderAttr);
        if (height > 1)
            frame[static_cast<size_t>(width + x)] = MakeCell(L' ', kHeaderAttr);
    }

    PutText(frame, width, height, 0, 0, L"WindowResizer NATIVE DLL selector", kHeaderAttr);
    PutText(frame, width, height, 0, 1, L"Up/Down, PgUp/PgDn, Home/End, mouse wheel/click, Enter accepts, Esc cancels", kHeaderAttr);

    const int bodyTop = 3;
    const int footerRow = height - 1;
    int bodyHeight = std::max(1, height - bodyTop - 1);

    offset = ClampInt(offset, 0, std::max(0, rowCount - bodyHeight));

    for (int row = 0; row < bodyHeight; ++row)
    {
        int index = offset + row;
        if (index >= rowCount)
            break;

        int y = bodyTop + row;
        if (y < 0 || y >= height)
            continue;

        bool isSelected = index == selected;
        WORD attr = isSelected ? kSelectedAttr : kNormalAttr;
        for (int x = 0; x < width; ++x)
            frame[static_cast<size_t>(y) * width + x] = MakeCell(L' ', attr);

        std::wstring line = isSelected ? L"> " : L"  ";
        line += SafeString(rows[index].DisplayText);
        PutText(frame, width, height, 0, y, line, isSelected ? kSelectedTextAttr : attr);
    }

    if (footerRow >= 0)
    {
        for (int x = 0; x < width; ++x)
            frame[static_cast<size_t>(footerRow) * width + x] = MakeCell(L' ', kStatusAttr);

        std::wstring status = L"NATIVE DLL selector | selected=" + std::to_wstring(selected + 1) +
            L"/" + std::to_wstring(rowCount) + L" | sequence=" + std::to_wstring(rows[selected].Sequence) + L" ";
        PutText(frame, width, height, 0, footerRow, status, kStatusAttr);
    }

    WriteVisibleWindow(out, info, frame);
}

static void RestoreConsole(HANDLE out, HANDLE input, bool saved, const CONSOLE_SCREEN_BUFFER_INFO& savedInfo,
    const std::vector<CHAR_INFO>& savedFrame, bool haveCursor, const CONSOLE_CURSOR_INFO& originalCursor, DWORD originalInputMode)
{
    if (saved)
        WriteVisibleWindow(out, savedInfo, savedFrame);
    if (haveCursor)
        SetConsoleCursorInfo(out, &originalCursor);
    if (input != INVALID_HANDLE_VALUE && input != nullptr)
        SetConsoleMode(input, originalInputMode);
}

static int RunSelector(const NativeSelectorRow* rows, int rowCount, int initialIndex, int* selectedIndexOut)
{
    if (!rows || rowCount <= 0)
        return -2;
    if (!selectedIndexOut)
        return -3;

    *selectedIndexOut = -1;

    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
    HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    if (out == INVALID_HANDLE_VALUE || out == nullptr || input == INVALID_HANDLE_VALUE || input == nullptr)
        return -4;

    CONSOLE_SCREEN_BUFFER_INFO savedInfo{};
    std::vector<CHAR_INFO> savedFrame;
    bool saved = ReadVisibleWindow(out, savedInfo, savedFrame);

    DWORD originalInputMode = 0;
    GetConsoleMode(input, &originalInputMode);
    DWORD mode = originalInputMode;
    mode |= ENABLE_EXTENDED_FLAGS | ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT;
    mode &= ~ENABLE_QUICK_EDIT_MODE;
    SetConsoleMode(input, mode);

    CONSOLE_CURSOR_INFO originalCursor{};
    bool haveCursor = GetConsoleCursorInfo(out, &originalCursor) != FALSE;
    if (haveCursor)
    {
        CONSOLE_CURSOR_INFO hidden = originalCursor;
        hidden.bVisible = FALSE;
        SetConsoleCursorInfo(out, &hidden);
    }

    int selected = ClampInt(initialIndex, 0, rowCount - 1);
    int offset = 0;
    bool dirty = true;

    while (true)
    {
        CONSOLE_SCREEN_BUFFER_INFO currentInfo{};
        int bodyHeight = 10;
        int windowTop = 0;
        if (GetConsoleScreenBufferInfo(out, &currentInfo))
        {
            bodyHeight = std::max(1, RectHeight(currentInfo.srWindow) - 4);
            windowTop = currentInfo.srWindow.Top;
        }

        if (selected < offset)
            offset = selected;
        if (selected >= offset + bodyHeight)
            offset = selected - bodyHeight + 1;
        offset = ClampInt(offset, 0, std::max(0, rowCount - bodyHeight));

        if (dirty)
        {
            RenderSelector(out, rows, rowCount, selected, offset);
            dirty = false;
        }

        DWORD wait = WaitForSingleObject(input, 50);
        if (wait != WAIT_OBJECT_0)
            continue;

        INPUT_RECORD events[64]{};
        DWORD read = 0;
        if (!ReadConsoleInputW(input, events, 64, &read))
            continue;

        for (DWORD i = 0; i < read; ++i)
        {
            const INPUT_RECORD& event = events[i];

            if (event.EventType == WINDOW_BUFFER_SIZE_EVENT)
            {
                dirty = true;
                continue;
            }

            if (event.EventType == MOUSE_EVENT)
            {
                const MOUSE_EVENT_RECORD& mouse = event.Event.MouseEvent;
                if (mouse.dwEventFlags == MOUSE_WHEELED)
                {
                    short wheelDelta = static_cast<short>((mouse.dwButtonState >> 16) & 0xffff);
                    selected = wheelDelta > 0 ? std::max(0, selected - 1) : std::min(rowCount - 1, selected + 1);
                    dirty = true;
                    continue;
                }

                bool leftDown = (mouse.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0;
                if (leftDown && (mouse.dwEventFlags == 0 || mouse.dwEventFlags == DOUBLE_CLICK))
                {
                    int row = mouse.dwMousePosition.Y - windowTop - 3;
                    if (row >= 0 && row < bodyHeight)
                    {
                        int index = offset + row;
                        if (index >= 0 && index < rowCount)
                        {
                            selected = index;
                            dirty = true;
                            if (mouse.dwEventFlags == DOUBLE_CLICK)
                            {
                                *selectedIndexOut = rows[selected].Sequence;
                                RestoreConsole(out, input, saved, savedInfo, savedFrame, haveCursor, originalCursor, originalInputMode);
                                return 1;
                            }
                        }
                    }
                    continue;
                }
            }

            if (event.EventType != KEY_EVENT || !event.Event.KeyEvent.bKeyDown)
                continue;

            switch (event.Event.KeyEvent.wVirtualKeyCode)
            {
            case VK_ESCAPE:
                RestoreConsole(out, input, saved, savedInfo, savedFrame, haveCursor, originalCursor, originalInputMode);
                return 0;
            case VK_RETURN:
                *selectedIndexOut = rows[selected].Sequence;
                RestoreConsole(out, input, saved, savedInfo, savedFrame, haveCursor, originalCursor, originalInputMode);
                return 1;
            case VK_UP:
                selected = std::max(0, selected - 1);
                dirty = true;
                break;
            case VK_DOWN:
                selected = std::min(rowCount - 1, selected + 1);
                dirty = true;
                break;
            case VK_PRIOR:
                selected = std::max(0, selected - bodyHeight);
                dirty = true;
                break;
            case VK_NEXT:
                selected = std::min(rowCount - 1, selected + bodyHeight);
                dirty = true;
                break;
            case VK_HOME:
                selected = 0;
                dirty = true;
                break;
            case VK_END:
                selected = rowCount - 1;
                dirty = true;
                break;
            default:
                break;
            }
        }
    }
}

extern "C" __declspec(dllexport)
int __stdcall SelectWindowFromRows(const NativeSelectorRow* rows, int rowCount, int initialIndex, int* selectedIndexOut)
{
    try
    {
        return RunSelector(rows, rowCount, initialIndex, selectedIndexOut);
    }
    catch (...)
    {
        if (selectedIndexOut)
            *selectedIndexOut = -1;
        return -100;
    }
}
