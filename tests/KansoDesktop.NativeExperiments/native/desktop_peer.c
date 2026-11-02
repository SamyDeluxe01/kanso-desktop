// Narrow native desktop identity checks. No filesystem mutation or discovery of
// unrelated windows. COM supplies the existing desktop view, never a new view.
#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#include <windows.h>
#include <shlobj.h>
#include <exdisp.h>
#include <servprov.h>
#include "desktop_peer.h"

static int process_image(DWORD pid, WCHAR *name, DWORD capacity) {
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return 0;
    BOOL ok = QueryFullProcessImageNameW(process, 0, name, &capacity);
    CloseHandle(process); return ok;
}
int desktop_window_matches(HWND window) {
    WCHAR name[64], parent_name[64], root_name[64]; DWORD pid = 0, session = 0, ours = 0;
    if (!GetClassNameW(window, name, 64) || lstrcmpW(name, L"SysListView32")) return 0;
    HWND parent = GetParent(window), root = GetParent(parent);
    if (!GetClassNameW(parent, parent_name, 64) || lstrcmpW(parent_name, L"SHELLDLL_DefView")) return 0;
    if (!GetClassNameW(root, root_name, 64) || (lstrcmpW(root_name, L"Progman") && lstrcmpW(root_name, L"WorkerW"))) return 0;
    GetWindowThreadProcessId(window, &pid);
    if (!ProcessIdToSessionId(pid, &session) || !ProcessIdToSessionId(GetCurrentProcessId(), &ours) || session != ours) return 0;
    WCHAR image[MAX_PATH], expected[MAX_PATH];
    UINT length = GetWindowsDirectoryW(expected, MAX_PATH);
    if (!length || length + 14 >= MAX_PATH || !process_image(pid, image, MAX_PATH)) return 0;
    lstrcatW(expected, L"\\explorer.exe");
    return CompareStringOrdinal(image, -1, expected, -1, TRUE) == CSTR_EQUAL;
}
int desktop_owner_matches(DWORD pid) {
    HMODULE module = NULL;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (LPCWSTR)(ULONG_PTR)&desktop_owner_matches, &module)) return 0;
    WCHAR *module_path = (WCHAR *)HeapAlloc(GetProcessHeap(), 0, 32768 * sizeof(WCHAR));
    WCHAR *owner_path = (WCHAR *)HeapAlloc(GetProcessHeap(), 0, 32768 * sizeof(WCHAR));
    int match = 0;
    DWORD module_length = module_path ? GetModuleFileNameW(module, module_path, 32768) : 0;
    if (module_path && owner_path && module_length && module_length < 32768 && process_image(pid, owner_path, 32768)) {
        WCHAR *module_file = wcsrchr(module_path, L'\\'), *owner_file = wcsrchr(owner_path, L'\\');
        if (module_file && owner_file && module_file - module_path == owner_file - owner_path &&
            CompareStringOrdinal(module_path, (int)(module_file - module_path), owner_path, (int)(owner_file - owner_path), TRUE) == CSTR_EQUAL &&
            (!lstrcmpiW(owner_file + 1, L"KansoDesktop.exe") || !lstrcmpiW(owner_file + 1, L"KansoDesktop.NativeExperiments.exe"))) match = 1;
    }
    HeapFree(GetProcessHeap(), 0, module_path); HeapFree(GetProcessHeap(), 0, owner_path); return match;
}
HRESULT desktop_folder(HWND target, IFolderView **folder, HWND *list) {
    *folder = NULL; *list = NULL;
    IShellWindows *windows = NULL; IDispatch *dispatch = NULL; IServiceProvider *services = NULL;
    IShellBrowser *browser = NULL; IShellView *view = NULL;
    const GUID top_browser = {0x4c96be40,0x915c,0x11cf,{0x99,0xd3,0x00,0xaa,0x00,0x4a,0xe8,0x37}};
    HRESULT hr = CoCreateInstance(&CLSID_ShellWindows, NULL, CLSCTX_LOCAL_SERVER, &IID_IShellWindows, (void **)&windows);
    if (SUCCEEDED(hr)) {
        VARIANT location, empty; VariantInit(&location); VariantInit(&empty); V_VT(&location) = VT_I4; V_I4(&location) = 0;
        LONG ignored = 0;
        hr = IShellWindows_FindWindowSW(windows, &location, &empty, SWC_DESKTOP, &ignored, SWFO_NEEDDISPATCH, &dispatch);
    }
    if (SUCCEEDED(hr) && dispatch) hr = IDispatch_QueryInterface(dispatch, &IID_IServiceProvider, (void **)&services);
    else if (SUCCEEDED(hr)) hr = E_NOINTERFACE;
    if (SUCCEEDED(hr)) hr = IServiceProvider_QueryService(services, &top_browser, &IID_IShellBrowser, (void **)&browser);
    if (SUCCEEDED(hr)) hr = IShellBrowser_QueryActiveShellView(browser, &view);
    if (SUCCEEDED(hr)) {
        HWND shell_window = NULL; hr = IShellView_GetWindow(view, &shell_window);
        if (SUCCEEDED(hr)) {
            *list = FindWindowExW(shell_window, NULL, L"SysListView32", L"FolderView");
            if (!*list || (target && target != *list) || !desktop_window_matches(*list)) hr = E_ACCESSDENIED;
        }
    }
    if (SUCCEEDED(hr)) hr = IShellView_QueryInterface(view, &IID_IFolderView, (void **)folder);
    if (view) IShellView_Release(view); if (browser) IShellBrowser_Release(browser);
    if (services) IServiceProvider_Release(services); if (dispatch) IDispatch_Release(dispatch);
    if (windows) IShellWindows_Release(windows);
    return hr;
}
