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
  `DeviceLineEditorTests`, `HexEditorControlTests` (the real hex editor control against a golden file), `TerminalSnapshots` (the real terminal control against a golden file). They compile all non-GUI code including `Controls/` (`Compile Include="../../**/*.cs"`
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
            └─ TerminalEmulatorBase   the view of a TerminalScreen (Terminal/Emulation): forwards the cursor, the buffers, tab stops, scroll region, writing
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
| 1A | Editor logic out of WinForms | In progress: A1 (types, boundary check), A2 (`TextEditing/`) and A3a (keys in `CodeEditing/`) A3b (folding, inline messages), A3c (`Completion/`) and A3d (line editing) done; A4 (`RemoteFileSession`) started: its Mono safety net is in (A4a), the server probes (A4b: `RemoteFileServer`) the drafts (A4c: `RemoteFileDrafts`), the loading thread (A4d: `RemoteFileLoader`) the server side of a save (A4e: `RemoteFileSaver`) and the taking of the bytes to save (A4f: `RemoteFileSession`) are out (the save flow with its threads, timers and questions stays in the view); A5 (`TextSearcher` on `ISearchTarget`, the status bar on `IEditorStatusSource`) and A6 (hex editor: the net, `HexEditingModel`; the value decoding of the data inspector was already `HexValueFormats`) done; A7 (`RemoteFileEditor.Syntax.cs` → `SyntaxChoice`) done: stage 1A is finished (what is left in `RemoteFileEditor` is view orchestration, see "A4 ends here"), next 1B |
| 1B | Terminal emulator core out of WinForms | In progress: B1 (the golden snapshots of the terminal control, `tests/TerminalSnapshots`) and B2 (the types of the screen that need no control: `EraseMode`, `TabStops`, `ScrollRegion`, the link types, `LinkTracker`) and B3a (`CellStyle`, its override colour an `RgbColor`) and B3b (`LineInfo`, `LineInfoStore`, `ScreenBuffer`) done; B4 (the VT parser, the screen operations, the input encoder) started: B4a (the helpers of the parser that need no screen) and B4b (the mode state: `TerminalModes`) done; B4c in steps: B4c-1 (the CSI commands, the modes and the OSC strings: `TerminalEmulator`, behind `ITerminalScreen` and `ITerminalHost`) done, B4c-2 (the escape sequence scanner) done, B4c-3 (the output loop) done, B4d (the mouse net, `TerminalInputEncoder`) done: the parser and the mouse reports are out; B5 (the screen: `TerminalScreen`, behind `ITerminalScreenView`) done: the model of stage 1B is finished (what is left in the controls is the view, the byte mode, the keys, the paste, the mouse events and the output queue); next stage 1C |
| 1C | SSH/serial/device session controls out of WinForms | In progress: C1 (the four files of the boundary allowlist: the completion of a command line, its popup request, the run of a command, the shell analyzer; the allowlist is empty) and C2 (the command history of the serial terminal: `LineHistory`) and C3 (what the local completion of the serial terminal offers: `LocalCompletionItems`) done; next the rest of the line input of the serial terminal (the input line, the completion session, the link scanner), the Smart Terminal command line, the `DeviceTerminalControl` partials |
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

Done in A4d: the loading thread of the editor (does the file exist, the write permission and size questions, the download and its preview) is
`CodeEditing/RemoteFileLoader.cs`. It keeps the flags the user's answers set (`IsNewFile`, `IsReadOnlyMode`, `IsPreview`, `SudoOnSave`; the
editor's properties read and write them there) and reaches the world through three seams from real call sites: `IUserPrompt` (the question asked
from the thread, `Controls/ControlUserPrompt`), `IUiDispatcher` (`Post`, `Controls/ControlUiDispatcher`, which also waits for the window handle) and
`IRemoteFileLoadView` (reload or not, tab closed, and what the thread tells the editor: preview, finish, cancelled, failed; the editor implements it
explicitly). `tests/RemoteFileLoaderTests` (net8.0) drives it with a fake connection, user and UI thread, including the questions that the Mono
stand cannot ask (no write permission, large files). `FinishLoading` and `ApplyContent` stay in the editor for now: they work on the document, the
caret, the hex view and the folds. Next, A4e: the save pipeline (`PrepareSave`, `UploadAndReplace`, validation, sudo, `CompleteSave`).

Done in A4e: what a save does on the server is `CodeEditing/RemoteFileSaver.cs` (upload of the temporary copy, the check of the file, the replace script and
its failures, the replacement through sudo, the clean-up, the reload command of a service) with `CodeEditing/RemoteSaveJob.cs` (the old private `SaveJob`).
It reaches the connection through `IRemoteFileWriteHost : IRemoteFileHost` (upload, `RunCommand` with status and timeout, the SFTP write of a Windows
host, the sudo channel with its password prompt; `Controls/SessionRemoteFileHost` implements it, and the static `RemoteFileEditor.SudoConfirmationPrompt`
stays in the view). The questions to the user (sudo? overwrite? CRLF? BOM? reload?), the threads and what a save does to the text and the hex view stay
in the editor. `tests/RemoteFileSaverTests` (net8.0) drives it with a fake connection, including sudo, which the Mono stand cannot do. Next, A4f: the
parts of `PrepareSave`/`CompleteSave`/`Save` that need no control (the encoding of the bytes, the decision of the questions, the state flags of a save).

Done in A4f: `CodeEditing/RemoteFileSession.cs` is the start of the session object: it holds the state of the saves that the editor kept in fields (the
answers to the CRLF and BOM questions, `BackupDone`, `ForceOverwrite`, `SkipValidation`, the save generation, the bytes kept from the hex view and the text they were
shown as) and `PrepareSave` (the text or the hex bytes, the two questions, the backup, the check of the file, the job). The editor gives it what it
needs through `IRemoteSaveSource` (implemented explicitly: the text, the hex view, the two changes to the text, `Confirm`, the syntax) and
`RemoteSaveOptions` (the settings and `IsNewFile`/`IsWindowsHost`, read after the questions as the old code did). `tests/RemoteFileSessionTests` (net8.0)
drives it with a fake editor. Next, A4g: the flow around it (`Save`, `SaveInBackground`, `FinishBackgroundSave`, `CompleteSave`, the reload and the encodings), which
moves the rest of the fields (`_loaded`, `_reloading`, `_rawBytes`, `_remoteSignature`, `IsSaving`, `_pendingReload`, `_saveRequestedWhileSaving`).

A4 ends here: what is left in `RemoteFileEditor` is the orchestration that the rules keep in the view (the threads of `Save` and `SaveInBackground`, `FinishBackgroundSave`,
the timers, the questions with their icons, the hex view switching, `FinishLoading`/`ApplyContent` on the document, the caret and the folds). The state it still keeps in
fields (`_loaded`, `_reloading`, `_rawBytes`, `_remoteSignature`, `IsSaving`, `_pendingReload`, `_saveRequestedWhileSaving`) can move to `RemoteFileSession` when the WPF/AvalonEdit editor of stage 3 needs it.

Done in A5a: `TextSearcher` (the WPF view model of the search bar) works on `TextEditing/ISearchTarget` instead of `TextEditorControl`: the selection, `Find`,
`ReplaceText`, `ReplaceAll`, the highlight of the matches and of the range the search is limited to, `GetLineText`/`GetRangeText`, and `RangeChanged` as a
`TextRangeChangedHandler` without the control in its arguments. `EditableTextBrowser` implements it (the event and the two text getters explicitly; the rest are the
members it already had). `FindSelectMode` moved from `ReadonlyTextBrowser` to `TextEditing/`. `RemoteFileEditorIntegrationTests` has a scenario that drives the real control
through the interface (the `search-target` scenario). `IByteSearchTarget` (the HEX terminal) was already such a seam. Next: the status bar (`RemoteFileEditorStatusBar` reads
about 35 members of the editor and the hex view: its seam, `IEditorStatusSource`, is designed together with the hex editor model of A6).

Done in A6a (before any move of the hex editor's logic): `tests/HexEditorControlTests` (net48, Mono + xvfb) drives the real `HexEditorControl` with a seeded random script of keys
(`OnKeyDown`/`OnKeyPress` by reflection: the control is sealed) and calls (typing in both columns, arrows with and without Shift, Backspace/Delete/Insert, fill, insert pattern, swap,
write value, search both ways, highlights, undo/redo, view options, read-only) and compares what it does after each of 3608 steps (2204 of the first scripts, then 1404 of two that search again from a caret on a match; caret, selection, mode, area, bytes, undo history,
answers, `StateChanged` and `SearchFinished` events) with `HexEditorControlGolden.txt`, which the code before the move wrote (`--record <file>`; the 1404 steps added in A6b were written by that same code, on a worktree of the commit before the move, and the first 2204 lines did not change). Not in the script:
the mouse, the clipboard and the page keys (they need the layout or the system clipboard). A hook, `HexEditorControl.AsciiAreaActive` (internal), puts the caret in the ASCII column
without a mouse. It must keep passing unchanged while the editing logic moves to `TextEditing/HexEditingModel.cs`.

Done in A6b: the state and the editing of the hex editor are `TextEditing/HexEditingModel.cs`: the caret, the anchor, the nibble, the column (`HexEditArea`), the typing mode,
read-only, big endian, bytes per row, the last search, what the keys and the commands do to the bytes (typing in both columns with the UTF-8 surrogate pairs, Backspace and
Delete, `InsertBytes`, fill, insert pattern, swap byte order, write value, undo and redo, go to, search, go to the next colour block, the movements by key). The control keeps
the painting, the scrolling and the layout, the mouse (it sets `Anchor`, `CaretOffset`, `Area`, `Nibble` on the model and calls `AfterCaretChange`), the clipboard and its dialogs,
the timers, the tool tip, the view options and the events. The model raises `Changed(HexChangeEffects)` where the old code called `AfterContentChange` / `AfterCaretChange`
(or did the same by hand): `ShowCaret`, `RestartCaretBlink`, `EnsureCaretVisible`; the view does those, then always redraws and raises `StateChanged`. `HexEditorControlTests` (3608
steps, the first 2204 written by the code before the move, the last 1404 by that code too with a script that searches again from a caret on a match) passes unchanged; the
net8.0 `HexEditingModelTests` check the model and what it tells the view without Mono. Not covered by either: the mouse, the clipboard and the page keys.

Done in A5b: the status bar below the remote file editor (`GUI/Backend/RemoteFileEditorStatusBar`, WPF) reads `CodeEditing/IEditorStatusSource` (the file, saved or not, the caret,
the selection with the text and line breaks of its lines, encoding, line endings, syntax, the hex view as `IHexViewStatus`, `StateChanged`/`CurrentLanguageChanged`, and the commands its menus
run) instead of `RemoteFileEditor`, `RemoteFileEditorPanel` and `HexEditorControl`. `Controls/RemoteFileEditorStatusSource` is the adapter (every member is the editor's own; `StateChanged` is
the panel's event, handed over as two delegates so that it can be tested under Mono, where the WPF-based panel cannot be made); `HexEditorControl` implements `IHexViewStatus`;
`TextEditing/HexViewChoices` holds the row widths and group sizes the menu lists. `RemoteFileEditorIntegrationTests` has a `status-source` scenario on the real editor.

Done in A6c: nothing to move. The data inspector (`Controls/HexDataInspector`) already read its values through `HexValueFormats` (`TryRead`, `GetName`, `UsesByteOrder`, `InspectorTypes`,
`TryParse`) in `TextEditing/`, and writes them through `HexEditorControl.WriteValue`, which is the model's; what is left in it is painting, the edit box and the clicks.

Done in A7: the state and the rules of the syntax of a file in the remote editor (the choice of the user, the detection from the name and the first line, the syntax applied) are
`CodeEditing/SyntaxChoice.cs`; `RemoteFileEditor.Syntax.cs` keeps the events, the completion language and the comment style (the choice is a field initializer: the base class may ask for the comment
style before the constructor runs). `tests/SyntaxChoiceTests` (net8.0) check it; the `status-source` scenario of the Mono stand drives it through the editor.
End of stage 1A: the allowlist of the boundary check holds 5 files, all about the terminal controls (`Terminal/CompletionPopupRequest`, `Terminal/CompletionSession`,
`Completion/ShellCompletionAnalyzer`, `Terminal/CommandRun`, `Terminal/OutputLinkScanner`: they belong to 1B/1C); nothing of the editor is on it.

Done in B1 (before any move of the terminal's logic, no change in `Controls/`): `tests/TerminalSnapshots` (net48, Mono + xvfb) drives the real `TerminalEmulatorControl` (a subclass that
counts `OnBell`) in an 80 x 24 form (the form is sized until the terminal has that size, whatever the fonts are) with 146 scripted outputs and 360 generated from a seeded random script, each fed in
the chunks given and, for the hand-written ones, also one character at a time (the screen must come out the same). It writes down what can be seen from outside: the lines of the active buffer with the style of
every cell (colours, bold, inverse, underline, the link target and its text, the severity of a log line), the cursor, the active buffer, the title and its events, the mouse mode, the directory the shell reported, the
bells, the exceptions the output made (the type only), the bytes the terminal sends back (the replies to the host, 20 keys and 6 characters typed, three pastes, `TypeText`, with the questions that the paste asked caught
through `ThemedPasteConfirm.ConfirmImpl` and `ThemedMessageBox.ShowImpl`) and what a click on each file link opens. `TerminalSnapshotsGolden.txt` (6483 lines: full dumps of the hand-written cases, one line with a hash for each generated one) was written by the code
before the move (`--record <file>`) and is never re-recorded; a difference means the move changed the behaviour. The accessors of the test (`ActiveBuffer`, `Lines.GetLine`, `LineInfo.Cells`, `OnBell`) are the only
things that may follow a rename. It was checked by mutation: 25 changes of the terminal code (a CSI handler, a mode, a colour code, the OSC titles, the replies, the link rules, the paste markers) each make it fail (two of them first passed and led to new cases). Not covered: the mouse
(reports and selection), painting, scrolling by the view, resize of a terminal with content, and the Smart Terminal and device line input of `SshTerminalControl` / `SerialTerminalControl`. A bug it found, kept as it is (the golden has it): a CSI `H` or `f` with more than two
items, one of which past the second is empty or not a number (`ESC [ ; ; ; ; H`), throws `IndexOutOfRangeException` in the parameter parsing (`CsiParameters.Parse` since B4a; it was `ParseParameters`).
(An earlier version of this note gave `ESC [ 1;2;3 H` as the example: that one does not throw, all its items are numbers.)

Done in B2: the nested types of `TerminalEmulatorBase` that need nothing of the control are top-level types in `Terminal/Emulation/` (namespace `SmarTTY.Terminal.Emulation`, code as it was, nested `private` became
`internal`): `EraseMode`, `TabStops`, `ScrollRegion`, `LinkTracker`, and in `LinkTypes.cs` the seam of the link scanners (`ILinkTarget`, `LinkCell`, `ILinkScanner`, `IColumnAwareLinkScanner`). The
controls (`TerminalEmulatorBase`, `TerminalEmulatorControl`, `SshTerminalControl`, `SerialTerminalControl`) and `Terminal/CommandRun` use them through `using SmarTTY.Terminal.Emulation;`. `Terminal/OutputLinkScanner`
no longer knows `TerminalEmulatorBase` and left the allowlist (4 files now); `tests/ModelBuildTests` compiles it (with `Utilities/UrlLinks.cs` added to its dependencies), and `tests/OutputLinkTests` links the real `LinkTypes.cs`
instead of its stub of the three nested types (246 checks before and after). `TerminalSnapshots` passes unchanged. `CellStyle` (it holds a `Brush` override and a `LinkCell`), `LineInfo`, `LineInfoStore` and `ScreenBuffer` use the control
(`ToDocumentPosition`, the visible columns, `Point`) and are B3.

Done in B3a: `CellStyle` (with its nested `ForegroundOverride`) is a top-level struct in `Terminal/Emulation/CellStyle.cs`. The foreground override of a cell holds an `RgbColor?` (null: no colour, as the old null `Brush`) instead of a
`Brush`; `TerminalEmulatorBase.GetOverrideBrush` makes and caches the brush when it paints, and `HexTerminalControl.EchoColor` (an `RgbColor?`, set from `Colors.Echo` with `ToRgb()` in `DeviceTerminalControl`) replaces `EchoBrush`.
Before the change `tests/TerminalSnapshots` got a second golden, `TerminalSpansGolden.txt` (1261 lines): the brushes of the colour spans of every line as the view asks for them (`GetLineColorSpans`: the palette, bold, inverse, 256 and true colours, the colour of a
log level or a diagnostic, the underline of a link), for all the cases of the first golden and five that use the override. It was written by the code before the change, on a worktree of that commit, and is never re-recorded either. The state golden did not
change. Three changes of the painting code (the alpha of the override brush, the palette entry of a warning, the override under inverse) each make it fail. Not covered: the HEX terminal itself (the echo colour reaches the cells through the override only); it needs a check on Windows.

Done in B3b: `LineInfo`, `LineInfoStore` and `ScreenBuffer` are top-level public classes in `Terminal/Emulation/` (public because `TerminalEmulatorBase.ActiveBuffer` is protected). The store no longer holds the terminal: it asks
`ILineInfoHost` (the position in the text of a screen cell, which counts the rows scrolled off the screen of the active buffer; `AlwaysTrackLines`; `HideMargins`; `VisibleColumns`), which `TerminalEmulatorBase` implements explicitly; `Point` became `GridPoint` in
`DeleteCells`, `InsertCells` and `ScreenBuffer.Cursor` (the control converts with `ToGridPoint`/`ToPoint`). The edit and the move are separate commits; both goldens of `TerminalSnapshots` pass unchanged after each. `tests/LineInfoStoreTests` (net8.0, a fake host, 47
checks) covers what the goldens cannot reach, such as the lines kept when margins are shown (`HideMargins` false changes the width of the screen, so a Mono check of it would depend on the fonts). Five of six mutations of the store each made a check fail (the sixth made the run crash). `TimestampMargin` stays
in `TerminalEmulatorBase` (it paints). What is left of `TerminalEmulatorBase` for the model is the screen operations and the cursor (`CursorPosition`, `WriteAt`, `ClearLineFrom`, scrolling), which are B4 together with the VT parser.

Done in B4a: three helpers of the escape sequence parser, which need no screen, are in `Terminal/Emulation/`: `SgrStyle.Apply(ref CellStyle, attributes)` (what the SGR attributes do to the style: the colours, bright and extended colours, bold, underline, inverse and the
resets; it was `TerminalEmulatorControl.ApplySgr` and `EncodeExtendedColor`), `CsiParameters.Parse` (the numbers of a CSI sequence with their defaults; it was `ParseParameters`, with its fault) and `OscString` (`TryParse` splits "command;text", `TitleText` takes the control characters out of a title; it was the
head and the tail of `ExecuteOsc`). The code is as it was; the control calls them. `tests/TerminalHelpersTests` (net8.0, 58 checks) covers them directly; both goldens of `TerminalSnapshots` pass unchanged. B4 is split because the rest of the parser (`ParseEscapeSequence`,
`ExecuteCsi`, `SetMode`, `ProcessOutput`) works on the screen and on the mode fields: B4b moves the mode state (`PrivateModeTable`, `DecCharsets`, the keypad and cursor key flags, the saved cursor and style, the incomplete escape) into a model class, B4c puts the screen operations
behind an interface and moves the parser and `CsiRepeatLimitTests`/`PrivateModeTableTests` (which read the source text of `ExecuteCsi`) with it, B4d is the input encoder (keys, mouse, paste to bytes).

Done in B4b: the state that the escape sequences set and that is not the screen is `Terminal/Emulation/TerminalModes.cs` (internal): the character sets (`DecCharsets`), the private modes (`PrivateModeTable`), the application keypad and cursor keys, the cursor and style
that ESC 7 / CSI s / CSI ? 1048 h saved (`SavedCursor` as a `GridPoint`, converted in `SaveCursor`/`RestoreCursor`), and the start of an escape sequence that has not arrived whole (`IncompleteEscape`). `Reset()` is the part of `ResetModes` that is not about the screen (the keys, the private modes,
the saved cursor and style, the character sets; not `IncompleteEscape`, which the full reset clears itself). The control holds one `_modes`; `_privateModes` and `_charsets` stay as read-only aliases into it so that the code that uses them reads as before. Left in the control on purpose: the mouse mode (its setter changes the
cursor and the context menu of the control), the tab width and the insert mode and the auto-wrap flag (they are in `TerminalEmulatorBase` with the screen: B4c). `PrivateModeTableTests` reads the source of `TerminalModes.cs` too, because the clearing of the table moved there. `TerminalHelpersTests` has 10 more checks of `TerminalModes`; both goldens of `TerminalSnapshots` pass unchanged.

Done in B4c-1 (B4c moves the parser bottom-up, so that each step builds): `Terminal/Emulation/TerminalEmulator.cs` (internal) holds what the CSI commands, the modes and the OSC strings do: `ExecuteCsi` (the cursor, erasing, inserting, scrolling and the region, the tabs, the colours, the replies,
the modes and their saving), `SetMode` (the cursor keys, the caret, the three alternate screen modes, 1048, the mouse modes, the insert mode), `ExecuteOsc` (title, directory), `ReportStatus` and `SaveCursor`/`RestoreCursor`; the code is as it was, with `_screen.`/`_host.` where it called the control and `GridPoint` where it used `Point`. It works through two
interfaces made of the calls it made: `ITerminalScreen` (the cursor, size, tab stops, style, active buffer, scroll region, and the operations that change cells) and `ITerminalHost` (the replies, the tab width, byte mode, the mouse mode, the title and the directory), both implemented
explicitly by `TerminalEmulatorControl` in `Controls/TerminalEmulatorControl.Screen.cs`; `IEscapeTrace` is what the Debug trace of an escape sequence is written to (`EscapeTrace` implements it; Release builds pass null). `MouseReportingMode` is a top-level enum of the model now (the two places that
named it as `TerminalEmulatorControl.MouseReportingMode` changed). The control still scans the escape sequences and writes the text (B4c-2, B4c-3) and calls `_emulator`. `CsiRepeatLimitTests` and `PrivateModeTableTests` read the source of `TerminalEmulator.cs` now (and of the control and `TerminalModes.cs` where the code stayed). `tests/TerminalEmulatorTests` (net8.0, 96 checks, a fake
screen and host) says directly what each command does; both goldens of `TerminalSnapshots` pass unchanged.

Done in B4c-2: the scanner of the escape sequences is in `TerminalEmulator` too: `ParseEscapeSequence` (what an ESC at a position is: a CSI sequence, an OSC/DCS/PM/APC/SOS string, a character set designation, ESC = > 7 8 H c D M E, and how many characters it takes, with the rules for a sequence that has not arrived whole, one that is cut short by a control character
and one that grows too long), `ParseControlString`, `KeepIncompleteEscape`, `ExecuteCsiSequence` (the private prefix and the intermediate bytes, soft reset), `SoftReset` and `ResetModes` (the control's full `Reset()` calls `_emulator.ResetModes()`); the code is as it was. The interfaces got what these call: `ITerminalScreen.HasScrollRegion` and `ScrollBottom`, `ITerminalHost.ResetByteState` and `ResetFromStream` (ESC c),
`IEscapeTrace.SetPosition`; `MaxPendingEscapeLength` moved with them. `TerminalEmulatorTests` has the checks of the scanner (163 in all); both goldens of `TerminalSnapshots` pass unchanged. What is left of the parser in the control is `ProcessOutput` (the chunking, the control characters, the text) and the writing of the text with the byte mode: B4c-3.

Done in B4c-3: the output loop is `TerminalEmulator.ProcessOutput` (the kept start of a sequence put before the chunk, the `MaxChunkLength` cut with its "skipped" line, the batch of changes, the segments of text between the control characters, CR LF TAB BEL SO SI BS, the escape sequences through `ParseEscapeSequence`, an unknown control character shown as its number when asked); the control's `ProcessOutput` is
`OutputReceived?.Invoke(output); _emulator.ProcessOutput(output);`. The text itself is written by the control behind `ITerminalHost.WriteOutputText` (it is where the byte mode of the serial terminal lives: the stray bytes of a letter put together, the half-crossed cell, `ByteColumns`; that mode, with its prediction for the keys, stays in the control for stage 1, and `ITerminalHost` is its seam).
The interfaces got what the loop calls: `ITerminalScreen.BeginBatch`/`EndBatch`/`ResetLinkTracking`/`WriteText`, `ITerminalHost.MaxChunkLength`/`ShowUnrecognizedAsHex`/`WriteOutputText`/`Bell`/`Trace`/`BeginEscapeTrace`, `MoveBackBytes` returns the bytes left; `IEscapeTrace` is `IDisposable` (the Debug trace of a sequence ends with it; Release builds get null from `BeginEscapeTrace`). One thing is not as it was: `Trace` of the control characters is a call through
the host interface now (it was a call that a Release build removed); it does nothing in Release. `TerminalEmulatorTests` has 37 more checks of the loop (200 in all), among them that the batch is ended when the known fault throws; both goldens of `TerminalSnapshots` pass unchanged. What is left in `TerminalEmulatorControl` is the view: the byte mode, the keys, the mouse, the paste, the output queue and its timer, the log, the menus.

Done in B4d: first a third golden for `tests/TerminalSnapshots`, `TerminalMouseGolden.txt` (924 lines, written by the code before the move, never re-recorded): the control is laid out at 80 x 24 with the screen full of text, and the mouse events (`OnMouseDown`/`OnMouseUp`/`OnMouseMove`/`OnMouseWheel` by reflection, at the pixel of a cell found with `HitTestCell`) are given in every reporting mode (off, X10, normal, highlight, button-event, any-event), with and without the SGR
encoding, for the three buttons and one that is not reported, the wheel up, down and 0, and motion with and without a button (twice at the same cell), at four cells and outside the text area; what is written down is the bytes sent. Not in it: the modifier keys (`Control.ModifierKeys` cannot be set from a test) and the partial cell at the edge of the text area (it depends on the fonts). Seven mutations of the
mouse code were tried: six make it fail. Then `Terminal/Emulation/TerminalInputEncoder.cs` took what the control decided about the mouse: which events are reported in which mode, the button codes, the motion bits, the wheel codes, the one-report-per-cell rule for motion (its `_lastMouseCell`, reset when the mouse mode changes) and the clamping of the cell to the screen; the control maps `MouseButtons` to the model's `TerminalMouseButton`, passes the modifier bits (it reads
the keyboard), the cell (`HitTestCell`), the size and the SGR mode, and sends the bytes it gets (`InputKind.MouseReport`). `TerminalEmulatorTests` has 40 more checks of the encoder, among them the clamping at the edge. The keys stay in the control for stage 1: they are mapped by `VisualTTY/XTermKeyMap` and its XML layout, which use `Keys` and are frozen, and `XtermProtocol.ApplyKeyModifiers` was already in the model; the paste goes through `PasteGuard` and its dialogs,
the byte-mode repeat of Left/Right/Delete through the prediction, so those are view code. What stage 1B still has: the screen operations that `ITerminalScreen` forwards to `TerminalEmulatorBase` (see the status row).

Done in B5: the screen is `Terminal/Emulation/TerminalScreen.cs` (internal; it implements `ITerminalScreen` itself, so `TerminalEmulator` works on it and no longer on the control): the two buffers and the active one, the cursor (a `GridPoint`, brought back to the screen when set), the batch that draws once, the style of the next text, the tab stops, auto-wrap, the insert mode, the scroll region, the link tracker, and the operations that were in `TerminalEmulatorBase` (move the cursor with the scroll, write with the wrap and the scroll, erase, insert and delete characters, insert blank lines, scroll the region, switch the buffer, blank the alternate screen, trim the scrollback, resize, clear the output, reset, archive the screen for a new session); the code is as it was, with `GridPoint` for `Point` and `_view.` where it called the control. The control is reached through `ITerminalScreenView` (the 19 members the operations needed: the size of the view, the document, the top line, the selection, the caret, `Redraw`, `UpdateLayout`, `OnTextReplaced`, `UpdateMaxLineWidth`, `ForceCaretUpdate`, `CreateLinkScanner`, the Debug trace), implemented explicitly by `TerminalEmulatorBase` in `Controls/TerminalEmulatorBase.Screen.cs`; `TerminalEmulatorBase` keeps the public members it had (`CursorPosition`, `ActiveBufferIndex`, `CurrentStyle`, `Tabs`, `AutoWrap`, `ScrollbackLimit`, `MoveCursorBy`, `WriteText`, `GetLineText`, `Reset`, `ClearOutput`, `ArchiveScreen`, `ActiveBuffer`) as one-line forwarders; `TerminalEmulatorControl` gets the screen as `Screen` for `WriteAt` and `ClearScrollRegion`. The buffers are made at the end of the constructor, as before (`ActiveBuffer` is null while the control is being built; the base class constructors may call `OnResize`). The Debug trace of the written text goes through `ITerminalScreenView.TraceEnabled` (false in a Release build, so no text is made for it). Public methods that nobody called outside the emulator (`BeginBatch`, `EndBatch`, `EraseInDisplay`, `EraseInLine`, `InsertChars`, `DeleteChars`, `SetScrollRegion`, `ClearScrollRegion`, `HasScrollRegion` and the protected `InvalidateView`, `MoveCursorTo`, `ClearLineFrom`, `InsertBlankLines`, `ScrollRegionUp`, `ClearActiveScreen`, `RepeatPreviousChar`, `WriteAt(Point...)`) are gone from the control; the screen has them. `tests/TerminalScreenTests` (net8.0, a fake view, 107 checks) drives the screen directly; all three goldens of `TerminalSnapshots` pass unchanged.

Done in C1: the four files that were on the boundary allowlist no longer know the controls, and the allowlist is empty. `Terminal/ISmartTerminalHost` (public) is the Smart Terminal tab as the completion of a command line sees it, made of the calls `CompletionSession` and `ShellCompletionAnalyzer` made on `SshTerminalControl`: the current and home directories, whether the tab has an environment and its names, the SSH session (read each time, a reconnect replaces it), the shell analyzer, `BuildRemoteCommand`, and `TryBeginInvoke` (the old `PostToUi` guard: not queued when the control is disposed or has no handle; the `InvalidOperationException` of a window that goes away is still caught by the session). `Terminal/ICommandRunHost` (in `CommandRun.cs`, internal) is `IOutputLinkHost` plus `CompilerColumns` (what `VtSettings?.CompilerColumns ?? Auto` was). `SshTerminalControl` implements them (the new members explicitly, the others are the members it had). `CompletionPopupRequest.ScreenLocation` is a `GridPoint` (a pixel place: `X` to the right, `Y` down; the three places that make it convert with `ToGridPoint`, the WPF popup reads `X`/`Y` as before). `tests/ModelBuildTests` compiles `CommandRun` and `CompletionPopupRequest` now; `CompletionSession`, `ISmartTerminalHost` and `ShellCompletionAnalyzer` stay out of it, because they need `SshSession` (SSH.NET), and the analyzer also the settings and the completion database (the cause is written in its csproj). `tests/OutputLinkTests` has 9 checks of `CommandRun`; `SmartTerminalIntegrationTests` (real completion and command runs on OpenSSH and Dropbear, 187 checks) and the three goldens of `TerminalSnapshots` pass unchanged.

Done in C2: first a net for what the Mono stand did not reach: `tests/DeviceLineEditorTests` (the real COM/TCP control against a device on TCP) has 8 more steps for the Ctrl+R search (the newest command that contains the typed text, the next press, round to the newest, Esc brings the typed text back, a text no command contains, the case is ignored, Up after a search), recorded on the code before the move (33 steps before, 41 after, all passing on both; four mutations of the search code each make one fail). Then the history of the typed commands of `SerialTerminalControl` is `Terminal/LineHistory.cs` (public): the list (the oldest first, `MaxSize` 1000, trimmed, a repeat of the last one is not added), `Remove`, `Clear`, `Distinct` (the window), `NewestFirst` (the copy for the grey suggestion, built again when the list changed), Up and Down by the beginning of the line (`Navigate`, `ResetNavigation`) and the Ctrl+R search (`NextSearchMatch`, `IsSearching`, `SearchOriginalInput`, `EndSearch`; the old nested `HistorySearchSession` is its private class); the code is as it was, and every place of the control that cleared the search or the navigation still does, as before. What stays in the control is what shows: the input line, `RewrapInput`, the caret, the echo to the device, and the grey suggestion (`_historySuggestion`, `_suggestionDismissedFor`, the policy that decides when it shows; the Mono stand reads `_historySuggestion` by reflection). `SshTerminalControl` has its own history code that is not moved yet. `tests/LineHistoryTests` (net8.0, 66 checks) drives the model; of 11 mutations of it 8 were caught at once; of the other three two led to the checks of the line edited at the typed entry and of Clear ending Up/Down, and one is an equivalent change (Down on a new line shows nothing either way).

Done in C3: first a net for the Tab completion of the serial terminal, which no stand drove: `tests/DeviceLineEditorTests` has a stand-in for the suggestion popup and 10 more steps on the real control (Tab on an empty line lists the saved commands, the 30 built-in commands with their signatures and descriptions, and the programs of this computer; Esc closes the list; Tab after the start of a saved command completes it; Tab after the start of a built-in command offers nothing, because the built-in commands and the programs are offered for an empty word only: recorded as it is), recorded on the code before the move (41 steps before, 51 after; five mutations of the completion code each make one fail). Then the static part of `LocalCompletionSession` is `Terminal/LocalCompletionItems.cs` (public static): the built-in commands and their documentation, the programs of PATH, the completion items of the names of a device directory (`BuildPathItems`: directories first, sorted, escaped for the shell), the directory part of a path word, the characters and the unescaped space that end a word, and the common start of two names. The code is cut and pasted; the only changed lines are the modifiers (`private` became `public static`; checked by comparing the text of the old and new members). The session keeps what has state: the token, the items, the listing generation, the popup and the answers to the terminal (`BeginInvoke`, `DeviceDirectoryLister`). `tests/ModelBuildTests` compiles the file now (with `DeviceDirectoryListing`, `DeviceLineInput` and `DeviceSessionKey` of `VisualTTY/DeviceTerminal/` added to its dependencies); `tests/LocalCompletionItemsTests` (net8.0, 50 checks) drives it.

Boundary check: `tests/check-model-boundary.sh` greps the four model folders for `System.Windows*`, `System.Drawing`,
`SmarTTY.Controls/Forms/GUI`; files of `tests/model-boundary-allowlist.txt` are the known violations (23 at the start, 15 after A2, 12 after A3a, 10 after A3b, 5 after A3c, 4 after B2, 0 after C1: the 20
of the specification plus `Completion/ShellCompletionAnalyzer`, `Terminal/CommandRun`, `Terminal/OutputLinkScanner`, which
use the terminal controls). The list only shrinks, and the script fails on a listed file that is already clean.
`tests/ModelBuildTests` (net8.0, no Windows) compiles the model folders without the listed files; a clean file that needs
listed ones is excluded in its csproj (`ITerminalSessionContext`): when a file leaves the allowlist, make sure it
is compiled there as well, and add to the dependency list there when the model starts to use new code outside its folders.

Safety nets that stage 1 adds before moving code: terminal golden snapshots captured from the old code before any
terminal move (`tests/TerminalSnapshots/`, never re-recorded); `tests/check-model-boundary.sh` with an allowlist that
only shrinks. Mono tests stay until each of their checks is ported to net8.0 tests on the model.
