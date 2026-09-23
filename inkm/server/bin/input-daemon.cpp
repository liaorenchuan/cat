// input-daemon.cpp - SendInput mouse+keyboard injection daemon
// Protocol: stdin line-based commands, stdout responses
// Compile: cl /EHsc /O2 /MT input-daemon.cpp /link user32.lib gdi32.lib

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <iostream>
#include <string>
#include <sstream>
#include <vector>
#include <algorithm>
#include <cctype>
#include <io.h>
#include <fcntl.h>

static std::string trim(const std::string& s) {
    size_t start = s.find_first_not_of(" \t\r\n");
    if (start == std::string::npos) return "";
    size_t end = s.find_last_not_of(" \t\r\n");
    return s.substr(start, end - start + 1);
}

static std::vector<std::string> split(const std::string& s, char delim) {
    std::vector<std::string> parts;
    std::stringstream ss(s);
    std::string item;
    while (std::getline(ss, item, delim)) {
        if (!item.empty()) parts.push_back(item);
    }
    return parts;
}

// ========== SendInput Mouse ==========

static void sendMouseMove(int dx, int dy) {
    INPUT input = {};
    input.type = INPUT_MOUSE;
    input.mi.dx = dx;
    input.mi.dy = dy;
    input.mi.dwFlags = MOUSEEVENTF_MOVE;
    SendInput(1, &input, sizeof(INPUT));
}

static void sendMouseGoto(int x, int y) {
    int screenW = GetSystemMetrics(SM_CXSCREEN);
    int screenH = GetSystemMetrics(SM_CYSCREEN);
    INPUT input = {};
    input.type = INPUT_MOUSE;
    input.mi.dx = (x * 65535) / screenW;
    input.mi.dy = (y * 65535) / screenH;
    input.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
    SendInput(1, &input, sizeof(INPUT));
}

static void sendMouseClick(int isRight) {
    INPUT input = {};
    input.type = INPUT_MOUSE;
    input.mi.dwFlags = isRight ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
    SendInput(1, &input, sizeof(INPUT));
    Sleep(10);
    input.mi.dwFlags = isRight ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
    SendInput(1, &input, sizeof(INPUT));
}

static void sendMouseDoubleClick() {
    for (int i = 0; i < 2; i++) {
        INPUT inputs[2] = {};
        inputs[0].type = INPUT_MOUSE;
        inputs[0].mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
        inputs[1].type = INPUT_MOUSE;
        inputs[1].mi.dwFlags = MOUSEEVENTF_LEFTUP;
        SendInput(2, inputs, sizeof(INPUT));
        Sleep(30);
    }
}

static void sendMouseDown() {
    INPUT input = {};
    input.type = INPUT_MOUSE;
    input.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
    SendInput(1, &input, sizeof(INPUT));
}

static void sendMouseUp() {
    INPUT input = {};
    input.type = INPUT_MOUSE;
    input.mi.dwFlags = MOUSEEVENTF_LEFTUP;
    SendInput(1, &input, sizeof(INPUT));
}

static void sendScroll(int dy) {
    INPUT input = {};
    input.type = INPUT_MOUSE;
    input.mi.mouseData = dy;
    input.mi.dwFlags = MOUSEEVENTF_WHEEL;
    SendInput(1, &input, sizeof(INPUT));
}

static void sendHScroll(int dx) {
    INPUT input = {};
    input.type = INPUT_MOUSE;
    input.mi.mouseData = dx;
    input.mi.dwFlags = MOUSEEVENTF_HWHEEL;
    SendInput(1, &input, sizeof(INPUT));
}

// ========== SendInput Keyboard ==========

struct KeyEntry {
    const char* name;
    WORD vk;
};

static const KeyEntry KEY_MAP[] = {
    {"enter",    VK_RETURN},
    {"return",   VK_RETURN},
    {"tab",      VK_TAB},
    {"escape",   VK_ESCAPE},
    {"esc",      VK_ESCAPE},
    {"backspace",VK_BACK},
    {"bksp",     VK_BACK},
    {"delete",   VK_DELETE},
    {"del",      VK_DELETE},
    {"up",       VK_UP},
    {"down",     VK_DOWN},
    {"left",     VK_LEFT},
    {"right",    VK_RIGHT},
    {"home",     VK_HOME},
    {"end",      VK_END},
    {"pageup",   VK_PRIOR},
    {"pagedown", VK_NEXT},
    {"space",    VK_SPACE},
    {"shift",    VK_SHIFT},
    {"ctrl",     VK_CONTROL},
    {"control",  VK_CONTROL},
    {"alt",      VK_MENU},
    {"insert",   VK_INSERT},
    {"f1",       VK_F1},
    {"f2",       VK_F2},
    {"f3",       VK_F3},
    {"f4",       VK_F4},
    {"f5",       VK_F5},
    {"f6",       VK_F6},
    {"f7",       VK_F7},
    {"f8",       VK_F8},
    {"f9",       VK_F9},
    {"f10",      VK_F10},
    {"f11",      VK_F11},
    {"f12",      VK_F12},
    {"win",      VK_LWIN},
    {"apps",     VK_APPS},
    {"printscr", VK_SNAPSHOT},
    {"scroll",   VK_SCROLL},
    {"capslock", VK_CAPITAL},
};

static WORD findVK(const std::string& name) {
    std::string lower = name;
    std::transform(lower.begin(), lower.end(), lower.begin(), ::tolower);
    for (const auto& entry : KEY_MAP) {
        if (lower == entry.name) return entry.vk;
    }
    return 0;
}

static void sendKeyPress(WORD vk) {
    INPUT inputs[2] = {};
    inputs[0].type = INPUT_KEYBOARD;
    inputs[0].ki.wVk = vk;
    inputs[1].type = INPUT_KEYBOARD;
    inputs[1].ki.wVk = vk;
    inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
    SendInput(2, inputs, sizeof(INPUT));
}

static void sendUnicodeText(const std::string& text) {
    int len = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), -1, NULL, 0);
    if (len <= 0) return;
    std::vector<wchar_t> wtext(len);
    MultiByteToWideChar(CP_UTF8, 0, text.c_str(), -1, wtext.data(), len);

    const size_t BATCH = 16;
    for (size_t i = 0; i < wtext.size() && wtext[i] != 0; ) {
        size_t count = min(BATCH, (size_t)(wtext.size() - i));
        std::vector<INPUT> inputs(count * 2);
        for (size_t j = 0; j < count; j++) {
            if (wtext[i + j] == 0) break;
            inputs[j * 2].type = INPUT_KEYBOARD;
            inputs[j * 2].ki.wScan = wtext[i + j];
            inputs[j * 2].ki.dwFlags = KEYEVENTF_UNICODE;
            inputs[j * 2 + 1].type = INPUT_KEYBOARD;
            inputs[j * 2 + 1].ki.wScan = wtext[i + j];
            inputs[j * 2 + 1].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        }
        SendInput((DWORD)inputs.size(), inputs.data(), sizeof(INPUT));
        i += count;
        Sleep(10);
    }
}

// ========== Screen Info ==========

struct EnumData {
    std::vector<std::string> lines;
};

static BOOL CALLBACK enumDisplayMonitorsProc(HMONITOR hMon, HDC, LPRECT, LPARAM lParam) {
    EnumData* data = reinterpret_cast<EnumData*>(lParam);
    MONITORINFOEXW mi = {};
    mi.cbSize = sizeof(mi);
    if (GetMonitorInfoW(hMon, &mi)) {
        int w = mi.rcMonitor.right - mi.rcMonitor.left;
        int h = mi.rcMonitor.bottom - mi.rcMonitor.top;
        BOOL primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
        char name[64] = {};
        WideCharToMultiByte(CP_UTF8, 0, mi.szDevice, -1, name, sizeof(name), NULL, NULL);
        char buf[256];
        sprintf_s(buf, "SCR|%d|%d|%d|%d|%d|%s",
            mi.rcMonitor.left, mi.rcMonitor.top, w, h, primary ? 1 : 0, name);
        data->lines.push_back(buf);
    }
    return TRUE;
}

static void dumpScreens() {
    EnumData data;
    EnumDisplayMonitors(NULL, NULL, enumDisplayMonitorsProc, reinterpret_cast<LPARAM>(&data));
    for (const auto& line : data.lines) {
        printf("%s\n", line.c_str());
    }
    printf("SCR_DONE\n");
    fflush(stdout);
}

// ========== Command Processing ==========

static void processCommand(const std::string& line) {
    if (line.empty()) return;
    auto parts = split(line, ' ');
    if (parts.empty()) return;

    const std::string& cmd = parts[0];

    if (cmd == "m" && parts.size() >= 3) {
        int dx = std::stoi(parts[1]);
        int dy = std::stoi(parts[2]);
        sendMouseMove(dx, dy);

    } else if (cmd == "g" && parts.size() >= 3) {
        int x = std::stoi(parts[1]);
        int y = std::stoi(parts[2]);
        sendMouseGoto(x, y);

    } else if (cmd == "c") {
        sendMouseClick(0);

    } else if (cmd == "d") {
        sendMouseDoubleClick();

    } else if (cmd == "r") {
        sendMouseClick(1);

    } else if (cmd == "t") {
        sendMouseDown();

    } else if (cmd == "u") {
        sendMouseUp();

    } else if (cmd == "s" && parts.size() >= 2) {
        int dy = std::stoi(parts[1]);
        sendScroll(dy);

    } else if (cmd == "h" && parts.size() >= 2) {
        int dx = std::stoi(parts[1]);
        sendHScroll(dx);

    } else if (cmd == "k" && parts.size() >= 2) {
        size_t firstSpace = line.find(' ');
        if (firstSpace != std::string::npos) {
            std::string text = line.substr(firstSpace + 1);
            sendUnicodeText(text);
        }

    } else if (cmd == "b") {
        sendKeyPress(VK_BACK);

    } else if (cmd == "e") {
        sendKeyPress(VK_RETURN);

    } else if (cmd == "p" && parts.size() >= 2) {
        WORD vk = findVK(parts[1]);
        if (vk != 0) {
            sendKeyPress(vk);
        } else {
            printf("ERR|unknown key: %s\n", parts[1].c_str());
            fflush(stdout);
        }

    } else if (cmd == "screens") {
        dumpScreens();

    } else {
        printf("ERR|unknown cmd\n");
        fflush(stdout);
    }
}

// ========== Main ==========

int main() {
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);

    printf("READY\n");
    fflush(stdout);

    std::string line;
    while (std::getline(std::cin, line)) {
        std::string t = trim(line);
        if (t.empty()) continue;
        processCommand(t);
    }

    return 0;
}
