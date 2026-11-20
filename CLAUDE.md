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
- Tests: 130 console projects in `tests/`, each run with `dotnet run --project tests/<Name> -c Release`. The full list
  and order are in `.github/workflows/build.yml`; run the ones that compile the files you touched
  (`grep -rln <FileName> tests/*/*.csproj`).
- Mono tests (net48, need `mono` + `xvfb`; `libmono-i18n4.0-all` for code pages): `RemoteFileEditorIntegrationTests` (the real
  remote editor on OpenSSH and Dropbear), `HexEditorControlTests` (the real hex editor control against a golden file) and
  `ModelInteropTests` (`VirtualKey` against the real `Keys`). The stands of the WinForms terminals went with them in stage 2 (see
  "Done in 2E"). They compile all non-GUI code including `Controls/` (`Compile Include="../../**/*.cs"`
  minus `GUI/**`, `App.xaml.cs`), so that code must still compile for net48: no `Range`/`Index`, `HashCode`,
  `Math.Clamp`, `ArgumentNullException.ThrowIfNull`, `string.Contains(char)` unless behind `#if NET5_0_OR_GREATER`.
  Build: `dotnet build tests/<Name> -c Release`, run: `xvfb-run -a mono tests/<Name>/bin/Release/net48/<Name>.exe`.
- `SshNetIntegrationTests` and `RemoteFileEditorIntegrationTests` need a real sshd and Dropbear
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
- `CsiRepeatLimitTests` and `PrivateModeTableTests` read the source text of `Terminal/Emulation/TerminalEmulator.cs`,
  `TerminalModes.cs` and `Terminal/Rendering/TerminalDisplayModel.cs` (`AssemblyMetadata`); `FileDialogTests`, `FolderBrowserTests` and
  `PasteProgressTests` read files of `GUI/DevicePanel/` and `GUI/TerminalView/`: update the paths if that code moves.

## Folder map

| Folder | What is there | UI? |
| --- | --- | --- |
| `App.xaml(.cs)` | Entry point, theme, wiring of WPF dialogs into WinForms code through static delegates | WPF |
| `GUI/` | WPF windows and dialogs (`MainWindow`, `ConnectionSelectionWindow`, settings, file lists) | WPF |
| `GUI/Backend/` | `MainWindowController`, tab contexts (`NormalTabContext` and `DeviceTabContext` show WPF terminals; `EditorTabContext` hosts the WinForms editor through `WinFormsHostWrapper : WindowsFormsHost`), `TextSearcher`, `RemoteFileEditorStatusBar` | WPF |
| `GUI/Styles/` | `ThemeManager`, theme palettes | WPF |
| `GUI/TerminalView/` | Stage 2: the WPF terminal of every tab (`WpfTerminal`: the classic tab and the sudo prompt; `WpfSmartTerminal`: the Smart Terminal; `WpfSerialTerminal` and `WpfHexTerminal`: the text and HEX views of a device; `TerminalRenderView`, `TerminalPanel`, `TerminalFont`, `KeyScanFlags`); `GUI/TerminalLabWindow` measures it | WPF |
| `GUI/DevicePanel/` | Stage 2D: `WpfDeviceTerminal`, the COM/TCP/telnet/netconsole tab (the port of `DeviceTerminalControl` and its partials; its tool bar and menus are still WinForms, hosted) | WPF |
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
            └─ (the WinForms terminals TerminalEmulatorBase, TerminalEmulatorControl, SshTerminalControl, SerialTerminalControl:
                removed in stage 2, see "Done in 2E")
HexEditorControl : Control                hex editing inside RemoteFileEditor
```

The terminals are WPF (`GUI/TerminalView/`): `WpfTerminal` (a `Border` with `TerminalPanel`: `TerminalRenderView` and a scroll bar) shows
a `Terminal/Rendering/TerminalDisplayModel`, which holds the emulator and the screen of `Terminal/Emulation/` (a screen is an
`EditableTextDocument` plus a `LineInfoStore`, one `CellStyle[]` per line, plus a `GridPoint` cursor); `WpfSmartTerminal`,
`WpfSerialTerminal` and the panes of `WpfHexTerminal` are `WpfTerminal`s, and `GUI/DevicePanel/WpfDeviceTerminal` is the device tab around
the last two. Bytes typed or sent back to the host leave through `WpfTerminal.DataEntered` via `TerminalEventDispatcher.PostInput`.

## Migration status (WinForms → WPF)

Goal: drop WinForms, render with WPF for better performance and scaling up to 8K. Order: 0 → 1A → 3 → 1B/1C → 2 → 4–5.

| Stage | Content | Status |
| --- | --- | --- |
| 0 | .NET 10, x64, single-file publish instead of Costura | Done in alexsys87/SmarTTY#506 |
| 1A | Editor logic out of WinForms | Done in alexsys87/SmarTTY#508–#526 (A1–A7, see the "Done in A…" notes below); what is left in `RemoteFileEditor` is view orchestration, see "A4 ends here" |
| 1B | Terminal emulator core out of WinForms | Done in alexsys87/SmarTTY#527–#537 (B1–B5, see the "Done in B…" notes below); what is left in the controls is the view, the byte mode, the keys, the paste, the mouse events and the output queue |
| 1C | SSH/serial/device session controls out of WinForms | Done for what has logic that needs no window, in alexsys87/SmarTTY#538–#552 (C1–C15, see the "Done in C…" notes below); what is left is view orchestration: the input line of `SerialTerminalControl`, the command channel and the key handling of `SshTerminalControl`, and the `DeviceTerminalControl` partials ZModem, YModem, Kermit, Tftp, GzipBase64, FileSend, FileDrop, QuickScript and SerialLines (threads, timers, dialogs and the progress windows around the protocols of `IO/`, which have their own tests). Z1: see "Stage 1 result" |
| 2 | New WPF terminal view (`GlyphRun`, row cache), device panel in XAML | In progress: 2A (the view, its model, the keyboard, the lab window), 2B (the classic SSH tab and the sudo prompt on `WpfTerminal`), 2C (the Smart Terminal on `WpfSmartTerminal`) and 2D (the device tab on `WpfDeviceTerminal` with `WpfSerialTerminal` and `WpfHexTerminal`) and 2E (the WinForms terminals and their Mono stands removed, their goldens on the display model) done, see "Stage 2". Left of the stage: the device panel's tool bar and menus are WinForms (hosted, they go with stage 4) |
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

### Stage 1 result (Z1)

Where things are. The model is `Terminal/` (with `Terminal/Emulation/`), `TextEditing/`, `CodeEditing/`, `Completion/` and `VisualTTY/DeviceTerminal/` (the state of the device panel; the settings types of `VisualTTY/` are frozen). The view is `Controls/` (with `Controls/TextView/`), `Forms/` and `GUI/`. A model class meets its control through an interface made of the calls it made (`I…Host`, `I…View`, `I…Source`) that the control implements explicitly. The border is checked by `tests/check-model-boundary.sh` (no `System.Windows*`, `System.Drawing`, `Controls/Forms/GUI` in the four model folders; the allowlist is empty and can only shrink) and by compiling the model on plain net8.0: `tests/ModelBuildTests` and the `*Tests` projects that link model files. The README is user documentation and has no architecture section, so this file is where the architecture is written down.

Size of the controls (lines, all partials, without the designer files), before stage 1 (after #507) and now:

| Control | Before | Now |
| --- | --- | --- |
| `RemoteFileEditor` | 2145 | 1471 |
| `TerminalEmulatorBase` | 1655 | 691 |
| `TerminalEmulatorControl` | 2597 | 1768 |
| `SshTerminalControl` | 2362 | 2265 |
| `SerialTerminalControl` | 2572 | 1783 |
| `DeviceTerminalControl` (13 partials) | 5627 | 5652 |
| `HexEditorControl` | 2126 | 1670 |
| `HexTerminalControl` | 712 | 580 |
| `CodeEditorPP` | 2002 | 1788 |

`DeviceTerminalControl` did not shrink: its protocol partials (ZModem, YModem, Kermit, Tftp, GzipBase64, FileSend, FileDrop, QuickScript, SerialLines) are threads, timers, dialogs and progress windows around the protocols of `IO/`, which have their own tests, and the spec says to leave a group of functions that is almost all UI as it is (the model classes of the other groups added the explicit interface members). `SshTerminalControl` keeps the command channel, the key handling and the completion popup; `SerialTerminalControl` keeps the input line, the echo, the byte mode and the keys; `TerminalEmulatorControl` the byte mode, the keys, the paste, the mouse events and the output queue; `RemoteFileEditor` the threads of a save, the questions with their icons, the hex view switching and what `FinishLoading` does to the document, the caret and the folds.

Mono checks and where their logic is tested now. They drive the real controls (key and mouse events, focus, the popup, the window, a real sshd or Dropbear) and stay on Mono until stage 2; none was deleted, and the projects stay (the stands of the terminals were removed with them in 2E, see there). What they check about the model is also checked on plain net8.0: `DeviceLineEditorTests` (92 steps) by `LineHistoryTests` (Up/Down, Ctrl+R), `HistorySuggestionStateTests`, `LocalCompletionItemsTests` and `LocalCompletionSessionTests` (Tab: commands, paths, directories), `DeviceListingSessionTests` and `DeviceDirectoryListingTests` (the listing), `DeviceCommandListsTests` and `DeviceLineInputTests` (saved history, command lists, secret prompts), `LocalLinkScannerTests` (links); `NetconsoleTerminalTests` (38) by `NetconsoleLineInputTests` and `NetconsoleTests` (decoder, settings, transport); `SmartTerminalIntegrationTests` (253 checks on two servers) by `HistoryWalkTests`, `SessionEnvironmentTests`, `InputRegionMapTests`, `LinePasteRunTests`, `HistorySuggestionStateTests`, `SuggestionLookupTests`, `SmartTerminalCommandLineTests`, `OutputLinkTests`; `RemoteFileEditorIntegrationTests` by `RemoteFileSessionTests`, `RemoteFileLoaderTests`, `RemoteFileSaverTests`, `RemoteFileServerTests`, `RemoteFileDraftsTests`, `SyntaxChoiceTests`; `HexEditorControlTests` (golden, 3608 steps) by `HexEditingModelTests`; `HexTerminalControlTests` (golden) by `HexTerminalModelTests`; `TerminalSnapshots` (three goldens, never re-recorded) by `TerminalEmulatorTests`, `TerminalScreenTests`, `TerminalHelpersTests`, `LineInfoStoreTests`; `ModelInteropTests` stays because it needs the real `Keys`. What only the real control can show stays there: the keys and the popup of the input lines, focus, the selection with the mouse and its survival of a resize, the status of a tab, the painting and everything about a real server.

Bugs found during stage 1, kept as they were while the stage only moved code, and fixed after it (the pull request after #553): `ESC [ ; ; ; ; H` threw `IndexOutOfRangeException` in `CsiParameters.Parse` (an item past the defaults that is empty or not a number is 0 now; the state and spans goldens of `TerminalSnapshots` changed in the one case that had it); the `ToArray()` copy of `SessionEnvironment` could throw while another thread changed the list (the list is replaced as a whole now), and a variable that referred to one that was set again was worked out before it and saw nothing (the settings are replayed in the order they were made and an earlier one is dropped only when nothing between it and the next setting of the same variable refers to it, so `export B=$A-two` keeps the value `A` had then, as in a shell); Tab after the start of a built-in command offered nothing (the built-in commands and the programs are put in the list for any word of the command position now, and the popup filters them); Right and Left across the end of a wrapped row of the Smart Terminal command line cost a press that changed no index (they move by one index of the text now, and so do they in the input line of the serial terminal, which had the same fault); an escape sequence typed as text in netconsole line mode was cut, the ESC to the device and the rest into the box (a sequence goes whole now: `ESC [ ...` with its final character, `ESC O` and a character, `ESC` and a printable character; an ESC before Enter stays alone); after `Clear` the views of the hex terminal kept the positions of the old selection, and the next search started from them (both are reset, with the mirroring of the selection off; the hex terminal golden changed in the selection positions after `Clear`). Not a defect, no change: the blank typed after a word of the serial terminal starts a completion that asks for a listing of its own and takes the place of a waiting request; that request belongs to a completion session that `ShowCompletions` has closed already (its answers are dropped), and the newest request winning is the rule of the one-command-at-a-time listing. End in the serial terminal put the caret at a column of the whole line on its first row (past the right edge when the line takes several rows), so Backspaces after it did not clear the line; it goes to the index of the end of the text now (`GetCaretPositionFromInputIndex`), and `DeviceLineEditorTests` has a step for it.

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

Done in C4: first a net for the scanner of the links in the text a device printed (addresses and paths with a drive letter), which no stand drove: `tests/DeviceLineEditorTests` takes the real scanner from the real `SerialTerminalControl` (`CreateLinkScanner`) and has 11 more steps (an address in a sentence covers its full stop but opens without it; one that comes in two pieces, with its query; the words left undecided at the end of the text; an address at the end of the text is live only after `Finish`; a Unix path and a bare name are no links; an address does not go on in the next line but does in the row of a wrapped line; two addresses; a quote ends one; a path with a drive letter), recorded on the code before the move (51 steps before, 62 after). Then the scanner is `Terminal/LocalLinkScanner.cs` (internal): `LocalLinkScanner`, `LocalLinkCandidate` and `LinkCandidateState`, cut and pasted; the only changed lines are the host: they asked the control for `PathExistence.Exists(path)` and `RequestOpenFile(path)`, and ask `ILocalLinkHost` (`PathExists`, `RequestOpenFile`) now, which `SerialTerminalControl` implements explicitly (`PathExistence`, the background check of the paths, stays in the control with the timer that paints again). The Mono steps cannot see a path with a drive letter open on Linux (`Path.GetFullPath` of "C:\\dir" is not a path of that kind there), so they record that such a path is a candidate that does not open. `tests/ModelBuildTests` compiles the file now (with `Utilities/LocalLinkRules.cs` added to its dependencies); `tests/LocalLinkScannerTests` (net8.0, a fake host, 91 checks) drives the scanner and the candidates directly, including what is platform-independent about drive paths (`Complete` makes them candidates).

Done in C5: the Up/Down walk of the Smart Terminal command line (`InputLine.NavigateHistory` in `SshTerminalControl`) is `Terminal/HistoryWalk.cs` (public): the line as it was typed is entry 0 and the commands follow, newest first (`buildHistory` makes a list of its own when the walk starts); the edits made on the way are kept in the entries (those of a recalled command only when the text is not empty), `StepByPrefix` picks the entry, `Reset` starts the walk again (the control calls it when the history changed); the code is as it was, `Step` returns the line to show or null and the control sets the text (with its `try`/`catch`). First a net for what `tests/SmartTerminalIntegrationTests` (the real control on OpenSSH and Dropbear) did not reach: 9 more steps for the edits made while walking (an edit of a recalled command is kept, an emptied recalled line is not, the typed line edited after Down is the one the prefix is taken from), recorded on the code before the move (187 checks before, 205 after: the steps run for both servers); two mutations of the old code (the edits kept always, never) each make one fail. `tests/HistoryWalkTests` (net8.0, 36 checks) drives the model. `InputRegion` (the mapping between the text of the command line and the positions in the document) and `LinePaste` stay in the control for now.

Done in C6: first a net for what the Smart Terminal stand did not reach: `tests/SmartTerminalIntegrationTests` has 8 more steps on the order of the exported variables (a variable that refers to an earlier one is worked out after it; one that is set again replaces the old value and goes to the end of the list, so the one that refers to it is worked out before it and sees nothing: `[three][-two]`, as the code does; unset is kept and the variable is gone from the next command; the names of the tab are those that have a value), recorded on the code before the move (205 checks before, 221 after: both servers). Then the variables of `SshTerminalControl` are `Terminal/SessionEnvironment.cs` (public): `Set` (drops an earlier setting of the name and puts the new one at the end; a null value is "unset"), `Any`, `Names` (those that have a value) and `BuildExports` (the "export A=1 && " and "unset A && " commands that `BuildRemoteCommand` puts in front of every command); the code is as it was, the control keeps `TryRunBuiltin` (which parses the line with `ShellCommandLine.TryParseVariableCommand`) and `BuildRemoteCommand`. The list is read from other threads (the completion) while the tab changes it, with a `ToArray()` copy as before: that copy is not safe against a change at the same moment (`List<T>.ToArray` can throw then), which the comment in the code does not say; it is as it was. `tests/SessionEnvironmentTests` (net8.0, 19 checks) drives it; seven mutations of it (the replacement, the order, the text of unset, the separator, the names filter, `Any`, the export format) each make a check fail. `InputRegion` and `LinePaste` stay in the control for now.

Done in C7: first a net for a command line that is longer than the screen and wraps over several rows, which the Smart Terminal stand did not drive: `tests/SmartTerminalIntegrationTests` has 9 more steps (a line of 400 characters is typed over several rows; Home and End go to the ends of the whole line; Right across the rows: the index of the text grows by one, or stays when the caret goes to the start of the next row, because the end of a row and the start of the next one are the same index, recorded as it is and checked per press, not by the number of the row, which depends on the width of the screen; a letter typed in the middle goes in at the caret; Backspace; Left back to 0), recorded on the code before the move (221 checks before, 239 after: both servers; of three mutations of the old code two make the stand fail and one survives: the end of a row in `PositionAt`, which the control uses only to set the caret). Then the text of the region, `IndexOf` and `PositionAt` of `InputRegion` in `SshTerminalControl` are `Terminal/InputRegionMap.cs` (public static: the text from a line and a column to the end of the document, the index of a position, the position of an index; they take the document and the start of the region); the code is as it was, `InputRegion` keeps the view part (the document of the control, `ReplaceText`, the caret). `tests/InputRegionMapTests` (net8.0, 26 checks) drives the mapping on documents of one row and of several. `LinePaste` stays in the control for now.

Done in C8: first a net for the paste line by line of the Smart Terminal, which no stand drove: `tests/SmartTerminalIntegrationTests` starts the run as `PasteLineByLine` does (by reflection, from the commands, because the clipboard is not used: `LinePaste`, `_linePaste` and `RunNextPastedLine` are the accessors of the test) and has 7 more steps (the commands run one after another; the paste stops at the first one that fails and says how many were not run; with the option off all run and nothing is said; a cancel while a command runs lets it go on and, when it ends, says "[paste cancelled, N command(s) not run]"; a paste of one command ends without a note), recorded on the code before the move (239 checks before, 253 after: both servers). Then the state of the paste is `Terminal/LinePasteRun.cs` (public): the pending commands, `Total`, `Started`, `StopOnError`, `Cancelled`, `IsOver` (the old test at the start of `RunNextPastedLine`), `TakeNext` (the next command and the text of the progress window, "Command 2 of 5: ..." with a command longer than 60 characters cut to 57 and "..."), `AfterCommand` (`Continue`, `EndSilently` or `EndWithNote` and the note: the old `PrintLinePasteNote`) and `Cancel`; the code is as it was. The nested `LinePaste` of the control keeps the progress window (`IPasteProgress`, a view type) and holds a `LinePasteRun`; the control keeps what shows or runs (`Submit`, the progress window, the note printed in the terminal, `ReadClipboardText` and the `PasteGuard` of the paste). `tests/LinePasteRunTests` (net8.0, 38 checks) drives it.

Done in C9: no new net was needed: `tests/DeviceLineEditorTests` (the real COM/TCP control) already drives the grey suggestion (the rest of a typed command, the newest match, Right and Ctrl+Right take it, Esc hides it, the triggers switch it off, a password prompt shows none), 62 checks before and after. The suggestion of `SerialTerminalControl` is `Terminal/HistorySuggestionState.cs` (internal): `Current` (the one that is shown), `DismissedFor` (the text Esc hid it for), `Clear`, `Dismiss`, `Compute` (the old `ComputeHistorySuggestion`: no input line, a disabled one, a selection, no columns, the trigger off, a secret prompt, an empty or dismissed text, the caret not after the last typed character, then `HistorySuggestion.Find`; the caret is a `Func<bool>` so that it is read last and only when everything else allows a suggestion, and what it throws reaches the control, which catches it as before) and `Show` (sets the shown suggestion, true when the view has to paint). The control keeps the reading of the view, `RefreshHistorySuggestion` with its `try`/`catch` and `Invalidate`, accepting the suggestion by the keys, and the painting. The one change of the stand is its accessor: it read the field `_historySuggestion` by reflection and reads `_suggestion.Current` now. `tests/HistorySuggestionStateTests` (net8.0, 32 checks) drives the state; six mutations of it (the column test, the secret prompt, the dismissed text, the caret test, the paint test of `Show`, `Clear` keeping the dismissed text) each make a check fail. The Smart Terminal keeps its own rules (C10).

Done in C10: no new net was needed: `tests/SmartTerminalIntegrationTests` (the real control on OpenSSH and Dropbear) already drives the grey suggestion of the Smart Terminal (`HistorySuggestionSteps`: the rest of a typed command, the newest match, accepting it by the keys, Esc, a command removed from the history is not suggested, no suggestion in the Ctrl+R search or with the trigger off), 253 checks before and after (both servers). The shown suggestion and the text Esc hid it for of `SshTerminalControl` are the `HistorySuggestionState` of C9 (`Current`, `DismissedFor`, `Clear`, `Dismiss`, `Show`; its `Compute` is the serial terminal's rule and the Smart Terminal does not call it: its rules read the input region and the Ctrl+R search, and stay in `ComputeHistorySuggestion`). What the control kept in seven fields is `Terminal/SuggestionLookup.cs` (internal): `GetHistory` (the history, newest first, kept until another analyzer, another version of the history of the session or another number of typed commands of the tab; it is made by the function given), `Find` (the answer for the last text and list, so that a key that only moves the caret does not search again; a list changed in place is not seen, as before) and `Invalidate` (commands were removed from the history window). The analyzer is passed as an `object` that is compared by reference (the real `ShellCompletionAnalyzer` needs SSH.NET and is not compiled by the net8.0 tests). The stand's accessor of the shown suggestion reads `_suggestion.Current` now instead of the field. `tests/SuggestionLookupTests` (net8.0, 20 checks) drives it; seven mutations of it (the three tests of when the history is made again, the list and the text tests of the last answer, and each of the two lines of `Invalidate`) each make a check fail.

Done in C11: first a net for the listing of a device directory, which no stand drove: `tests/DeviceLineEditorTests` (the real COM/TCP panel against a device on TCP) has a shell that answers the listing command (its echo, the two marker lines around the names, the prompt) and 10 more steps (a listing gives the names as the shell printed them, directories marked; the command, its output and the markers are not on the screen; the device got one command for it; a device that only echoes is given up after four seconds, the listing says "none" and its text is shown as it came; a listing whose prompt is late gives its names after half a second; a request made while a listing runs waits, a newer one replaces it and the older one is told "none" at once, what is typed meanwhile is held and goes out when the listing has finished; all requests are accepted), recorded on the code before the move (62 steps before, 72 after, stable over several runs). The run showed something the code does and that is recorded as it is: the blank typed after "echo" starts the completion of a word, which asks for a listing of its own and takes the place of the waiting request, and when the first listing has finished the waiting request is asked again while the shell has not yet printed its prompt, so it is told "none". Then the flow is `VisualTTY/DeviceTerminal/DeviceListingSession.cs` (public): `Start` (the queue of one waiting request, the token, the command, the capture, the time of the start), `Filter` (the answer taken out of the received text; the moment of the end marker is kept with its lowest bit set because 0 means "not yet"), `CheckTimeout` (4000 ms without an answer, 500 ms without the prompt after a complete answer, a changed connection), the end of a listing (the timer stops, the text of a device that did not answer is shown, what was typed goes out in order if there is a connection, the listing is told, the waiting request starts) and `HoldInput`; the code is cut and pasted, with the host behind `IDeviceListingHost` (whether a listing can start now, the current connection compared by reference, the clock, the bytes of a command line, sending, the timer, the screen) that `DeviceTerminalControl` implements explicitly in `Controls/DeviceTerminalControl.DeviceListing.cs`, which keeps the timer, the check of whether a listing can start (`CanListDeviceDirectory`: a connection, no claimed line, no hex mode, no netconsole, no paste job, a shell at its prompt) and `ListDeviceDirectory`, which the stand calls by reflection. `tests/DeviceListingSessionTests` (net8.0, a fake host with a clock, 48 checks) drives it; eleven mutations of it (both limits, the connection test, the replaced request, the held input with and without a connection, the copy of the held bytes, a waiting request that cannot start, the lowest bit of the clock, the token, the timer stop, a command that could not be sent) each make a check fail (the lowest bit first survived and led to a check). The number of test projects in the first section was out of date (97) and is 127 now.

Done in C12: first a net for the line input of a netconsole tab: `tests/NetconsoleTerminalTests` (the real panel with a UDP device, Mono + xvfb) has 19 more steps (Down below the newest line; the draft typed into the terminal is what the walk starts from and what Down brings back; Up stops at the oldest line; Escape clears the box and the walk starts from the newest line again; Backspace and 0x7f typed into the terminal take the last letter out of the box and do nothing in an empty one; a tab goes into the box; text typed after the box's text is added to it and Enter sends all of it; CR and LF in one piece of text are one line and a lone LF is a line; a control character goes to the device at once and the letters around it into the box; an escape sequence typed as text is cut, the ESC to the device and the rest into the box, recorded as it is), recorded on the code before the move (19 steps before, 38 after; five mutations of the old code each make one fail). Then the history walk and the redirect of typed text are `VisualTTY/DeviceTerminal/NetconsoleLineInput.cs` (public): `Older` and `Newer` (the place in the history, the draft that is the box as it was when the walk began; a place past the end of a history that became shorter is the end), `Reset` (after a line was sent or Esc), and `Process` (letters and tabs collected and added to the box, Enter or a lone LF sends the line, CR LF in one text is one ending, Backspace and 0x7f take back a letter of this text or else of the box, the other control characters are collected and sent to the device in one write after the whole text); the code is cut and pasted, with the box, the sending and the encoding behind `INetconsoleLineHost` that `DeviceTerminalControl` implements explicitly in `Controls/DeviceTerminalControl.Netconsole.cs`, which keeps the box, `SendNetconsoleLine`, the focus and the key handler. `tests/NetconsoleLineInputTests` (net8.0, a fake host, 37 checks) drives it; of 17 mutations 16 make a check fail (two of them first survived and led to the check of a history that grows while the walk is at its end and to counting by the exit code); the 17th, `Reset` not clearing the draft, is an equivalent change: the draft is taken again at the start of every walk before it can be shown. The first build of the move failed (the panel did not declare the interface) and the stand run after it was of the old binary: it was built and run again.

Done in C13: first a net for the command lists and the saved history of a device, which the Mono stand reached only in part (the history window, deleting, a password prompt): `tests/DeviceLineEditorTests` has a stand-in for the themed editor of a command list (`DeviceTerminalControl.CommandListEditor`) and for the question box (`ThemedMessageBox.ShowImpl`) and 14 more steps (the list of the device is saved to its file and Tab offers it after the recent commands; the list for all devices comes after it, a repeated command keeps its first place; the editor is told the titles and the description; an editor that gives nothing leaves both lists as they are; a command typed with a leading blank is sent trimmed, is in the history of the terminal and not in the saved history; a command typed normally is saved, also to the file, and Tab offers it first; what was typed at the password prompt is in neither the saved history nor the file; clearing asks first, No keeps everything, Yes clears the saved history, the history of the terminal, the file and what Tab offered from them, the lists stay; with nothing to clear the question is not asked, and with only the terminal holding a command it is), recorded on the code before the move (72 steps before, 86 after, stable over several runs; six mutations of the old code each make a step fail, two of them after a step was added for them). Then the state is `VisualTTY/DeviceTerminal/DeviceCommandLists.cs` (public): `Configure` (the key of the device and the data folder: the history, the list of the device and the list of all devices are loaded, the history of the terminal is cleared and filled from the saved one, oldest first), `Refresh` (what Tab offers: the history newest first, then the list of the device, then the shared list, a repeated command keeps its first place), `OnSubmitted` and `Remember` (a command is saved to the history and its file unless the connection has no key, the prompt asks for a secret, the text starts with a blank, is blank or repeats the last command; the netconsole input remembers its lines through `Remember`), `Delete`, `HasNothingToClear` (both histories empty), `ClearHistory`, `SaveList` and `SetList`; the code is cut and pasted, with the terminal behind `IDeviceCommandHost` that `DeviceTerminalControl` implements explicitly in `Controls/DeviceTerminalControl.LineInput.cs`, which keeps the choice of the key by the type of the connection (it reads the settings), the menus, the question and the message boxes and the editor of a list. The stand's accessor of the saved history reads `_commandLists.SavedHistory` now instead of the field `_savedHistory`. `tests/DeviceCommandListsTests` (net8.0, real files in a temporary folder, a fake host, 42 checks) drives it; sixteen mutations of it (each load, the clearing of the terminal history, the secret prompt, the key test, the write of the history after a command, the write after a delete, a delete that writes without a change, the terminal history of a delete, both tests of `HasNothingToClear` and of the clearing, which list a save and an assignment go to, and the order of what Tab offers) each make a check fail. The number of test projects in the first section is 129 now.

Done in C14: first a net for the path completion of the serial terminal, which the Mono stand did not reach: `tests/DeviceLineEditorTests` has a device that answers the listing of more directories and 6 more steps (Tab after "cd /etc/" asks the device for the directory, offers the directories only and applies the only one; the device got a listing for the shell's directory, which the blank typed after "cd" starts, and one for the directory as it was typed; Tab after "cat /etc/h" completes the only name that matches; Tab after "cat /cfg/c" completes the common start of the names and offers them; the answer of a listing that the word has moved away from is dropped), recorded on the code before the move (86 steps before, 92 after, stable over several runs; of seven mutations of the old code five make a step fail; the other two cannot be seen from the stand: the newer answer overwrites a late stale one a few milliseconds later, and applying a common start that is what is typed already changes nothing on the screen). Then the session is `Terminal/LocalCompletionSession.cs` (internal): what a completion of a command (the saved commands, and for an empty word the built-in commands and the programs of PATH), a path or a directory offers, `OnTextChanged` (the edits that end the session: outside the word, past its end, a character of the shell syntax, an unescaped blank in an unquoted word; a word that moved to another directory is listed again), the filter, the listing of the device with its generation number (a late answer, a disposed session and an older request's late update are dropped; no lister, no shell to ask: nothing is offered, not the files of this computer), `ApplyBestMatch` (the only match, or the common start with the directory in front of it and escaped) and `ApplyItem` (a directory keeps its slash unless Enter commits it); the code is cut and pasted, the terminal is behind `ILocalCompletionHost` (the suggested commands, running something on the window thread, the lister and whether it can start now) that `SerialTerminalControl` implements explicitly, and the lister's delegate `DeviceDirectoryListerHandler` is a top-level type in `SmarTTY.Terminal` now (it was nested in the control). Two things are not a plain move: the session uses the shared `SmarTTY.Terminal.CompletionTarget` (the control had a private nested enum of the same name with the same first three members, which hid it; only those three values are used), and `tests/ModelBuildTests` does not compile the file because it names `CompletionSession.AppliedEventArgs` (the cause is in its csproj). `tests/LocalCompletionSessionTests` (net8.0) compiles it with the logic folders as `ModelBuildTests` does and a stand-in `CompletionSessionStub.cs` of that nested type, with a fake host and a fake popup, 66 checks; 25 mutations of the session (the generation tests, directories only, the common start and its escaping, the tests of an edit, the characters that end a word, a new listing, the slash of a directory, Enter, a missing insert text, the filter, the commands, the exception, the close, the case of the filter, the subscription, auto-apply and the window thread) each make a check fail (one first survived and led to the check of an edit that reaches past the word). The number of test projects in the first section is 130 now.

Done in C15: first a net for the hex view of a device terminal, which no test drove: `tests/HexTerminalControlTests` (net48, Mono + xvfb) drives the real `HexTerminalControl` with scripted bytes and writes down after every step (183 lines, then 226 with the bytes) the rows of the HEX view and of the text view, the number of bytes in a row and the layout state, the match that the search selected, what both views select, the marks of the matches and `AllBytes`: the rows at 1, 4, 8 and 16 bytes in a row, bytes that come in pieces, a byte above 0x7e shown as a dot, no text view, a null buffer and counts that are 0, negative or too big, the breaks around the echo and by a pause, a layout that changes (with echo and breaks, the same number again, bytes added at another layout), the search (across rows, with and without wrap, backward, one byte on from the match, a match that does not go across a break, a match that stays selected when the layout changes) and the marks (another pattern, an empty one, more bytes, Clear). The layout is set through the maximum number of bytes in a row in a window that is wide enough, so that it does not depend on the fonts. The golden `HexTerminalControlGolden.txt` was written by the code before the move (twice, the second time with `AllBytes`, by the same code; never after the move) and gives the same lines over several runs; of 15 mutations of the old code 12 make it fail (one of them, the pause, only after the stand was changed for it: the row was full already); the other three cannot be seen from the stand (the layout made again with the same number gives the same rows, and the columns of a selection made with the mouse and the columns of the marks are not reachable through the public API) and are covered by the net8.0 tests of the model. Then the model is `TextEditing/HexTerminalModel.cs` (public): the stream (`HexStreamMap`), the chunks and the breaks (`ShouldBreak`, `AddBreak`, `AddData` with the count brought between 0 and the length of the buffer, a chunk that goes on while the kind of data is the same), `BytesPerLine`, the layout state (`BeginLayout`, `LayoutMixed`, `NeedsRelayout`, `BeginRelayout`), `FormatRows` (the rows of hex digits and of characters, the bytes in each and whether the row is full; the code of the loop of `RenderData`), the match (`SetMatch`, `ClearMatch`, `IsMatchSelected`, `GetSearchStart`) and where a byte is in the two views (`HexStartColumn`, `HexEndColumn`, `HexSelectionToText`, `TextSelectionToHex`); the control keeps the two views, the cursors, the styles, the selection, the marks, the timer of the resize and the painting, and writes the rows `FormatRows` made (the map of the bytes is made before each write, as before). The stand's accessors read `_model` now instead of the fields. `tests/HexTerminalModelTests` (net8.0, compiled like `ModelBuildTests` because `TextDocument` needs the logic folders, 82 checks) drives the model; of 45 mutations of it 41 made a check fail at once and the other four led to the cases of a non-echo chunk after a break, an invalid end of the selection in a backward search, columns that are not a multiple of three in both directions and column 0. `HexTerminalControl.cs` is 714 lines before and 580 after. The number of test projects in the first section is 132 now.

### Stage 2 (WPF terminal view)

Done in 2A (nothing in the tabs changed yet; the view is reached through Tools > WPF terminal lab):
- Model, no WPF (checked by the boundary script and `ModelBuildTests`): `Terminal/Rendering/TerminalColorScheme` is the colour rule of a cell (palette, bold,
  256/24-bit, inverse, override, severity, active link); `TerminalEmulatorBase.GetLineColorSpans` uses it too, so both views paint the same colours, and the
  spans golden of `TerminalSnapshots` passes unchanged (a mutation of the inverse rule makes it fail). `RowRunBuilder` turns a row into background
  rectangles and text runs (cells of one colour make one run, a blank never splits one) with a 64-bit signature; `FrameOutputQueue` is the output that
  waits for the next frame (any thread appends, the view takes pieces without copying the backlog again); `TerminalDisplayModel` is the emulator, the
  screen, the scroll position, the caret and the selection for a view that draws itself (it implements `ITerminalScreenView` and `ITerminalHost`; byte
  mode and the link scanners are not there yet); `TerminalBenchmarkScripts` are the generated outputs of the lab. `Terminal/Input/XtermKeyTable` reads
  `VisualTTY/XTermKeys.xml` with `VirtualKey` names, `TerminalKeyEncoder` decides KeyDown and TextInput: a dead key and a key of the IME are never
  handled on key down; Ctrl+letter by the virtual key (any layout); AltGr (Ctrl with the right Alt) is left to the layout, the left Ctrl with the left Alt
  is ESC + control character; Alt with digits of the keypad is an Alt code (its character is typed without ESC); Alt with a character is ESC + the
  system text; Ctrl+Space/2/3..8, [ \ ] - / as in xterm; Ctrl+Backspace is BS; Shift+Insert, Ctrl+Insert, Ctrl+Shift+C/V, Shift+PageUp/Down are
  actions of the view. A key the encoder sends for must be marked handled: WPF then does not translate it, so no TextInput follows.
- View (`GUI/TerminalView/`): one `DrawingVisual` per row that keeps its drawing; each frame finds, by signature and text, the visuals of the last frame
  that show the same row and moves them (scrolling moves rows, it draws none), draws only the rest and keeps at most one screen of spare visuals;
  `GlyphRun` with the advances of the grid (a fallback font for a character the font lacks); cell size and baseline in whole device pixels (DPI from
  the visual, again on `OnDpiChanged` and on load); output parsed in `CompositionTarget.Rendering` with a 12 ms budget, the handler attached only while
  there is work, a 100 ms timer parsing while the window is minimized (no frames then); the caret blinks by opacity. IME: `Native/ImeCompositionWindow`
  puts the composition and candidate windows at the caret (IMM32 calls; WPF does it only for its text boxes). The extended flag of a key is read from
  the thread's messages before WPF raises KeyDown (`KeyScanFlags`, `ComponentDispatcher.ThreadFilterMessage`).
- `TerminalLabWindow`: the scripts with frames/s, parse and build time, rows drawn and kept, budget hits and throughput; a mirror of a classic terminal
  tab (its `OutputReceived` feeds the view, what is typed goes to `TerminalEmulatorControl.SendInput`; the seed is the visible text and the cursor,
  Ctrl+L makes the program draw again); the key log; the rendering tier, `SystemParameters.IsRemoteSession`, the render mode of the process and of the
  window and a switch to `RenderMode.SoftwareOnly` for this window.
- Tests: `TerminalRenderModelTests` (112 checks), `TerminalKeyEncoderTests` (139, with the real key layout). Only the model runs in CI; the view is
  compile-checked on Linux and has to be tried on Windows.

Done in 2B: the classic terminal tab (a shell, or a command in a PTY of its own: Run, tail, the package manager) and the sudo prompt
(`GUI/SudoConfirmationWindow`) are `GUI/TerminalView/WpfTerminal` (a WPF `Border` with the panel); `CreateNewTerminal` makes it for every tab that is
not a Smart Terminal. What `TerminalEmulatorControl` did for such a tab is there or in the model: byte mode (`TerminalDisplayModel`, the code of the
control with the screen in place of the control: the stray bytes put together, the half-crossed cell, the key counts with the prediction; the view
repeats Left/Right/Delete/Backspace by `ByteKeyCount` and parses the waiting output before counting), the links (`OutputLinkScanner` with the same
options, Ctrl+click and the hand cursor in the view), the colours of a log (`HighlightLogLevels`), Clear, a new session after a reconnect, the paste
(`PasteGuard`, the upload offer, `PasteJob` in pieces with its progress window, the paste menu of Ctrl+Shift+Insert / Ctrl+Shift+V, the files of the
clipboard, the middle button and the right button as the settings say, Esc cancels), the log file, the bell, the settings of the window (font, text
colour, palette, copy on select, Alt+letter, Shift+PageUp, the wheel as arrows, the scroll bar mode, the minimalistic mode at the edges), the window
shortcuts (Ctrl+W, Ctrl+Shift+T/N, the Ctrl+C/X/V interception, Alt+Enter, Ctrl+Tab), the size freeze while the window is resized
(`SuppressTerminalResizeEvents`), the search bar (`WpfTerminal` is the `ISearchTarget`: `Terminal/Rendering/DocumentSearch`, highlights drawn by the
view) and the context menu (the same WinForms `ContextMenuStrip`, shown at the mouse; `MainWindowController.MenuTerminal` is the terminal it was
opened on). The window, the session and the menus see a tab's terminal as `Controls/ITerminalSurface` (the members they called; the WinForms
`TerminalEmulatorControl` implements it in `TerminalEmulatorControl.Surface.cs` for the Smart Terminal); `NormalTabContext.Terminal` is that interface
and `EditorControl` is null for a WPF tab. `Controls/PasteAnchor` is what a paste needs of its terminal (handle, thread, place on the screen) so that
`PasteJob`, `PasteMenu` and the progress windows work for both. The lab lost its mirror of a classic tab (the tabs are the WPF terminal now).
`TerminalRenderModelTests` has 148 checks (byte mode, log colours, links, Clear, new session, search, and a check of the source of
`WpfTerminal` for the fault below). A fault found on the first run on Windows: the session reads the `Tag` of a tab's terminal on the thread of the
channel and the window on the thread of the input dispatcher; the `Tag` of a WPF element is a dependency property that throws off the UI thread, so
no output was shown and no key reached the shell (only the caret blinked). `WpfTerminal` hides it with `public new object Tag` on a field, the
members of `ITerminalSurface` that other threads call are listed on the interface, the retry of a shell on a key press is posted to the UI thread,
and a frame of the view that throws no longer stops the next ones (`TerminalFrameStats.FrameErrors`, shown in the lab). A second one: the font was not that of
the WinForms terminal: a tab took `Settings.ConfiguredFont`, which is null when nothing is set (the WinForms terminal kept Courier New 12 points of
its designer then, the WPF one used Consolas), and bold and italic of the settings were dropped; a tab takes `Settings.EffectiveFont` now (the
setting or Courier New 12), `TerminalRenderView.SetFont` takes the weight and the style, and the default of the view is Courier New 16 pixels. Not ported, on purpose: the warning of
Ctrl+Insert (it copies and sends nothing now), the timestamps margin (never shown in a classic tab).

Done in 2C: the Smart Terminal tab is `GUI/TerminalView/WpfSmartTerminal` (a `WpfTerminal`); `SshTerminalControl` is no longer made by the
application (it stays, with `SmartTerminalIntegrationTests` on Mono, until the WinForms terminals are removed). The logic of the control is moved as it
was where it did not touch the editor: the prompt, the commands in channels of their own with the output through one queue with a 25 ms budget (now
at the background priority of the dispatcher), cd/export/unset/exit/clear/history, the history (Up/Down with `HistoryWalk`, Ctrl+R with
`HistorySearch`, Ctrl+Shift+R as a list, the window of the history), the grey suggestion (`HistorySuggestionState`, `SuggestionLookup`; drawn by the
view as ghost text after the end of the line), the completion (`CompletionSession` and the WPF popup of the window, at the caret in device pixels),
Ctrl+L/C/D/U/K/A/V/X, the paste into the command line and line by line, links by a plain click (`CommandRun.CreateScanner` through
`TerminalDisplayModel.LinkScannerFactory`), the file list (`IFileListHost`). What the editor did is `Terminal/Rendering/PromptLine` (UI-free, in
`TerminalRenderModelTests`): the prompt and the text at the end of the document, wrapped at the width of the screen and written again on every
change (`TerminalDisplayModel.ReplaceDocumentTail`), the caret (`SetPromptCaret`) and the selection, Left/Right by one index and Ctrl+Left/Right by
the words of `IsWordChar`, Home/End, Backspace/Delete with Ctrl by words, surrogate pairs whole; `BeginOutputAtEnd` starts the screen of a command
on a new line after it. `WpfTerminal` got the hooks: `OnTerminalKey` (after the window's shortcuts), `OnTerminalText`, `OnDocumentClicked`,
`OnGridResized`, `CancelActivePaste`, `EditsLocally` (the window leaves Ctrl+C/X/V to the prompt, as `CanEdit` did). The WinForms branches of
`NormalTabContext` and `MainWindowController` for SSH tabs are gone (`NormalTabContext` takes a `WpfTerminal`). Not as before: Ctrl+Z (the undo of
the editor) does nothing in the command line, a selection made with the mouse inside the command line is not replaced by typing (it is dropped and
the text goes in at the caret), Alt+letter does nothing at the prompt (it did nothing useful before: there was no channel).

Done in 2D: the device tab (COM, TCP, telnet, netconsole) is `GUI/DevicePanel/WpfDeviceTerminal` (a WPF `DockPanel`), the port of
`DeviceTerminalControl` and its 13 partials: the partial files are copies of the old ones with the terminals replaced and the control's own
calls (`BeginInvoke`, `Handle`, the owner of a dialog, `IsHandleCreated`) answered by `WpfDeviceTerminal.View.cs`; the static helpers
(`GetConnectionTitle`, `CreateTransportCallback`, `TestConnection`) and the hooks that `App` sets stay those of `DeviceTerminalControl`.
The tool bar, the netconsole bar and the context menu are the WinForms ones of the old designer (`WpfDeviceTerminal.Toolbar.cs`), the bars
hosted in a `WindowsFormsHost` strip above and below the terminals and the menu shown as a popup: they go to WPF in stage 4 with the other
menus. The "connection closed" prompt is the WPF `ConnectionClosedPopup` over the terminals (no `ElementHost` now). The text view is
`GUI/TerminalView/WpfSerialTerminal` (the port of `SerialTerminalControl`: the input line, which continues the device's own prompt on its
row through `PromptLine` with the prompt on the screen, keeping the device's colours, or follows a local prompt; the history, Ctrl+R, the
grey suggestion, the local completion, the links of a device's text with a plain click, the raw echo, the terminal size command, the text
the device prints while a line is typed, ESC c through `TerminalDisplayModel.StreamReset`); the HEX view is `GUI/TerminalView/WpfHexTerminal`
(two WPF terminal panes written row by row from `HexTerminalModel`, scrolled together and mirroring their selections through
`TerminalDisplayModel.SelectionChanged`, with the byte search, its marks, the echo colour and the layout made again after a resize). The
TFTP window and the log replay take `Controls/IDeviceConsole` and `IDeviceReplayTarget`, which both panels implement. The old
`DeviceTerminalControl`, `SerialTerminalControl` and `HexTerminalControl` stay, with `DeviceLineEditorTests`, `NetconsoleTerminalTests`
and `HexTerminalControlTests` on Mono (all passing), until they are removed. `TerminalRenderModelTests` has 204 checks (the device
prompt), `HexTerminalModelTests` 89 (the panes). Fixed in the port: the caret after a completion was put the length of the prompt too far
to the left (`OnCompletionApplied` took it off an index of the text). Not as before: the input line has the editing keys of the Smart
Terminal (Delete, Ctrl+Backspace, Ctrl+Left/Right, Shift selection); a selection made with the mouse inside it is not replaced by typing.
Manual check on Windows: a COM port or a TCP device with a shell (the prompt continued, colours kept, Tab, Up/Down, Ctrl+R, the grey
suggestion, Ctrl+C, Ctrl+L, a line longer than the screen, text printed while typing, `mc`/`vi` taking the keys, "Return to input line"),
telnet with and without the server's echo, netconsole in line and character mode, the HEX and Text + HEX views (typing into the send box,
a search for bytes, the marks, a resize), a file dropped on the terminal, XMODEM/ZMODEM, the log and its replay, the TFTP window, the
paste menu, a reconnect and the prompt over the terminal.

Done in 2E: the WinForms terminals are gone: `TerminalEmulatorBase`, `TerminalEmulatorControl` (with `.Screen`, `.Suggestion`, `.Surface`),
`SshTerminalControl`, `SerialTerminalControl`, `HexTerminalControl` and `DeviceTerminalControl` with its 13 partials, and the Mono stands that
drove them: `TerminalSnapshots`, `SmartTerminalIntegrationTests`, `DeviceLineEditorTests`, `NetconsoleTerminalTests`, `HexTerminalControlTests`.
Before the removal the stand of `TerminalSnapshots` wrote two more goldens on the WinForms control (its `--record-model` option, twice,
the same both times): `TerminalModelGolden.txt`, the screen state of every case without the probes of the keys and the pastes (the lines of the
hand-written cases are exactly those of the state golden without the probe lines, checked), and `TerminalCellColorsGolden.txt`, the colour of
every painted cell (background, and text colour and underline where they show) made from the colour spans the control painted with.
`tests/TerminalModelSnapshots` (net8.0, in CI) runs the same cases (`Cases.cs`, copied as they were, with the same seeds) on
`TerminalDisplayModel` and compares the state, the cells as `RowRunBuilder` paints them, and the mouse golden of B4d through
`TerminalInputEncoder` as `TerminalRenderView` calls it; all three pass, and a mutation of the colour rule and one of erase-in-line each make
it fail. Two things are written down instead of compared: the case "always track lines" (the display model keeps no line info for a
line without style; the WinForms view kept one for its margins), and the mouse steps at "cell 0,0", which the old stand clicked two pixels
into the padding left of the text, where the control reported nothing; they are given as no report. It found one difference, fixed: the
WPF view took a wheel step of 0 as up, the WinForms terminal as down (`Delta <= 0`). The keys and the pastes the old stand probed were the
WinForms input; the keys of the WPF view are `TerminalKeyEncoderTests`. What only the removed Mono stands checked (the keys, the popups and
the focus of the real controls, the Smart Terminal on a real sshd and Dropbear, the input line of a device over TCP and UDP) has no stand
now; the model logic under it is in the net8.0 tests listed in "Stage 1 result", and the WPF terminals are checked by hand (the lists above).
Moved out of the deleted files: `BeepMode` (`VisualTTY/BeepMode.cs`; it was the nested `TerminalEmulatorControl.BeepMode`, the settings file
holds the names of its values, which did not change), `Colorize`/`ForegroundCode`/`ResetSequence` (`Terminal/AnsiEscapeUtils`),
`GetConnectionTitle`/`CreateTransportCallback`/`TestConnection` (`Transport/DeviceConnections`), the hooks `App` sets for the device
windows (`WpfDeviceTerminal`; the WinForms `ConnectionPromptHostFactory` is gone), `DeviceFileDropProtocol` (`GUI/DevicePanel`), the rule of a
relative linked path (`Terminal/Rendering/LinkedPath`, used by `WpfTerminal`). `VisualTTY/XTermKeyMap` (the key map of the WinForms terminal)
is gone; `XtermKeyTable` reads the same `XTermKeys.xml`. `CsiRepeatLimitTests`, `PrivateModeTableTests`, `FileDialogTests`,
`FolderBrowserTests` and `PasteProgressTests` read the WPF files now.

Not in 2A–2E: the view in the other tabs (`ITabContext.EditorControl` is a `TextEditorControl` for the editor tabs), wide (East Asian) characters taking two cells (the
screen gives every UTF-16 unit one cell; a wide glyph is drawn in one), bold glyphs (neither view draws them), UI Automation. If `GlyphRun` is too
slow in the lab, the next step is Direct2D through `D3DImage` (not `HwndHost`, which brings back the airspace problems of `WindowsFormsHost`).

Manual check on Windows, for every change of the view (record the numbers of the lab in the pull request): local with a GPU; the same with
"Software rendering (this window)"; over Remote Desktop (tier 0, remote session yes); a virtual machine without 3D (Hyper-V or VirtualBox
without acceleration); 100 %, 150 % and 200 % scaling and a window moved between monitors of different DPI. On each: the five scripts (no frame
over about 30 ms of build time, rows kept above 90 % in the scrolling log, the window responsive during the colour flood); a classic tab with `htop`, `mc` (mouse), `vim` (application keys), `less`, a paste of 100 KB (progress window, Esc), Ctrl+click on a file
name and an address, the search bar, Clear, a reconnect (the old screen dimmed), BusyBox `ash` without Unicode (Cyrillic letters and Backspace); a Smart Terminal tab: the prompt and typing, a line longer than the screen,
Home/End/Ctrl+Left/Right/Shift selection, Up/Down, Ctrl+R, the grey suggestion (Right takes it, Esc hides it), Tab and the completion list (Esc,
Enter, a directory), `cd`/`export`/`clear`/`history`/`exit`, a command that runs (`top`, `vim`, Ctrl+C), the exit code note, Ctrl+L, a paste and a
paste line by line, the history window, a click on a file name in the output; the keys Ctrl+C, Ctrl+Left, Alt+F, Alt+Backspace, F1-F12, the keypad in
`vim` with `:set` in application mode, Ctrl+Space; the German layout (AltGr+Q gives @, AltGr+7 gives {, ^ then e gives the e with a circumflex,
the left Ctrl+Alt+A sends ESC ^A), the Russian layout (letters, Ctrl+C, Alt+letter), Alt+0233 on the keypad (e acute), Japanese and Chinese IME
(the composition window at the caret, Enter that ends a composition is not sent); minimise during the colour flood and restore.

Boundary check: `tests/check-model-boundary.sh` greps the four model folders for `System.Windows*`, `System.Drawing`,
`SmarTTY.Controls/Forms/GUI`; files of `tests/model-boundary-allowlist.txt` are the known violations (23 at the start, 15 after A2, 12 after A3a, 10 after A3b, 5 after A3c, 4 after B2, 0 after C1: the 20
of the specification plus `Completion/ShellCompletionAnalyzer`, `Terminal/CommandRun`, `Terminal/OutputLinkScanner`, which
use the terminal controls). The list only shrinks, and the script fails on a listed file that is already clean.
`tests/ModelBuildTests` (net8.0, no Windows) compiles the model folders without the listed files; a clean file that needs
listed ones is excluded in its csproj (`ITerminalSessionContext`): when a file leaves the allowlist, make sure it
is compiled there as well, and add to the dependency list there when the model starts to use new code outside its folders.

Safety nets that stage 1 adds before moving code: terminal golden snapshots captured from the old code before any
terminal move (`tests/TerminalSnapshots/`, never re-recorded); `tests/check-model-boundary.sh` with an allowlist that
only shrinks. Mono tests stay until each of their checks is ported to net8.0 tests on the model (or, for the terminals, until
the controls they drove are gone: 2E).
