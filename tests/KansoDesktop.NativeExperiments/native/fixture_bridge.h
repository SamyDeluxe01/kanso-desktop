#pragma once
#include <windows.h>
#define FIXTURE_TOKEN L"Kanso.NativeFixture.Token.v1"
#define FIXTURE_OWNER L"Kanso.NativeFixture.Owner.v1"
#define FIXTURE_FOLDER L"Kanso.NativeFixture.Folder.v1"
#define FIXTURE_MAGIC 0x4B414E53u
#define FIXTURE_ATTACH 1
#define FIXTURE_PING 2
#define FIXTURE_DETACH 3
#define FIXTURE_STATUS 4
#define DESKTOP_OBSERVE_ATTACH 5
#define DESKTOP_ARTIFICIAL_ATTACH 6
#define FIXTURE_UPDATE_RESULT 7
int fixture_message(HWND window, UINT message, WPARAM wp, LPARAM lp, LRESULT *result);
void fixture_closed(HWND window);
#define DESKTOP_REFERENCES_ATTACH 8
