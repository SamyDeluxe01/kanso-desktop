// Native bridge for cooperating fixtures and explicitly activated desktop sessions.
// Never mutates files, attributes or registry; desktop rules must match enumerated items.
#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#include <windows.h>
#include <commctrl.h>
#include <shlobj.h>
#include <stdint.h>
#include "fixture_bridge.h"
#include "desktop_peer.h"

#define EXPORT __declspec(dllexport)
#define TIMER_ID ((UINT_PTR)0x4B414E54)
#define PENDING_TIMER_ID ((UINT_PTR)0x4B414E55)
#define MAX_PACKET (256u * 1024u)
extern int __cdecl KansoAttachOwnedList(HWND list);
extern int __cdecl KansoDetachOwnedList(void);
extern int __cdecl KansoSetHiddenShellPaths(IFolderView *, const WCHAR **, int);

static HWND fixture_window;
static HANDLE owner_process;
static DWORD owner_pid;
static ULONG_PTR fixture_token;
static ULONGLONG heartbeat;
static IFolderView *connected_folder;
static int desktop_observation;
static int desktop_artificial, pending_artificial;
static WCHAR *queued_rules;
static uint32_t queued_count;
static int update_result;
#define UPDATE_TIMER_ID ((UINT_PTR)0x4B414E56)

// Exact session-specific names; this pure predicate is tested without Explorer.
EXPORT int __cdecl KansoArtificialPathAllowed(const WCHAR *root, ULONG_PTR token, const WCHAR *path) {
    if (!root || !path || !token) return 0;
    WCHAR expected[32768], suffix[128];
    int length = lstrlenW(root);
    if (!length || length > 32500) return 0;
    const WCHAR *names[] = {L"Alpha", L"Beta", L"Gamma"};
    for (int i = 0; i < 3; i++) {
        wsprintfW(suffix, L"\\Kanso-Test-%08X%08X-%s.txt", (DWORD)((uint64_t)token >> 32), (DWORD)token, names[i]);
        lstrcpyW(expected, root); lstrcatW(expected, suffix);
        if (CompareStringOrdinal(expected, -1, path, -1, TRUE) == CSTR_EQUAL) return 1;
    }
    return 0;
}
static int artificial_paths(const WCHAR **paths, uint32_t count) {
    if (count > 3) return 0;
    PWSTR root = NULL;
    if (desktop_artificial == 2) {
        IPersistFolder2 *persistent = NULL; PIDLIST_ABSOLUTE id = NULL;
        if (!connected_folder || FAILED(IFolderView_GetFolder(connected_folder, &IID_IPersistFolder2, (void **)&persistent))) return 0;
        HRESULT hr = IPersistFolder2_GetCurFolder(persistent, &id); IPersistFolder2_Release(persistent);
        if (SUCCEEDED(hr)) hr = SHGetNameFromIDList(id, SIGDN_FILESYSPATH, &root);
        CoTaskMemFree(id); if (FAILED(hr)) return 0;
    } else if (FAILED(SHGetKnownFolderPath(&FOLDERID_Desktop, KF_FLAG_DONT_VERIFY, NULL, &root))) return 0;
    int ok = 1;
    for (uint32_t i = 0; i < count; i++) if (!KansoArtificialPathAllowed(root, fixture_token, paths[i])) ok = 0;
    CoTaskMemFree(root); return ok;
}
static HWND pending_window;
static HANDLE pending_owner;
static DWORD pending_pid;
static ULONG_PTR pending_token;
static LONG observation_diagnostic[8];
static UINT bridge_message(void) { return RegisterWindowMessageW(L"KansoDesktop.ArtificialFixtureBridge.v1"); }
static void diagnostic_name(WCHAR *name, DWORD owner, ULONG_PTR token) {
    wsprintfW(name, L"Local\\KansoDesktop.Observation.%lu.%08lx%08lx", owner, (DWORD)((uint64_t)token >> 32), (DWORD)token);
}
static void diagnostic_write(DWORD owner, ULONG_PTR token, int index, LONG value) {
    WCHAR name[128]; diagnostic_name(name, owner, token);
    HANDLE mapping = OpenFileMappingW(FILE_MAP_WRITE, FALSE, name);
    if (!mapping) return;
    volatile LONG *view = (volatile LONG *)MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, 8 * sizeof(LONG));
    if (view) { InterlockedExchange(&view[index], value); UnmapViewOfFile((const void *)view); }
    CloseHandle(mapping);
}

// Resolve ONLY the already enumerated fixture items. Reject an outside path
// before the generic projector could ask the Shell to parse that path.
static int fixture_contains_paths(IFolderView *folder, const WCHAR **paths, uint32_t count) {
    IPersistFolder2 *persistent = NULL;
    PIDLIST_ABSOLUTE root = NULL;
    int items = 0, accepted = 1;
    if (FAILED(IFolderView_GetFolder(folder, &IID_IPersistFolder2, (void **)&persistent))) return 0;
    HRESULT hr = IPersistFolder2_GetCurFolder(persistent, &root);
    IPersistFolder2_Release(persistent);
    if (FAILED(hr) || FAILED(IFolderView_ItemCount(folder, SVGIO_ALLVIEW, &items)) || items < 0 || items > 4096) { CoTaskMemFree(root); return 0; }
    // Enumerate COM identities once per update, not once per requested path.
    PWSTR *names = (PWSTR *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, (SIZE_T)(items + 1) * sizeof(PWSTR));
    if (!names) { CoTaskMemFree(root); return 0; }
    for (int i = 0; i < items; i++) {
        PITEMID_CHILD child = NULL;
        if (FAILED(IFolderView_Item(folder, i, &child))) continue;
        PIDLIST_ABSOLUTE full = ILCombine(root, child); CoTaskMemFree(child);
        if (full) SHGetNameFromIDList(full, SIGDN_FILESYSPATH, &names[i]);
        CoTaskMemFree(full);
    }
    for (uint32_t p = 0; p < count && accepted; p++) {
        int matched = 0;
        for (int i = 0; i < items && !matched; i++)
            if (names[i]) matched = CompareStringOrdinal(names[i], -1, paths[p], -1, TRUE) == CSTR_EQUAL;
        if (!matched) accepted = 0;
    }
    for (int i = 0; i < items; i++) CoTaskMemFree(names[i]);
    HeapFree(GetProcessHeap(), 0, names);
    CoTaskMemFree(root); return accepted;
}

static void stop_fixture(void) {
    HWND window = fixture_window;
    fixture_window = NULL;
    if (window) { KillTimer(window, TIMER_ID); KillTimer(window, UPDATE_TIMER_ID); }
    HeapFree(GetProcessHeap(), 0, queued_rules); queued_rules = NULL; queued_count = 0; update_result = -1;
    if (owner_process) CloseHandle(owner_process);
    owner_process = NULL; owner_pid = 0; fixture_token = 0;
    if (connected_folder) IFolderView_Release(connected_folder);
    connected_folder = NULL; desktop_observation = 0; desktop_artificial = 0;
    KansoDetachOwnedList();
}

static void CALLBACK check_owner(HWND window, UINT message, UINT_PTR timer, DWORD tick) {
    (void)message; (void)timer; (void)tick;
    if (window != fixture_window) return;
    if (!owner_process || WaitForSingleObject(owner_process, 0) != WAIT_TIMEOUT || GetTickCount64() - heartbeat > 2000)
        stop_fixture();
}

// COM rejects outbound calls made inside an inter-process SendMessage
// (RPC_E_CANTCALLOUT_ININPUTSYNCCALL). Finish desktop negotiation later in
// the normal UI message loop, outside the synchronous hook callback.
static void CALLBACK finish_observation_attach(HWND window, UINT message, UINT_PTR timer, DWORD tick) {
    (void)message; (void)timer; (void)tick;
    if (window != pending_window) return;
    KillTimer(window, PENDING_TIMER_ID);
    HANDLE owner = pending_owner; DWORD pid = pending_pid; ULONG_PTR token = pending_token;
    int artificial = pending_artificial; pending_artificial = 0;
    pending_window = NULL; pending_owner = NULL; pending_pid = 0; pending_token = 0;
    diagnostic_write(pid, token, 0, 2);
    if (!owner || WaitForSingleObject(owner, 0) != WAIT_TIMEOUT || !desktop_window_matches(window) || !desktop_owner_matches(pid)) {
        if (owner) CloseHandle(owner); return;
    }
    IFolderView *folder = NULL; HWND actual = NULL;
    HRESULT hr = desktop_folder(window, &folder, &actual);
    diagnostic_write(pid, token, 2, hr);
    if (SUCCEEDED(hr) && folder && !fixture_window && WaitForSingleObject(owner, 0) == WAIT_TIMEOUT) {
        int attached = KansoAttachOwnedList(window);
        diagnostic_write(pid, token, 3, attached);
        if (attached == 0) {
            fixture_window = window; owner_process = owner; owner_pid = pid;
            connected_folder = folder; fixture_token = token; desktop_observation = !artificial; desktop_artificial = artificial;
            heartbeat = GetTickCount64(); diagnostic_write(pid, token, 4, 1);
            if (!SetTimer(window, TIMER_ID, 100, check_owner)) stop_fixture();
            return;
        }
    }
    if (folder) IFolderView_Release(folder);
    CloseHandle(owner);
}

EXPORT LRESULT CALLBACK KansoArtificialFixtureHook(int code, WPARAM wp, LPARAM lp) {
    if (code >= 0 && lp) {
        CWPSTRUCT *call = (CWPSTRUCT *)lp;
        if (call->message == bridge_message() && (LOWORD(call->wParam) == FIXTURE_ATTACH || LOWORD(call->wParam) == DESKTOP_OBSERVE_ATTACH || (LOWORD(call->wParam) == DESKTOP_ARTIFICIAL_ATTACH || LOWORD(call->wParam) == DESKTOP_REFERENCES_ATTACH)) && !fixture_window) {
            DWORD requested_owner = (DWORD)(call->wParam >> 16);
            int observe = LOWORD(call->wParam) == DESKTOP_OBSERVE_ATTACH || LOWORD(call->wParam) == DESKTOP_ARTIFICIAL_ATTACH || LOWORD(call->wParam) == DESKTOP_REFERENCES_ATTACH;
            if (observe) diagnostic_write(requested_owner, (ULONG_PTR)call->lParam, 0, 1);
            // These properties are published only by our fixture executable.
            // There is deliberately NO fallback for the real Explorer desktop.
            int fixture_identity = !observe && GetPropW(call->hwnd, FIXTURE_TOKEN) == (HANDLE)call->lParam && call->lParam && (DWORD)(ULONG_PTR)GetPropW(call->hwnd, FIXTURE_OWNER) == requested_owner;
            int desktop_identity = observe && call->lParam && desktop_window_matches(call->hwnd) && desktop_owner_matches(requested_owner);
            if (observe) diagnostic_write(requested_owner, (ULONG_PTR)call->lParam, 1, desktop_identity);
            if (desktop_identity && !pending_window && requested_owner != GetCurrentProcessId()) {
                HANDLE owner = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, requested_owner);
                HMODULE retained = NULL;
                if (owner && WaitForSingleObject(owner, 0) == WAIT_TIMEOUT && GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)(ULONG_PTR)&KansoArtificialFixtureHook, &retained)) {
                    pending_window = call->hwnd; pending_owner = owner; pending_pid = requested_owner; pending_token = (ULONG_PTR)call->lParam;
                    pending_artificial = LOWORD(call->wParam) == DESKTOP_REFERENCES_ATTACH ? 3 : LOWORD(call->wParam) == DESKTOP_ARTIFICIAL_ATTACH;
                    if (!SetTimer(pending_window, PENDING_TIMER_ID, 10, finish_observation_attach)) {
                        pending_window = NULL; pending_owner = NULL; pending_pid = 0; pending_token = 0; CloseHandle(owner);
                    }
                } else if (owner) CloseHandle(owner);
                return CallNextHookEx(NULL, code, wp, lp);
            }
            if (fixture_identity && requested_owner != GetCurrentProcessId()) {
                IFolderView *folder = NULL;
                if (fixture_identity) {
                    folder = (IFolderView *)GetPropW(call->hwnd, FIXTURE_FOLDER);
                    if (folder) IFolderView_AddRef(folder);
                } else {
                    HWND actual = NULL;
                    HRESULT folder_hr = desktop_folder(call->hwnd, &folder, &actual);
                    diagnostic_write(requested_owner, (ULONG_PTR)call->lParam, 2, folder_hr);
                    if (FAILED(folder_hr)) folder = NULL;
                }
                HANDLE owner = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, requested_owner);
                DWORD ours = 0, theirs = 0;
                int same_session = ProcessIdToSessionId(GetCurrentProcessId(), &ours) && ProcessIdToSessionId(requested_owner, &theirs) && ours == theirs;
                if (observe) diagnostic_write(requested_owner, (ULONG_PTR)call->lParam, 3, folder && owner && same_session);
                // Hold this development DLL until the fixture process exits.
                // Thus abrupt owner death cannot unload active callback code.
                // This lifecycle is NOT approved for a long-lived Explorer host.
                HMODULE retained = NULL;
                if (folder && owner && same_session && WaitForSingleObject(owner, 0) == WAIT_TIMEOUT &&
                    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                        (LPCWSTR)(ULONG_PTR)&KansoArtificialFixtureHook, &retained) && KansoAttachOwnedList(call->hwnd) == 0) {
                    fixture_window = call->hwnd; owner_process = owner; owner_pid = requested_owner;
                    connected_folder = folder; folder = NULL; desktop_observation = observe;
                    desktop_artificial = GetPropW(call->hwnd, L"Kanso.NativeFixture.References.v1") ? 3 : GetPropW(call->hwnd, L"Kanso.NativeFixture.Artificial.v1") ? 2 : 0;
                    fixture_token = (ULONG_PTR)call->lParam; heartbeat = GetTickCount64();
                    if (observe) diagnostic_write(requested_owner, (ULONG_PTR)call->lParam, 4, 1);
                    if (!SetTimer(fixture_window, TIMER_ID, 100, check_owner)) stop_fixture();
                } else if (owner) CloseHandle(owner);
                if (folder) IFolderView_Release(folder);
            }
        }
    }
    return CallNextHookEx(NULL, code, wp, lp);
}

EXPORT int __cdecl KansoConnectArtificialFixture(HWND target, DWORD expected_pid, ULONG_PTR token) {
    DWORD actual_pid = 0;
    DWORD thread = GetWindowThreadProcessId(target, &actual_pid);
    if (!thread || actual_pid != expected_pid || actual_pid == GetCurrentProcessId() || !token ||
        GetPropW(target, FIXTURE_TOKEN) != (HANDLE)token ||
        (DWORD)(ULONG_PTR)GetPropW(target, FIXTURE_OWNER) != GetCurrentProcessId()) return -1;
    WCHAR name[64];
    if (!GetClassNameW(target, name, 64) || lstrcmpW(name, L"SysListView32")) return -2;
    HMODULE module = NULL;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (LPCWSTR)(ULONG_PTR)&KansoArtificialFixtureHook, &module)) return -3;
    HHOOK hook = SetWindowsHookExW(WH_CALLWNDPROC, KansoArtificialFixtureHook, module, thread);
    if (!hook) return -4;
    DWORD_PTR result = 0;
    LRESULT sent = SendMessageTimeoutW(target, bridge_message(), ((WPARAM)GetCurrentProcessId() << 16) | FIXTURE_ATTACH,
        (LPARAM)token, SMTO_ABORTIFHUNG | SMTO_BLOCK, 1500, &result);
    UnhookWindowsHookEx(hook);
    return sent && result == FIXTURE_MAGIC ? 0 : -5;
}

EXPORT int __cdecl KansoArtificialFixtureActive(void) { return fixture_window != NULL; }
EXPORT HRESULT __cdecl KansoFindExistingNativeDesktop(HWND *window) {
    if (!window) return E_POINTER;
    HRESULT initialized = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized) && initialized != RPC_E_CHANGED_MODE) return initialized;
    IFolderView *folder = NULL; HRESULT hr = desktop_folder(NULL, &folder, window);
    if (folder) IFolderView_Release(folder);
    if (SUCCEEDED(initialized)) CoUninitialize();
    return hr;
}
// Observation-only first connection: the receiver rejects every non-empty rule.
static int connect_desktop(HWND target, ULONG_PTR token, int command) {
    if (!token || !desktop_window_matches(target) || !desktop_owner_matches(GetCurrentProcessId())) return -1;
    DWORD thread = GetWindowThreadProcessId(target, NULL); HMODULE module = NULL;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)(ULONG_PTR)&KansoArtificialFixtureHook, &module)) return -3;
    HHOOK hook = SetWindowsHookExW(WH_CALLWNDPROC, KansoArtificialFixtureHook, module, thread);
    if (!hook) return -4;
    WCHAR name[128]; diagnostic_name(name, GetCurrentProcessId(), token);
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(observation_diagnostic), name);
    if (!mapping) { UnhookWindowsHookEx(hook); return -6; }
    LONG *diagnostic = (LONG *)MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(observation_diagnostic));
    if (!diagnostic) { CloseHandle(mapping); UnhookWindowsHookEx(hook); return -7; }
    ZeroMemory(diagnostic, sizeof(observation_diagnostic));
    DWORD_PTR result = 0;
    SetLastError(0);
    LRESULT sent = SendMessageTimeoutW(target, bridge_message(), ((WPARAM)GetCurrentProcessId() << 16) | command, (LPARAM)token, SMTO_ABORTIFHUNG | SMTO_BLOCK, 1500, &result);
    DWORD error = GetLastError();
    UnhookWindowsHookEx(hook);
    if (sent && result != FIXTURE_MAGIC) {
        ULONGLONG deadline = GetTickCount64() + 2000;
        while (GetTickCount64() < deadline) {
            Sleep(20);
            DWORD_PTR ready = 0;
            if (SendMessageTimeoutW(target, bridge_message(), ((WPARAM)GetCurrentProcessId() << 16) | FIXTURE_STATUS, (LPARAM)token, SMTO_ABORTIFHUNG | SMTO_BLOCK, 200, &ready) && ready == FIXTURE_MAGIC) { result = ready; break; }
        }
    }
    diagnostic[5] = (LONG)sent; diagnostic[6] = (LONG)result; diagnostic[7] = (LONG)error;
    CopyMemory(observation_diagnostic, diagnostic, sizeof(observation_diagnostic));
    UnmapViewOfFile(diagnostic); CloseHandle(mapping);
    return sent && result == FIXTURE_MAGIC ? 0 : -5;
}
EXPORT int __cdecl KansoObserveExistingNativeDesktop(HWND target, ULONG_PTR token) { return connect_desktop(target, token, DESKTOP_OBSERVE_ATTACH); }
EXPORT int __cdecl KansoConnectArtificialDesktop(HWND target, ULONG_PTR token) { return connect_desktop(target, token, DESKTOP_ARTIFICIAL_ATTACH); }
EXPORT int __cdecl KansoConnectDesktopReferences(HWND target, ULONG_PTR token) { return connect_desktop(target, token, DESKTOP_REFERENCES_ATTACH); }
EXPORT int __cdecl KansoObservationDiagnostic(LONG *values, int length) {
    if (!values || length != 8) return -1;
    CopyMemory(values, observation_diagnostic, sizeof(observation_diagnostic)); return 0;
}

void fixture_closed(HWND window) {
    if (window == fixture_window) {
        KillTimer(window, TIMER_ID);
        KillTimer(window, UPDATE_TIMER_ID);
        HeapFree(GetProcessHeap(), 0, queued_rules); queued_rules = NULL; queued_count = 0; update_result = -1;
        fixture_window = NULL;
        if (owner_process) CloseHandle(owner_process);
        owner_process = NULL; owner_pid = 0; fixture_token = 0;
        if (connected_folder) IFolderView_Release(connected_folder);
        connected_folder = NULL; desktop_observation = 0; desktop_artificial = 0;
    }
}

static void CALLBACK apply_queued_rules(HWND window, UINT message, UINT_PTR timer, DWORD tick) {
    (void)message; (void)timer; (void)tick;
    KillTimer(window, UPDATE_TIMER_ID);
    if (window != fixture_window || !queued_rules) return;
    WCHAR *buffer = queued_rules; uint32_t count = queued_count;
    queued_rules = NULL; queued_count = 0;
    const WCHAR *paths[4096]; const WCHAR *cursor = buffer;
    for (uint32_t i = 0; i < count; i++) { paths[i] = cursor; cursor += lstrlenW(cursor) + 1; }
    update_result = -1;
    if (owner_process && WaitForSingleObject(owner_process, 0) == WAIT_TIMEOUT && connected_folder &&
        (desktop_artificial == 3 || artificial_paths(paths, count)) && fixture_contains_paths(connected_folder, paths, count) &&
        KansoSetHiddenShellPaths(connected_folder, paths, (int)count) == 0) update_result = 1;
    HeapFree(GetProcessHeap(), 0, buffer);
}

int fixture_message(HWND window, UINT message, WPARAM wp, LPARAM lp, LRESULT *result) {
    if (window != fixture_window) return 0;
    if (message == bridge_message()) {
        if ((ULONG_PTR)lp != fixture_token || (DWORD)(wp >> 16) != owner_pid) { *result = 0; return 1; }
        switch (LOWORD(wp)) {
        case FIXTURE_UPDATE_RESULT: *result = update_result == 1 ? FIXTURE_MAGIC : update_result == -1 ? 1 : 0; return 1;
        case FIXTURE_ATTACH: case DESKTOP_OBSERVE_ATTACH: case DESKTOP_ARTIFICIAL_ATTACH: case DESKTOP_REFERENCES_ATTACH: *result = FIXTURE_MAGIC; return 1;
        case FIXTURE_PING: heartbeat = GetTickCount64(); *result = FIXTURE_MAGIC; return 1;
        case FIXTURE_STATUS: *result = FIXTURE_MAGIC; return 1;
        case FIXTURE_DETACH: stop_fixture(); *result = FIXTURE_MAGIC; return 1;
        default: *result = 0; return 1;
        }
    }
    if (message == WM_COPYDATA) {
        *result = 0;
        if (!lp) return 1;
        COPYDATASTRUCT *packet = (COPYDATASTRUCT *)lp;
        if (packet->dwData != fixture_token || !packet->lpData || packet->cbData < 16 || packet->cbData > MAX_PACKET || (packet->cbData & 1)) return 1;
        const uint32_t *header = (const uint32_t *)packet->lpData;
        if (header[0] != FIXTURE_MAGIC || header[1] != 1 || header[2] != owner_pid || header[3] > 4096) return 1;
        if (desktop_observation && header[3] != 0) return 1;
        const WCHAR *paths[4096];
        const WCHAR *cursor = (const WCHAR *)(header + 4);
        const WCHAR *end = (const WCHAR *)((const unsigned char *)packet->lpData + packet->cbData);
        for (uint32_t i = 0; i < header[3]; i++) {
            if (cursor >= end || !*cursor) return 1;
            paths[i] = cursor;
            while (cursor < end && *cursor) cursor++;
            if (cursor >= end) return 1;
            cursor++;
        }
        if (cursor != end) return 1;
        if (desktop_artificial) {
            if ((desktop_artificial != 3 && header[3] > 3) || queued_rules) return 1;
            SIZE_T bytes = packet->cbData - 16;
            queued_rules = (WCHAR *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, bytes + sizeof(WCHAR));
            if (!queued_rules) return 1;
            CopyMemory(queued_rules, header + 4, bytes); queued_count = header[3]; update_result = 0;
            if (!SetTimer(window, UPDATE_TIMER_ID, 10, apply_queued_rules)) {
                HeapFree(GetProcessHeap(), 0, queued_rules); queued_rules = NULL; update_result = -1; return 1;
            }
            heartbeat = GetTickCount64(); *result = FIXTURE_MAGIC; return 1;
        }
        IFolderView *folder = connected_folder;
        if (!folder || !fixture_contains_paths(folder, paths, header[3]) || KansoSetHiddenShellPaths(folder, paths, (int)header[3]) != 0) return 1;
        heartbeat = GetTickCount64(); *result = FIXTURE_MAGIC; return 1;
    }
    return 0;
}


