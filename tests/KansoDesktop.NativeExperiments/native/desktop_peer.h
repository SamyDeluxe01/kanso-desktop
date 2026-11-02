#pragma once
#include <windows.h>
#include <shlobj.h>
int desktop_window_matches(HWND window);
int desktop_owner_matches(DWORD pid);
HRESULT desktop_folder(HWND target, IFolderView **folder, HWND *list);
