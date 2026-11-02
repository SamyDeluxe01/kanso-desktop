// Kanso development prototype. No disk writes or Explorer lookup.
// Only attaches to a SysListView32 owned by this process AND the calling thread.
#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#include <windows.h>
#include <commctrl.h>
#include <shlobj.h>
#include <oleacc.h>
#include <stdint.h>
#include "fixture_bridge.h"

#define EXPORT __declspec(dllexport)
#define LIMIT 4096
#define SUBCLASS_ID ((UINT_PTR)0x4B414E53)
typedef struct Projection {
    HWND list, parent;
    int count, applying;
    IFolderView *folder;
    PIDLIST_ABSOLUTE root;
    PWSTR excluded_names[LIMIT];
    int excluded_count;
    unsigned char hidden[LIMIT];
    uint32_t draw_skips, selection_vetoes, hit_skips, resets;
    WCHAR type_prefix[128];
    int type_length;
    ULONGLONG type_tick;
    IAccPropServices *accessibility;
    unsigned char annotated[LIMIT];
    int syncing_accessibility;
} Projection;
static Projection state;
static LRESULT CALLBACK list_proc(HWND, UINT, WPARAM, LPARAM, UINT_PTR, DWORD_PTR);
static LRESULT CALLBACK parent_proc(HWND, UINT, WPARAM, LPARAM, UINT_PTR, DWORD_PTR);

static int valid(void) { return state.list && IsWindow(state.list); }
static int hidden(int index);
static void clear_accessibility(void) {
    if (!state.accessibility) return;
    for (int i = 0; i < LIMIT; i++) if (state.annotated[i]) {
        IAccPropServices_ClearHwndProps(state.accessibility, state.list, OBJID_CLIENT, (DWORD)i + 1, &PROPID_ACC_STATE, 1);
        state.annotated[i] = 0;
        NotifyWinEvent(EVENT_OBJECT_SHOW, state.list, OBJID_CLIENT, i + 1);
    }
}
static HRESULT sync_accessibility(void) {
    if (state.syncing_accessibility) return S_OK;
    state.syncing_accessibility = 1;
    HRESULT hr = S_OK;
    if (!state.accessibility) hr = CoCreateInstance(&CLSID_AccPropServices, NULL, CLSCTX_INPROC_SERVER, &IID_IAccPropServices, (void **)&state.accessibility);
    if (SUCCEEDED(hr)) {
        int count = ListView_GetItemCount(state.list);
        if (count > LIMIT) hr = E_INVALIDARG;
        else for (int i = 0; i < LIMIT; i++) {
            int suppress = i < count && hidden(i);
            if (suppress == state.annotated[i]) continue;
            if (suppress) {
                VARIANT value; VariantInit(&value); V_VT(&value) = VT_I4; V_I4(&value) = STATE_SYSTEM_INVISIBLE | STATE_SYSTEM_UNAVAILABLE;
                hr = IAccPropServices_SetHwndProp(state.accessibility, state.list, OBJID_CLIENT, (DWORD)i + 1, PROPID_ACC_STATE, value);
            } else hr = IAccPropServices_ClearHwndProps(state.accessibility, state.list, OBJID_CLIENT, (DWORD)i + 1, &PROPID_ACC_STATE, 1);
            if (FAILED(hr)) break;
            state.annotated[i] = (unsigned char)suppress;
            NotifyWinEvent(suppress ? EVENT_OBJECT_HIDE : EVENT_OBJECT_SHOW, state.list, OBJID_CLIENT, i + 1);
        }
    }
    state.syncing_accessibility = 0;
    return hr;
}
static void release_accessibility(void) {
    clear_accessibility();
    if (state.accessibility) { IAccPropServices_Release(state.accessibility); state.accessibility = NULL; }
}
static void release_paths(void) {
    for (int i = 0; i < state.excluded_count; i++) { CoTaskMemFree(state.excluded_names[i]); state.excluded_names[i] = NULL; }
    state.excluded_count = 0;
    if (state.root) { CoTaskMemFree(state.root); state.root = NULL; }
    if (state.folder) { IFolderView_Release(state.folder); state.folder = NULL; }
}
static int hidden(int index) {
    if (index < 0) return 0;
    if (state.folder) {
        if (state.excluded_count == 0) return 0;
        PITEMID_CHILD child = NULL;
        if (FAILED(IFolderView_Item(state.folder, index, &child)) || !child) return 0;
        PIDLIST_ABSOLUTE full = ILCombine(state.root, child);
        CoTaskMemFree(child);
        int match = 0;
        if (full) {
            PWSTR name = NULL;
            SHGetNameFromIDList(full, SIGDN_FILESYSPATH, &name);
            for (int i = 0; i < state.excluded_count; i++)
                if (name && state.excluded_names[i] && CompareStringOrdinal(name, -1, state.excluded_names[i], -1, TRUE) == CSTR_EQUAL) { match = 1; break; }
            CoTaskMemFree(name);
            CoTaskMemFree(full);
        }
        return match;
    }
    return index < state.count && state.hidden[index];
}
static void reveal(void) {
    clear_accessibility();
    release_paths();
    ZeroMemory(state.hidden, sizeof(state.hidden));
    state.count = 0;
    state.resets++;
    if (valid()) InvalidateRect(state.list, NULL, TRUE);
}
static void repair_selection(void) {
    if (state.applying || !valid()) return;
    state.applying = 1;
    int focused = ListView_GetNextItem(state.list, -1, LVNI_FOCUSED);
    int i = ListView_GetNextItem(state.list, -1, LVNI_SELECTED);
    while (i >= 0) {
        int next = ListView_GetNextItem(state.list, i, LVNI_SELECTED);
        if (hidden(i)) {
            LVITEMW clear = {0}; clear.stateMask = LVIS_SELECTED | LVIS_FOCUSED;
            if (state.folder) IFolderView_SelectItem(state.folder, i, SVSI_DESELECT);
            SendMessageW(state.list, LVM_SETITEMSTATE, (WPARAM)i, (LPARAM)&clear);
            state.selection_vetoes++;
        }
        i = next;
    }
    if (focused >= 0 && hidden(focused)) {
        LVITEMW clear = {0}; clear.stateMask = LVIS_FOCUSED;
        SendMessageW(state.list, LVM_SETITEMSTATE, (WPARAM)focused, (LPARAM)&clear);
    }
    state.applying = 0;
}
static int index_at(LPARAM point) {
    LVHITTESTINFO hit = {0};
    hit.pt.x = (short)LOWORD(point);
    hit.pt.y = (short)HIWORD(point);
    // DefSubclassProc avoids our own public LVM_HITTEST filter.
    return (int)DefSubclassProc(state.list, LVM_HITTEST, 0, (LPARAM)&hit);
}
static int has_rule(void) {
    if (state.folder) return state.excluded_count > 0;
    for (int i = 0; i < state.count; i++) if (state.hidden[i]) return 1;
    return 0;
}
static void set_item_state(int index, UINT bits, UINT mask) {
    if (state.folder) {
        UINT flags = bits & LVIS_SELECTED ? SVSI_SELECT : SVSI_DESELECT;
        if (bits & LVIS_FOCUSED) flags |= SVSI_FOCUSED;
        IFolderView_SelectItem(state.folder, index, flags);
    } else {
        LVITEMW item = {0}; item.state = bits; item.stateMask = mask;
        SendMessageW(state.list, LVM_SETITEMSTATE, (WPARAM)index, (LPARAM)&item);
    }
}
static void navigate_to(int destination, int shift, int control) {
    int focused = ListView_GetNextItem(state.list, -1, LVNI_FOCUSED);
    int anchor = ListView_GetSelectionMark(state.list);
    int count = ListView_GetItemCount(state.list);
    if (anchor < 0 || hidden(anchor)) anchor = focused >= 0 ? focused : destination;
    if (!control) {
        for (int i = 0; i < count; i++)
            if (ListView_GetItemState(state.list, i, LVIS_SELECTED)) set_item_state(i, 0, LVIS_SELECTED);
    }
    if (shift) {
        int first = anchor < destination ? anchor : destination;
        int last = anchor > destination ? anchor : destination;
        for (int i = first; i <= last; i++) if (!hidden(i)) set_item_state(i, LVIS_SELECTED, LVIS_SELECTED);
    }
    if (control && !shift) {
        if (state.folder) IFolderView_SelectItem(state.folder, destination, SVSI_FOCUSED);
        else set_item_state(destination, LVIS_FOCUSED, LVIS_FOCUSED);
    } else set_item_state(destination, LVIS_SELECTED | LVIS_FOCUSED, LVIS_SELECTED | LVIS_FOCUSED);
    ListView_SetSelectionMark(state.list, shift ? anchor : destination);
    ListView_EnsureVisible(state.list, destination, FALSE);
}
static int navigate_key(UINT key) {
    state.type_length = 0;
    int count = ListView_GetItemCount(state.list);
    int current = ListView_GetNextItem(state.list, -1, LVNI_FOCUSED);
    int next = -1;
    UINT direction = key == VK_LEFT ? LVNI_TOLEFT : key == VK_RIGHT ? LVNI_TORIGHT : key == VK_UP ? LVNI_ABOVE : LVNI_BELOW;
    if (key == VK_HOME || key == VK_END || current < 0) {
        int start = key == VK_END ? count - 1 : 0;
        int step = key == VK_END ? -1 : 1;
        for (int i = start; i >= 0 && i < count; i += step) if (!hidden(i)) { next = i; break; }
    } else {
        int candidate = current;
        for (int attempt = 0; attempt < count; attempt++) {
            candidate = ListView_GetNextItem(state.list, candidate, direction);
            if (candidate < 0) break;
            if (!hidden(candidate)) { next = candidate; break; }
        }
    }
    if (next >= 0) navigate_to(next, (GetKeyState(VK_SHIFT) & 0x8000) != 0, (GetKeyState(VK_CONTROL) & 0x8000) != 0);
    return 1;
}
static int type_visible(WCHAR character) {
    if (character < 32 || (GetKeyState(VK_CONTROL) & 0x8000) || (GetKeyState(VK_MENU) & 0x8000)) return 0;
    ULONGLONG tick = GetTickCount64();
    if (tick - state.type_tick > 1000 || state.type_length >= 127) state.type_length = 0;
    int cycle = state.type_length == 1 && state.type_prefix[0] == character;
    if (!cycle) state.type_prefix[state.type_length++] = character;
    state.type_tick = tick; state.type_prefix[state.type_length] = 0;
    int count = ListView_GetItemCount(state.list), current = ListView_GetNextItem(state.list, -1, LVNI_FOCUSED);
    int start = state.type_length > 1 && !cycle && current >= 0 ? current : current + 1;
    for (int offset = 0; offset < count; offset++) {
        int index = (start + offset) % count;
        if (hidden(index)) continue;
        WCHAR text[512]; text[0] = 0;
        ListView_GetItemText(state.list, index, 0, text, 512);
        if (lstrlenW(text) >= state.type_length && CompareStringOrdinal(text, state.type_length, state.type_prefix, state.type_length, TRUE) == CSTR_EQUAL) {
            navigate_to(index, 0, 0); break;
        }
    }
    return 1;
}
static LRESULT CALLBACK parent_proc(HWND window, UINT message, WPARAM wp, LPARAM lp, UINT_PTR id, DWORD_PTR data) {
    (void)id; (void)data;
    if (message == WM_COMMAND || message == WM_CONTEXTMENU) repair_selection();
    if (message == WM_NOTIFY && lp) {
        NMHDR *header = (NMHDR *)lp;
        if (header->hwndFrom == state.list) {
            if (header->code == NM_RETURN || header->code == LVN_KEYDOWN || header->code == LVN_BEGINDRAG || header->code == LVN_BEGINRDRAG || header->code == LVN_ITEMACTIVATE) repair_selection();
            if (header->code == LVN_ODSTATECHANGED && !state.applying) repair_selection();
            if ((header->code == NM_DBLCLK || header->code == LVN_ITEMACTIVATE) && hidden(((NMITEMACTIVATE *)lp)->iItem)) return 0;
            if (header->code == NM_CUSTOMDRAW) {
                NMLVCUSTOMDRAW *draw = (NMLVCUSTOMDRAW *)lp;
                if (draw->nmcd.dwDrawStage == CDDS_PREPAINT)
                    return DefSubclassProc(window, message, wp, lp) | CDRF_NOTIFYITEMDRAW;
                if ((draw->nmcd.dwDrawStage & CDDS_ITEM) && hidden((int)draw->nmcd.dwItemSpec)) {
                    state.draw_skips++;
                    return CDRF_SKIPDEFAULT;
                }
            }
            if (header->code == LVN_ITEMCHANGING && !state.applying) {
                NMLISTVIEW *change = (NMLISTVIEW *)lp;
                if (hidden(change->iItem) && (change->uChanged & LVIF_STATE) &&
                    (change->uNewState & (LVIS_SELECTED | LVIS_FOCUSED))) {
                    state.selection_vetoes++;
                    return TRUE;
                }
            }
            // Any index mutation invalidates the snapshot. Reveal rather than
            // accidentally suppressing a different original after a refresh.
            if (header->code == LVN_INSERTITEM || header->code == LVN_DELETEITEM || header->code == LVN_DELETEALLITEMS)
                if (!state.folder) reveal();
        }
    }
    if (message == WM_NCDESTROY) {
        fixture_closed(state.list);
        RemoveWindowSubclass(window, parent_proc, SUBCLASS_ID);
        if (valid()) RemoveWindowSubclass(state.list, list_proc, SUBCLASS_ID);
        release_accessibility(); release_paths(); ZeroMemory(&state, sizeof(state));
    }
    return DefSubclassProc(window, message, wp, lp);
}

static LRESULT CALLBACK list_proc(HWND window, UINT message, WPARAM wp, LPARAM lp, UINT_PTR id, DWORD_PTR data) {
    (void)id; (void)data;
    LRESULT bridge_result = 0;
    if (fixture_message(window, message, wp, lp, &bridge_result)) return bridge_result;
    if (message == WM_GETOBJECT || message == WM_PAINT || message == WM_PRINTCLIENT) {
        if (FAILED(sync_accessibility())) reveal();
    }
    if (message == LVM_SETITEMCOUNT || message == LVM_SORTITEMS || message == LVM_SORTITEMSEX) {
        if (!state.folder) reveal();
    }
    if (message == WM_KEYDOWN || message == WM_CONTEXTMENU || message == WM_LBUTTONUP || message == WM_RBUTTONUP || message == WM_PAINT || message == WM_PRINTCLIENT) repair_selection();
    if (message == WM_KEYDOWN && has_rule()) {
        if (wp == VK_LEFT || wp == VK_RIGHT || wp == VK_UP || wp == VK_DOWN || wp == VK_HOME || wp == VK_END) return navigate_key((UINT)wp);
    }
    if (message == WM_CHAR && has_rule() && type_visible((WCHAR)wp)) return 0;
    if (message == LVM_SETITEMSTATE && !state.applying && lp) {
        LVITEMW *item = (LVITEMW *)lp;
        if (item->state & item->stateMask & (LVIS_SELECTED | LVIS_FOCUSED)) {
            if ((int)wp == -1 && state.count > 0) {
                LRESULT ok = TRUE;
                for (int i = 0; i < ListView_GetItemCount(window); i++)
                    if (!hidden(i)) ok &= DefSubclassProc(window, message, (WPARAM)i, lp);
                state.selection_vetoes++;
                return ok;
            }
            if (hidden((int)wp)) { state.selection_vetoes++; return FALSE; }
        }
    }
    if (message == LVM_HITTEST || message == LVM_SUBITEMHITTEST) {
        LRESULT index = DefSubclassProc(window, message, wp, lp);
        if (hidden((int)index)) {
            LVHITTESTINFO *hit = (LVHITTESTINFO *)lp;
            if (hit) { hit->iItem = -1; hit->flags = LVHT_NOWHERE; }
            state.hit_skips++;
            return -1;
        }
        return index;
    }
    if (message == LVM_EDITLABELW || message == LVM_EDITLABELA || message == LVM_ENSUREVISIBLE) {
        if (hidden((int)wp)) return 0;
    }
    if (message == WM_LBUTTONDOWN || message == WM_LBUTTONDBLCLK || message == WM_RBUTTONDOWN ||
        message == WM_RBUTTONUP || message == WM_RBUTTONDBLCLK || message == WM_MBUTTONDOWN || message == WM_MBUTTONDBLCLK) {
        if (hidden(index_at(lp))) {
            state.hit_skips++;
            // Do not let a real icon remain selected behind its projection.
            if (message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN) {
                LVITEMW clear = {0}; clear.stateMask = LVIS_SELECTED | LVIS_FOCUSED;
                DefSubclassProc(window, LVM_SETITEMSTATE, (WPARAM)-1, (LPARAM)&clear);
                SetFocus(window);
            }
            if (message == WM_RBUTTONUP)
                return DefSubclassProc(window, WM_CONTEXTMENU, (WPARAM)window, (LPARAM)-1);
            return 0;
        }
    }
    if (message == WM_NCDESTROY) {
        fixture_closed(window);
        if (state.parent && IsWindow(state.parent)) RemoveWindowSubclass(state.parent, parent_proc, SUBCLASS_ID);
        RemoveWindowSubclass(window, list_proc, SUBCLASS_ID);
        release_accessibility(); release_paths(); ZeroMemory(&state, sizeof(state));
    }
    LRESULT result = DefSubclassProc(window, message, wp, lp);
    if (message == WM_KEYDOWN || message == WM_CHAR || message == WM_LBUTTONUP || message == WM_RBUTTONUP ||
        (message == WM_MOUSEMOVE && (wp & (MK_LBUTTON | MK_RBUTTON)))) repair_selection();
    return result;
}

EXPORT int __cdecl KansoAttachOwnedList(HWND list) {
    if (valid()) return -1;
    DWORD pid = 0;
    DWORD thread = GetWindowThreadProcessId(list, &pid);
    WCHAR name[64];
    if (pid != GetCurrentProcessId() || thread != GetCurrentThreadId()) return -2;
    if (!GetClassNameW(list, name, 64) ||
        (lstrcmpW(name, L"SysListView32") && wcsncmp(name, L"WindowsForms10.SysListView32.", 28))) return -3;
    HWND parent = GetParent(list);
    if (!parent || GetWindowThreadProcessId(parent, NULL) != thread) return -4;
    int item_count = ListView_GetItemCount(list);
    if (item_count < 0 || item_count > LIMIT) return -8;
    ZeroMemory(&state, sizeof(state)); state.list = list; state.parent = parent;
    if (!SetWindowSubclass(parent, parent_proc, SUBCLASS_ID, 0)) { ZeroMemory(&state, sizeof(state)); return -6; }
    if (!SetWindowSubclass(list, list_proc, SUBCLASS_ID, 0)) {
        RemoveWindowSubclass(parent, parent_proc, SUBCLASS_ID); ZeroMemory(&state, sizeof(state)); return -7;
    }
    state.count = item_count;
    return 0;
}

EXPORT int __cdecl KansoSetHiddenIndices(const int *indices, int length, int expected_count) {
    if (!valid() || GetWindowThreadProcessId(state.list, NULL) != GetCurrentThreadId()) return -1;
    int count = ListView_GetItemCount(state.list);
    if (count != expected_count || count > LIMIT || length < 0 || length > count || (length && !indices)) { reveal(); return -2; }
    // Validate the entire update before changing an active rule.
    for (int i = 0; i < length; i++) if (indices[i] < 0 || indices[i] >= count) return -3;
    state.applying = 1;
    release_paths();
    ZeroMemory(state.hidden, sizeof(state.hidden)); state.count = count;
    for (int i = 0; i < length; i++) {
        state.hidden[indices[i]] = 1;
        LVITEMW clear = {0}; clear.stateMask = LVIS_SELECTED | LVIS_FOCUSED;
        SendMessageW(state.list, LVM_SETITEMSTATE, (WPARAM)indices[i], (LPARAM)&clear);
    }
    state.applying = 0;
    HRESULT annotation = sync_accessibility();
    if (FAILED(annotation)) { reveal(); return (int)annotation; }
    InvalidateRect(state.list, NULL, TRUE);
    return 0;
}

EXPORT int __cdecl KansoSetHiddenShellPaths(IFolderView *folder, const WCHAR **paths, int count) {
    if (!valid() || GetWindowThreadProcessId(state.list, NULL) != GetCurrentThreadId() || !folder || count < 0 || count > LIMIT || (count && !paths)) return -1;
    IPersistFolder2 *persistent = NULL;
    // The root is obtained from the view itself, never guessed from filenames.
    HRESULT hr = IFolderView_GetFolder(folder, &IID_IPersistFolder2, (void **)&persistent);
    if (FAILED(hr)) return (int)hr;
    PIDLIST_ABSOLUTE root = NULL;
    hr = IPersistFolder2_GetCurFolder(persistent, &root);
    IPersistFolder2_Release(persistent);
    if (FAILED(hr)) return (int)hr;
    PWSTR parsed[LIMIT] = {0};
    for (int i = 0; i < count; i++) {
        // Pure string normalization: never ask the Shell to parse arbitrary
        // originals, resolve shortcuts or contact an offline/cloud location.
        const WCHAR *path = paths[i];
        DWORD needed = path ? GetFullPathNameW(path, 0, NULL, NULL) : 0;
        int absolute = path && ((path[0] && path[1] == L':' && (path[2] == L'\\' || path[2] == L'/')) || (path[0] == L'\\' && path[1] == L'\\'));
        if (!absolute || !needed || needed > 32768) hr = E_INVALIDARG;
        else {
            parsed[i] = (PWSTR)CoTaskMemAlloc((SIZE_T)needed * sizeof(WCHAR));
            if (!parsed[i]) hr = E_OUTOFMEMORY;
            else {
                DWORD written = GetFullPathNameW(path, needed, parsed[i], NULL);
                if (!written || written >= needed) hr = E_INVALIDARG;
                else {
                int length = lstrlenW(parsed[i]);
                while (length > 3 && (parsed[i][length - 1] == L'\\' || parsed[i][length - 1] == L'/')) parsed[i][--length] = 0;
                hr = S_OK;
                }
            }
        }
        if (FAILED(hr)) {
            CoTaskMemFree(parsed[i]);
            for (int j = 0; j < i; j++) CoTaskMemFree(parsed[j]);
            CoTaskMemFree(root); return (int)hr;
        }
    }
    release_paths();
    state.folder = folder; IFolderView_AddRef(folder);
    state.root = root; state.excluded_count = count;
    for (int i = 0; i < count; i++) state.excluded_names[i] = parsed[i];
    state.count = ListView_GetItemCount(state.list);
    HRESULT annotation = sync_accessibility();
    if (FAILED(annotation)) { reveal(); return (int)annotation; }
    repair_selection(); InvalidateRect(state.list, NULL, TRUE);
    return 0;
}

EXPORT int __cdecl KansoDetachOwnedList(void) {
    if (!valid()) return 0;
    if (GetWindowThreadProcessId(state.list, NULL) != GetCurrentThreadId()) return -1;
    HWND list = state.list;
    RemoveWindowSubclass(list, list_proc, SUBCLASS_ID);
    RemoveWindowSubclass(state.parent, parent_proc, SUBCLASS_ID);
    fixture_closed(list);
    release_accessibility(); release_paths(); ZeroMemory(&state, sizeof(state)); InvalidateRect(list, NULL, TRUE);
    return 0;
}

EXPORT int __cdecl KansoProjectionStats(uint32_t *values, int length) {
    if (!values || length != 4) return -1;
    values[0] = state.draw_skips; values[1] = state.selection_vetoes;
    values[2] = state.hit_skips; values[3] = state.resets;
    return 0;
}
EXPORT int __cdecl KansoIsIndexHidden(int index) { return hidden(index); }
EXPORT HRESULT __cdecl KansoFixtureAccessibleState(HWND list, int index, LONG *result) {
    if (!result) return E_POINTER;
    IAccessible *accessible = NULL;
    HRESULT hr = AccessibleObjectFromWindow(list, OBJID_CLIENT, &IID_IAccessible, (void **)&accessible);
    if (FAILED(hr)) return hr;
    VARIANT child, value; VariantInit(&child); VariantInit(&value);
    V_VT(&child) = VT_I4; V_I4(&child) = index + 1;
    hr = IAccessible_get_accState(accessible, child, &value);
    if (SUCCEEDED(hr) && V_VT(&value) == VT_I4) *result = V_I4(&value);
    else if (SUCCEEDED(hr)) hr = E_UNEXPECTED;
    VariantClear(&value); IAccessible_Release(accessible); return hr;
}
