# Development

The release application uses the original WPF group UI and the existing Explorer desktop. The current entry project is `tests/KansoDesktop.NativeExperiments`; only the internal folder/project name is retained. Its assembly and apphost are now named `KansoDesktop`, producing `KansoDesktop.exe` by default. `src/KansoDesktop` alone contains a historical entry point and is not the release entry project.

`PersonalDesktopMode` is the ordinary product entry mode. The `--startup` argument starts the same persistent mode without opening Settings. `WindowsStartup` manages the current user's `KansoDesktop` Run value. `Settings` stores startup preferences, desired desktop activation state and interface language.

Translations are embedded from `assets/localization/translations.json`. The source catalog has a German key and complete translations for English, French, Spanish, Italian, Portuguese and Japanese. Calls to `L.T` and `L.F` apply only to explicit interface strings; user group names, file names and paths are not translated. `Controller.SetLanguage` rebuilds UI and tray labels while preserving group references and preferences. Native file context menus continue to use the Windows UI language.

The native projection bridge accepts only filesystem entries enumerated in the connected shell view. Desktop references are filtered in place; external references do not create desktop items. Owner/heartbeat handling restores filtered entries on disconnect. The module stays loaded but inactive until Explorer exits normally. See source for protocol validation and bounds.

## Checks

```powershell
dotnet run --project tests/KansoDesktop.Checks -c Release
./dist/KansoDesktop/KansoDesktop.exe appearance-checks ./artifacts/ui-checks
./dist/KansoDesktop/KansoDesktop.exe projection ./artifacts/projection-checks
./dist/KansoDesktop/KansoDesktop.exe bridge-check ./artifacts/bridge-checks
```

These modes use artificial data. `bridge-check` launches only its own hidden fixture processes. `appearance-checks` does not register actual Windows startup or attach Explorer. Live desktop experiments are separate from routine checks. Do not run competing organizer sessions.

Current validation: 54 core checks and 93 packaged UI/controller checks. Language checks cover all seven selections, persistence, placeholders, preservation of user titles/references, and rebuilding hidden settings without opening a window. Native projection code is unchanged from the previous accepted version. Historical native checks include 34 projection checks, 18 path checks and five process/recovery scenarios. This is not complete accessibility, reboot or long-running desktop coverage.

Public media includes user-provided desktop screenshots and neutral example UI renders. A caption identifies the source and scope. No personal layout, file inventory or diagnostic log is included in the repository or distribution archive.
