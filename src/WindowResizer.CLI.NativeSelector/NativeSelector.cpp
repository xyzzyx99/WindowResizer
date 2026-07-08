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

static HANDLE gOut = INVALID_HANDLE_VALUE;
static HANDLE gIn = INVALID_HANDLE_VALUE;
static HANDLE gHidden = INVALID_HANDLE_VALUE;
static DWORD gOriginalInputMode = 0;
static CONSOLE_CURSOR_INFO gOriginalCursor{};
static bool gHadCursor = false;

static const int kHeaderLines = 2;
static const int kFooterLines = 1;
static const int kBodyTop = kHeaderLines;

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

static bool GetVisibleInfo(CONSOLE_SCREEN_BUFFER_INFO& info, int& width, int& height)
{
    if (!GetConsoleScreenBufferInfo(gOut, &info))
        return false;

    width = RectWidth(info.srWindow);
    height = RectHeight(info.srWindow);

    if (width < 1) width = 1;
    if (height < 1) height = 1;
    return true;
}

static WORD NormalAttrFromInfo(const CONSOLE_SCREEN_BUFFER_INFO& info)
{
    return info.wAttributes;
}

static WORD ForegroundBits(WORD attr)
{
    return attr & (FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY);
}

static WORD BackgroundBits(WORD attr)
{
    return attr & (BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE | BACKGROUND_INTENSITY);
}

static WORD ForegroundFromBackground(WORD attr)
{
    WORD bg = BackgroundBits(attr);
    WORD fg = 0;
    if (bg & BACKGROUND_RED) fg |= FOREGROUND_RED;
    if (bg & BACKGROUND_GREEN) fg |= FOREGROUND_GREEN;
    if (bg & BACKGROUND_BLUE) fg |= FOREGROUND_BLUE;
    if (bg & BACKGROUND_INTENSITY) fg |= FOREGROUND_INTENSITY;

    // If the terminal background is black, use black-on-grey for the selected row.
    return fg;
}

static WORD SelectedAttrFromInfo(const CONSOLE_SCREEN_BUFFER_INFO& info)
{
    const WORD greyBackground = BACKGROUND_RED | BACKGROUND_GREEN | BACKGROUND_BLUE;
    return greyBackground | ForegroundFromBackground(info.wAttributes);
}

static WORD HeaderAttrFromInfo(const CONSOLE_SCREEN_BUFFER_INFO& info)
{
    return BackgroundBits(info.wAttributes) | FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY;
}

static WORD TopProcessAttrFromInfo(const CONSOLE_SCREEN_BUFFER_INFO& info)
{
    return BackgroundBits(info.wAttributes) | FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY;
}

static WORD FooterAttrFromInfo(const CONSOLE_SCREEN_BUFFER_INFO& info)
{
    return info.wAttributes;
}

static HANDLE CreateHiddenBuffer()
{
    return CreateConsoleScreenBuffer(
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        CONSOLE_TEXTMODE_BUFFER,
        nullptr);
}

static void SetBufferSizeSafe(HANDLE h, int width, int height)
{
    if (width < 1) width = 1;
    if (height < 1) height = 1;
    if (width > 32760) width = 32760;
    if (height > 32760) height = 32760;

    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (GetConsoleScreenBufferInfo(h, &info))
    {
        if (info.dwSize.X == width && info.dwSize.Y == height)
            return;
    }

    COORD size{};
    size.X = static_cast<SHORT>(width);
    size.Y = static_cast<SHORT>(height);
    SetConsoleScreenBufferSize(h, size);
}

static void ClearLine(HANDLE h, short y, short width, WORD attr)
{
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

static wchar_t FirstJumpLetter(const std::wstring& s)
{
    for (wchar_t ch : s)
    {
        if (iswalnum(ch))
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

static std::wstring SafeText(const wchar_t* p)
{
    return p ? std::wstring(p) : std::wstring();
}

static size_t ProcessNameLength(const std::wstring& row)
{
    size_t bar = row.find(L" |");
    size_t spaceBracket = row.find(L" [");

    size_t end = std::wstring::npos;
    if (bar != std::wstring::npos) end = bar;
    if (spaceBracket != std::wstring::npos) end = std::min(end, spaceBracket);

    if (end == std::wstring::npos)
        end = row.size();

    while (end > 0 && iswspace(row[end - 1]))
        --end;

    return end;
}

static void WriteTopHighlights(HANDLE h, short y, const std::wstring& row, WORD topAttr)
{
    size_t procLen = ProcessNameLength(row);
    if (procLen > 0)
        WriteAt(h, 2, y, row.substr(0, procLen), topAttr);

    size_t topPos = row.find(L"Top");
    if (topPos != std::wstring::npos)
        WriteAt(h, static_cast<short>(2 + static_cast<int>(topPos)), y, L"Top", topAttr);
}

static void DrawHidden(
    const std::vector<std::wstring>& rows,
    int selectedIndex,
    int virtualTop,
    int visibleWidth,
    int visibleHeight,
    const CONSOLE_SCREEN_BUFFER_INFO& visibleInfo)
{
    WORD normalAttr = NormalAttrFromInfo(visibleInfo);
    WORD headerAttr = HeaderAttrFromInfo(visibleInfo);
    WORD selectedAttr = SelectedAttrFromInfo(visibleInfo);
    WORD topAttr = TopProcessAttrFromInfo(visibleInfo);
    WORD footerAttr = FooterAttrFromInfo(visibleInfo);

    int hiddenWidth = std::max(visibleWidth, 120);
    int hiddenHeight = std::max(visibleHeight, kBodyTop + static_cast<int>(rows.size()) + kFooterLines + 1);

    // This hidden dwSize is intentionally larger than the visible viewport.  The
    // full rows are rendered in the hidden buffer first; then the visible window
    // is copied cell-for-cell to the real console.  This matches the C++ demo
    // behavior and lets the console host place double-width CJK cells before copy.
    SetBufferSizeSafe(gHidden, hiddenWidth, hiddenHeight);

    for (int y = 0; y < hiddenHeight; ++y)
        ClearLine(gHidden, static_cast<short>(y), static_cast<short>(hiddenWidth), normalAttr);

    WriteAt(gHidden, 0, 0, L"WindowResizer interactive selector", headerAttr);
    WriteAt(gHidden, 0, 1, L"Up/Down select, PgUp/PgDn, Home/End, letters jump, Enter accepts, Esc cancels", headerAttr);

    int bodyHeight = std::max(1, visibleHeight - kHeaderLines - kFooterLines);
    int maxTop = std::max(0, static_cast<int>(rows.size()) - bodyHeight);
    virtualTop = ClampInt(virtualTop, 0, maxTop);

    for (int screenRow = 0; screenRow < bodyHeight; ++screenRow)
    {
        int index = virtualTop + screenRow;
        if (index < 0 || index >= static_cast<int>(rows.size()))
            continue;

        short y = static_cast<short>(kBodyTop + screenRow);
        bool selected = index == selectedIndex;
        WORD rowAttr = selected ? selectedAttr : normalAttr;

        ClearLine(gHidden, y, static_cast<short>(hiddenWidth), rowAttr);

        std::wstring line = selected ? L"> " : L"  ";
        line += rows[static_cast<size_t>(index)];
        WriteAt(gHidden, 0, y, line, rowAttr);

        if (!selected)
        {
            // Only top-process markers use yellow.  The selected row keeps the
            // exact selected foreground/background requested by the C# scheme.
            if (line.find(L"Top") != std::wstring::npos)
                WriteTopHighlights(gHidden, y, line.substr(2), topAttr);
        }
    }

    short footerY = static_cast<short>(visibleHeight - 1);
    ClearLine(gHidden, footerY, static_cast<short>(hiddenWidth), footerAttr);
    std::wstring footer = L"Enter: resize selected   Esc: cancel   Rows: " + std::to_wstring(rows.size());
    WriteAt(gHidden, 0, footerY, footer, footerAttr);
}

static bool CopyHiddenToVisible(int visibleWidth, int visibleHeight)
{
    if (visibleWidth <= 0 || visibleHeight <= 0)
        return false;

    std::vector<CHAR_INFO> cells(static_cast<size_t>(visibleWidth) * static_cast<size_t>(visibleHeight));

    COORD bufferSize{};
    bufferSize.X = static_cast<SHORT>(visibleWidth);
    bufferSize.Y = static_cast<SHORT>(visibleHeight);

    COORD bufferCoord{};
    bufferCoord.X = 0;
    bufferCoord.Y = 0;

    SMALL_RECT rect{};
    rect.Left = 0;
    rect.Top = 0;
    rect.Right = static_cast<SHORT>(visibleWidth - 1);
    rect.Bottom = static_cast<SHORT>(visibleHeight - 1);

    if (!ReadConsoleOutputW(gHidden, cells.data(), bufferSize, bufferCoord, &rect))
        return false;

    SMALL_RECT dest{};
    dest.Left = 0;
    dest.Top = 0;
    dest.Right = static_cast<SHORT>(visibleWidth - 1);
    dest.Bottom = static_cast<SHORT>(visibleHeight - 1);

    return WriteConsoleOutputW(gOut, cells.data(), bufferSize, bufferCoord, &dest) != FALSE;
}

static void SaveVisible(std::vector<CHAR_INFO>& saved, int width, int height)
{
    saved.clear();
    if (width <= 0 || height <= 0)
        return;

    saved.resize(static_cast<size_t>(width) * static_cast<size_t>(height));

    COORD bufferSize{static_cast<SHORT>(width), static_cast<SHORT>(height)};
    COORD bufferCoord{0, 0};
    SMALL_RECT rect{0, 0, static_cast<SHORT>(width - 1), static_cast<SHORT>(height - 1)};
    ReadConsoleOutputW(gOut, saved.data(), bufferSize, bufferCoord, &rect);
}

static void RestoreVisible(const std::vector<CHAR_INFO>& saved, int width, int height)
{
    if (saved.empty() || width <= 0 || height <= 0)
        return;

    COORD bufferSize{static_cast<SHORT>(width), static_cast<SHORT>(height)};
    COORD bufferCoord{0, 0};
    SMALL_RECT rect{0, 0, static_cast<SHORT>(width - 1), static_cast<SHORT>(height - 1)};
    WriteConsoleOutputW(gOut, saved.data(), bufferSize, bufferCoord, &rect);
}

static void HideCursor()
{
    if (gOut == INVALID_HANDLE_VALUE)
        return;

    CONSOLE_CURSOR_INFO cursor{};
    if (GetConsoleCursorInfo(gOut, &cursor))
    {
        cursor.bVisible = FALSE;
        SetConsoleCursorInfo(gOut, &cursor);
    }
}

static void SetupInput()
{
    if (gIn == INVALID_HANDLE_VALUE)
        return;

    if (GetConsoleMode(gIn, &gOriginalInputMode))
    {
        DWORD mode = gOriginalInputMode;
        mode |= ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS;
        mode &= ~ENABLE_QUICK_EDIT_MODE;
        SetConsoleMode(gIn, mode);
    }
}

static int RunSelector(const std::vector<std::wstring>& rows, int initialIndex, int* selectedIndexOut)
{
    if (rows.empty())
        return -2;

    gOut = GetStdHandle(STD_OUTPUT_HANDLE);
    gIn = GetStdHandle(STD_INPUT_HANDLE);
    if (gOut == INVALID_HANDLE_VALUE || gIn == INVALID_HANDLE_VALUE)
        return -3;

    gHadCursor = GetConsoleCursorInfo(gOut, &gOriginalCursor) != FALSE;
    SetupInput();
    HideCursor();

    gHidden = CreateHiddenBuffer();
    if (gHidden == INVALID_HANDLE_VALUE)
        return -4;

    int selectedIndex = ClampInt(initialIndex, 0, static_cast<int>(rows.size()) - 1);
    int virtualTop = 0;
    int lastWidth = -1;
    int lastHeight = -1;
    int savedWidth = 0;
    int savedHeight = 0;
    std::vector<CHAR_INFO> saved;
    bool dirty = true;

    while (true)
    {
        CONSOLE_SCREEN_BUFFER_INFO info{};
        int width = 0;
        int height = 0;
        if (GetVisibleInfo(info, width, height))
        {
            if (width != lastWidth || height != lastHeight)
            {
                if (saved.empty())
                {
                    savedWidth = width;
                    savedHeight = height;
                    SaveVisible(saved, savedWidth, savedHeight);
                }

                lastWidth = width;
                lastHeight = height;
                dirty = true;
            }

            int bodyHeight = std::max(1, height - kHeaderLines - kFooterLines);
            if (selectedIndex < virtualTop)
                virtualTop = selectedIndex;
            if (selectedIndex >= virtualTop + bodyHeight)
                virtualTop = selectedIndex - bodyHeight + 1;
            virtualTop = ClampInt(virtualTop, 0, std::max(0, static_cast<int>(rows.size()) - bodyHeight));

            if (dirty)
            {
                DrawHidden(rows, selectedIndex, virtualTop, width, height, info);
                CopyHiddenToVisible(width, height);
                HideCursor();
                dirty = false;
            }
        }

        DWORD wait = WaitForSingleObject(gIn, 30);
        if (wait != WAIT_OBJECT_0)
            continue;

        DWORD available = 0;
        if (!GetNumberOfConsoleInputEvents(gIn, &available) || available == 0)
            continue;

        std::vector<INPUT_RECORD> events(available);
        DWORD read = 0;
        if (!ReadConsoleInputW(gIn, events.data(), available, &read))
            continue;

        for (DWORD i = 0; i < read; ++i)
        {
            const INPUT_RECORD& e = events[static_cast<size_t>(i)];

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
                    int old = selectedIndex;
                    selectedIndex = wheelDelta > 0
                        ? std::max(0, selectedIndex - 1)
                        : std::min(static_cast<int>(rows.size()) - 1, selectedIndex + 1);
                    dirty = dirty || (selectedIndex != old);
                    continue;
                }

                bool leftDown = (m.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0;
                if (leftDown && (m.dwEventFlags == 0 || m.dwEventFlags == DOUBLE_CLICK))
                {
                    int clicked = virtualTop + m.dwMousePosition.Y - kBodyTop;
                    if (clicked >= 0 && clicked < static_cast<int>(rows.size()))
                    {
                        selectedIndex = clicked;
                        dirty = true;
                        if (m.dwEventFlags == DOUBLE_CLICK)
                        {
                            if (selectedIndexOut) *selectedIndexOut = selectedIndex;
                            RestoreVisible(saved, savedWidth, savedHeight);
                            return 1;
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
                RestoreVisible(saved, savedWidth, savedHeight);
                return 0;

            case VK_RETURN:
                if (selectedIndexOut) *selectedIndexOut = selectedIndex;
                RestoreVisible(saved, savedWidth, savedHeight);
                return 1;

            case VK_UP:
                selectedIndex = std::max(0, selectedIndex - 1);
                break;

            case VK_DOWN:
                selectedIndex = std::min(static_cast<int>(rows.size()) - 1, selectedIndex + 1);
                break;

            case VK_PRIOR:
                selectedIndex = std::max(0, selectedIndex - std::max(1, lastHeight - kHeaderLines - kFooterLines));
                break;

            case VK_NEXT:
                selectedIndex = std::min(static_cast<int>(rows.size()) - 1, selectedIndex + std::max(1, lastHeight - kHeaderLines - kFooterLines));
                break;

            case VK_HOME:
                selectedIndex = 0;
                break;

            case VK_END:
                selectedIndex = static_cast<int>(rows.size()) - 1;
                break;

            default:
                if (k.uChar.UnicodeChar != L'\0')
                    JumpToLetter(rows, k.uChar.UnicodeChar, selectedIndex);
                break;
            }

            if (selectedIndex != old)
                dirty = true;
        }
    }
}

extern "C" __declspec(dllexport)
int __stdcall SelectWindowFromRows(
    const NativeSelectorRow* nativeRows,
    int rowCount,
    int initialIndex,
    int* selectedIndexOut)
{
    int result = -1;

    try
    {
        if (selectedIndexOut)
            *selectedIndexOut = 0;

        if (!nativeRows || rowCount <= 0)
            return -2;

        std::vector<std::wstring> rows;
        rows.reserve(static_cast<size_t>(rowCount));

        for (int i = 0; i < rowCount; ++i)
        {
            std::wstring text = SafeText(nativeRows[i].DisplayText);
            if (text.empty())
                text = L"(empty)";
            rows.push_back(text);
        }

        result = RunSelector(rows, initialIndex, selectedIndexOut);
    }
    catch (...)
    {
        result = -100;
    }

    if (gHadCursor && gOut != INVALID_HANDLE_VALUE)
        SetConsoleCursorInfo(gOut, &gOriginalCursor);

    if (gIn != INVALID_HANDLE_VALUE && gOriginalInputMode != 0)
        SetConsoleMode(gIn, gOriginalInputMode);

    if (gHidden != INVALID_HANDLE_VALUE)
    {
        CloseHandle(gHidden);
        gHidden = INVALID_HANDLE_VALUE;
    }

    return result;
}
