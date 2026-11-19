# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows port of **Listful**, the Mac (and iPhone/iPad) outliner for task lists kept as plain
Markdown files. The Mac app is the source of truth and keeps evolving; this repo follows it. It is
checked out as the git submodule `upstream/` (origin `drchrispinnock/listful`; a working copy
usually also sits at `..\listful`). Before porting or changing a feature, read `upstream/CLAUDE.md`
(detailed architecture) and the matching Swift files. Behaviour, wording, file format and edge
cases should match the Mac app unless Windows forces a difference; note each difference here.

Stack: C# / .NET 10, WinUI 3 (Windows App SDK, unpackaged), xUnit v3. The layout and conventions
follow `..\signet-windows`, the Windows port of Signet.

## Commands

```powershell
git submodule update --init                     # after cloning
dotnet build Listful.slnx -p:Platform=x64
./scripts/sign-build.ps1 -Tests                 # sign the test binaries (Smart App Control, below)
dotnet test --project tests/Listful.Core.Tests --no-build
dotnet test --project tests/Listful.Core.Tests --no-build --filter-method "*.OutlineTests.IndentMovesSubtreeUnderPreviousSibling"
dotnet test --project tests/Listful.Core.Tests --no-build --filter-class "*.MarkdownCodecTests"
./scripts/sign-build.ps1                        # sign the Debug x64 app build
src\Listful.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Listful.exe
./scripts/make-icon.ps1                         # regenerate Assets\Listful.ico/.png from upstream's spec/icon.png
./scripts/upstream-changes.ps1                  # list upstream commits not yet ported
dotnet build src/Listful.App/Listful.App.csproj -c Release -p:Platform=x64   # the runnable Release output (self-contained)
./scripts/pack.ps1 -BuildDir srcistful.appindrelease
et10.0-windows10.0.22621.0win-x64 -rid win-x64 -version 0.6.0 -signparams (get-signparams (get-signingcertificate))   # after `. ./scripts/signing.ps1`: signed setup, portable zip and update feed in dist\n./scripts/release.ps1 -tag v0.6.0               # sign, pack and draft a tag's ci build (docs/releasing.md)
```

Package versions are pinned centrally in `Directory.Packages.props`, and warnings are errors. Tests
run on Microsoft.Testing.Platform (opted in by `global.json`), so use the `--filter-*` options
above, not VSTest's `--filter`.

**Smart App Control is enforced on this PC.** It blocks a freshly built, unsigned
`Listful.Core.Tests.exe`/`.dll` or `Listful.exe` ("An Application Control policy has blocked this
file"; `dotnet exec` of the DLL is blocked too, and xUnit v3 needs its app host). With SimplySign
Desktop connected, `scripts/sign-build.ps1` signs them with the Certum certificate (the same one
and helper, `scripts/signing.ps1`, as signet-windows), which Smart App Control accepts. Sign after
every build, before `dotnet test --no-build` or launching. A timestamp-server failure is transient:
run it again.

Running the app for a test: set `LISTFUL_PREFERENCES` to a scratch preferences file (JSON with
`libraryFolder` pointing at a scratch folder) so the user's own settings and lists are untouched;
`scratch/` is git-ignored. Launching takes keyboard focus, so say so first, stop the test copy by
its PID (never every `Listful.exe`: the user may have one open), and check that Listful is the
foreground window before sending synthetic keys. WinUI ignores `SetCursorPos` moves during a
drag; use absolute `mouse_event` moves. `LISTFUL_TRACE=<file>` turns on `Services.Trace` lines.

## Layout

- `src/Listful.Core` (`net10.0`, no WinUI, namespace `Listful`): everything testable.
  `Models/` is upstream's `ListfulCore` (Outline, MarkdownCodec, TextMerge, FileSync,
  TaskLibrary, RepeatSchedule, RepeatingChecklists, TaskClipboard). `ViewModels/` holds
  `TaskDocument` and `LibraryModel`, which upstream keeps in the app target; here they are in Core
  so `ViewModelTests` can drive them. `Services/` has `Preferences` and `FileWatcher`.
- `src/Listful.App` (WinUI 3): `MainWindow` (menu bar, shortcuts, sidebar beside the outline),
  `Views/` (`SidebarView`, `OutlineView`, `TaskRowView`, `Dialogs`), `Services/` (clipboard,
  pickers, Explorer, idle time, crash log). Views are built in code; only `App.xaml` is XAML.
- `tests/Listful.Core.Tests`: ports of `upstream/Tests/ListfulCoreTests`, plus Windows-only tests.

## Porting conventions (keep the two codebases diffable)

- Mirror names one-to-one: `upstream/Sources/ListfulCore/Foo.swift` becomes
  `src/Listful.Core/Models/Foo.cs` with the same type and member names, PascalCased, and the doc
  comments carried over. Swift argument labels are dropped (`indent(at:)` is `Indent(i)`,
  `join(_:into:)` is `Join(i, previous)`). Test suite `FooTests`/test `doesBar` becomes
  `FooTests.DoesBar` in a file of the suite's name.
- `Outline` is a class here (Swift: a struct), so take `Clone()` where Swift relies on copying.
  Items are a `List<TaskItem>` of `record struct`s edited in place through `CollectionsMarshal`
  (`At(i)`); every structural edit must keep the depth invariant (upstream CLAUDE.md).
  `Range<Int>` is `IntRange` (half-open), `ClosedRange<Int>` is `ClosedIntRange`.
- Swift enums with payloads become abstract records with nested cases (`SaveOutcome.Merged`,
  `TextMerge.Result.Conflict`, `TaskDocument.LoadStateValue.Failed`). `RepeatSchedule.Frequency`
  is a C# enum; the property is `FrequencyValue` (and `WeekdaysValue`), since a member can't
  share its type's name.
- `URL`s are path strings, compared with `PathUtility.SamePath` (case-insensitive) and keyed with
  `PathUtility.Comparer`. Folders carry no trailing separator, so the sidebar tells lists from
  folders by row type, not `hasDirectoryPath`. `localizedStandardCompare` is
  `StringOrdering.LocalizedStandardCompare` (.NET 10's `CompareOptions.NumericOrdering`).
- `@Observable` view models raise a `Changed` event and the views redraw from them. Swift
  `Task`s are `async` methods that resume on the UI thread's synchronization context; off-thread
  file work is `Task.Run`. `TaskDocument.SetTitle` doesn't raise `Changed` for plain typing (the
  field already shows it), only when the row's kind changes.
- Files stay byte-compatible with the Mac and iOS apps: UTF-8 without a BOM, LF line endings
  (CRLF and a BOM are read), atomic replace. Don't let Windows defaults leak in.
- Source files are UTF-8 without a BOM and contain `…`, `“”` and `—`. Windows PowerShell 5.1
  reads them as ANSI, so a scripted rewrite garbles them; edit with the editor tools. Writing
  U+2028/U+2029 into a source file breaks the C# lexer; use `\p{Zl}`/`\p{Zp}` in regexes.

## Windows differences from upstream

- **No iCloud.** The lists folder is any folder: on first run (no `libraryFolder` preference)
  `Dialogs.ChooseListsFolder` offers OneDrive\Listful, Dropbox\Listful (found from Dropbox's
  `info.json`), Documents\Listful or another folder; Settings › Choose… changes it. The default
  folder isn't created until chosen. Upstream's `ListsMigration`, iCloud placeholders and
  `ICloudPlaceholderTests`/`ListsMigrationTests` are not ported. `MarkdownFileStore.NeedsDownload`
  reports OneDrive/Dropbox online-only files (recall-on-access attributes); they show as
  Downloading… while read off the UI thread, and the sidebar doesn't read titles from them. There
  is no `NSFileCoordinator`: reads, writes and moves retry briefly on sharing violations.
- Deleting goes to the Recycle Bin (`Trash`, `SHFileOperation`); menus say Delete…, not Move to
  Trash. Tests replace `Trash.MoveToTrash` with a plain delete (`TrashForTests`).
- List and folder names also refuse `\ * ? " < > |`, trailing dots or spaces, and device names.
- `RepeatSchedule` takes local `DateTime`s, not a `Calendar`; the week starts on the culture's
  first day. Labels have no ICU templates: `PeriodLabel` puts the day first or second following
  the culture's month-day pattern ("Tue 3 Nov" en-GB, "Tue, Nov 3" en-US).
- Preferences are `%LOCALAPPDATA%\Listful\preferences.json` (upstream's UserDefaults keys), plus
  window bounds and sidebar width. Crash log: `%LOCALAPPDATA%\Listful\Logs\crash.log`.
- Menus are a MenuBar; Cmd shortcuts are Ctrl (Cmd+Option is Ctrl+Alt: headings Ctrl+Alt+1/2/3/0,
  Sort Ctrl+Alt+S). `MainWindow.Shortcut` handles them in the root's PreviewKeyDown, ahead of the
  focused text box; menu items only display them (`KeyboardAcceleratorTextOverride`). Added:
  Settings Ctrl+, in File, Show Sidebar Ctrl+Shift+L, Help › Keyboard Shortcuts, F2 to rename in
  the sidebar, a resizable sidebar.
- Rows (`TaskRowView`) are TextBoxes with a bare template (`RowTextBox` in `App.xaml`). TextBox
  can't strike text through, so a done row shows an overlay TextBlock while it isn't being edited.
  `OutlineView.Render` reuses row views by id and inserts or removes only the rows that changed:
  taking a focused field out of the tree sends focus to the menu bar. Focus requests are applied
  synchronously (after `UpdateLayout` for a new row) so keys typed after Return land in the new row.
  Row selection, copy/paste of rows, Shift-click and bullet drags (within the list, or onto a
  sidebar list) are ported; selecting by dragging across rows is not yet.
- Indent/outdent/move keep the caret where it was (`FocusRequestKeepsCaret`); upstream moves it
  to the end. `Outline.RemoveBlock`/`Place` keep the title's attribute block, which upstream drops.
- The clipboard carries plain text plus Listful's Markdown under the format name
  `com.chrispinnock.listful.tasks`.
- Copies due after a new day are made once the computer is in use (`GetLastInputInfo`), checked
  each minute; window activation makes them straight away. There's no separate wake handler.
- Updates and packaging are Velopack, as in signet-windows (upstream: Sparkle and a notarised
  DMG). `Program.cs` replaces the generated `Main` (`DISABLE_XAML_GENERATED_MAIN`) so
  `VelopackApp` runs first. `Services/UpdaterService` checks the GitHub releases of
  `drchrispinnock/listful-win` daily (Settings › Updates, upstream's "Automatically check for
  updates"); `UpdateDialog` (Help › Check for Updates…) offers Download and Install, then Restart
  to Update, saving the open list first. Nothing downloads unasked: Velopack has no EdDSA check
  like Sparkle's. Only an installed copy (Setup or Portable) updates; `LISTFUL_UPDATE_SOURCE`
  points it at a local `vpk pack` folder for testing. The Velopack app id is `ListfulApp`, not
  `Listful`: it installs into `%LOCALAPPDATA%<app id>`, and uninstalling deletes that folder, which
  must not be the preferences folder `%LOCALAPPDATA%istful`. version is `<version>` in
  `listful.app.csproj`, semver (upstream's `0.6` is `0.6.0`). release builds are self-contained;
  `dotnet publish` drops the `.pri`/`.xbf` files (signet-windows), so pack the build output.
  `.github/workflows/release.yml` builds and tests a tag; `scripts/release.ps1` signs, packs and
  drafts the release here (docs/releasing.md).
- not yet: the ios-only features.

## Syncing with upstream

The submodule pin marks the last upstream commit that has been ported (currently `26b5eea`,
release 0.6). Run `./scripts/upstream-changes.ps1`, port each commit in order, then
`git -C upstream checkout <sha>; git add upstream` and commit as `Port upstream <sha>: <subject>`.
iOS-only and Mac-packaging commits need nothing here but still advance the pin.
