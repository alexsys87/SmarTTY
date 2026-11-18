# CLAUDE.md

SmarTTY: a graphical SSH client and terminal for Windows (SSH, SFTP/SCP, COM/TCP/telnet device terminals, remote file
editor). C#, WPF shell with WinForms controls hosted in `WindowsFormsHost`. User docs: `README.md` (in Russian).

Read this file instead of re-exploring the repository. Update the "Migration status" and the maps below in the same
PR that changes them.

## Build and test

- Platform: `net10.0-windows`, x64, WPF + WinForms (after alexsys87/SmarTTY#506; before it `main` was `net48` x86 built
  with `msbuild`).
- Build (also works on Linux for compile checks thanks to `EnableWindowsTargeting`; the app runs only on Windows):
  `dotnet build SmarTTY.csproj -c Release`
- Single exe: `dotnet publish SmarTTY.csproj -c Release -p:SingleExe=true` → `bin/Release/net10.0-windows/publish/SmarTTY.exe`
- Tests: 97 console projects in `tests/`, each run with `dotnet run --project tests/<Name> -c Release`. The full list
  and order are in `.github/workflows/build.yml`; run the ones that compile the files you touched
  (`grep -rln <FileName> tests/*/*.csproj`).
- Mono tests (net48, need `mono` + `xvfb`; `libmono-i18n4.0-all` for code pages): `SmartTerminalIntegrationTests`,
  `RemoteFileEditorIntegrationTests` (the real remote editor on OpenSSH and Dropbear), `NetconsoleTerminalTests`,
  `DeviceLineEditorTests`. They compile all non-GUI code including `Controls/` (`Compile Include="../../**/*.cs"`
  minus `GUI/**`, `App.xaml.cs`), so that code must still compile for net48: no `Range`/`Index`, `HashCode`,
  `Math.Clamp`, `ArgumentNullException.ThrowIfNull`, `string.Contains(char)` unless behind `#if NET5_0_OR_GREATER`.
  Build: `dotnet build tests/<Name> -c Release`, run: `xvfb-run -a mono tests/<Name>/bin/Release/net48/<Name>.exe`.
- `SshNetIntegrationTests`, `SmartTerminalIntegrationTests` and `RemoteFileEditorIntegrationTests` need a real sshd and Dropbear
  (`tests/SshNetIntegrationTests/setup-*.sh`; apt `openssh-server dropbear-bin busybox`; as root they also run in a sandbox:
  `set -a; source <(setup-sshd.sh); source <(setup-dropbear.sh); set +a`). Mono cannot open its sockets on a kernel without
  IPv6: build `tests/RemoteFileEditorIntegrationTests/mono-ipv4-shim.c` (command in its csproj) and run mono with `LD_PRELOAD`.

## Conventions

- Code comments and commit messages in English; README in Russian. Commit subjects in the imperative, as in `git log`.
- File-scoped namespaces, `_camelCase` fields, `/// <summary>` on public members. No `Nullable`, no `ImplicitUsings`.
- Tests: plain console apps with `Check`/`Equal` helpers printing `PASS:`/`FAIL:`, sources linked with
  `<Compile Include="../../X/Y.cs" Link="Y.cs" />` (model: `tests/MultiCursorTests`). No xUnit/NUnit.
  A new test project needs its own step in `build.yml`.
- Pattern for UI-free logic: model class in a logic folder + thin partial in `Controls/` that wires it to the control
  (example: `CodeEditing/MultiCursor.cs` + `Controls/CodeEditorPP.MultiCursor.cs` via `IMultiCursorText`).
- Settings types in `VisualTTY/` are serialized to XML: do not change their member types or names.
- `CsiRepeatLimitTests` and `PrivateModeTableTests` read the source text of `Controls/TerminalEmulatorControl.cs`
  (`AssemblyMetadata EmulatorSource`): update the path if that code moves.

## Folder map

| Folder | What is there | UI? |
| --- | --- | --- |
| `App.xaml(.cs)` | Entry point, theme, wiring of WPF dialogs into WinForms code through static delegates | WPF |
| `GUI/` | WPF windows and dialogs (`MainWindow`, `ConnectionSelectionWindow`, settings, file lists) | WPF |
| `GUI/Backend/` | `MainWindowController`, tab contexts (`NormalTabContext`, `EditorTabContext`, `DeviceTabContext` host WinForms controls through `WinFormsHostWrapper : WindowsFormsHost`), `TextSearcher`, `RemoteFileEditorStatusBar` | WPF |
| `GUI/Styles/` | `ThemeManager`, theme palettes | WPF |
| `Controls/` | WinForms custom-drawn controls: terminals, editors, hex editor; themed adapters | WinForms |
| `Forms/` | WinForms forms and DPI support (`DpiAwareForm`, `DpiScaling`), popups (`SuggestionListForm`, `TextHintForm`) | WinForms |
| `Terminal/` | Terminal logic: xterm protocol, decoders, paste, completion sessions, command history, links | mostly no |
| `TextEditing/` | Text and hex documents, encodings, line endings, matching; also carets and margins (UI) | partly |
| `CodeEditing/` | Syntax highlighting, folding, editor commands, key bindings, multi-cursor, diff | partly |
| `Completion/` | Suggestion and signature sources and controllers, API symbols | partly |
| `VisualTTY/` | Settings and their models (`GlobalSettings`, palettes, key layouts), `DeviceTerminal/` state and line input | no (but uses `Color`/`Keys`) |
| `Transport/` | TCP, telnet (+TLS), serial, netconsole transports; file transfer manager; backup/restore | no |
| `IO/` | Remote command and file helpers, `ls`/`df` parsers, editor drafts, transfer protocols (X/Y/ZMODEM, Kermit, gzip+base64, TFTP) | no |
| `Ssh/` | SSH.NET wrappers, SFTP/SCP, keys, port forwarding, X11; `Connection/SshConnector` | no |
| `Sync/`, `LocalRepo/`, `PackageManagers/` | Folder sync and tar, local OpenWrt repository, remote package managers | no |
| `Native/` | P/Invoke: serial port and its arrival watcher, setupapi, clipboard, reparse points | no |
| `Utilities/` | Shared helpers | no |

## Control hierarchy (Controls/)

```
DpiAwareControl (WinForms UserControl)
└─ ReadonlyTextBrowser            3600 lines (+ .Wrap, .DragScroll): document view, caret, selection, highlights, scrolling
   └─ ReadonlySmartScrollBrowser
      └─ EditableTextBrowser      undo/redo edit commands
         └─ TextEditorControl
            ├─ CodeEditorPP       (+ .LineEditing, .MultiCursor): completion, folding, inline messages
            │  └─ RemoteFileEditor (+ .Syntax): load/save over SSH, drafts, sudo, encodings; hosts HexEditorControl
            └─ TerminalEmulatorBase   screen buffers, CellStyle, LineInfoStore, tab stops, scroll region
               └─ TerminalEmulatorControl (+ .Suggestion): VT parser (ProcessOutput), keys (ProcessCmdKey), mouse, paste
                  ├─ SshTerminalControl      Smart Terminal command line, completion, links
                  └─ SerialTerminalControl   line input, local echo
DeviceTerminalControl : DpiAwareControl   COM/TCP/telnet panel (+ 13 partials: ZModem, YModem, Kermit, Tftp, GzipBase64,
                                          Netconsole, FileSend, FileDrop, QuickScript, SerialLines, DeviceListing, LineInput);
                                          contains a SerialTerminalControl and a HexTerminalControl
HexEditorControl : Control                hex editing inside RemoteFileEditor
HexTerminalControl : DpiAwareControl      hex view of a device stream
```

Key coupling: the terminal is a subclass of the text editor. A terminal screen is an `EditableTextDocument` plus a
`LineInfoStore` (one `CellStyle[]` per line) plus a `Point Cursor` (`TerminalEmulatorBase.ScreenBuffer`). Bytes typed
or sent back to the host leave through the `DataEntered` event via `TerminalEventDispatcher.PostInput`.

## Migration status (WinForms → WPF)

Goal: drop WinForms, render with WPF for better performance and scaling up to 8K. Order: 0 → 1A → 3 → 1B/1C → 2 → 4–5.

| Stage | Content | Status |
| --- | --- | --- |
| 0 | .NET 10, x64, single-file publish instead of Costura | Done in alexsys87/SmarTTY#506 |
| 1A | Editor logic out of WinForms | In progress: A1 (types, boundary check), A2 (`TextEditing/`) and A3a (keys in `CodeEditing/`) A3b (folding, inline messages), A3c (`Completion/`) and A3d (line editing) done; A4 (`RemoteFileSession`) started: its Mono safety net is in (A4a), the server probes (A4b: `RemoteFileServer`) and the drafts (A4c: `RemoteFileDrafts`) are out, next the load |
| 1B | Terminal emulator core out of WinForms | Planned |
| 1C | SSH/serial/device session controls out of WinForms | Planned |
| 2 | New WPF terminal view (`GlyphRun`, row cache), device panel in XAML | Planned |
| 3 | Editor on AvalonEdit, hex editor on WPF | Planned (after 1A) |
| 4 | Popups and menus to WPF, remove `Themed*Adapter`, `Forms/` | Planned |
| 5 | Remove `UseWindowsForms` and `System.Drawing`, vector icons | Planned |

### Stage 1 rules

Pure refactoring: no behavior change, code moved as is (`git mv` for whole files, the move and the edit in separate
commits), one small PR per step, each green on its own. Do not fix bugs found on the way; list them in the PR.

- Model folders `Terminal/`, `TextEditing/`, `CodeEditing/`, `Completion/` end with no reference to
  `System.Windows.Forms`, `System.Drawing` (including `Point`, `Color`, `Rectangle`), WPF, `Controls/`, `Forms/`, `GUI/`.
  View files found there move to `Controls/TextView/` or `Controls/TerminalView/`.
- New model types in `TextEditing/`: `RgbColor` (ARGB, `Empty`), `GridPoint` (mutable `X` column / `Y` row, like `Point`),
  `VirtualKey` (exact copy of `Keys`: same names and values), `KeyStroke`. Conversions only in `Controls/ModelInterop.cs`.
- Seams are derived from real call sites, no methods "for later": `IUiDispatcher`, `IUserPrompt`, `IEditorTextBuffer`,
  `ISearchTarget`, `IEditorStatusSource`, events on `TerminalEmulator` (`RowsChanged`, `BufferSwitched`, `CursorMoved`,
  `TitleChanged`, `Bell`, `ModesChanged`, `DataEntered`). Events fire synchronously where the direct call was.
- Timers, painting, brushes, input events, menus, `MessageBox`, `Clipboard`, `BeginInvoke` stay in the view.
- Threading unchanged: the model is single-threaded and called where the old code ran.

### Stage 1 move map

| From | To |
| --- | --- |
| `RemoteFileEditor`: load, drafts, server-change check, encodings, save pipeline, sudo, validation, service reload, diff | `CodeEditing/RemoteFileSession.cs` (view keeps hex mode, focus, colors, clipboard, prompts) |
| `RemoteFileEditor.Syntax.cs` | `CodeEditing/` |
| `CodeEditing/EditorCommands`, `KeyBindingMap`, `Terminal/CompletionSession`, `ICompletionPopup` | `Keys` → `VirtualKey`, `KeyEventArgs` → `KeyStroke` |
| Painting in `CodeEditing/FoldingManager`, `InlineMessageProvider` | `Controls/TextView/` |
| `TextEditing/SoftwareCaret`, `WindowsCaret`, `LineNumberMargin`, `IMargin`, `ICaret`, `ICaretHost`; `Completion/SuggestionPopup` | `Controls/TextView/` |
| `TextEditing/HighlightStyle` brushes | View-side cache; the model keeps `RgbColor` |
| `HexEditorControl` editing logic, `HexDataInspector` value decoding | `TextEditing/HexEditingModel.cs` |
| `TextSearcher` / `RemoteFileEditorStatusBar` dependencies on controls | `ISearchTarget` / `IEditorStatusSource` |
| `TerminalEmulatorBase` nested `CellStyle` (Brush → `RgbColor`), `LineInfo`, `LineInfoStore`, `ScreenBuffer` (`Point` → `GridPoint`), `TabStops`, `ScrollRegion`, `LinkTracker`, `EraseMode` | `Terminal/Emulation/` |
| `TerminalEmulatorControl` VT parsing, screen operations, host responses | `Terminal/Emulation/TerminalEmulator.cs` |
| Key/mouse/paste → bytes | `Terminal/Emulation/TerminalInputEncoder.cs` |
| Smart Terminal command line (`SshTerminalControl`), line input (`SerialTerminalControl`), `DeviceTerminalControl` feature partials | `Terminal/`, `VisualTTY/DeviceTerminal/` (`*Session`/`*Controller`) |

Done in A1: `TextEditing/RgbColor`, `GridPoint`, `VirtualKey` (generated from the `Keys` of WinForms, 194 members), `KeyStroke`;
`Controls/ModelInterop.cs` (the only conversions). `tests/ModelTypesTests` (net8.0) checks the types, `tests/ModelInteropTests`
(net48, Mono) checks `VirtualKey` against the real `Keys` member by member and every conversion.

Done in A2: the caret, margin and scroll bar files (`SoftwareCaret`, `WindowsCaret`, `LineNumberMargin`, `IMargin`, `ICaret`,
`ICaretHost`, `IScrollBar`) moved to `Controls/TextView/` (namespace unchanged); `TextEditing/` is free of UI types.
`HighlightStyle` holds `RgbColor` values and `UseSystemSelectionColors`; `Controls/TextView/HighlightBrushes` makes and caches the
brushes and pens when the control paints (the system colours are read then).

Done in A3a: `KeyBindingMap` and `EditorCommands` use `VirtualKey` and `KeyStroke`; the commands work on the editor through
`CodeEditing/IEditorCommandTarget` (the 15 methods they call, implemented by `CodeEditorPP`; `TokenPredicate` moved there from
`CodeEditorPP`). The key names of a shortcut (`KeysConverter`) are in `Controls/KeyShortcutText` (`Format`, `TryParse`), moved from
`KeyShortcut.ToString`/`TryParse` without changes; nothing in the application calls them. `ICompletionPopup` and
`CompletionSession.HandleKey` take `VirtualKey`/`KeyStroke`. Next, A3b: the painting of `FoldingManager` and
`InlineMessageProvider`, `ICompletionHost`, the completion controllers and `SuggestionPopup`, the pure line transforms of
`CodeEditorPP`.

Done in A3b: `FoldingManager` is the model of the folded blocks (`CodeEditing/`); `Controls/TextView/FoldingMarginPainter` draws the
fold marks and handles the click, `Controls/TextView/FoldingLineProvider` answers the text view (hidden lines, decorator of a folded
line: gray label and frame). `InlineMessageProvider` holds the messages and the row bookkeeping; `Controls/TextView/InlineMessageView`
is the provider of the text view with the painting, font, icons and theme colours (`CodeEditorPP.ApplyMessageColors` sets them on the
view). `tests/FoldingTests` compares the answers of the folding model with a golden file written by the old class.
Next, A3c: `ICompletionHost` (model part / view part), the controllers' `Color` → `RgbColor`, `SuggestionPopup` to `Controls/TextView/`;
then the pure line transforms of `CodeEditorPP` to `CodeEditing/`.

Done in A3c: `SuggestionPopup` and `ICompletionHost` moved to `Controls/TextView/` (nothing in `Completion/` calls the host except to hand it to
a window, so there is no model half of it). `SuggestionController` works through `ISuggestionPopup` / `ISuggestionPopupFactory`, and
`SignatureHintController` makes its `IHintView` through a `Func` (`IHintView.ShowAt/Reposition` have no host argument); the view makes the
windows in `Controls/TextView/SuggestionPopupFactory` and `HintViewFactory`. The list background is an `RgbColor` in the controller.
`tests/CompletionControllersTests` drives the controllers and `SuggestionCatalog` with fake windows. Next, A3d: the pure line
transforms of `CodeEditorPP` (`.LineEditing`) to `CodeEditing/`.

Done in A3d: `CodeEditing/LineEditing.cs` (the lines a line command works on, the smallest replacements between old and new lines, where a
column goes, the text of a duplicate and of a move), `CodeEditing/AnalysisText.cs` (the text and the caret the completion works on) and
`FoldingManager.RestoreFolds` (which line a saved fold goes back to) hold what was in `CodeEditorPP.LineEditing.cs` and `CodeEditorPP.cs`;
the control reads the lines, asks the model and makes the edits. `tests/LineEditingTests` compares 300 random cases and 60 documents around
the analysis budget with golden files written by the old code of the control. Next, A4: `RemoteFileSession`.

Done in A4a (before any move of the editor's logic): `tests/RemoteFileEditorIntegrationTests` (net48, Mono + xvfb) runs the real
`RemoteFileEditor` against OpenSSH and Dropbear: load, change, save and read back for UTF-8, BOM, ISO-8859-1/Windows-1251, LF/CRLF, a new file,
the file mode, the backup copy, conflicts (save on close and background save), a background save with a second save asked for meanwhile,
reload, the shell script check, the offer to reload a service, drafts; the questions the editor asks are caught through
`ThemedMessageBox.ShowImpl` and answered. It was written against the code before the move and must keep passing unchanged. The SSH layer
sometimes drops the connection after a command (an end of file for a closed channel, seen with OpenSSH 9.6): a scenario cut short by it is
run again on a new connection (printed in the log), anything else fails the run.

Done in A4b: the calls `RemoteFileEditor` made on the SSH session to learn the state of the file (does a path exist, can it be written, the
download stream, the signature that tells whether somebody else changed the file, the text of the conflict question) are in
`CodeEditing/RemoteFileServer.cs`, which reaches the connection through `CodeEditing/IRemoteFileHost` (the eight calls the editor made;
`Controls/SessionRemoteFileHost` is the adapter and reads `ITerminalSessionContext.Session` on every call, because a reconnect replaces it).
`tests/RemoteFileServerTests` (net8.0) drives it with a fake connection; `tests/RemoteFileEditorIntegrationTests` passes unchanged. Next, A4c:
the drafts (`ClaimDraftKey`, the write schedule, `QueueDraft`), then the load, the save pipeline and the rest of `RemoteFileSession`.

Done in A4c: the draft of the unsaved changes is in `CodeEditing/RemoteFileDrafts.cs` (the key, the base hash, when a text is taken with the
`DraftSchedule`, the writes and removals of which the latest wins, finding the draft an earlier editor left, the cleanup of old drafts once per
run) and `CodeEditing/DraftKeyClaims.cs` (the numbered key and its lock file, with the folder as a parameter). The editor keeps the static
`EditorDraftStore` (with the Windows encryption) and `DraftKeyClaims` instances, the timer that asks for a draft, the reading of the text and of
the hex view, and the question "restore the draft?". `tests/RemoteFileDraftsTests` (net8.0) runs them on a real `EditorDraftStore` in a temporary
folder; `tests/RemoteFileEditorIntegrationTests` passes unchanged. Next, A4d: the load (`LoadFile`, `FinishLoading`, `ApplyContent`) with
`IUiDispatcher` and `IUserPrompt`, then the save pipeline.

Boundary check: `tests/check-model-boundary.sh` greps the four model folders for `System.Windows*`, `System.Drawing`,
`SmarTTY.Controls/Forms/GUI`; files of `tests/model-boundary-allowlist.txt` are the known violations (23 at the start, 15 after A2, 12 after A3a, 10 after A3b, 5 after A3c: the 20
of the specification plus `Completion/ShellCompletionAnalyzer`, `Terminal/CommandRun`, `Terminal/OutputLinkScanner`, which
use the terminal controls). The list only shrinks, and the script fails on a listed file that is already clean.
`tests/ModelBuildTests` (net8.0, no Windows) compiles the model folders without the listed files; a clean file that needs
listed ones is excluded in its csproj (`ITerminalSessionContext`): when a file leaves the allowlist, make sure it
is compiled there as well, and add to the dependency list there when the model starts to use new code outside its folders.

Safety nets that stage 1 adds before moving code: terminal golden snapshots captured from the old code before any
terminal move (`tests/TerminalSnapshots/`, never re-recorded); `tests/check-model-boundary.sh` with an allowlist that
only shrinks. Mono tests stay until each of their checks is ported to net8.0 tests on the model.
