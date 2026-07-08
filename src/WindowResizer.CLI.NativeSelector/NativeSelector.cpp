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
#include <cstdlib>
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

struct RowInfo
{
    std::wstring Text;
    bool IsTopForProcess;
};

struct TextSegment
{
    int Start;
    int End;
    WORD Attr;
};

static HANDLE gOutput = INVALID_HANDLE_VALUE;
static HANDLE gInput = INVALID_HANDLE_VALUE;
static DWORD gOriginalInputMode = 0;
static CONSOLE_CURSOR_INFO gOriginalCursor{};
static WORD gOriginalAttributes = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE;
static bool gHadCursor = false;

static const int kHeaderLines = 2;
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

static WORD ForegroundFromBackground(WORD attr)
{
    WORD fg = static_cast<WORD>((attr >> 4) & 0x000F);
    // If the terminal background is black, black-on-grey is the intended inverse-like selection.
    return fg;
}

static WORD NormalAttr()
{
    return static_cast<WORD>(OriginalBackground() | OriginalForeground());
}

static WORD HeaderAttr()
{
    return static_cast<WORD>(OriginalBackground() | OriginalForeground() | FOREGROUND_INTENSITY);
}

static WORD FooterAttr()
{
    return HeaderAttr();
}

static WORD SelectedAttr()
{
    WORD greyBackground = BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE;
    WORD fgFromTerminalBackground = ForegroundFromBackground(gOriginalAttributes);
    return static_cast<WORD>(greyBackground | fgFromTerminalBackground);
}

static WORD TopProcessAttr(bool selected)
{
    WORD yellow = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY;
    WORD background = selected
        ? static_cast<WORD>(BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE)
        : OriginalBackground();
    return static_cast<WORD>(background | yellow);
}

static CHAR_INFO MakeCell(wchar_t ch, WORD attr)
{
    CHAR_INFO cell{};
    cell.Char.UnicodeChar = ch;
    cell.Attributes = attr;
    return cell;
}

static bool GetConsoleWindow(HANDLE output, SMALL_RECT& windowRect, int& width, int& height)
{
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (!GetConsoleScreenBufferInfo(output, &info))
        return false;

    gOriginalAttributes = info.wAttributes;
    windowRect = info.srWindow;
    width = std::max(1, RectWidth(info.srWindow));
    height = std::max(1, RectHeight(info.srWindow));
    return true;
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

static void PutText(std::vector<CHAR_INFO>& frame, int width, int height, int x, int y,
    const std::wstring& text, WORD attr, int textOffset = 0)
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

static void PutTextWithSegments(std::vector<CHAR_INFO>& frame, int width, int height, int x, int y,
    const std::wstring& text, WORD defaultAttr, int textOffset, const std::vector<TextSegment>& segments)
{
    if (y < 0 || y >= height || width <= 0)
        return;

    if (textOffset < 0)
        textOffset = 0;

    int outX = x;
    for (int i = textOffset; i < static_cast<int>(text.size()) && outX < width; ++i)
    {
        WORD attr = defaultAttr;
        for (const TextSegment& segment : segments)
        {
            if (i >= segment.Start && i < segment.End)
            {
                attr = segment.Attr;
                break;
            }
        }

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

static void JumpToLetter(const std::vector<RowInfo>& rows, wchar_t key, int& selectedIndex)
{
    if (rows.empty() || key == L'\0')
        return;

    key = static_cast<wchar_t>(towupper(key));
    int count = static_cast<int>(rows.size());
    int start = ClampInt(selectedIndex, 0, count - 1);

    for (int step = 1; step <= count; ++step)
    {
        int index = (start + step) % count;
        if (FirstJumpLetter(rows[static_cast<size_t>(index)].Text) == key)
        {
            selectedIndex = index;
            return;
        }
    }
}

static int ProcessNameEnd(const std::wstring& displayText)
{
    size_t pipe = displayText.find(L" | ");
    size_t bracket = displayText.find(L" [");
    size_t end = std::wstring::npos;

    if (pipe != std::wstring::npos)
        end = pipe;
    if (bracket != std::wstring::npos)
        end = end == std::wstring::npos ? bracket : std::min(end, bracket);

    if (end == std::wstring::npos)
        end = displayText.find(L' ');
    if (end == std::wstring::npos)
        end = displayText.size();

    return static_cast<int>(end);
}

static std::vector<TextSegment> TopSegments(const RowInfo& row, int prefixLength, bool selected)
{
    std::vector<TextSegment> segments;
    if (!row.IsTopForProcess || row.Text.empty())
        return segments;

    WORD topAttr = TopProcessAttr(selected);

    int processEnd = ProcessNameEnd(row.Text);
    if (processEnd > 0)
        segments.push_back(TextSegment{prefixLength, prefixLength + processEnd, topAttr});

    size_t searchFrom = 0;
    while (true)
    {
        size_t top = row.Text.find(L"Top", searchFrom);
        if (top == std::wstring::npos)
            break;

        bool leftOk = top == 0 || !iswalpha(row.Text[top - 1]);
        bool rightOk = top + 3 >= row.Text.size() || !iswalpha(row.Text[top + 3]);
        if (leftOk && rightOk)
        {
            int start = prefixLength + static_cast<int>(top);
            segments.push_back(TextSegment{start, start + 3, topAttr});
        }
        searchFrom = top + 3;
    }

    return segments;
}

static void Render(const std::vector<RowInfo>& rows, int selectedIndex, int top, int left, int width, int height, const SMALL_RECT& windowRect)
{
    width = std::max(width, 1);
    height = std::max(height, 1);

    std::vector<CHAR_INFO> frame(static_cast<size_t>(width) * static_cast<size_t>(height), MakeCell(L' ', NormalAttr()));

    PutText(frame, width, height, 0, 0, L"WindowResizer interactive selector", HeaderAttr());
    PutText(frame, width, height, 0, 1, L"Up/Down/PgUp/PgDn/Home/End move | letters jump | Left/Right pan | Enter accepts | Esc cancels", HeaderAttr());

    int bodyTop = kHeaderLines;
    int bodyHeight = std::max(0, height - kHeaderLines - kFooterLines);

    for (int y = 0; y < bodyHeight; ++y)
    {
        int index = top + y;
        int screenY = bodyTop + y;
        if (index < 0 || index >= static_cast<int>(rows.size()))
            continue;

        const RowInfo& row = rows[static_cast<size_t>(index)];
        bool selected = index == selectedIndex;
        WORD attr = selected ? SelectedAttr() : NormalAttr();

        if (selected)
        {
            for (int x = 0; x < width; ++x)
                frame[static_cast<size_t>(screenY) * static_cast<size_t>(width) + static_cast<size_t>(x)] = MakeCell(L' ', attr);
        }

        std::wstring prefix = selected ? L"> " : L"  ";
        std::wstring line = prefix + row.Text;
        std::vector<TextSegment> segments = TopSegments(row, static_cast<int>(prefix.size()), selected);
        PutTextWithSegments(frame, width, height, 0, screenY, line, attr, left, segments);
    }

    int footerY = height - 1;
    if (footerY >= 0)
    {
        for (int x = 0; x < width; ++x)
            frame[static_cast<size_t>(footerY) * static_cast<size_t>(width) + static_cast<size_t>(x)] = MakeCell(L' ', NormalAttr());

        std::wstring footer = L"rows=" + std::to_wstring(rows.size()) +
            L" selected=" + std::to_wstring(selectedIndex + 1) + L"/" + std::to_wstring(rows.size()) +
            L" left=" + std::to_wstring(left);
        PutText(frame, width, height, 0, footerY, footer, FooterAttr());
    }

    COORD bufferSize{};
    bufferSize.X = static_cast<SHORT>(width);
    bufferSize.Y = static_cast<SHORT>(height);
    COORD bufferCoord{0, 0};
    SMALL_RECT target = windowRect;
    WriteConsoleOutputW(gOutput, frame.data(), bufferSize, bufferCoord, &target);
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

static std::vector<RowInfo> ConvertRows(const NativeSelectorRow* nativeRows, int rowCount)
{
    std::vector<RowInfo> rows;
    if (!nativeRows || rowCount <= 0)
        return rows;

    rows.reserve(static_cast<size_t>(rowCount));
    for (int i = 0; i < rowCount; ++i)
    {
        const wchar_t* text = nativeRows[i].DisplayText;
        RowInfo row;
        row.Text = (text && *text) ? std::wstring(text) : std::wstring(L"(empty row)");
        row.IsTopForProcess = nativeRows[i].IsTopForProcess != 0;
        rows.push_back(row);
    }
    return rows;
}

static int RunSelector(const std::vector<RowInfo>& rows, int initialIndex, int* selectedIndexOut)
{
    if (rows.empty())
        return -2;

    gOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    gInput = GetStdHandle(STD_INPUT_HANDLE);

    if (gOutput == INVALID_HANDLE_VALUE || gInput == INVALID_HANDLE_VALUE)
        return -3;

    CONSOLE_SCREEN_BUFFER_INFO originalInfo{};
    if (GetConsoleScreenBufferInfo(gOutput, &originalInfo))
        gOriginalAttributes = originalInfo.wAttributes;

    gHadCursor = GetConsoleCursorInfo(gOutput, &gOriginalCursor) != FALSE;
    SetupInput();
    HideCursor(gOutput);

    int selectedIndex = ClampInt(initialIndex, 0, static_cast<int>(rows.size()) - 1);
    int top = 0;
    int left = 0;
    int width = 80;
    int height = 25;
    int lastWidth = -1;
    int lastHeight = -1;
    SMALL_RECT windowRect{};
    bool dirty = true;

    while (true)
    {
        int currentWidth = width;
        int currentHeight = height;
        SMALL_RECT currentRect = windowRect;
        if (GetConsoleWindow(gOutput, currentRect, currentWidth, currentHeight))
        {
            if (currentWidth != lastWidth || currentHeight != lastHeight ||
                currentRect.Left != windowRect.Left || currentRect.Top != windowRect.Top)
            {
                width = currentWidth;
                height = currentHeight;
                windowRect = currentRect;
                lastWidth = currentWidth;
                lastHeight = currentHeight;
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
            Render(rows, selectedIndex, top, left, width, height, windowRect);
            HideCursor(gOutput);
            dirty = false;
        }

        DWORD waitResult = WaitForSingleObject(gInput, 20);
        if (waitResult != WAIT_OBJECT_0)
        {
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
                    int row = mouse.dwMousePosition.Y - windowRect.Top - kHeaderLines;
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
            int oldLeft = left;

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

            if (selectedIndex != oldSelected || left != oldLeft || key.wVirtualKeyCode == VK_HOME || key.wVirtualKeyCode == VK_END)
                dirty = true;
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
        std::vector<RowInfo> rows = ConvertRows(nativeRows, rowCount);
        result = RunSelector(rows, initialIndex, selectedIndexOut);
    }
    catch (...)
    {
        result = -100;
    }

    if (gHadCursor && gOutput != INVALID_HANDLE_VALUE)
        SetConsoleCursorInfo(gOutput, &gOriginalCursor);

    if (gInput != INVALID_HANDLE_VALUE)
        SetConsoleMode(gInput, gOriginalInputMode);

    return result;
}
