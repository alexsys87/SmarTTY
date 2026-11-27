# CLAUDE.md

SmarTTY: a graphical SSH client and terminal for Windows (SSH, SFTP/SCP, COM/TCP/telnet device terminals, remote file
editor). C#, WPF (no WinForms since stage 5B, no `System.Drawing` since 5C). User docs (in Russian): short `README.md` plus the detailed guides in `docs/` (indexed in the README).

Read this file instead of re-exploring the repository. Update the "Migration status" and the maps below in the same
PR that changes them.

## Build and test

- Platform: `net10.0-windows`, x64, WPF (WinForms too until stage 5B; after alexsys87/SmarTTY#506, before it `main` was `net48` x86
  built with `msbuild`).
- Build (also works on Linux for compile checks thanks to `EnableWindowsTargeting`; the app runs only on Windows):
  `dotnet build SmarTTY.csproj -c Release`
- Single exe: `dotnet publish SmarTTY.csproj -c Release -p:SingleExe=true` → `bin/Release/net10.0-windows/publish/SmarTTY.exe`
- Tests: 143 console projects in `tests/`, each run with `dotnet run --project tests/<Name> -c Release`. The full list
  and order are in `.github/workflows/build.yml`; run the ones that compile the files you touched
  (`grep -rln <FileName> tests/*/*.csproj`).
- No test needs Mono any more (see "The stands off Mono"). `RemoteFileEditorIntegrationTests` (net10.0, the real remote editor on
  OpenSSH and Dropbear, on views of the test built on the models since 3E-5) compiles all non-GUI code including `Controls/`
  (`Compile Include="../../**/*.cs"` minus `GUI/**`, `App.xaml.cs` and the DPAPI protector of the drafts), so that code must build without
  Windows and WPF: no `System.Windows*` outside `GUI/` and `App.xaml.cs` (the thread of the windows is `Utilities/IUiThread`), and nothing
  that only Windows has on the paths the stand runs. `ModelInteropTests` (net8.0) checks `VirtualKey` against `KeysGolden.txt` (the WinForms
  `Keys`, recorded on Mono) and the colour numbers of the settings against the named colours of `System.Drawing.Color`.
- `SshNetIntegrationTests` and `RemoteFileEditorIntegrationTests` need a real sshd and Dropbear
  (`tests/SshNetIntegrationTests/setup-*.sh`; apt `openssh-server dropbear-bin busybox`; as root they also run in a sandbox:
  `set -a; source <(setup-sshd.sh); source <(setup-dropbear.sh); set +a`).

## Conventions

- Code comments and commit messages in English; README in Russian. Commit subjects in the imperative, as in `git log`.
- File-scoped namespaces, `_camelCase` fields, `/// <summary>` on public members. No `Nullable`, no `ImplicitUsings`.
- Tests: plain console apps with `Check`/`Equal` helpers printing `PASS:`/`FAIL:`, sources linked with
  `<Compile Include="../../X/Y.cs" Link="Y.cs" />` (model: `tests/MultiCursorTests`). No xUnit/NUnit.
  A new test project needs its own step in `build.yml`.
- Pattern for UI-free logic: model class in a logic folder + a view that wires it to its window through an interface of the
  calls it makes (example: `CodeEditing/MultiCursorSession.cs` + `GUI/TextView/WpfTextEditor` via `IMultiCursorHost`).
- Settings types in `VisualTTY/` are serialized to XML: do not change their member types or names.
- `CsiRepeatLimitTests` and `PrivateModeTableTests` read the source text of `Terminal/Emulation/TerminalEmulator.cs`,
  `TerminalModes.cs` and `Terminal/Rendering/TerminalDisplayModel.cs` (`AssemblyMetadata`); `FileDialogTests`, `FolderBrowserTests` and
  `PasteProgressTests` read files of `GUI/DevicePanel/` and `GUI/TerminalView/`, and with `PasteConfirmTests` and `SnippetEditorTests` also
  `GUI/PasteGuard.cs`, `GUI/PasteJob.cs`, `GUI/PasteMenu.cs`, `Controls/UserDialogs.cs` and `App.xaml.cs`; `IconResourceTests` reads the XAML and the
  code of `GUI/` and `Controls/`, `GUI/WpfMenus.cs` and the dictionaries of `GUI/Icons/`: update the paths if that code moves.

## Folder map

| Folder | What is there | UI? |
| --- | --- | --- |
| `App.xaml(.cs)` | Entry point, theme, wiring of the WPF dialogs into the code that cannot reference `GUI/` (`UserDialogs` and other static delegates) | WPF |
| `GUI/` | WPF windows and dialogs (`MainWindow`, `ConnectionSelectionWindow`, settings, file lists, `MessageDialog`); `WpfMenus` (icons and items of the WPF menus); `IconImages` and the `{gui:Icon ...}` extension (the icons, see "Done in 5A"); `UiTimer` (the timer of the windows); `WindowAppearance` (the title bar and the remembered size of every window) and `TerminalBackdrop` (the see-through terminal background of the main window), see "Window chrome"; the paste of the terminals (`PasteGuard`, `PasteJob`, `PasteAnchor`, `PasteMenu` and their windows) | WPF |
| `GUI/Icons/` | Stage 5A: the icons as vector images, one resource dictionary of `DrawingImage`s for each folder the PNG files were in (`Root`, `MainWindow`, `Editor`, `Menu`, `Keys`, `Transfers`, `FileTypes`; converted from SVG outside the repository) | WPF |
| `GUI/Backend/` | `MainWindowController`, tab contexts (`NormalTabContext` and `DeviceTabContext` show WPF terminals; `EditorTabContext` shows the WPF text view and the WPF hex view of a `RemoteFileEditor` and holds the commands of the tab, `Controls/RemoteFileEditorCommands`), `TextSearcher`, `RemoteFileEditorStatusBar` | WPF |
| `GUI/HexView/` | Stage 3B: the hex editor on WPF that the editor tab shows (`WpfHexEditor`, its data inspector `WpfHexInspector`, its context menu `HexViewContextMenu`) | WPF |
| `GUI/TextView/` | Stage 3D: the text view on WPF on the models of the text view, with the syntax colours, the folds, the inline messages and the completion (`WpfTextEditor`, its word list `WpfSuggestionList` and parameter hint `WpfHintView`), its context menu in the tab `TextViewContextMenu`, its thread for the editor `WpfViewDispatcher`), the text view of the editor tab since 3E-4; `MarkdownPreview`, the rendered Markdown beside the text (see "Markdown preview") | WPF |
| `GUI/Styles/` | `ThemeManager`, theme palettes | WPF |
| `GUI/TerminalView/` | Stage 2: the WPF terminal of every tab (`WpfTerminal`: the classic tab and the sudo prompt; `WpfSmartTerminal`: the Smart Terminal; `WpfSerialTerminal` and `WpfHexTerminal`: the text and HEX views of a device; `TerminalRenderView`, `TerminalPanel`, `TerminalFont`, `KeyScanFlags`) | WPF |
| `GUI/DevicePanel/` | Stage 2D: `WpfDeviceTerminal`, the COM/TCP/telnet/netconsole tab (the port of `DeviceTerminalControl` and its partials; its tool bar, netconsole bar and menus are WPF since 4A) | WPF |
| `Controls/` | The code the remote editor stand compiles that is not model: the remote file editor (not a control since 3E-4) and the commands of its tab, `UserDialogs` (the message boxes and file choosers that `App` points at the WPF windows), the interop of the model types, the interfaces of the terminal tabs; the WinForms terminals went in 2E, the text view, code editor and hex control in 3E-5, the themed facades and `Forms/` in 4B | no window of its own |
| `Terminal/` | Terminal logic: xterm protocol, decoders, paste, completion sessions, command history, links | mostly no |
| `TextEditing/` | Text and hex documents, encodings, line endings, matching, the Markdown parser of the preview (`MarkdownParser`, `MarkdownDocument`, `MarkdownLinks`, `MarkdownImages`); also carets and margins (UI) | partly |
| `CodeEditing/` | Syntax highlighting, folding, editor commands, key bindings, multi-cursor, diff | partly |
| `Completion/` | Suggestion and signature sources and controllers, API symbols | partly |
| `VisualTTY/` | Settings and their models (`GlobalSettings`, palettes, the terminal colour schemes `TerminalColorPresets`; colours as `ColorWrapper`/`RgbColor`, `FontSpec`, `ScreenRect` since 5C), `DeviceTerminal/` state and line input | no |
| `Transport/` | TCP, telnet (+TLS), serial, netconsole transports; the local console (`ConPtyTransport`, see "Local console"); file transfer manager; backup/restore; `ReachabilityChecker` (whether the port of a saved connection answers, see "Launcher") | no |
| `IO/` | Remote command and file helpers, `ls`/`df` parsers, editor drafts, transfer protocols (X/Y/ZMODEM, Kermit, gzip+base64, TFTP) | no |
| `Ssh/` | SSH.NET wrappers, SFTP/SCP, keys, port forwarding, X11; `Connection/SshConnector` | no |
| `Sync/`, `LocalRepo/`, `PackageManagers/` | Folder sync and tar, local OpenWrt repository, remote package managers | no |
| `Native/` | P/Invoke: serial port and its arrival watcher, setupapi, clipboard, reparse points, the monitors (`DisplayMonitors`), the system colours (`SystemColorValues`), the title bar of a window (`DwmTitleBar`), the blur behind a see-through window and its maximized size (`DwmBackdrop`), the Windows pseudo console (`PseudoConsole`) | no |
| `Utilities/` | Shared helpers; `IconPaths` (the rules of the icon paths, UI-free for the tests) | no |

## The editor and the views (since stage 3E-5 there is no WinForms control hierarchy)

```
RemoteFileEditor (+ .Syntax)   Controls/, not a control: load/save over SSH, drafts, sudo, encodings and the questions (the models of
                               CodeEditing/RemoteFile*), on a text view and a hex view it is given:
├─ IRemoteTextView             GUI/TextView/WpfTextEditor: the text view on the models (CaretNavigator, WrapMap, TextSelection, UndoHistory,
│                              TextEditCommands, TextReplace, TextTyping, TextViewLayout, TextPointer, SyntaxColoring, FoldingManager,
│                              InlineMessageProvider, EditorCompletion, LineCommands, TokenMoves, EditorZoom, TextSearch, BracketPair,
│                              BlankMarks, MultiCursorSession)
└─ IHexEditorView              GUI/HexView/WpfHexEditor: the hex view on HexEditingModel, HexViewLayout, HexKeyMap, HexPointer, HexByteText,
                               HexMenuModel, HexInspectorModel; shown in place of the text through IHexViewPlace (the tab)
RemoteFileEditorCommands       Controls/: the commands of the tab and their state (the WPF tool bar, the context menus, the keys)
EditorTabContext               GUI/Backend/: the tab: the two views in one grid, the commands, the tool bar, the status bar, the search bar
```

The WinForms controls this replaced (`ReadonlyTextBrowser` and its subclasses up to `CodeEditorPP`, `RemoteTextView`, `RemoteFileEditorPanel`,
`HexEditorControl` with its menu and inspector, the WinForms terminals of stage 2) are gone; the notes below still name them, as they were.

The terminals are WPF (`GUI/TerminalView/`): `WpfTerminal` (a `Border` with `TerminalPanel`: `TerminalRenderView` and a scroll bar) shows
a `Terminal/Rendering/TerminalDisplayModel`, which holds the emulator and the screen of `Terminal/Emulation/` (a screen is an
`EditableTextDocument` plus a `LineInfoStore`, one `CellStyle[]` per line, plus a `GridPoint` cursor; a column is an index of the text of its line,
a wide character is itself and the filler of its second cell, and a character of no width is kept with the cell before it in `LineInfo.Marks`,
`Terminal/Emulation/CellWidth`, see "Wide characters"); `WpfSmartTerminal`,
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
| 2 | New WPF terminal view (`GlyphRun`, row cache), device panel in XAML | In progress: 2A (the view, its model, the keyboard, the lab window), 2B (the classic SSH tab and the sudo prompt on `WpfTerminal`), 2C (the Smart Terminal on `WpfSmartTerminal`) and 2D (the device tab on `WpfDeviceTerminal` with `WpfSerialTerminal` and `WpfHexTerminal`) and 2E (the WinForms terminals and their Mono stands removed, their goldens on the display model) done, see "Stage 2". Left of the stage: nothing (the tool bar and menus of the device panel, which stayed WinForms, went to WPF in 4A) |
| 3 | Editor on AvalonEdit, hex editor on WPF | Done in alexsys87/SmarTTY#557 (the text view is a WPF view of our own on the model, not AvalonEdit): 3A (what `RemoteFileEditor` decides that needs no window) done in 3A-1 (the content of the file and the switching between the text and the hex view, `RemoteFileContent`) and 3A-2 (the loading and the saves, `RemoteFileFlow`), see "Stage 3"; 3B (the hex view on WPF) done in 3B-1 (the layout, the keys, the mouse selection and the texts of `HexEditorControl` to the model), 3B-2 (the WPF view `GUI/HexView/WpfHexEditor` and its lab window) and 3B-3 (the consumers through `IHexEditorView`, the menu and the data inspector to the model, the WPF inspector and context menu, the WPF view in the editor tab); 3C (the caret, the selection, the wrap and the undo of the text view to the model) done in 3C-1 (the caret moves and the wrap map, `CaretNavigator`, `WrapMap`), 3C-2 (the selection, `TextSelection`) and 3C-3 (the undo history, `UndoHistory`, `IndentEdits`); 3D (the text view on WPF, a view of our own on the model, not AvalonEdit) in progress: 3D-1 (the edit commands and the typing to the model, `TextEditCommands`, `TextTyping`) and 3D-2 (what an edit becomes and the navigation keys to the model, `TextReplace`, the layout and the mouse selection, `TextViewLayout`, `TextPointer`, the WPF view `GUI/TextView/WpfTextEditor` and its lab window) 3D-3 (the syntax colours: `SyntaxColoring`, the colour runs of a row, the tokens in the WPF view) 3D-4 (the folds: `FoldingManager.ClickMark`, the folds in `TextViewLayout` through `ITextFolds`, the fold margin in the WPF view) and 3D-5 (the inline messages: `InlineMessageBand`, `InlineMessageProvider.AddReported`, the rows of the messages in `TextViewLayout` through `ILineMessages`, the messages in the WPF view) and 3D-6 (the completion: `EditorCompletion`, what the list selects and its keys `SuggestionListMatch`, the WPF list and hint) done; 3E (the WPF editor in the tab) done: 3E-1 (the line commands, the moves to a bracket, the zoom steps and the key bindings: `LineCommands`, `TokenMoves`, `EditorZoom`, the commands in the WPF view) and 3E-2 (the search, the highlights, the pair of a bracket and the marks of the blanks: `TextSearch`, `BracketPair`, `BlankMarks`, the WPF view as an `ISearchTarget`) and 3E-3 (several cursors: `MultiCursorSession`, the cursors in the WPF view) and 3E-4 (`RemoteFileEditor` on a text view it is given: `IRemoteTextView`, `RemoteTextView`; the commands of the tab: `RemoteFileEditorCommands`; the tab, the status bar and the search on the WPF text view) and 3E-5 (the WinForms editor, hex control and their Mono stands removed; the hex golden replayed on the models, the remote editor stand on views of the test) done |
| 4 | Popups and menus to WPF, remove `Themed*Adapter`, `Forms/` | Done: 4A (the menus and bars of the terminal tabs and the device tab) in alexsys87/SmarTTY#558, 4B (the WinForms helpers nothing used, the paste and snippet dialogs without facades and `Forms/`, one dialogs seam `UserDialogs` for the message boxes and file choosers) after it, see "Stage 4" |
| 5 | Remove `UseWindowsForms` and `System.Drawing`, vector icons | Done in alexsys87/SmarTTY#562: 5A (the icons as vector images, the PNG files removed) and 5B (no WinForms: `UseWindowsForms` off; the lab windows removed) and 5C (no `System.Drawing`) done, see "Stage 5" |

### Stage 1 rules

Pure refactoring: no behavior change, code moved as is (`git mv` for whole files, the move and the edit in separate
commits), one small PR per step, each green on its own. Do not fix bugs found on the way; list them in the PR.

- Model folders `Terminal/`, `TextEditing/`, `CodeEditing/`, `Completion/` end with no reference to
  `System.Windows.Forms`, `System.Drawing` (including `Point`, `Color`, `Rectangle`), WPF, `Controls/`, `Forms/`, `GUI/`.
  View files found there move to `Controls/TextView/` or `Controls/TerminalView/`.
- New model types in `TextEditing/`: `RgbColor` (ARGB, `Empty`), `GridPoint` (mutable `X` column / `Y` row, like `Point`),
  `VirtualKey` (exact copy of `Keys`: same names and values), `KeyStroke`. Conversions only in `Controls/ModelInterop.cs` (gone in 5C: nothing converts any more).
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

Not in 2A–2E: the view in the other tabs (`ITabContext.EditorControl` was a `TextEditorControl` for the editor tabs; it went in 3E-4), wide (East Asian) characters taking two cells (the
screen gave every UTF-16 unit one cell and drew a wide glyph in one; done after stage 5, see "Wide characters"), bold glyphs (neither view draws them), UI Automation. If `GlyphRun` is too
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

### Stage 4 (popups and menus)

Done in 4A (the terminal side; the editor's menus and popups wait for stage 3, which changes the editor): the menus and bars of the terminal
tabs are WPF. `GUI/WpfMenus` loads the icons of the items from the same embedded files as `Controls/Icons` (`SmarTTY.Icons.<name>.png`,
frozen and shared), makes items and opens a menu at the mouse or below a button; a text from the user (a command, a snippet, a file name)
goes through `WpfMenus.PlainText`, which doubles the underscore (the access key mark of WPF; the WinForms menus doubled the ampersand). The
look is the theme's implicit styles of `GUI/Styles/Controls.xaml`, so `ThemedMenu.Apply` is not called for them. The context menu of the
classic and Smart Terminal tabs is a WPF `ContextMenu` of `MainWindowController` (`ShowTerminalContextMenu`, the quick commands added each
time it opens; `MenuTerminal` is the terminal it was opened on); the paste menu of the WPF terminals is `GUI/PasteMenu` (it replaces
`Controls/PasteMenu`; the snippet editor is called directly). The device tab (`GUI/DevicePanel/WpfDeviceTerminal.Toolbar.cs`) keeps the
names of the old designer's fields for WPF elements: two `ToolBar`s in a row (the "Send values" box at the right end), the split and
drop-down buttons are `ToolBarMenuButton` (a main button and an arrow that open a `ContextMenu` below it), the menu items are `MenuItem`s
(`DeviceMenuItem` is the one-expression form the partials used with `ToolStripMenuItem`); the netconsole bar is a WPF `Border` below the
terminals; the context menu opens at the mouse. Items that their handlers check and uncheck themselves are not `IsCheckable` (WPF would
toggle them a second time). No `WindowsFormsHost` is left in the device tab. Not as before: the tool bar does not move items into an
overflow menu by the WinForms rules (WPF's `ToolBar` has its own overflow). Tests: `SnippetEditorTests` reads `GUI/PasteMenu.cs`; the menus themselves are checked by hand on Windows: the terminal
context menu (Copy, Paste, Paste special, quick commands, Clear, New/Close Tab), the paste menu (recent pastes, snippets, save and edit a
snippet), the device tool bar in each connection type (COM with the baud rate and the Lines button, TCP, telnet with its menu, netconsole
with its bar), "Send values" in HEX and text mode, the file transfer menu, the settings menu, the log button while recording, light and dark
themes.

Done in 4B: no WinForms window, control, menu or box is left. First the code nothing called any more after stages 2, 3 and 4A went
(`Controls/ThemedToolStripRenderer` with `ThemedMenu`, `DoubleBufferedListView`, `Controls/Icons`, `GUI/Backend/WinFormsHostWrapper`,
`Forms/DpiAwareForm`, `DpiScaling`, `FormIcons` with the window icons `icons/forms/*.ico`, and the designer entries of `SmarTTY.csproj` for
files gone in stages 2 and 3); `IconResourceTests` reads the icon names of the WPF calls (`WpfMenus.Icon/LoadIcon/Item`, `DeviceMenuItem`,
the tool bar buttons) since stage 3 removed the last `Icons.Load`. Then the paste: `PasteGuard`, `PasteJob` and `PasteAnchor` serve the WPF
terminals and the device tab only, so they are in `GUI/` (namespace `SmarTTY.GUI`) and open `PasteConfirmWindow`, `PasteProgressWindow`
(with `IPasteProgress` in its file) and `SnippetEditWindow` themselves; `ThemedPasteConfirm`, `ThemedPasteProgress`, `ThemedSnippetEditor`,
their adapters and the WinForms forms behind them (`Forms/PasteConfirmForm`, `PasteProgressForm`, `SnippetEditForm`) are gone, and with
them `Forms/`. `WindowOwnership.FromHandle`/`Of` finds the WPF window behind a Win32 owner, which every adapter did on its own. Last the
questions: `ThemedMessageBox`, `ThemedFileDialog` and `ThemedFolderBrowser` (the WinForms vocabulary `MessageBoxButtons`/`MessageBoxIcon`/
`DialogResult`, a fallback to the WinForms dialogs, an adapter each) are one seam, `Controls/UserDialogs`: `Show` (with `DialogButtons`,
`DialogIcon`, `DialogAnswer`; a box closed without a button is `Cancel`, as before), `OpenFile`, `OpenFiles`, `SaveFile`, `SaveFileEx`,
`Browse`, and three hooks that `App` points at `MessageDialog.Ask`, `LocalFileDialogWindow.Show` and the folder browser (a question with no
hook throws: there is no WinForms box behind it). `MessageDialog` takes the same buttons and icons. The Mono stand of the remote editor
answers through `UserDialogs.MessageImpl` (552 checks on OpenSSH and Dropbear pass); `FileDialogTests`, `FolderBrowserTests`,
`PasteConfirmTests`, `PasteProgressTests` and `SnippetEditorTests` check the wiring and that the facades, adapters and forms are gone.
What is left of `System.Windows.Forms` for stage 5 (none of it shows anything): `Timer` (`RemoteFileEditor`, `PasteJob`, the device tab),
`IWin32Window` as the owner of a question (`UserDialogs`, `PasteAnchor`, `PasteGuard`, `IRemoteTextView`, the device tab, `NormalTabContext`),
`Keys` of the frozen settings (`VisualTTY/TerminalKeyLayout`, `Controls/ModelInterop`, `RemoteFileEditorCommands`, `ConnectionSelectionWindow`
with `Control.ModifierKeys`), `Screen` (`MainWindowController`, `CommonGUIPreferences`), the clipboard and `DataFormats` of `MainWindow`,
`WpfTextEditor` and `NormalTabContext`, `NativeWindow` of `Native/SerialPortArrivalWatcher`, `Utilities/ScreenEdgeUtils`, and
`Application.EnableVisualStyles`/`SetDefaultFont` in `App`. The embedded PNGs of `SmarTTY.csproj` that no menu asks for any more (most were the
editor's) stay until the vector icons of stage 5.

### Stage 5 (no WinForms, no System.Drawing, vector icons)

Done in 5A (the icons): the 187 PNG files of `icons/` are gone; the icons are the `DrawingImage`s of `GUI/Icons/*.xaml` (converted from the
SVG versions of the PNGs outside the repository and taken as they came: one dictionary for each folder, `Root` for `icons/` itself; the
dictionary of the window icons of the WinForms forms was not needed, they went in 4B). An icon is still named by the path its PNG had:
`Utilities/IconPaths` (UI-free) takes `"MainWindow/refresh16.png"`, `"../icons/net16.png"`, `"pack://application:,,,/icons/keys/locked.png"`
(back slashes, another case, no extension) to the folder and the name, the folder to its dictionary and the name to the key
(`refresh16DrawingImage`: what is not a letter, a digit or an underscore is an underscore). `GUI/IconImages.Get` loads the dictionary once
(`Application.LoadComponent`) and gives the image frozen, scaled to the size WPF showed its PNG at (the pixels at the DPI the file declared:
most 16 x 16, a 16-pixel PNG of 72 DPI 21.33, the dialog pictures 48 to 128; `IconPaths.SizeOf`, a table made from the PNG headers before they
were removed), with a transparent square of that size under it, so no layout moved; `FromIconOrFile` is the same or a picture file of this
computer (a connection may show one the user chose). The XAML says `Source="{gui:Icon MainWindow/refresh16.png}"` (`IconExtension`, with the
prefix of `SmarTTY.GUI` each file had or `gui`), in attributes, setters and triggers; the code passes the paths to `IconImages.Get` (the icon
helpers of the windows, the tool bar and the menus of the editor tab, `MessageDialog`, the file and folder windows, the transfer windows);
`WpfMenus.LoadIcon` maps the names the menus asked the embedded files for ("copy", "terminal-send") through a table (`PathOf`; the editor's
own icons by their names under `editor/`); the connection window keeps the name of an icon as before (`ImagePath`, "router.png", which the
settings hold) and shows `Image` (the picker lists `ConnectionIcon`s); the editor draws its message icons as the vector images (no colour
key now). `SmarTTY.csproj` embeds no PNG. `IconResourceTests` (1016 checks, links `IconPaths`) checks the rules, that every `{...:Icon ...}`
of the XAML, every icon path in a string of `GUI/` and `Controls/` and every name the menus ask for has its key in its dictionary, that no XAML
names a PNG, no code a pack URI of `icons/`, and that no PNG and no `.resx` is left; `RemoteFileOperationsTests` checks the image of each
file-list icon and `EditorTextTests` that of Show All Characters in the dictionaries. The view is compile-checked on Linux only. Manual check
on Windows, at 100 %, 150 % and 200 % and on a 4K screen: the main window (tool bar, tabs, session list, menus), the file list and its
file-type icons, the connection list and the icon picker of a connection (and a picture file chosen for one), the dialogs with pictures
(host key, credentials, public key, transfers, about), the message boxes, the editor tab (tool bar, context menus, the icons of the inline
messages), the device tab; every icon as large as before and sharp; light and dark theme.

Done in 5B (no WinForms): first the three lab windows of the Tools menu are gone (`GUI/TerminalLabWindow`, `GUI/HexView/HexLabWindow`,
`GUI/TextView/TextLabWindow`, which tried the WPF views before they went into the tabs; the manual checks of stages 2 and 3 that name a lab are
done in the tabs now; `TerminalBenchmarkScripts` stays, `TerminalRenderModelTests` uses it). Then what was left of `System.Windows.Forms`
(the list of "Done in 4B"): the timers of the windows are `GUI/UiTimer` (a `DispatcherTimer` with `Interval` in milliseconds, `Tick`, `Start`,
`Stop`, `Enabled`: the paste job, the device tab, whose line status timer the panel disposes itself instead of the designer's container), and
`RemoteFileEditor` asks its text view for the timer of its drafts (`IRemoteTextView.StartTimer`: `UiTimer` in the WPF view, the WinForms timer in
the Mono stand); the always-null `IRemoteTextView.MessageOwner` is gone. The owner of a dialog is `Controls/IDialogOwner` (a window handle)
instead of `IWin32Window` (`UserDialogs`, `PasteAnchor`, `PasteGuard`, `WindowOwnership.Of`, the device tab). The device tab takes its keys as
`KeyStroke` (`OnTerminalKeyDown` returns whether it took the key); the conversions between `VirtualKey` and the WinForms keys moved from
`Controls/ModelInterop` into `tests/ModelInteropTests`, which keeps checking that `VirtualKey` is an exact copy of `Keys` (40 checks pass on
Mono); `VisualTTY/TerminalKeyLayout`, which nothing used since `XtermKeyTable` reads `XTermKeys.xml` itself, is gone; the connection window
reads Shift from `Keyboard.Modifiers`. `Native/DisplayMonitors` gives the bounds `Screen` gave (the same `EnumDisplayMonitors`,
`MonitorFromPoint` and `GetMonitorInfo`, the same order and pixels), for the full screen mode and the remembered placement of
`CommonGUIPreferences`. The COM port watcher (`Native/SerialPortArrivalWatcher`) has a hidden top-level window of a class of its own through
Win32 (`RegisterClassEx`, `CreateWindowEx`, its procedure kept alive) instead of a `NativeWindow`; the WPF dispatcher of the UI thread pumps its
messages. `SshConnector` lost `SynchronizationControl` and its `Site` (a WinForms control of the designer that nothing set; the dispatcher was
always there). `App` no longer sets the WinForms default font and visual styles. `ScreenEdgeUtils.GetNearEdges` (on a WinForms control, not
called) is gone, and so are the WinForms imports and aliases. Then `UseWindowsForms` is off in `SmarTTY.csproj`, and the built assembly has
no reference to `System.Windows.Forms` or `WindowsFormsIntegration`. The Mono stand of the remote editor keeps its own reference to WinForms
(its marshalling control and timer) and dropped that of the WinForms host of WPF; it passes (552 checks on OpenSSH and Dropbear). What is left
for 5C: `System.Drawing` (`Color` and `Font` in the settings, `Rectangle` of the remembered placements, `ModelInterop` with `RgbColor` and
`GridPoint`, `PasteAnchor.ScreenBounds`), which `System.Drawing.Common` of the WPF framework still provides. Manual check on Windows: a COM
port plugged in again while its tab waits (the tab reconnects), the full screen mode on each monitor, the remembered size and place of the
connection window, the drafts of an edited file (a draft every 10 s), a long and a slow paste (the progress window, Esc), the device tab's
timers (the line status of a COM port, a quick script waiting for the prompt, the listing of a directory, the reconnect), Ctrl+W, Ctrl+D,
Ctrl+Shift+Insert, Alt+Enter and Ctrl+Shift+C/V in a device tab, a connection deleted with Shift held (no question), the questions and the file
choosers of the editor and the device tab.

Done in 5C (no `System.Drawing`): the settings and the views use types of their own, and the settings files are read and written as before.
The colours were `ColorWrapper` in the settings already (four numbers in the XML); it converts to `RgbColor` now instead of `Color`, and the
defaults that were named colours (`Color.LightGray`, `Black`, `White`, `DarkGray`, `LightGreen`, `LightGoldenrodYellow`) are their numbers with
the name as a comment (`RgbColor.FromArgb(211, 211, 211) /* LightGray */`); `SystemColors.ButtonFace` of the light editor colours is
`Native/SystemColorValues` (the same `GetSysColor`, the light grey of Windows where it cannot be asked). The `[XmlInclude(typeof(Color))]` of
`ColorWrapper` went: it names a type for members declared as `object`, which the settings have none of. The font of the settings
(`EffectiveFont`, `ConfiguredFont`) is `VisualTTY/FontSpec` (family, points, bold, italic), which `WpfTerminal.SetFont` takes as the WinForms
font was taken (the points at 96/72); `FontVariant` went, and so did the `try`/`catch` around the font of the settings (making a `Font` of a
family that is not installed could throw, a `FontSpec` cannot; WPF falls back itself). The remembered placement of the connection window and the
monitors it was remembered on (`CommonGUIPreferences.WindowPlacement`) are `VisualTTY/ScreenRect`, a struct written to XML as
`System.Drawing.Rectangle` was (an array item is a `Rectangle` element, then `Location`, `Size`, `X`, `Y`, `Width`, `Height`);
`tests/ScreenRectXmlTests` (net8.0, where `Rectangle` is part of .NET, 32 checks) writes both under the same root and compares the XML and
reads each with the other (one monitor, two with negative coordinates, nothing remembered, an empty list); of three mutations two make it
fail and the third (no `[XmlIgnore]` on the edges, which have no setter) was an equivalent change, so those attributes were taken out.
`DisplayMonitors`, `PasteAnchor.ScreenBounds` and the full screen mode use `ScreenRect` too; the context menu events of `WpfTerminal` and
`WpfHexTerminal` give a WPF `Point` (device pixels, rounded as before); the colour of a hex mark (`HexHighlightWindow`) is an `RgbColor`;
`ThemeManager.GetColor` (for the WinForms controls) is gone; `Controls/ModelInterop` is gone (nothing converts between the model types and
GDI+), and `ModelInteropTests` checks instead that every `RgbColor.FromArgb(...) /* Name */` of the settings and of the device tab is the
named colour (51 checks on Mono). The four test projects that link `DeviceTerminalSettings.cs` link `RgbColor.cs` too. The built assembly has no
reference to `System.Drawing` (nor to WinForms). Manual check on Windows: the colours of a new settings file (the editor in the light and the
dark theme, the line numbers on the system's button colour, a device tab and its disconnected colour, the echo of the HEX view) and of an old
one; the font of the settings in a classic, a Smart Terminal and a device tab, and the font picker; the remembered place of the connection
window after an update from the previous version; a hex mark with a preset and with another colour; the paste progress window near its
terminal; the full screen mode.

### Wide characters (after stage 5)

Done: a character that the programs on the other side count as two columns (`wcwidth`) takes two cells of the screen. The width is
`Terminal/Emulation/CellWidth.IsWide`: the East Asian Wide and Fullwidth ranges of Unicode 15.1 below U+10000 (58 ranges, 42184 characters,
generated from Python's `unicodedata`; `TerminalScreenTests` checks the counts). A wide character is kept in the text of its line as itself and
`CellWidth.Filler` (U+FFFF, a noncharacter; one that arrives is written as U+FFFD), so that a column stays an index of the text and the styles, the
selection, the cursor, the mouse and the screen operations need no mapping; a filler that does not follow a wide character is a cell left empty
before one that did not fit at the end of a row. A surrogate pair took two cells as two units already and still does (right for the wide ones
there; a narrow one takes a cell too many). A character of no width takes no cell (added afterwards in the same pull request, see the last item).

- The screen (`TerminalScreen`): `WriteText` and `WriteAt` write the cells of the text (`ToCells`); a wide character never starts in the last
  column when the text wraps (that cell gets a filler of its own and the character goes to the next row; not on a screen of one column, where the
  two cells go to two rows); a write, an erase from a column, an insertion, a deletion and the truncation of an insertion at the right edge that
  would cut a wide character in two make both its cells blanks first (`SplitWideCharacter`, as xterm does); REP after a wide character repeats it.
  `WideCharacters` (on) is turned off by `TerminalDisplayModel` while it writes in the byte mode of the serial terminal, where the device counts a
  byte as a column and every character takes one cell, as before.
- What leaves the screen has no fillers (`CellWidth.Strip`): the copy (`TerminalDisplayModel.GetSelectedText`), the text of the document of a tab
  (`WpfTerminal.DocumentText`, which the device tab saves and copies), `ISearchTarget.GetRangeText` (the text the search bar takes from the
  selection) and the directory `AppEnvironment` reads from a prompt. The search (`DocumentSearch.Find` and `ForEachMatch`, which the highlight of
  all the matches uses now) matches the text without the fillers and gives cells. The two link scanners take a filler as part of what the character
  before it is part of and leave it out of the text of a link (an address with wide characters opens without them). A double click selects a word
  of wide characters whole.
- The view: `RowRunBuilder` keeps a wide character and its second cell in one run (a filler of an empty cell is a blank), `DrawGlyphs` gives a wide
  glyph the advance of two cells and draws nothing for a filler, the caret covers both cells of a wide character or a surrogate pair, and the grey
  suggestion is drawn in cells.
- The command line (`PromptLine`, the Smart Terminal and the input line of a device) lays its rows out itself: a wide character takes two cells and is
  never cut by the end of a row (the cell left empty gets a filler, and the caret before the character is at the start of the next row); the end of a
  full row and the start of the next one are the same index, with the caret at the end of the row, as before; `PositionAt`, `IndexOf` and `Contains`
  read that layout (a place in the second cell of a wide character is the index after it), and a local prompt with wide characters takes its cells.
  `Terminal/InputRegionMap` (C7), which counted a character as a cell, had no other caller and is gone with `tests/InputRegionMapTests`; the checks of
  `PromptLine` in `TerminalRenderModelTests` that were written against it pass unchanged.
- Tests: `TerminalScreenTests` (169 checks, 62 new: the table, the writes, the edges, the halves, erase, insert, delete, REP, the byte mode, one column),
  `TerminalRenderModelTests` (234, 30 new: the runs, the search in cells, the copy, the byte mode, the command line), `OutputLinkTests` (258) and
  `LocalLinkScannerTests` (93). `TerminalModelSnapshots`: the WinForms terminal gave a wide character one cell, so the 59 cases whose output has one
  (the hand-written "unicode text" and "wide characters wrap" and 57 generated ones) are compared with goldens of their own,
  `TerminalWideGolden.txt` and `TerminalWideCellColorsGolden.txt` (written by the display model, `--record-wide <folder>`: the cursor three columns
  further after three wide characters, the last cell of a row left empty and the two characters in the next row); every other case still gives the
  lines of the WinForms goldens, which were not recorded again. Four mutations (no empty cell at the edge, no split at the end of a write, every filler
  taken for a second cell, no empty cell in the command line) each make a check fail.
- Characters of no width (`wcwidth` 0): `CellWidth.IsZeroWidth` is the table of Unicode 15.1 of what glibc gives no width (the combining marks Mn
  and Me, the format characters Cf but the soft hyphen and the prepended concatenation marks, the medial and final Hangul jamo; 354 ranges, 2394
  code points, also above U+FFFF; generated from Python's `unicodedata`, the counts checked by `TerminalScreenTests`). Such a character is not put in
  the text of its line: `TerminalScreen.WriteCells` writes the text around it and `AddMark` keeps it in `LineInfo.Marks` (a `string` per cell, null
  for a line without any) of the cell before the place it is written at (the first cell of a wide character; at the start of a row there is none and
  it is dropped); the cursor does not move. `LineInfoStore` keeps the marks with their cells: a cell written again loses them (`SetCells`), a deletion
  and an insertion move them (the cells put in have none), a wide character cut in two loses them (`ClearMarks`), and lines move with their
  `LineInfo`. A mark that arrives in a write of its own finds its cell the same way, so the output fed one character at a time gives the same
  screen. In the byte mode a mark takes a cell of its own, as before. Out: `TerminalDisplayModel.GetText` (the copy, `WpfTerminal.DocumentText` and
  `GetRangeText`) gives every mark after the character it went with (`CellWidth.TextOf`), as it came, so a file name copied keeps its decomposed
  form; the search matches the letters without the marks; the links do not see them. The view: `RowRunBuilder` carries the marks of the row
  (`RowLayout.Marks`, in the signature; a blank with a mark is drawn), and `DrawGlyphs` draws a cell with marks as the character they compose with
  it in Normalization Form C when that is one character (e and U+0301 as é), otherwise as its character with each mark as a glyph of no advance
  put to end at the right edge of the cell (`DrawMarks`: a mark of no advance has its ink left of its origin; one of a cell's advance lands on the
  cell). The command line (`PromptLine`) lays marks out the same way: a character of no width takes no cell, its place is after the character
  before it, and it goes to the marks of that character's cell (the first cell of a wide character, the first half of a surrogate pair; a local
  prompt's last cell when the text starts with one, none on the device's prompt, whose cells keep the device's own marks), written after the rows
  (`TerminalDisplayModel.AddMarks`, `LineInfoStore.AddMarkToLine`; `ReplaceDocumentTail` with kept columns clears the marks after them); Left,
  Right, Backspace and Delete step over a character with the marks after it, and a mark is part of the word of its letter for Ctrl+Left/Right; the
  grey suggestion draws its marks over their cells (`CellWidth.ToCells(text, out marks)`). Tests: `TerminalScreenTests` (189 checks, 20 of the marks),
  `TerminalRenderModelTests` (262, 28 of them: the runs and the signature, the copy, the search, the command line); `TerminalModelSnapshots` compares the cases with
  a character of no width (the hand-written "combining marks", "unicode text" and generated ones with `e\u0301`) with the wide goldens too, which
  were recorded again and write the marks of a line (`mark 0:\u0301`): 111 cases there now, and of the 59 recorded before only the five with marks
  changed (checked case by case); every other case still gives the WinForms lines. Of four mutations (a mark on the filler, a write that keeps the
  marks, an insertion that copies one into the cell put in, a copy without the marks) each makes a check fail, the third after a check was added; of
  nine of the command line eight did at once, the ninth (the place before a mark on its letter's cell) after a check was added.
- Manual check on Windows: `ls` of a directory with Chinese, Japanese and Korean names in a classic tab and in a Smart Terminal; `vim` and `mc` with
  such files (the columns stay in place, the cursor over a wide character); `htop` with a process of a CJK name; a line of wide characters longer than
  the screen (the wrap, the empty last cell); emoji such as U+231A and U+1F600; the selection and the copy of wide characters, a double click on a
  word of them, the search bar (Enter, the highlight of all the matches), Ctrl+click on an address with them; the caret (block, underline) on a wide
  character; typing them with the IME in a Smart Terminal and in the input line of a COM or TCP device (Left, Right, Backspace, the wrap, the grey
  suggestion); a BusyBox device in byte mode (one cell each, as before). For the characters of no width: `ls` of files made on macOS (names with
  decomposed accents: the letters and the columns in place), `printf 'e\u0301 o\u0302\u0323 \u0915\u093f \u231a\ufe0f \U0001F468\u200D\U0001F469\n'`
  (the accents over their letters, the Devanagari vowel sign, the emoji with the selector and the joined emoji, the cursor after each in its
  place), `vim` with such a line, the copy of it pasted elsewhere (the same bytes), and the selection over it; a name with decomposed accents pasted into a
  Smart Terminal and into the input line of a device (the accent over its letter, Left, Right, Backspace and Ctrl+Left over it, Tab completing
  it, the grey suggestion of a command with it).

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

### Window chrome (after stage 5)

Done: every window (the 56 of XAML and the ten made in code) gets, without a line of its own, the title bar in the colours of the theme and
the size the user gave it; the terminals of the main window can have a see-through background. `SecondaryWindowStyle` (all windows use it) sets `GUI/WindowAppearance.Manage`; the
class handler of `Loaded` that `WindowAppearance.Install()` (in `App.OnStartup`) adds takes a window that has no such style. Both end in
`Attach`, which works once for a window, at `SourceInitialized` (the handle exists, nothing is shown yet).

- The title bar: `Native/DwmTitleBar.Apply` sets the dark frame (`DWMWA_USE_IMMERSIVE_DARK_MODE`, Windows 10 1809 and later: dark when
  `Theme.WindowBackground` is dark, `WindowChromeRules.IsDark`) and, on Windows 11 (build 22000), the caption, text and border colours from
  `Theme.WindowBackground`, `Theme.Text` and `Theme.Border`; on Windows 10 only the dark or light frame. A high contrast theme of the system
  gets the colours back (null). `ThemeManager.ThemeChanged` and `SystemParameters.HighContrast` apply it again to the open windows. Not done:
  Mica (the acrylic backdrop is only behind a see-through terminal, below), a title bar drawn by the program (`FluentWindow`, `WPF-UI`), tabs or controls in the title bar.
- The opacity of all windows (`GlobalSettings.WindowOpacityPercent`, a layered window with `SetLayeredWindowAttributes` and a hook that kept
  the layered style) is gone: on Windows it never made a window transparent, and its first hook (the style put back after `WM_STYLECHANGED`
  with a `BeginInvoke` at the background priority, which WPF took away again each time) made a loop that kept the dispatcher busy, so the
  work below that priority (`ContextIdle`: the start of the Smart Terminal, the loading of the file manager) never ran (alexsys87/SmarTTY#565
  stopped the loop). Do not put a window style back from the dispatcher. The settings member went; an old settings file with it is read as before.
- The see-through terminal background instead: `GlobalSettings.TerminalOpacityPercent` (30 to 100, 100 by default, brought into the range)
  and `TerminalBlurBehind` (on by default), on the terminal part of the settings window with a slider and a check box, applied with OK.
  `WpfTerminal.ApplySettings` (the SSH tabs only; the device tab keeps its own colours and stays opaque) gives the view
  `TerminalRenderView.BackgroundAlpha`, the alpha of its default background only (the text, the cells with a background colour, the caret
  and the selection stay opaque), used only while the inherited `TerminalBackdrop.IsActive` is set on it. The first version (alexsys87/SmarTTY#566)
  kept the system frame and extended it over the client area (`DwmExtendFrameIntoClientArea`, a transparent composition target, the acrylic
  system backdrop or the accent policy): on Windows 10 22H2 the client area was black, the DWM does not use the alpha of a plain WPF window
  there. So the window is one with per-pixel alpha now: `MainWindow.InitializeWindow` calls `GUI/TerminalBackdrop.Prepare` before the controller
  exists (it sets the style for the full screen mode), and below 100 % it makes the window `WindowStyle.None` with `AllowsTransparency`, a
  `WindowChrome` (caption 30, resize border 6, no glass) and a title bar of its own above the old content in a frame of `Theme.Border` (the
  title, minimize, maximize or restore and close in Segoe MDL2 Assets; dragging, the double click and the system menu are the caption area's),
  and a hook of `WM_GETMINMAXINFO` that fits a maximized window to the work area of its monitor (`Native/DwmBackdrop.FitMaximizedToWorkArea`).
  This cannot change once a window is shown: `MainWindowController.ApplyGlobalVisualSettings` calls `TerminalBackdrop.Apply`, which on a
  see-through window sets `IsActive` (the content of `MainWindowTabControl` transparent through a trigger in `Controls.xaml`, the window
  background almost transparent, alpha 1, so that the mouse is not let through) and the blur (`DwmBackdrop.SetBlur`, the accent policy
  `ACCENT_ENABLE_BLURBEHIND`, which Windows 10 and 11 apply behind a layered window), and at 100 % gives the backgrounds of the theme back
  (the window keeps its own title bar); on a window opened opaque it does nothing, and the settings window says the change takes effect the
  next time the main window is opened. The full screen mode hides the title bar of a see-through window and does not give it the system
  frame back (`WindowStyle.SingleBorderWindow` would throw with `AllowsTransparency`). The device tab has a background of its own
  (`Theme.ContentBackground`). A window with per-pixel alpha is drawn by WPF and copied for the DWM on every frame, so it costs more than an
  opaque one, most on large monitors; at 100 % nothing of this is used.
- The colours of the terminal: `VisualTTY/TerminalColorPresets` (UI-free) are the schemes of popular terminals (the default first, the
  colours SmarTTY always had: black, light grey, white bold text, the PuTTY palette; Ubuntu, Ubuntu (Tango), Tango Dark, Campbell, One Half
  Dark, xterm, Solarized Dark and Light, Dracula, Monokai, Gruvbox Dark, Nord, macOS Basic): the background, the text colour, the bold text
  colour and the 16 colours. The settings window lists them; Apply sets `TerminalBackgroundColor`, `DefaultTextColor`, `DefaultBoldTextColor`
  (new `ColorWrapper` members; no alpha means "never set", the default of the view) and `PaletteOverride`, and keeps the name in
  `TerminalColorSchemeName`; each colour can still be changed on its own. `WpfTerminal.ApplySettings` sets the three colours of the
  `TerminalColorScheme` from them. `tests/TerminalColorPresetsTests` (net8.0, 216 checks): every scheme is complete and opaque, the names
  are unique and found again, the default is the old colours, the palettes go through XML unchanged, the alpha of the opacity.
- The sizes: `CommonGUIPreferences.WindowSizes` (a `List<RememberedWindowSize>` by the full name of the window class, in
  `GUIPreferences.xml`; `VisualTTY/WindowSizeList`, at most 200, the oldest goes first) holds the size of every window that can be
  resized (`ResizeMode` `CanResize` or `CanResizeWithGrip`) and that the user changed. At `SourceInitialized` the window gets the size back
  (`SizeToContent` is respected: only the width of a `Height` window, only the height of a `Width` one, none of a `WidthAndHeight` one; the
  size is cut to `MinWidth`/`MaxWidth` and the monitor, and the window keeps the middle it had, inside its monitor); when it closes the size
  (`RestoreBounds` if maximized) is saved if it differs from the one the window opened at by more than a pixel. `MainWindow` and
  `ConnectionSelectionWindow` keep their placement (`CommonGUIPreferences.WindowPlacement`) and are left out. The width of the file manager
  beside the terminals is `CommonGUIPreferences.FileListWidth` (0: never changed), read in `MainWindow.InitializeWindow` into `_FilesWidth` (which
  the column takes when the file manager is shown) and written from `OnClosing` (the window is not shown any more in `OnClosed`).
- Tests: `tests/WindowChromeTests` (net8.0, 68 checks) drives `Utilities/WindowChromeRules` (opacity, alpha, COLORREF, dark or light, the
  clamp of a length, the middle kept), `VisualTTY/WindowSizeList` and the XML of the preferences, an old file without the new members included.
  Of 7 mutations of them each makes a check fail (two only after a check of the boundary was added: a grey just below half brightness, half a pixel past the end). The title bar, the see-through background and the restoring of a size
  need a real window and are compile-checked on Linux only. Manual check on Windows 11 and 10, 100 %, 150 % and 200 %: every theme (the
  title bar, the border, the text of the title; a custom theme; the theme switched while a dialog is open); High Contrast on and off; the
  background opacity at 30 %, 60 % and 100 % with and without blur, after a restart (a classic and a Smart Terminal tab: the desktop behind
  shows through, the text, the tab strip, the menu, the tool bar and the file manager stay opaque; an editor and a device tab look as before;
  the own title bar: drag, double click, the system menu, minimize, maximize and restore, Win+arrows, the size kept off the task bar, resize
  at every edge, the full screen mode on and off; 100 % draws as before with the system frame; the terminal speed at 4K; Remote Desktop); each colour scheme applied and a colour changed after it (`htop`, `mc`, `ls --color`); resize a dialog (the file transfer windows, the settings, the host-to-host copy), close it and open
  it again, also from another monitor and with the owner on a monitor of another scale; a dialog with `SizeToContent="Height"` (the directory
  download window) keeps only its width; a window made larger than the monitor is cut to it; the file manager width after a restart (shown,
  hidden before closing, never changed); an old `GUIPreferences.xml`.

### The stands off Mono (after stage 5)

Done: the last two stands that ran on Mono (net48 with WinForms, under `xvfb`) run on .NET on any machine. First `ModelInteropTests`, still on
Mono, wrote the members of the WinForms `Keys` with their values, the underlying type and `[Flags]` to `tests/ModelInteropTests/KeysGolden.txt`
(197 lines, a `--record-keys` option that was not committed); the stand (net8.0) now compares `VirtualKey` with that file member by member,
checks that a `KeyStroke` reads the key code and the modifiers of every key code with every set of modifiers (the round trips through `Keys`
and `KeyEventArgs` went with WinForms), and compares the colour numbers of the settings with `System.Drawing.Color`, which .NET has on every
platform (48 checks; a changed value in the golden makes it fail). Then the WPF types left in the code outside `GUI/`: the `Dispatcher` of
`Ssh/Connection/SshConnector` (`UiThread` now) and of `Transport/AsyncTransportConnection` is `Utilities/IUiThread` (`IsCurrent`, `Post`, `Send`;
`GUI/DispatcherUiThread` for a WPF dispatcher, `Post` at the normal priority as before); `NewConnectionController`, the view model of the new
connection window (it has `Visibility` and the dispatcher), moved from `VisualTTY/Backend/` to `GUI/Backend/` (namespace unchanged; `FileDialogTests`
reads it there); `Apply` and `Save` of `CommonGUIPreferences.WindowPlacement` (on a WPF `Window`) are `GUI/WindowPlacements.Apply` and `Of` (the
members of the struct, which the preferences file holds, did not change). `AsyncTransportConnectionTests` and `TcpTransportTests` stub the thread as
`FakeUiThread` instead of a fake `System.Windows.Threading.Dispatcher`. Then `RemoteFileEditorIntegrationTests` (net10.0): the main thread is the UI
thread of the stand (`TestUiThread`: the posts of the views wait in a queue that `Pump` runs where `Application.DoEvents` was, a post of a view
that was closed is dropped, the timer of the drafts posts its ticks; the WinForms forms, the marshalling control and the timer are gone), and
`TestDraftProtector.cs` stands for `IO/DpapiDraftProtector` (DPAPI is Windows only; it turns the bytes with a key, so a draft is still not plain
text). The 552 checks pass on OpenSSH and Dropbear with the same lines as on Mono (compared line by line, but for the temporary folder names).
CI runs both with `dotnet run` in the SSH job; Mono, `xvfb` and `mono-ipv4-shim.c` are gone. The two `#if NET5_0_OR_GREATER` written for net48
(the code pages of `TextFileEncodings` and `TerminalEncodings`) went. Manual check on Windows: a connection from the launcher with a new host key, a changed host key and
a proxy that asks for credentials (the questions on the UI thread), a COM and a TCP device tab (connect, data, reconnect), the remembered place of
the main window and of the launcher after a restart, the new connection window (the save buttons, connect, an error).

### Launcher: unreachable connections (after stage 5)

Done: the launcher (`GUI/ConnectionSelectionWindow`) checks the address and port of every saved SSH, raw TCP and telnet connection once, in
the background, the first time it lists it after the window opened, and marks the ones that did not answer with a red badge (bottom left
of the icon, `IConnectionSettingsWrapper.IsUnreachable`; the found computers of the network scan have their badge top left). The check is
`Transport/ReachabilityChecker` (the nested `MachineConnectionTester` of the window, moved and made UI-free): a TCP connect that is closed at
once, on the thread pool, given up after 5 s; the results are kept by host (case and blanks do not count) and port for the life of the
window, `Check(host, port, recheck)` starts a check only for a port never checked, or, with `recheck`, one whose result is older than 30 s
(the old "Probe all connections in the background", which also dims what has not answered and sorts it last, `IsInactive`, as before);
`BackgroundCheckCompleted` redraws the list, so it never waits. `ControllerImpl.UpdateMachineStates(list, probeContinuously, check)` sets
both flags; an SSH connection with `SuppressAutoProbing` and a netconsole receiver (UDP) are never checked. The check box "Mark unreachable
connections" in the options popup of the list (`CommonGUIPreferences.MarkUnreachableConnections`, on by default, an older file keeps the
default) switches the one-time check off, for devices that serve one client at a time. Tests: `TcpTransportTests` (the checker with a
queue of work and a fake connect: one check per port, none while one runs, none again without `recheck`, a recheck that waits, the case of
the host, a failure and a throw, invalid hosts and ports; and for real against a listening and a closed port on 127.0.0.1, and after
`Dispose`); `WindowChromeTests` (the new member of the preferences in XML and its default in an old file). A mutation that rechecks always
makes five checks fail. Manual check on Windows: the launcher with reachable and unreachable SSH, TCP and telnet connections (the badge
appears within 5 s, the window stays responsive, the tool tip), a COM port and a netconsole receiver (no badge), the check box off and on,
"Probe all connections" on (dimmed and sorted, the badge too), File > Back to launcher (checked again).

### Local console (after stage 5)

Done: cmd, PowerShell, WSL or any console program of this computer in a tab, through the Windows pseudo console (ConPTY, Windows 10 1809+).
It is a fifth kind of device connection, so the device tab (`WpfDeviceTerminal`) shows it with its terminal, log, paste, search and colours.

- Settings: `DeviceTerminalSettings.ConnectionSettings.LocalConsole` (`Name`, `Program`, `Arguments`, `WorkingDirectory`; XML type
  `com.sysprogs.terminal.connection.localconsole`; saved as a `.rawconnection` under `console_<name>`). `VisualTTY/DeviceTerminal/LocalConsoleProfiles`
  (UI-free) has the built-in consoles of the installed programs (Command Prompt, Windows PowerShell, PowerShell 7 from Program Files or PATH, WSL with
  `~`), the command line (the program quoted when it has a blank), the start folder (expanded, else the profile folder) and `NewSettings` (Enter is
  CR, UTF-8, no prompt detection, no echo, no `stty` size command, no automatic reconnect).
- Transport: `Native/PseudoConsole` (`CreatePseudoConsole`, two pipes, `CreateProcessW` with the pseudo console attribute and
  `STARTF_USESTDHANDLES` with no handles; a thread waits for the process and closes the console, which ends the output; `Dispose` closes the
  input at once and the console and then the output on the thread pool, because before Windows 11 `ClosePseudoConsole` waits until the output
  is read). `Transport/ConPtyTransport` is the `IDeviceTransport` and `ITerminalSizeAwareTransport` on it (the size given before `Open` is the size it
  starts with; a resize is `ResizePseudoConsole`). `DeviceConnections` makes it.
- The tab (`GUI/DevicePanel/WpfDeviceTerminal.LocalConsole.cs`): the input line is off (every key goes to the program, which echoes), no local echo,
  no stty items, no quick commands, no file transfer button, no echo item; the status says "running". The tab icon is `IconType.Terminal`.
- The launcher: `SettingsWrappers.LocalConsole` (a `Raw` item, never probed), the built-in consoles added in `RefreshAll` unless one of the same name is
  saved (opening one saves it, as a COM port is, for the last use time), the filter **Local consoles** (`ShownConnectionTypes.HideLocalConsoles`,
  inverted so an older file shows them), **New local console...**; `RawConnectionSettingsWindow` has a "Local console" group (name, program with a
  file chooser, arguments, start folder with a folder chooser) and does not test a console on OK.
- Tests: `tests/LocalConsoleTests` (net8.0, 57 checks: the built-in consoles on a fake machine, PATH, the command line, the start folder, the
  names, the settings, the XML of a profile and of the shown types); five mutations of `LocalConsoleProfiles` each make a check fail. The pseudo
  console needs Windows and is compile-checked on Linux only. Manual check on Windows 10 and 11: each built-in console opens, the prompt, typing,
  arrows, Tab, history, Ctrl+C, `cls`, PSReadLine in PowerShell (Ctrl+Backspace, Ctrl+Left), Cyrillic and CJK text (`chcp`, `dir` of such
  names), `vim`, `mc` and `htop` in WSL (the mouse, the size after a resize of the tab), `exit` (the closed prompt, reconnect starts it again),
  closing the tab while a program runs (no process left), a console with arguments and a start folder, one with a program that does not exist
  (the error), a saved console with the name of a built-in one, the filter, an older `GUIPreferences.xml`.

### Markdown preview (after stage 5)

Done: a Markdown file (`.md`, `.markdown`, or any file whose syntax is Markdown, `RemoteFileEditor.DetectSyntax`) can be shown rendered beside
its text in the editor tab.

- Model (UI-free, `tests/MarkdownPreviewTests`, 245 checks): `TextEditing/MarkdownParser.Parse` reads the text into a `MarkdownDocument` of
  `MarkdownBlock`s (paragraph, ATX and setext heading, fenced and indented code, quote, list with tight/loose and task items, GitHub table with the
  alignments, rule, HTML block shown as its text) with the first and last line of the source of each block, and `MarkdownInline`s (text, code
  span, emphasis and strong by the delimiter rules of CommonMark, `~~struck~~`, inline and reference links and images, autolinks and bare
  addresses, escapes, entities, hard breaks; `<br>` is a break and `<img>` an image, other tags show nothing). Headings get the anchors of
  GitHub (`MakeAnchor`, `-1`, `-2` for repeats). It is not the full specification (a lazy line of a quote or an item is taken whatever it
  continues) and never throws: the nesting of quotes and lists (64) and of emphasis and links (32) is limited, so deep input cannot overflow
  the stack. `MarkdownLinks.Resolve` says what a link leads to: a web address (http, https, ftp, mailto; other schemes open nothing), a heading
  (`#x`) or a file of the server relative to the document or the root (dots, percent escapes, the query dropped, a drive of a Windows host).
  `CodeEditing/MarkdownCodeColoring` gives the language of a fence's info string (aliases such as `bash`, `python`, then the extension rules of
  `SyntaxHighlighting`) and the coloured parts of its lines from the editor's tokenizers.
- View: `GUI/TextView/MarkdownPreview` (a `FlowDocumentScrollViewer`) builds a `FlowDocument` in the brushes of the theme (`Theme.*` resource
  references, so a theme switch applies at once); code blocks take the background, the text and the token colours of the editor
  (`WpfTextEditor.TextBackground`, `TextForeground`, `SyntaxColors`, `SyntaxChanged`). A web link opens through `UrlOpener`, an anchor scrolls to its heading, a file link
  raises `FileLinkClicked`, which `MainWindowController.OpenLinkedFile` opens in an editor tab (`EditorTabContext.ShowMarkdownAnchor` once loaded).
- The tab (`EditorTabContext`): a grid of the text (or the hex view), a `GridSplitter` and the preview; the preview is shown while
  `RemoteFileEditorCommands.MarkdownPreviewOn` is set, the file is Markdown and the hex view is closed; it is drawn again 300 ms after the last
  `WpfTextEditor.TextChanged` (a new event: an edit of a line or a range, a new document) and brings the block of the caret into view
  (`MarkdownDocument.BlockIndexAtLine`) when the caret moves to another block. The command `EditorToolbarCommand.MarkdownPreview` (a toggle on
  the tool bar and in the context menu, shown only for a Markdown file, off in the hex view, `Ctrl+Shift+M` in the text and in the preview) switches
  it; the choice is `EditorSettings.HideMarkdownPreview` (an "off" switch: an old settings file shows the preview).
- Manual check on Windows: a README with headings, lists, a nested and a task list, a table, code blocks in `bash`/`python`/`json`, a quote, HTML
  (`<p align="center"><img ...></p>`, `<details>`), badges (images in links); typing (the preview follows, the caret block in view), the splitter,
  Ctrl+Shift+M and the tool bar button, the setting after a restart, the hex view on and off, a file without the extension with the syntax chosen
  by hand, links (web, `#anchor`, `docs/x.md`, `../y.md#z`, a folder), light and dark themes and a theme switched while open, 150 % and 200 %,
  selection and copy, Ctrl+wheel, a file of some MB.
- Images (for a repository cloned on the server; nothing is loaded from the internet): `TextEditing/MarkdownImages` (UI-free, in
  `MarkdownPreviewTests`) says where a picture is (`Locate`: a data address or a file of the server relative to the document; web addresses,
  other schemes and folders are not read; a web image keeps its alternative text, a click opens it in the browser) and the key it is kept under,
  where a path from `/` is looked for (`ServerFileCandidates`: under the root of the git repository first, as GitHub shows it, then from the root
  of the file system; `RepositoryRootCandidates`, the folders up from the document, the first with `.git` is the root), decodes data addresses,
  tells the format by the first bytes (`Detect`: the raster formats of Windows, SVG; an HTML page or an error text is no picture), and the shown
  width (`ShownWidth`, the `width`/`height` of `<img>` through `ParseLength`, never wider than the preview). `GUI/TextView/MarkdownImageLoader`
  (one a tab, `EditorTabContext`) reads them in the background, four at a time: a file of the server through `RemoteFileEditor.ReadServerFile`
  (`RemoteFileServer.ReadOtherFile`, SCP or SFTP as the file itself, at most `MarkdownImages.MaxBytes`, 16 MB; the root of the repository found
  once through `RemoteFileEditor.ServerPathExists`), a data address from itself; it decodes a raster picture frozen on the pool and an SVG on the UI thread with
  SharpVectors (`SharpVectors.Wpf`, BSD-3; external resources ignored), keeps every picture or its error until the tab closes or the file is loaded
  again (`Clear`), and calls back on the UI thread. `MarkdownPreview` shows the alternative text until the picture arrives (a later drawing
  of the document ignores the callback of an earlier one by a generation number), then an `Image` in an `InlineUIContainer` at its pixel size,
  narrowed with the preview; a picture in a link takes its own press and opens the link. Manual check on Windows: the README of a cloned GitHub
  repository with relative PNG, JPEG, GIF and SVG pictures (`?raw=true`, `#gh-dark-mode-only`), a path from `/` inside the repository and in
  `docs/`, shields.io badges in links (their text, the browser on a click), a data address, `<img width>`, a missing file, a picture of more than
  16 MB, an HTML page given as a picture, a Windows server, Reload from Server after replacing a picture, a narrow preview.

### Stage 3 (the editor and the hex editor on WPF)

3A takes out of `RemoteFileEditor` what it still decides itself and that needs no window, so that a WPF hex view (3B) and a WPF text view (3D) can
be put under the same model; 3A is the same whether the text view is AvalonEdit or a view of its own on the model of the editor, which is not
decided yet. The rules of stage 1 hold: a net first, then the move, no change of behaviour, faults found are kept and listed.

Done in 3A-1: first a net for the switching between the text and the hex view, which the Mono stand reached only as far as opening the hex view:
`tests/RemoteFileEditorIntegrationTests` has two more scenarios, `hex-view` and `hex-draft` (what each view shows and which one can be edited, the
`ModifiedChanged`, `HexModeChanged` and `FormatChanged` events and their order, the text document with its undo history and the caret kept when
nothing changed, a change of the bytes that makes a new text with its line breaks found again and the caret brought into a shorter line, the
same hex document shown again for the same text, the question about bytes that are not UTF-8 and both answers, the text made from them saved as
those bytes, a save in the hex view, the modified state set in the hex view, a reload in the hex view that opens it again at the same offset, the
encoding and the line breaks that cannot be changed there, Select all and Delete of the hex view, a new encoding that leaves it, the draft of the
bytes that comes back in the hex view), recorded on the code before the move (258 checks before, 378 after: both servers); seven mutations of the
old code (the text document never kept, the hex document not saved with the text, no offset after a reload, the line of the caret, a restored
draft that is not modified, the hex view read-only, the hex view not reset) each make it fail. A text that the encoding cannot store (the error,
the text view stays) cannot be checked there: the encodings of Mono do not throw for such a letter. Then the content is
`CodeEditing/RemoteFileContent.cs` (public): the bytes on the server (`RawBytes`) and the encoding they are read with, the encoding and the line
breaks of a save, the hex view (`HexMode`, `HexDocument`, the text, its saved state and its caret kept while the bytes are shown),
`EnterHexMode` (with the old `GetBytesForHexView` and `ShowHexView`), `ExitHexMode`, `ShowRestoredHexDraft`, `ResetHexMode`, `IsModified` of
both views, `ApplyContent`, `SetSaveEncoding`, `SetLineEnding`, `GetChanges` and `CompleteSave` (the bytes of the file and the saved state of
the view they came from, and of the other one when it holds the same bytes), with the events `HexModeChanged` and `FormatChanged` that the
editor raises again as its own; the code is as it was. The editor is reached through `CodeEditing/IRemoteFileContentHost` (the text view: its
document, modified state, read-only state, caret, `SetDocument`, focus, `MarkSaved`, `OnModifiedChanged`; the state: loaded, saving, read-only
mode; where the hex view is: show, hide; the error and the question), which `RemoteFileEditor` implements explicitly, the hex view through
`IRemoteHexView` (`Document`, `ReadOnly`, `Focus`, `Invalidate`: members `HexEditorControl` has). One thing is not in the old order:
`CompleteSave` of the editor sets the bytes of the file after `BackupDone` and `IsNewFile`, not before them (plain fields that nothing reads in
between). `_content` is made by the constructor that opens a file, after the session; before it `IsModified` is the text's and `IsHexMode` is
false, as they were. A fault found by the net, kept as it is (the net records it): after a save in the hex view, closing it shows the text from
before the hex view, as not modified (the bytes are the file's now, so the text kept from before is taken as theirs); an edit of that text and a
save then write it over the bytes that were saved. `tests/RemoteFileContentTests` (net8.0, a fake editor and hex view that log what they are
asked, 105 checks) drives the model, including the error of a letter the encoding cannot store; of 24 mutations of it 19 made a check fail at
once and the other five led to checks (the kept bytes of a hex document that changed after the view was left, an edited text whose bytes were set
back to the file's in the hex view, the encoding rule in the hex view on its own, the changes read in the encoding of the bytes of the server).
`RemoteFileEditor.cs` is 1420 lines before and 1233 after. Next, 3A-2: the state of the loading and the saves that the editor keeps in fields
(`_loaded`, `_reloading`, `_remoteSignature`, `IsSaving`, `_pendingReload`, `_saveRequestedWhileSaving`, what a reload restores) and the
decisions of `Save`, `SaveInBackground`, `FinishBackgroundSave` and `FinishLoading` that need no control.

Done in 3A-2: first a net for the paths of the loading and the saves that the Mono stand did not take: `tests/RemoteFileEditorIntegrationTests`
has a `flow` scenario (before the file is loaded nothing can be saved, switched, read again, followed or run, and a save on close, a background
save and a reload do nothing; the caret of a compiler message in screen columns after a tab; while the file is loaded again a save throws and
says so, a save on close shows that and keeps the tab, a background save does not start and a second reload does nothing, and the caret comes
back; a reload that fails (the file made unreadable) keeps the text of the user, modified and editable; a refused write asks about sudo, and
answered No it cancels the save on close, keeps the tab and leaves the background save silent; the check of a script answered No puts the caret
on the line it named; a service reload answered Yes that fails offers sudo), recorded on the code before the move (378 checks before, 440
after: both servers); of seven mutations of the old code five make it fail, one is an equivalent change (`TrySave` before the load: `Save`
does nothing then either) and one hangs the stand. One check was made looser afterwards in a commit of its own: once on Dropbear the reload
command of nginx got no exit code (a channel the SSH layer closed early, as in A4a), and the question then has no "(exit code N)"; the
editor offers sudo all the same, which is what is checked. Then the flow is `CodeEditing/RemoteFileFlow.cs` (public): `IsLoaded`,
`IsReloading`, `IsSaving` with `SavingChanged`, the signature a save compares with, a save asked for while one runs, a reload asked for by
the answer No to a conflict, the upload event of a background save; `CanSave`, `CanToggleHexMode`, `CanReopenWithEncoding`, `CanFollowLog`;
`FinishLoading` (the state part: the signature, the backup, loaded), `LoadFailed` (the old `HandleLoadError`), `Reload`, `TrySave`,
`Save` (the wait for a background save, the conflict question, sudo, the clean-up), `SaveInBackground` (the body of its thread),
`FinishBackgroundSave` (an error, the conflict and its three answers, the check of the file, sudo, a superseded save, a save asked for
meanwhile, a reload), the decision part of `HandleValidationFailure`, `ReloadToOffer`, `OfferReload`, `FinishReload`, `SaveWithSudo` and
`CompleteSave`; the code is as it was. The editor is reached through `CodeEditing/IRemoteFileFlowHost` (`PrepareSave`; `Ask` with the
buttons and a `PromptIcon` (question or warning), `Inform`, `ReportError`; the inline messages of the check and going to the first problem;
the setting of the service reload offer; the check of the connection; a thread of its own, the pool and a post to the UI thread, which is
the old `BeginInvoke` with its catch; clearing the messages, what a reload keeps of the view (the caret, the offset of the hex view, the
folds, both views read-only) and starting the loading thread; the text read-only; the `LoadFailed` event; dropping the draft), which
`RemoteFileEditor` implements explicitly. What stays in the editor: `FinishLoading` (the draft question, the document, the draft timer, the
caret, the hex view and the folds that a reload puts back), `PrepareSave` (it reads the settings), the drafts, `CanRunInTerminal` (it reads
the first line). `tests/RemoteFileFlowTests` (net8.0, 88 checks) drives the model with a fake editor that logs what it is asked and keeps the
threads, the pool and the posts it is given in queues that the checks run, and a fake connection under the real `RemoteFileServer` and
`RemoteFileSaver`; the session, the content and the loader are the real classes. Of 38 mutations of the model 31 made a check fail at once,
six led to checks (the check of the file not run on close, the conflict check skipped after "overwrite", the error of a superseded save, the
check skipped after a conflict answered No, a save again after the check answered Yes for a text that was not edited, the offer of a service
reload when a save asked for meanwhile runs) and one is an equivalent change (`TrySave` during a reload: a reload needs a loaded file).
`RemoteFileEditor.cs` is 1233 lines before and 969 after (1420 before 3A). This ends 3A: what is left in `RemoteFileEditor` is the view (the
text and the hex view, focus, colours, the clipboard, the questions with their icons as `ThemedMessageBox`, the threads as `Thread`,
`ThreadPool` and `BeginInvoke`, the draft timer and question, `FinishLoading` on the document). Next, 3B: the hex view on WPF under
`IRemoteHexView` (the window and the host show it in place of the WinForms control).

3B puts the hex view on WPF: 3B-1 takes out of `HexEditorControl` what it decides without a window, so that the WPF view (3B-2) uses the same
code, and 3B-3 shows the WPF view in the tab in place of the WinForms control.

Done in 3B-1: first a net for what the golden of `tests/HexEditorControlTests` did not reach (the mouse, the page keys, the layout): a run of its
own at the end of the golden, `== view`, 47 lines written by the code before the move (the first 3608 lines did not change). The control is
shown in a form whose height gives exactly 12 rows and whose width holds 16 bytes in a row, and every point is found from the cells of the layout
(by reflection: the metrics, `HexCellX`), so that the lines are the same with another font (checked with a serif font of 15 points). It writes down
the page keys with and without Shift and at both ends, the scroll that keeps the caret in view (below, above, at the end), clicks on the high and
the low digit, in the gap after a group, on a character, on the header, left of the bytes and right of the characters, a drag (back to its start,
into the characters, beyond the table, where the selection ends at the edge without scrolling), the right button inside and outside the
selection and the middle button, the byte under the mouse (a digit, the gap, a character, right of the characters, past the end, the header),
the wheel (a notch, the small steps of a touchpad, both ends), the text of the tool tip (groups, both byte orders, a group the end cuts, a
named block and one without a name, a changed byte) and the colours of the bytes by kind on a dark and a light background. Of 13 mutations of
the old code 12 make it fail (one after a point was added for it); the 13th is an equivalent change (an anchor equal to the caret is no
selection either way). Not in it: the shift with the mouse (`ModifierKeys`), the automatic scrolling (it reads the real mouse), the link of the
data inspector (its place is found when the control paints) and the clipboard. Then the model: `TextEditing/HexViewLayout.cs` (public: the metrics
from the measured character, the columns and the groups, the rows and the width in view, the rows of the document, the byte under a point,
where a click puts the caret, the table and a point brought into it, the step of the automatic scrolling, the rows of the wheel with the
remainder of a touchpad, the first row brought into the content, the place of the caret, the scroll that keeps it in view, which scroll bars
are needed, whether the data inspector fits; the first row, the horizontal scroll and the group size are its state), which reads the sizes
of the view through `IHexLayoutView` (client area, inspector, the two scroll bars; implemented explicitly by the control);
`TextEditing/HexKeyMap.cs` (`Map`: which `HexKeyAction` a key is with Ctrl, Shift and Alt; `Apply`: the movements, the page keys by the rows
in view less one, Backspace and Delete on the model; the view runs the rest); `TextEditing/HexPointer.cs` (a press with the left button, Shift,
the right button inside or outside the selection, another button; the drag; the release); `TextEditing/HexByteText.cs` (the tool tip, the
colour of a byte, `IsDark` with the formula of `Color.GetBrightness` of .NET, `Blend`). The code is as it was. The control keeps the scroll
bars and their values, the timers, the tool tip, the painting, the measuring of the font and the inspector; `VisibleRows`, `RowCount`,
`ByteAtPoint`, `DescribeByte`, `ByteTextColor` and `Blend` are forwarders. One thing is not in the old order: a left press starts the timer of
the automatic scrolling after the model told the change, not before (starting a timer does nothing at once). The golden passes unchanged
(3655 steps); its accessors read `_layout` instead of the fields that are gone. `tests/HexViewLayoutTests` (net8.0, a fake view, 148 checks)
drives the four classes; of 41 mutations of them 34 made a check fail at once, six led to checks (a byte behind the edge of a narrow window,
the place of the caret after the last byte, the first pixel of the low digit, a click well above the view, the first pixel below the table,
the horizontal bar that the vertical one makes necessary) and one is an equivalent change (End in the last row: `MoveTo` brings the offset into
the bytes itself). `HexEditorControl.cs` is 1670 lines before and 1395 after.

Done in 3B-2: first the paste: the part of `HexEditorControl.Paste` between reading the clipboard and inserting the bytes (the UTF-8 of the text in
the character column; in the digits the bytes of a hexadecimal text, the UTF-8 of another one, and the question for a text that can be both) is
`TextEditing/HexPasteText.cs` (`ToBytes` with the question as a `Func`), as it was; the control asks the question with its message box, and
`HexViewLayoutTests` has 9 checks of it (157 in all). Then `GUI/HexView/WpfHexEditor.cs` (internal), the view of `HexEditingModel` on WPF: a
`FrameworkElement` with the same layout (`HexViewLayout`, through `IHexLayoutView`), keys (`HexKeyMap`: KeyDown with the virtual key of the
WPF key, the characters through TextInput into `TypeCharacter`), mouse selection (`HexPointer`, the mouse captured while a drag runs, the
automatic scrolling by a `DispatcherTimer`), tool tip and colours (`HexByteText`), the same commands and events as `HexEditorControl` (the
clipboard of WPF, `MessageDialog` for the questions and the errors) and `IRemoteHexView`, `IHexViewStatus`. It counts in whole device pixels:
the DPI is read from the visual (again on `Loaded` and on `OnDpiChanged`), the font is measured with its `GlyphTypeface` (the advance of "0" and
the height, rounded up), the drawing is scaled back by the DPI, and the text is put on the grid of the characters with one `GlyphRun` for each
run of bytes of one colour in a row (the gaps of the groups are blanks), the backgrounds as rectangles; its two `ScrollBar`s are its visual
children (a WPF scroll bar goes from 0 to the last first row, with the visible rows as its viewport). Colours and font are set by
`SetColors` and `SetFont`. Not yet in it: the data inspector (the flag is kept and reported, so the settings keep it), the link to it in the
header and the context menu (3B-3). Tools > WPF hex editor lab (`GUI/HexView/HexLabWindow.cs`) opens a file of this computer (up to 64 MB) in it
with the editor colours, font (Courier New 12 points with the zoom) and hex view options of the settings, with the view options on a tool bar
and the state in a status line; the bytes can be changed but are never saved. The view is compile-checked on Linux only. Manual check on
Windows (the lab): a file of a few bytes, of some KB and of tens of MB (scrolling and paging stay fast); 100 %, 150 % and 200 % scaling and a
window moved between monitors (crisp text, the caret and the click on the right digit); light and dark theme; the arrows, Home/End,
Ctrl+Home/End, PageUp/Down with and without Shift; typing hex digits and characters (Insert for overwrite), Backspace and Delete; Ctrl+A, C,
X, V (a hexadecimal text, a plain text, "dead" with its question), Ctrl+Z and Ctrl+Y; a click on each digit, in the gap after a group, on a
character; a drag inside the table and beyond its edges (automatic scrolling), a Shift+click; the wheel and Shift+wheel; the tool tip of a
byte; colours by kind; 8, 16 and 32 bytes in a row and groups of 1 to 8; read-only.

Done in 3B-3: first a net for what the hex golden did not reach: `tests/HexEditorControlTests` has two more runs at its end, written by the code
before the move (the first 3655 lines did not change): `== menu` (the real `HexEditorMenu` on the real control: every item with its text, key,
image, enabled and checked state in nine states, and the items clicked, not Copy As, with the dialog asked for, the calls after a command and the
message boxes) and `== inspector` (the real `HexDataInspector`: the text an edit starts with for every value at five offsets, Enter with the same,
a new and a wrong text, Esc, read-only, the keys of its rows, the rows at their edges, the clicks on the byte order, a value and the Hide link);
of 7 mutations of the old code 6 made it fail at once and the 7th after a state was added for it. Then: the panel, the menu, the find window,
the dialogs of the tab, the main window and the status source use the hex view through `CodeEditing/IHexEditorView` (the calls they made on
`HexEditorControl`; `HighlightSelection` takes an `RgbColor`), which `HexEditorControl` and `WpfHexEditor` implement; the marking colours are
`TextEditing/HexViewChoices.HighlightColors` (`RgbColor`). The items of the hex submenus and their rules are `CodeEditing/HexMenuModel` (with
`HexDialogKind`), what the data inspector decides is `TextEditing/HexInspectorModel` (through `IHexInspectorSource`), both moved as they were; the
WinForms menu and inspector use them, and `tests/HexMenuInspectorTests` (net8.0, 219 checks: a fake view for the menu, the real `HexEditingModel`
for the inspector) drives them; of 26 mutations 24 made a check fail (one after a check was added), two are equivalent changes. The WPF view
got its data inspector (`GUI/HexView/WpfHexInspector`, on the model, in device pixels, at the right with the whole height, the header link
while it is hidden; its keys and typed text do not reach the bytes) and the tab its context menu (`GUI/HexView/HexViewContextMenu`: the
commands of the panel with its state, the Text submenu off, the hex submenus). `RemoteFileEditor` takes a hex view in its constructor (null:
its own `HexEditorControl`, as the Mono stands use) and shows, hides and styles it through `CodeEditing/IHexViewPlace` (colours, the font family
and its size at the zoom of the text before the monitor scale); `EditorTabContext` is that place (the WPF view over the `WindowsFormsHost` in a
grid, one of the two shown; closing the hex view gives the keyboard back to the host when the view had it), and `MainWindowController` makes the
editor with the WPF view. The Mono stand has a `hex-place` scenario (a WinForms view given to the editor stands in for the WPF one, a place
logs what it is asked; 486 checks in all). One thing is not word for word: the find window gives the focus back to the view without asking
whether a WinForms view was disposed (the tab closes it before it disposes the view). Keys while the WPF view has the focus go through WPF:
Ctrl+F is the main window's (as before), Ctrl+S, Ctrl+G, F3 and the rest are the view's; the shortcuts of the window (Ctrl+W, Ctrl+Tab) may
now reach the window, which they did not while the WinForms hex control had the focus. Manual check on Windows, in a file tab: the checklist
of 3B-2 in the tab; Hex Editor on and off from the tool bar and the context menu (the text, its caret and undo history come back, the
keyboard is in the text); the status bar (offset, selection, INS/OVR, the byte order, groups and row width menus); Ctrl+S, Ctrl+F (the find
window, F3 and Shift+F3), Ctrl+G (Go to Offset); the context menu over the bytes (each item, the colours, Color and Label, Fill, Insert, Swap,
Checksums, the view options, Copy As); the data inspector: Ctrl+I, the tool bar button, the header link, the Hide link, the byte order link,
a double-click or Enter on a value and its edit (Enter, Esc, a wrong value), Up/Down, Ctrl+C, a window too narrow for it; the editor zoom
(Ctrl+wheel in the text, then the hex view) and the light/dark theme; a reload and a save in the hex view; Ctrl+W and Ctrl+Tab with the
hex view focused; closing a tab with the hex view open.

3C takes out of the text view (`ReadonlyTextBrowser`, `EditableTextBrowser`) what it decides about the caret, the selection, the wrap and the undo,
so that a WPF text view (3D) can be put on the same model: 3C-1 the caret moves and the wrap map, 3C-2 the selection (by the keys with Shift,
the mouse, words, lines, Select all), 3C-3 the undo and redo of the edit commands. The rules of stage 1 hold.

Done in 3C-1: first a net, as nothing drove the text view itself: `tests/TextViewTests` (net48, Mono + xvfb) drives the real text view
through a `TextEditorControl` in a form sized until it shows 8 rows (and wraps at 20 columns), so that what it writes does not depend on the
fonts: for every position of a document of 30 lines (empty lines, tabs, words with punctuation, letters that are not ASCII, blanks, lines longer
than the wrap width) the target of each navigation key as the key handler finds it (`TryGetNavigationTarget`), the word moves, Ctrl+Home and
Ctrl+End (asked of `ComputeMovedPosition` with the keys: `Control.ModifierKeys` cannot be set by a test) and the word under the caret; runs of
keys through the real `OnKeyDown` (the kept column, the scroll); the same with word wrap, and the wrap map (breaks, row indexes, segments) at two
tab sizes and for another text. `TextViewGolden.txt` (1113 lines) was written by the code before the move and is never re-recorded; nine
mutations of the old code each make it fail. A fault it found, kept as it is: with word wrap on, Right at the end of a wrapped line that is on the
screen goes to the start of that line instead of the next one (`GetNextRealLine` takes the next row of the screen, a row of the same line). Then
`CodeEditing/CaretNavigator.cs` (public): `Move` (what `ComputeMovedPosition` did, with Ctrl as a flag; `MoveWrappedRow`, `MoveWrappedRows`),
`BeginMove` and `DesiredColumn`/`DesiredWrapOffset` (the column Up, Down and the pages keep: the part of `TryGetNavigationTarget` that took and
dropped it), `TryGetWordBounds`; `CaretMove` is a top-level enum of `CodeEditing` now (it was protected in the view; `TextEditorControl`,
`SshTerminalControl` and `SerialTerminalControl`, which name it, use `SmarTTY.CodeEditing`). `CodeEditing/WrapMap.cs` (public): the breaks
and first rows, `Ensure` (built again when the text, the width or the tab size changed, or after `Invalidate`), `RowCount`, `RowIndexToLine`,
`SegmentOf`, `SegmentStart`, `SegmentEnd`, `PositionToRowIndex`. The code is as it was. The view gives them what they read through
`ICaretNavigationHost` (the document, the tab size, the rows in view, wrapping and the map, `IsWordChar`, `GetPreviousRealLine`/`GetNextRealLine`,
`OnMoveBeyondDocument`) and `IWrapMapSource` (the lines, the wrap width, the tab size), implemented explicitly in
`Controls/ReadonlyTextBrowser.Navigation.cs` and the Wrap partial; both models are made when first used (the base class constructors may ask
before the view's own has run). `ComputeMovedPosition` stays virtual (the terminals override it) and forwards; the private wrap methods the rest
of the view calls are one-line forwarders. The text view golden, the three goldens of `TerminalSnapshots`, `SmartTerminalIntegrationTests`
(253), `RemoteFileEditorIntegrationTests` (486), `DeviceLineEditorTests`, `NetconsoleTerminalTests` and `HexTerminalControlTests` pass
unchanged; the golden's accessors read `Navigator` and `WrapLayout`. `tests/CaretNavigatorTests` (net8.0, a fake view over a real document,
106 checks) drives the models; of 17 mutations 14 made a check fail at once, one after a check of the kept place in a row was added, two are
equivalent changes. `ReadonlyTextBrowser.cs` is 3603 lines before and 3374 after, the Wrap partial 337 and 247.

Done in 3C-2: first a net: `tests/TextViewTests` has a selection run, with and without word wrap, written by the code before the move (the 1113
lines of before did not change; 1199 in all, the same on every run): a click, a double click (a word, a blank, an empty line), a third click,
a drag with the left button (down, above the start, back to the start), a move without a button, after a double click and after the release, a
click left of the text (the line), the right and middle buttons with and without a selection, `ClearSelection`, `SetSelection`,
`SetSelectionEnd`, a key without Shift, Select all, ranges of lines (reversed, out of the text, one line) and the selected lines of six
selections, and the same things in terminal mode, where the selection has an end of its own; every step writes the selection, the caret, the
selected lines and the `SelectionChanged` events. Shift is not in it (the view reads `Control.ModifierKeys`). The clicks are at the left edge of a
cell found from the view (`ColumnToX`, the row of the line), so the lines do not depend on the fonts; the click timing fields are reset by
reflection between gestures. Eight mutations of the selection code each make it fail. Recorded as it is: outside terminal mode
`SelectLineRange` selects from the first line to the caret (the end it sets is the end of a terminal selection; only the terminal calls it,
through `SelectTerminalLine`). Then `CodeEditing/TextSelection.cs` (public): `HasSelection`, `Anchor`, `TerminalEnd` (the old fields
`_hasSelection`, `_selectionStart`, `_selectionEnd`), `End` (the caret, or the terminal end), `SetActive`, `MoveAnchor`, `MoveTerminalEnd` (each
says whether it changed something, as `SetSelectionCore` and `UpdateSelectionFromPoint` compared), `Clear`, `StartIfNone` (a key or a click with
Shift, a drag), `GetSelectedLines`, `ClampLineRange` and `DocumentEnd` (Select all); the code is as it was. The three fields are aliases into it
now (properties of the same names, as `_privateModes` in B4b), so that the view and its subclasses (`EditableTextBrowser`, `TextEditorControl`,
`ReadonlySmartScrollBrowser`, the terminals) read as before; the model is made when first used. The view keeps the mouse, the keys, the clicks
and their timing, `AdjustSelectionEnd` (still called after the selection is switched on: the serial terminal's override reads `HasSelection`), the
redraws and the events. All the stands of 3C-1 pass unchanged; `tests/CaretNavigatorTests` has 37 checks of the model (143 in all), and of 12
mutations of it 11 made a check fail at once, the 12th after a check was added. `ReadonlyTextBrowser.cs` is 3374 lines before and 3343 after.

Done in 3C-3: first a net: `tests/TextViewTests` has an edits run at its end, written by the code before the move (the 1199 lines of before did
not change; 1261 in all, the same on every run): `ReplaceText` in a line, across lines, at the start only, an empty edit, an end before the start,
columns of -1, a line break deleted at the end of a line and the join that drops the indentation the lines share; `Indent` and unindent with a
selection (a tab) and without one (spaces), by more than there is, blank lines left alone; Undo and Redo at both ends and an edit after an undo;
the modified state with `IsModified`, `CurrentVersion` and `MarkSaved`, undoing to the saved text and past it, a change outside an edit command;
`ExecuteAsSingleUndoStep` (nested, empty); Replace All (literal, of nothing, of an empty text, in a range with a line break in the replacement);
read-only; a new document while modified and while saved; every step writes the text, the caret, the selection, the modified state, what can be
undone and redone, and the `ModifiedChanged`, `LineChanged` and `RangeChanged` events. Nine mutations of the undo and edit code each make it fail.
A fault it found, kept as it is: an edit within one line puts the caret at the end of the new text in the caret's own line, which is another line
when the edit was not on the caret's line (`ReplaceInLineCommand` calls `SetCaretColumn`). Then `CodeEditing/UndoHistory.cs` (public): the two
stacks, the saved step and the marker of a text never saved, the depth of a running edit (`InEdit`), the steps collected as one,
`IsModified`, `CurrentVersion`, `MarkSaved`, `HasUndo`/`HasRedo`, `Execute`, `ExecuteAsSingleStep`, `Undo`, `Redo`, `Reset`; the steps are
`IUndoStep` (the edit commands of the view, which keep their view and their code) and `CompoundUndoStep` (the old `CompoundCommand`).
`CodeEditing/IndentEdits.cs` (public static): `IndentUnit`, `IndentText`, `UnindentWidth`, `SharedIndentLength` (the indentation a join drops).
The code is as it was. Not word for word: a change outside an edit still sets the view's virtual `IsModified` (the remote editor sends it to the
hex view) when `History.InEdit` is false; the unindent no longer tests the length of a line with two sentinels added (never 0); the compound step
no longer takes the caret and the selection at its start (never used). As before, when an edit of a single step throws, the edits made before it
stay as one step and the modified state is not looked at again. All the stands of 3C-1 and `EditorTextTests` pass unchanged;
`tests/CaretNavigatorTests` has 64 checks of the two models (207 in all), and all 15 mutations of them make a check fail. `EditableTextBrowser.cs`
is 763 lines before and 623 after. This ends 3C: what is left in the text view is the view (painting, the caret drawn, scrolling, the mouse and
the keys with their timing and modifiers, the edit commands' calls on the document and the view, the events). Next, 3D: the text view on WPF.

3D puts the text view on WPF as a view of our own on the models of 3C (not AvalonEdit): 3D-1 takes the edit commands and the typing out of the
WinForms view, so that the WPF view edits the text with the same code and the same undo; 3D-2 is the WPF view (`GUI/TextView/WpfTextEditor`) with
a lab window, as 3B-2 was for the hex view; then the highlighting, the folds, the margin, the inline messages and the completion on it, and the view
in the editor tab.

Done in 3D-1: first a net: `tests/TextViewTests` has a typing run at its end, written by the code before the move (the 1261 lines of before did not
change; 1315 in all, the same on every run): characters (over a selection, overwrite and at the end of a line, a control character), Enter (the
indentation carried over, a caret inside it, a selection, a line without a line break of its own), Backspace (a character, the start of a line and
of the text, a level of space indentation, a selection), Tab (a tab, spaces to the next tab stop, a selection of two lines indents, one in a line is
replaced), Delete, Ctrl+Delete and Ctrl+Backspace (words, blanks, the ends of a line and of the text, a selection), Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z
and the AltGr chords that are not commands, the `CharTyping` hook that types a pair and puts the caret inside it or types nothing,
`GetWordAtCaret`, `ReplaceWordAtCaret`, `InsertText`, `RemoveLines` and `DeleteSelection`. Shift+Tab (it reads `ModifierKeys`) and the clipboard
are not in it. Of ten mutations of the old typing code nine make it fail (one after a step was added); the tenth is an equivalent change. Then
`CodeEditing/TextEditCommands.cs` (public): `TextEditCommand : IUndoStep` (the caret and the selection before the edit and putting them back,
`DeleteRange`), `ReplaceInLineCommand`, `ReplaceRangeCommand`, `IndentCommand`: the nested edit commands of `EditableTextBrowser`, as they were,
through `ITextEditView` (the document, the caret, the selection, the typing mode, the indentation settings, the change events, the caret moves, the
redraw and the layout), implemented explicitly in `Controls/EditableTextBrowser.Edits.cs`. And `CodeEditing/TextTyping.cs` (public): `TypeCharacter`
(the `OnKeyPress` of `TextEditorControl`, with Shift as a flag), `KeyDown` (its edit keys and the Ctrl and Shift chords; it returns
`TypingKeyResult`, which the control turns into `Handled` and `SuppressKeyPress`), `DeleteWord`, `RemoveLines`, `DeleteSelection`, `InsertText`,
`GetWordAtCaret`, `ReplaceWordAtCaret`, `SpacesToNextTabStop` and `SpaceIndentBackspaceLength`, as they were, through `ITextTypingHost` (also the
`CharTyping`/`CharTyped` hooks, the moved position of a caret move, the width of a line, the clipboard commands, Undo and Redo), implemented
explicitly in `Controls/TextEditorControl.Typing.cs`; the control keeps the clipboard and its events. As before, Ctrl+Delete looks at the width of
the line twice (in the word and again in Delete), also when it removed nothing. The text view golden, the three goldens of `TerminalSnapshots`,
the hex editor golden, `SmartTerminalIntegrationTests` (253), `RemoteFileEditorIntegrationTests` (486), `DeviceLineEditorTests`,
`NetconsoleTerminalTests`, `HexTerminalControlTests`, `EditorTextTests` and `MultiCursorTests` pass unchanged. `tests/CaretNavigatorTests` has 93
checks of the two models (300 in all: a fake view over a real document that logs what it is told); of 48 mutations of them 44 made a check fail at
once, three led to checks (the default line break of a line that inherits it, the removal of the last lines from the second one, an unindent by two
levels) and one is an equivalent change (`DeleteRange` moves its local end line, which is not read
again). `EditableTextBrowser.cs` is 623 lines before and 358 after, `TextEditorControl.cs` 474 and 188.

Done in 3D-2: first a net for what the edits run of `tests/TextViewTests` did not reach: a replace run at its end, written by the code before the move (the
1315 lines of before did not change; 1334 in all, the same on every run): a single-line view drops the line breaks of the text, a lone CR in an
edit within a line breaks the line, an insertion with CR LF takes the line ending of an LF text and keeps its own without the existing one, Replace
All up to the end column of a range, a replacement with line breaks whose new lines would match again (after LF and after a lone CR), the end of the
range after line breaks in its last line and in the same line, and the caret brought into a line that became shorter. Of 17 mutations of the old code
15 make it fail; two are equivalent changes (the read-only test of Replace All, which `ExecuteAsSingleUndoStep` and `ReplaceText` make again, and
the caret column, which `SetCaret` brings into the line). A fault it found, kept as it is: when a line of the range before the last one gets line
breaks, Replace All looks for the last line of the range at its old index, so its limit and the new end of the range are worked out for another
line. Then `CodeEditing/TextReplace.cs` (public): `Replace` (the old `ReplaceTextCore`: an empty edit, the columns -1, a single-line view, the
indentation a join drops, a replacement within a line or of a range), `ReplaceAll` (one undo step, the range and its new end, the caret brought into
its line) and `CountLineBreaks`, as they were, through `ITextReplaceHost` (what the edit commands see, `SingleLine`, the line ending flag,
`GetLineOrNull`, `GetIndentation` and the view's own `ReplaceText`, which the terminals route), implemented in `Controls/EditableTextBrowser.Edits.cs`;
the view keeps read-only and its events. And the switch of `TryGetNavigationTarget` is `CaretNavigator.TryMapKey` and `KeepsColumn` (the view still
reads Ctrl from the keyboard and asks its own `GetMovedPosition`, which the terminals override; Ctrl cannot be held by a test, so Ctrl+Left and
Ctrl+Right are checked on the model only). The golden, the three terminal goldens, the hex editor golden and every stand pass unchanged after each
move. `CaretNavigatorTests` has 48 more checks (348 in all); of 32 mutations of `TextReplace` 28 made a check fail at once, two after checks were
added (select within a line, a column -1 at the start of a range) and two are equivalent changes (with select the end is the start already; the
matcher makes a null replacement empty itself); all 9 of the key map. `EditableTextBrowser.cs` is 358 lines before and 248 after.

Then new code for the new view (not a move; the WinForms view keeps its own): `CodeEditing/TextViewLayout.cs` (public, in device pixels, through
`ITextLayoutView`: the rows, a line each or the rows of the wrap map, which keeps room for the vertical scroll bar so that showing it does not wrap
the lines again; the margin of the line numbers; the rows and the width in view; what a row shows with the tabs spread over their columns; the
selected columns of a row and whether its line break is selected; the widest line; the position under a point, the nearer edge of a character
brought into its row, and the point of a position; the scroll that keeps the caret in view, with four columns before it on the left; which scroll
bars are needed; the wheel and the automatic scrolling of a drag with `HexScroll`) and `CodeEditing/TextPointer.cs` (public: a click, a click with
Shift from where the selection starts or the caret, a double click on a word as the navigator finds it, a triple click or a click in the margin on a
line, and a drag by the same unit). `tests/TextViewLayoutTests` (net8.0, a fake view over a real document, the real selection and navigator, 155
checks); of 58 mutations 52 made a check fail at once, two after a check was added, two are equivalent changes and two showed code that changed
nothing, which was taken out. Then `GUI/TextView/WpfTextEditor.cs` (internal), a `FrameworkElement` on all these models: the keys in the order of
`TextEditorControl` and its base class (the edit keys and chords of `TextTyping`, Ctrl+Up/Down, Ctrl+A, Ctrl+S as `SaveRequested`, the navigation
keys with and without Shift, Insert, then Backspace, Enter and Tab as the characters the WinForms view gets as `KeyPress`; the other characters
through `TextInput`), the mouse through `TextPointer` (the mouse captured while a drag runs, the automatic scrolling by a `DispatcherTimer`, the right
button moves the caret outside the selection), the wheel (Shift: sideways), the clipboard of WPF (as the WinForms view: the caret line when nothing
is selected), a context menu of the edit commands, and the drawing in whole device pixels as the WPF hex view does (one `GlyphRun` a colour in a row,
only the columns in view, the selection with a cell for a selected line break, the caret as a bar or as a block in overwrite mode, the margin with the
number of the caret line in the colour of the text). With no folds, the line before and after a line are its neighbours (so the fault of Right at
the end of a wrapped line of 3C-1 is not in it). Not in it yet: the syntax colours, the folds, the inline messages, the completion, the marks of the
blanks, the search highlights, characters the font has no glyph for (drawn as "?") and characters of two cells. Tools > WPF text editor lab
(`GUI/TextView/TextLabWindow.cs`) opens a file of this computer (up to 16 MB, UTF-8 unless it has another byte order mark) with the colours, the
zoom, the tab size, the indentation, word wrap and the line numbers of the editor settings and switches for them; the text is never saved. The view
is compile-checked on Linux only. Manual check on Windows (the lab): typing, with a selection and in overwrite mode (Insert), Enter with the
indentation, Backspace in space indentation, Tab and Shift+Tab with and without a selection of lines, Delete, Ctrl+Delete, Ctrl+Backspace; Ctrl+Z,
Ctrl+Y, Ctrl+Shift+Z; Ctrl+C and Ctrl+X without a selection (the line), Ctrl+V, Shift+Insert, Shift+Delete; the arrows, Ctrl+Left/Right, Home/End,
Ctrl+Home/End, PageUp/Down with and without Shift, Ctrl+Up/Down, Ctrl+A; a click, a double and a triple click, a click in the margin, a drag inside
the text and beyond its edges, a Shift+click, the right button and the context menu; the wheel and Shift+wheel; word wrap on and off (the first line
stays in view), the line numbers, the tab sizes; a file of some MB (scrolling and paging stay fast); 100 %, 150 % and 200 % scaling and a window moved
between monitors; light and dark theme; AltGr letters and an IME.

Done in 3D-3 (the syntax colours): first a net: `tests/TextViewTests` has a syntax run at its end on a real `CodeEditorPP`, written by the code before
the move (the 1334 lines of before did not change; 1357 in all, the same on every run): the spans each line of a C text is drawn with (start, end,
which token brush) after edits that change a line, put a line break in at the start of a line and take an empty line out, open and close a block
comment over several lines, insert and remove lines, and with two brushes for more token types, none, an empty list and no tokenizer; then the inline
messages and a collapsed fold after line breaks put in and taken out at the start of the line of a message and of the fold, of the line before them
and of the first line, and a line added after the last one. Of 9 mutations of the old code 8 make it fail; the 9th is an equivalent change (the
number of removed lines is counted before the start line moves). What it shows: the rule that a line break put in at the start of a line (or an
empty line taken out) counts from the line before changes nothing in the colours; it is what makes an inline message (which moves only on the lines
after the start line) go down with the text of its line. Then `CodeEditing/SyntaxColoring.cs` (public static): `ChangedLines` (the first line, the
removed and the added lines of a range edit that `CodeEditorPP.OnRangeChanged` tells the token cache, the messages and the folds) and `ColorIndex`
(the colour of a token type, the first one for a type past them; the view keeps its test for no brushes, so a negative type still throws as
before), as they were. Every golden and stand passes unchanged. For the WPF view: `TextViewLayout.RowRuns` (the coloured parts of a line, in indexes
of the line, turned into the runs of a row in its columns: a tab takes its columns, cut to the row of a wrapped line, the rest in the colour of the
text, neighbours of one colour joined, a part that goes back over an earlier one cut) with the struct `TextRun`; `tests/TextViewLayoutTests` checks
it and `SyntaxColoring` (183 checks); of 19 mutations 17 made a check fail at once and two after a check was added. `WpfTextEditor` keeps the tokens
in a `TokenCache` as `CodeEditorPP` does (`SetSyntax(tokenizer, colours)`, the line tokenized again on a line change, the lines of `ChangedLines` on a
range change) and draws a row by its runs, the selected part of a run in the colour of a selected text. The lab has the syntax menu of the editor:
a file opened takes the syntax of its name or first line when the highlighting is on in the settings, with the colours of the settings. Not in it
yet: the highlight of the pair of a bracket at the caret (`CodeEditorPP.OnSelectionChanged`). Manual check on Windows (the lab): the colours of a C,
a shell, a Python and a YAML file in the light and the dark theme; a block comment opened and closed by typing, the lines after it coloured again;
lines put in and taken out inside a comment and a string; the colours of a selected text; a syntax chosen in the menu; a large file (typing stays
fast).

Done in 3D-4 (the folds): first a net: `tests/TextViewTests` has a fold run at its end on a real `CodeEditorPP`, written by the code before the move (the
1357 lines of before did not change; 1380 in all, the same on every run): a click on the mark of the fold margin (through the real
`FoldingMarginPainter`) on a block whose brace is on the next line and on one whose brace ends the header line, with the caret inside the block, on
the line of the closing brace, on the header before the line of the opening brace and outside the block, again to unfold, on a line without a block
and while the lines are wrapped; the rows the view shows and the text of a folded header with its label; Up, Down and End over a folded block; the
caret put in a hidden line; Collapse and Expand at the caret line. Of 7 mutations of the old click 4 make it fail at once and two after a step was
added; the 7th is an equivalent change (`UpdateLayout` builds the rows again too). A fault it found, kept as it is: folding a block whose opening
brace ends its header line while the caret is inside it puts the caret on the line above the header (the line of the opening brace less one), not on
the header. Then `FoldingManager.ClickMark` is what `FoldingMarginPainter.OnMouseDown` decided (nothing while wrapping, the fold toggled, where the caret
goes), as it was; the painter keeps the test for a virtual row, the caret move and the rows built again. Every golden and stand passes unchanged;
`tests/FoldingTests` has 11 more checks (28 in all), and all 7 mutations of `ClickMark` make one fail. For the WPF view: `TextViewLayout` reads the
folds through `ITextFolds` (`GetHiddenLineCount`, `GetFoldedText` with the label), which `FoldingManager` implements: while the lines are not wrapped
the rows are the lines shown (built again after `InvalidateRows`), a hidden line is in the row of its header, a folded header shows and measures the
text of its fold, its colours end at the label (`FoldLabelColor`) and the rest is in the colour of the text, `LineBefore`/`LineAfter` go past the
hidden lines, and the margin of the fold marks comes after the numbers (`NumbersWidth`, `FoldMarginWidth`, `IsInFoldMargin`); the columns of a line
are counted on the text it shows (`IndexToColumn`, `ColumnToIndex`, checked against `TextDocument` on texts with tabs). `tests/TextViewLayoutTests` has
206 checks; of 22 mutations 20 made a check fail at once, one after a check was added, and one showed a clip that changed nothing, which was taken
out. `WpfTextEditor` keeps a `FoldingManager` on its tokens as `CodeEditorPP` does (cleared with a new document or syntax, stale folds removed after an
edit, a caret put inside a folded block unfolds it, Up and Down past the hidden lines, a click on the marks through `ClickMark`, Fold and Unfold in
the context menu) and draws the marks as `FoldingMarginPainter` does and the label grey in a grey frame; while wrapping nothing is folded, as in the
WinForms view. Not in it yet: the keys of the fold commands (the key bindings of `CodeEditorPP`). Manual check on Windows (the lab): the marks of a C,
a shell, a Python and a JSON file; a click on a mark folds and unfolds, the caret inside a folded block moves as in the editor tab; Up, Down, PageUp
and PageDown over folded blocks; a click and a drag on a folded header and on its label; the caret put into a hidden line by a search or Ctrl+End
unfolds the block; typing inside and around a folded block (a fold whose tokens change goes away); Fold and Unfold in the context menu; word wrap on
and off with folds; 100 %, 150 % and 200 % scaling (the marks stay crisp).

Done in 3D-5 (the inline messages): first a net: `tests/TextViewTests` has a message run at its end on a real `CodeEditorPP`, written by the code before
the move (the 1380 lines of before did not change; 1399 in all, the same on every run): the line and the column a message is added at (counted from
1, a column of 0 for none, a line past the text on the last line, a line below 1 on the first, a second message of a line in its place, one at a time
and in a batch), the messages dropped when word wrap is switched on and off; and where the real row of a message (`InlineMessageView`) puts its band,
drawn into a bitmap with colours of its own (under its column after a tab, scrolled, scrolled past it, in the middle, a text too long for the row, at
two row heights). The band is found as the frame drawn around it, and its width is written less the width the row measures its text at (and, in the
middle, its left edge less where the middle puts the text), so that the lines do not depend on the fonts. The colour of the text (an error or a
warning) was in the first recording and is not any more: whether a pixel of the text has that very colour depends on the fonts of the machine (CI
found none; the lines without it were written again by the code before the move, on a worktree of that commit, and are the same with the code after
it). Of 18 mutations of the old code 17 make it fail; the 18th, the colour of a warning, is in the view and did not move. Then `CodeEditing/InlineMessageBand.cs` (public struct): `IndentColumn` and `Place` (the text no wider than
the row leaves room for, in the middle or under the column, the icon in front of the text, the band behind both), the arithmetic of the row of
`InlineMessageView`, as it was; and `InlineMessageProvider.AddReported` (the line counted from 1 brought into the text, the column counted from 1),
what `CodeEditorPP.AddMessage`, `AddMessages` and `MessageLine` did, as it was. Every golden and stand passes unchanged. For the WPF view:
`TextViewLayout` reads the lines that have a message through `ILineMessages` (`HasMessage`, which `InlineMessageProvider` implements): while the lines
are not wrapped a message is a row of its own under its line (hidden with a hidden line), with no text, colours, number or selection; it has the
columns of its line, so that a point on it is a point on its line; a position is in the row of its line, and `LineBefore`/`LineAfter` are lines.
`tests/TextViewLayoutTests` checks it, `InlineMessageBand` and `AddReported` (230 checks); of 25 mutations 21 made a check fail at once and four after
a check was added. `WpfTextEditor` keeps an `InlineMessageProvider` as `CodeEditorPP` does (`AddMessages`, `AddMessage`, `ClearMessages`, the messages
moved with the lines of `ChangedLines`, dropped when word wrap is switched) and draws a message as `InlineMessageView` does: the band of
`InlineMessageBand.Place` (the columns scrolled away are the pixels of the horizontal scroll over the width of a character), the icon of the WinForms
view (its resource, magenta made transparent), the text in the colour of its kind in Times New Roman of five eighths of the row in points, clipped to
its width, and the frame; `SetMessageColors` gives the colours. The lab puts an error or a warning at the caret (every other one without a column)
and clears them, with the colours of the settings (`RemoteFileEditor.RefreshMessageColors`). As in `CodeEditorPP`, a new document keeps the messages.
Manual check on Windows (the lab): a message under a column, in the middle, under a line with tabs, a long one; scrolled sideways; under a folded
header and inside a folded block; lines put in and taken out above a message (it moves with its line), a line break at the start of its line;
the icons and the colours in the light and the dark theme; word wrap on drops them; Up and Down over a message row; a click on a message row puts the
caret on its line; 100 %, 150 % and 200 % scaling.

Done in 3D-6 (the completion): first a net: `tests/TextViewTests` has a completion run on a real `CodeEditorPP` (its list replaced by a stand-in
that logs what it is asked, set into the controller by reflection; the hint is the real one), written by the code before the move (the 1399 lines
of before did not change; 1433 in all, the same on every run): two letters of a word open the list by themselves and not one, the word it is told
to select, Backspace, a blank that accepts the selected word when it continues the typed one and a mark that closes the list when it does not, a
word that starts with a digit, Ctrl+Space with and without a word, Escape, the language and the switches; the hint opened by the parenthesis of a
call, the active parameter after a comma, closed after the call, opened again by Left (a new window) or kept (the same window), hidden while the
list is open, Escape, the lost focus, completion switched off. Of 27 mutations of the old code 25 make it fail; two are equivalent changes
(`ShowCompletion` tests the switch itself; a character that is not a letter or a digit leaves no word before the caret). Then
`CodeEditing/EditorCompletion.cs` (internal) holds what `CodeEditorPP` decided about the list and the hint, as it was: `ShowCompletion`,
`HideCompletion`, `OnCharTyping`, `OnCharTyped`, `AfterKeyPress`, `OnEscape`, `AfterKeyDown`, `ShowSuggestions`, `ShowSignatureHint`,
`UpdateSignatureHint`, `HideSignatureHint`, `SetActiveParameter`, `OnLostFocus` (the hint window that takes the focus while it is shown does not
close it), `RepositionHint`, `Language`, `Enabled`, `OnTyping`, `ApiSymbols`; through `IEditorCompletionHost` (the document, the caret, the typed
word, the back colour, the windows of the list and of the hint, the focus, the word separators), implemented in
`Controls/CodeEditorPP.Completion.cs`; Ctrl is a flag. Every golden and stand passes unchanged; the accessors of the run read the model's fields.

Then the list: first a net: `tests/TextViewTests` has a list run on the real `SuggestionListForm` and `SuggestionListView` (1515 lines in all, the
1433 of before did not change): the item a typed word selects with and without substrings (the same name, a name it starts, the nearest item
before it), the items put on top and their order (the abbreviations of the capital letters, digits and words, exact first; a word that is the
typed one or that it starts; the parts in the same letters, then in others; the rest grey), and the keys (Escape and Alt hide; Enter, Tab and a
blank accept; a blank after a word the item does not continue, in other letters or longer, is typed instead; Home and End; the keys and the
characters handed to the editor; a double click). Mono keeps the focused item when the selection is cleared, so a list with nothing focused
cannot be shown there. Of 35 mutations of the old code 34 make it fail; the 35th is an equivalent change. A fault it found, kept as it is: after
a filtered list the nearest item is counted in the filtered order and selected after the order is put back, so another item is selected. Then
`Completion/SuggestionListMatch.cs` (public static): `Find` (the old `SelectMatch` on the names of the list: the index to select, or the indexes
that go on top), `SplitWords`, `MapKey` (the old `HandleKey` and the keys the view handed on, as a `SuggestionListKeyAction`); the form keeps the
items, their colours and the order, the view the window and the messages. Not word for word: the typed word is read once for a blank, not twice.
`tests/EditorCompletionModelTests` (net8.0, compiled like `ModelBuildTests`, a fake editor over a real document and fake windows under the real
controllers, 166 checks) drives both models; of 63 mutations of `EditorCompletion` 59 make a check fail (some after checks were added: a list
of another language, a word that starts with 0, a hint window that closed by itself), four are equivalent changes; of 32 of `SuggestionListMatch`
31 (one is equivalent).

For the WPF view: `WpfTextEditor` keeps an `EditorCompletion` as `CodeEditorPP` does (the typing hooks, `AfterKeyPress` after a typed character,
`AfterKeyDown` after a key, Escape, Ctrl+Space, the lost focus, the scroll) and `CompletionEnabled`, `CompletionOnTyping`, `CompletionLanguage`,
`ApiSymbols`, `ShowCompletion`. `GUI/TextView/WpfSuggestionList` is the list on `SuggestionListMatch`: a popup under the caret that never takes
the keyboard; the editor hands it its keys first (Up, Down and the pages move the selection, the rest as `MapKey`), a click selects and a double
click accepts, the signature of the selected item is shown beside it; a click in the text and the lost focus close it.
`GUI/TextView/WpfHintView` is the hint (the active parameter in bold, one per line for a vertical signature, the description, above the caret
when the controller asks). The lab has switches for the completion and for completion on typing and takes the API symbols of the settings. Manual
check on Windows (the lab): two letters of a word open the list (with completion on typing), Ctrl+Space; Up, Down, PageUp, PageDown, Home, End;
Enter, Tab and a blank accept, a blank after a word the item does not continue is typed; a mark closes the list; Escape; a click on an item and a
double click; the list near the bottom and the right edge of the screen; the hint of a function of the file and of a C library function (API
symbols on): the parameter after a comma, closed after the call, Left and Right back into it, scrolled; the light and the dark theme; 100 %,
150 % and 200 % scaling.

3E puts the WPF text view of 3D in the editor tab in place of the WinForms editor: 3E-1 to 3E-3 give the view what `CodeEditorPP` still does that it
has not (the commands of the editor and their keys; the search highlights, the marks of the blanks and the pair of a bracket; several cursors),
3E-4 puts `RemoteFileEditor` on a text view it is given (as 3B-3 did for the hex view) with the panel, the tab, the status bar and the search on
the WPF editor, and 3E-5 removes the WinForms editor, as 2E removed the WinForms terminals. The rules of stage 1 hold for what moves.

Done in 3E-1 (the commands of the editor): first a net: `tests/TextViewTests` has a line command run on a real `CodeEditorPP` (a subclass with a
comment style it is given), written by the code before the move (the 1515 lines of before did not change; 1623 in all, the same on every run):
duplicate, move and delete of the caret line and of selected lines (the first and the last line, a selection that ends at the start of a line, a
text without a final line break, a line before an empty last one), comment and uncomment (a selection, the caret line after its indentation, a
mark with an end, no comment style), the transforms of the Text menu with and without a selection (sorting and duplicates need two lines, the
caret moved with its text, a block that loses lines replaced and selected, a final line break added once), read-only, the undo step of each
command, the zoom steps, a zoom set rounded and its limits with the `ZoomChanged` events, the moves to a bracket forward and back with and
without the selection, and the key bindings (the keys with their modifiers in `KeyData`) that run them. Of 26 mutations of the old code 23 make it
fail; three are equivalent changes (a transform that changes nothing makes an empty undo step; `RemoveLines` clears the selection itself;
`SuppressKeyPress` sets `Handled`). A fault it found, kept as it is and not run by the stand: a search back for a bracket that goes over a pair
whose opening bracket is the first token of its line never ends (the index before that bracket is -1, which the next turn of the loop reads as the
end of the line, so the same closing bracket is met again); forward, the search goes over the pair the caret is in, and from a line without a
token after the caret it goes on from the last token of the next line. Then `CodeEditing/LineCommands.cs` (public, through `ILineCommandHost`:
the document, the caret, the selection, whether the lines can be edited, the tab size, the comment style, `ReplaceText`,
`ExecuteAsSingleUndoStep`, `SetSelection`, `SetCaret`, `RemoveLines`, `UpdateMaxLineWidth`, implemented in `Controls/CodeEditorPP.LineEditing.cs`):
`HasLineSelection`, `ApplyLineTransform`, `DuplicateLines`, `MoveLines`, `DeleteLines`, `ToggleComment`; `CodeEditing/TokenMoves.cs` (public
static: `TryFind`, the search of `MoveToToken`; the view selects and moves the caret); `CodeEditing/EditorZoom.cs` (public static: `By`, `Round`,
`Clamp`, `Min`, `Max`; `ReadonlyTextBrowser.MinFontZoom`/`MaxFontZoom` and the `FontZoom` setter take them from it). The code is as it was. Every
golden and stand passes unchanged. `tests/LineCommandTests` (net8.0, compiled like `ModelBuildTests`, a fake view over a real document that logs
what it is asked, the real token cache, 87 checks, the search that never ends on a background thread) drives them; of 31 mutations 28 made a
check fail at once, one after a check was added (a line left without a match: the next one is searched from its first token), two are
equivalent changes (a document has a line always; the line of a token is never past the text).

For the WPF view: `WpfTextEditor` is an `IEditorCommandTarget` with the key bindings of `CodeEditorPP` (`KeyBindingMap` on `EditorCommands`,
`SetKeyBindings` for those of the settings; a key that has a command runs it and nothing else, after an open word list has had its keys), the
line commands through `LineCommands` (it is the `ILineCommandHost`), `MoveToToken` through `TokenMoves`, `Retokenize`, the folds, the completion,
and the zoom (`FontZoom`, `ZoomBy`, `ResetZoom`, `SetZoom` with `EditorZoom`, `ZoomChanged`, Ctrl+wheel); `CommentStyle` is set by its owner. The
commands of several cursors did nothing until 3E-3. The lab takes the zoom of the settings as `FontZoom` and the comment marks of the syntax, and
shows the zoom. Manual check on Windows (the lab): Ctrl+Shift+D, Alt+Up, Alt+Down, Ctrl+Shift+K with the caret and with a selection of lines;
Ctrl+/ in a C, a shell and an XML file (and the numpad /, and Ctrl+Shift+7 where / is Shift+7); Ctrl+Z after each; Ctrl+Alt+9 and Ctrl+Alt+0,
Ctrl+Shift+9 and Ctrl+Shift+0, Ctrl+[ and Ctrl+], Ctrl+, and Ctrl+. ; Ctrl+R; Ctrl+Alt+Left and Right on a fold; Ctrl+Plus, Ctrl+Minus, Ctrl+0,
the numpad keys and Ctrl+wheel (the size in the status line, the text crisp at 150 %); read-only.

Done in 3E-2 (the search, the highlights and the marks): first a net: `tests/TextViewTests` has a search run on a real `CodeEditorPP`, written by the
code before the move (the 1623 lines of before did not change; 1682 in all, the same on every run): `Find` forward and back, with and without a wrap,
in a range (a match cut by its start or by its end on the last line, a caret above or below the range), the selection it makes (in terminal mode the
end of the selection too), the column it scrolls to (a match in the first screen, far right, near the end of its line, wider than the screen); the
cells the highlight of the matches fills, found by calling `PaintMatches` on a white bitmap and looking at a point of each cell (overlapping matches,
a regular expression, terminal mode, a long line searched only around the screen with its cap of 500 matches); the range and the style of the
highlight of the bracket at the caret (paired, unpaired, kept while a selection is made); the mark of every character (a hash of all, the list
below U+3000); the cells of the marks of a line (any ink in the cell, `PaintInvisibleCharacters` on a white bitmap) and of the grey line under the
tabs (`DrawToBitmap`, its exact colour at the middle of the row). Of 32 mutations of the old code 30 make it fail; two are equivalent changes (a
space is white space anyway; a match left of the row is drawn only left of the view). Then `CodeEditing/TextSearch.cs` (public static: `TryFind`,
the search of `ReadonlyTextBrowser.Find`; `ScrollColumnFor`, the column it scrolled to; `RowMatches`, the matches `PaintMatches` filled, as
`MatchSpan`s), `CodeEditing/BracketPair.cs` (public static: `Find`, what `CodeEditorPP.OnSelectionChanged` decided, as a `BracketPairKind` the view
maps to its styles, with `Keep` for a bracket token without text) and `CodeEditing/BlankMarks.cs` (public static: `Marker`, the old
`GetInvisibleCharacterMarker`, and `LineMarks`, the walk of `PaintInvisibleCharacters`); the code is as it was, the views select, scroll, keep the
highlights and paint. The grey line under the tabs stays in the painting of a line of the WinForms view. Not word for word: the matches of a row
are all found before they are painted, so a matcher that throws in a row stops the highlight before that row instead of after its first matches.
Every golden and stand passes unchanged; `EditorTextTests` reads the marks in their new file. `tests/TextSearchTests` (net8.0, compiled like
`ModelBuildTests`, 100 checks) drives the three; of 48 mutations 42 make a check fail (two after checks were added: the columns of a long line
after a tab, a match that ends where the row starts), six are equivalent changes (a document always has a line; a line out of the text has no
text, so a range past the text needs no clamp; for a match as wide as the screen its middle and its start give the same column; the token found
is always on the caret's line; one more turn past the last column adds no mark).

For the WPF view: `WpfTextEditor` is an `ISearchTarget` (as `EditableTextBrowser` is): `Find` through `TextSearch` (the caret at the match, the
selection to its end, the view scrolled to `ScrollColumnFor`; a failed search that clears the selection puts the caret at the end of the text,
the WinForms view put it below the last line), `ReplaceAll`, `SetMatchHighlight` (the matches of a row drawn from `RowMatches`), `AddHighlight`,
`SetHighlight`, `RemoveHighlight` (the fill and a frame of a pixel in the colours of the style; the text colour of a style is not used yet),
`GetLineText`, `GetRangeText` and `RangeChanged`. It keeps the highlight of the pair of the bracket at the caret as `CodeEditorPP` does (`BracketPair`,
the same styles, updated with the state of the caret) and draws the marks of the blanks (`ShowAllCharacters`, `BlankMarks`, in DarkGray; a mark
the font has no glyph for is drawn as "?") and the grey line under the tabs (`ShowTabs`). The lab has switches for both marks and a find box
(Enter, Shift+Enter, every match highlighted). Manual check on Windows (the lab): the find box forward and back with a wrap, a query that is not
there, the highlight of all matches while scrolling sideways and in a file of some MB with a long line; the pair of a bracket as the caret moves
past `(`, `)`, `[`, `{` and an unpaired one, and kept while a selection is made; All characters with spaces, tabs, a no-break space, a control
character and the line ends (not after the last line); Tab lines with tabs at the start and in the middle of a line; word wrap on with both;
the light and the dark theme.

Done in 3E-3 (several cursors): first a net: `tests/TextViewTests` has a cursor run on a real `CodeEditorPP`, written by the code before the move (the
1682 lines of before did not change; 1734 in all, the same on every run; the cursors are read from the session by reflection): Ctrl+D and Ctrl+Shift+L,
Ctrl+Alt+Up and Down by their keys; typing, Enter, Backspace (also a level of spaces), Tab with spaces and with a tab, a control character; the keys
that move the cursors with Shift and Ctrl, Ctrl+Backspace, Delete and Ctrl+Delete; the keys that go back to one cursor (Escape, the page keys,
Ctrl+A, Ctrl+Home, Ctrl+End, Ctrl+Z, Ctrl+Y), a command for one cursor (duplicate, zoom), a caret set by something of one cursor and a click; the
undo step of an edit; Copy, Cut and the paste of a line for each cursor and of one text for all; read-only. Alt+click is not in it (the view reads
`Control.ModifierKeys`). Of 29 mutations of the old code 24 make it fail; five are equivalent changes (the same set shown again; an empty edit
made; the cursors not brought into the text, which the plans never leave; PageUp left to the view, which clears the cursors when it moves the
caret; an empty paste, which the view never hands on). Then `CodeEditing/MultiCursorSession.cs` (public) holds what `CodeEditorPP.MultiCursor`
decided: the extra cursors, the main one taken from the caret and the selection, the column of the vertical moves, the whole-word mode of Ctrl+D,
the anchor of Alt+Shift+click, `IsMultiCursorCommand`, `ClearExtraCursors`, `OnViewChanged` (a change that is not the cursors' own clears them),
`AddNextOccurrence`, `SelectAllOccurrences`, `AddCursorAbove`/`Below`, `TypeCharacter`, `KeyDown` (as a `CursorKeyResult`), `TryCopyCursors`,
`ForClipboard`, `TryCut`, `TryPaste`, `AltClick`, and the plans of `MultiCursor` made as one undo step, through `IMultiCursorHost` (the document, the
caret, the selection, whether the cursors can edit, the indentation settings, `SetSelection`, `SetCaret`, `ReplaceText`, `ExecuteAsSingleUndoStep`,
`UpdateMaxLineWidth`, `HideCompletion`, `Redraw`), which `CodeEditorPP` implements; the code is as it was, the control keeps the keyboard, the
mouse, the clipboard and the painting of the other carets and selections. Every golden and stand passes unchanged. `tests/MultiCursorSessionTests`
(net8.0, compiled like `ModelBuildTests`, a fake view over a real document that logs the edits and the undo steps it is asked for, 110 checks) drives it; all 42 mutations of it
make a check fail.

For the WPF view: `WpfTextEditor` is the `IMultiCursorHost` and keeps a `MultiCursorSession` as `CodeEditorPP` does: a key binding that is not a
command of several cursors clears them first, then the keys of the session (with several cursors), the characters typed (Enter, Tab and Backspace
as characters too) at every cursor, copy, cut and paste through the session, Alt+click and Alt+Shift+click (no selection by the mouse then), a click
that clears the cursors and is the anchor of the next Alt+Shift+click, and `OnViewChanged` where the WinForms view called it (a caret set, a
selection set, a line or a range changed); a new document clears them. The other selections are drawn as the background of the main one (not as in
the WinForms view: the text in them keeps the colours of its syntax, and a selected line break is a cell), the other carets as bars while the caret shows. The lab shows the number of cursors. Manual check
on Windows (the lab): Ctrl+D on a word and again, Ctrl+Shift+L, Ctrl+Alt+Up and Down, Alt+click to add and to take away a cursor, Alt+Shift+click
down and up; typing, Enter, Tab, Backspace, Delete, Ctrl+Backspace, the arrows with Shift and Ctrl, Home and End at every cursor, then one Ctrl+Z;
Copy and a paste of one line for each cursor, a paste of one text, Cut; Escape, a click, PageDown and Ctrl+Shift+D go back to one cursor; the cursors
scrolled sideways and in wrapped lines; read-only.

Done in 3E-4 (the editor tab on the WPF text view), in three parts. 3E-4a: `RemoteFileEditor` is no longer a `CodeEditorPP`: it is given its text view
through `Controls/IRemoteTextView` (the calls it made on its base class: the document, the modified state of the text, read-only, the caret and the
selection, the version and the saved mark of the undo history, a new document, the messages and their colours, the folds, the completion language,
copy, cut, paste, delete and Select all, the colours and the font a hex view takes and their change, closing, posting to the thread of the view, the
questions of the loading and the owner of the message boxes) and makes the WinForms one, `Controls/RemoteTextView : CodeEditorPP`, when it is given
none (then it may make its own `HexEditorControl` too, which that view hosts). `RemoteTextView` is the old base with what the editor overrode of it:
the modified state, copy, cut, paste, delete and Select all go to the editor (which sends them to the hex view while that is shown), the comment
style is the editor's, and a change of the colours or the font goes to the hosted hex control and to the editor; the posts, the dispatcher and the
prompt of the loading are the old ones of the control. The editor is `IDisposable` (it closes its view) and has a `Tag`. The Mono stand passed
unchanged (486 checks), only its accessors of the view following (`TextControl`). 3E-4b: what `RemoteFileEditorPanel` decided about the commands of the
tab is `Controls/RemoteFileEditorCommands.cs` (the events of the commands the main window runs, `RunCommand`, `RunTransform`, `CaptureState`,
`CommandState`, `RefreshState` with `StateChanged`, `ScheduleRefresh` posted by the tab, the subscriptions to the editor and its hex view), the members as
they were (compared text for text), with the text view behind `IRemoteTextView`, which got the members they read and run (undo and redo, the marks,
the wrap, the lines, the selected lines, the transforms); the panel keeps its WinForms context menu, which runs them and follows their state, and the
WPF tool bar, the WPF hex context menu, the status source, the tab and the main window use them. The old panel cannot be made under Mono, so no net
of the old code drove it; the stand got a `commands` scenario after the move (the commands on the real editor and file: the state of a file just
loaded, a refresh asked for three times posted once, a disabled command, the marks and the wrap with their events, the commands that go to the main
window, Sort Ascending with Undo and Redo, the hex view with the inspector and a dialog, Reload with its question and without a change, the text of an
empty, a one-letter and a line-break file; 554 checks on both servers), and all 8 mutations of the commands make it fail (two after cases were added).
`EditorTextTests` reads the toggle of the marks and the editor's calls on its text view in their new places. 3E-4c (new code, not a move): the tab gives
the editor the WPF text view (`WpfTextEditor` is an `IRemoteTextView`: `Close`, `IsClosed` and `Closed`, `GoToPosition`, `CopyAll`, `LineCount`, the
appearance for the hex view raised by `SetColors`, `SetFont` and the zoom, the folds of a reload, and `GUI/TextView/WpfViewDispatcher` for the posts and the
questions of the loading on its dispatcher, nothing once it is closed) and shows it and the WPF hex view in one grid: no `WindowsFormsHost` is left in
the tab, and its scroll bars are those the views draw (the tab's are null). The tab owns the `RemoteFileEditorCommands` (refreshed after the view's
`StateChanged`, `RangeChanged` and `KeyUp`), the tool bar, the status bar, the search bar on the WPF view and its context menu
(`GUI/TextView/TextViewContextMenu`: the commands of the WinForms menu and the Text submenu). The main window gives the WPF view the settings (the
colours, Courier New of 12 points at the zoom, as the WinForms editor had, the tab size, the indentation, the line numbers, the marks, the wrap, the
completion), the colours of the syntax (`SetSyntax`) and the zoom of all the tabs, keeps the keys of the old `KeyDown` handler of the editor (Ctrl+S,
Ctrl+O, Ctrl+W, Ctrl+H, Ctrl+G, F5, F3 and Shift+F3) and Ctrl+Tab on `PreviewKeyDown`, the release of Ctrl on `KeyUp`, the edges of the minimalistic mode
on `MouseMove` and the title on `ModifiedChanged`; copy, paste and copy all of the main window go to the editor of an editor tab, and
`ITabContext.EditorControl` (a WinForms text view, which no tab has now) is gone. The WinForms view and panel stay for the Mono stand until 3E-5. Not as
before: the selections of the other cursors (3E-3), and the editor's own context menu of the WPF view is replaced by the tab's. Manual check on
Windows, in a file tab: the checklists of 3D-2 to 3E-3 in the tab; opening a file (a new one, one without the right to write, a large one with its
question, a preview), the draft question after a lost connection, Ctrl+S and the save in the background, a conflict, sudo, the check of a script with its
messages in the text and the caret on the problem, the offer to reload a service, Reload from the tool bar with and without a change, the encodings
and the line ends from the status bar, Show Changes, Run (F5) and Follow Log, Ctrl+F, Ctrl+H, F3 and Shift+F3 with the search bar, Ctrl+G, Ctrl+W
with unsaved changes, Ctrl+Tab between tabs, the context menu (each item, the Text submenu), the hex view and back (Ctrl+S, Ctrl+F, Ctrl+G and F3 in it),
the zoom shared by the tabs, the theme switched while a tab is open, the settings window applied to an open tab, and the minimalistic mode at the edges.

Done in 3E-5 (the WinForms editor removed), in three parts. 3E-5b (first, so that the hex view it makes could serve 3E-5a): `tests/HexEditorModelSnapshots`
(net8.0, in CI) runs the script of the Mono stand of the hex control (`HexEditorControlTests`: six seeded runs of keys and calls; the layout, the mouse,
the wheel, the tool tip and the colours of the shown view; the submenus; the data inspector) on the models of the hex editor through `HexReplayView` and
`HexReplayInspector`, which do what the control and its inspector did around them without a window (the code of the control with the painting, the
timers, the tool tip and the clipboard of the system left out, and a focus that a shown window gives and takes), and compares every line with the golden
file the control wrote, copied as it was: all 3771 lines match. Two things are not compared word for word: `states=` in the two runs that showed the
control in a form (it counted the focus events of the window too), and a floating point value of the inspector, which Mono writes with 15 or 17 digits
and .NET 8 as the shortest text that reads back the same (5 lines, compared as numbers). Of 6 mutations of the hex models 5 make it fail; the sixth (the
first row chosen when the inspector gets the focus) cannot be seen from the script, which always chose a row first. 3E-5a: the Mono stand of the remote
editor (`RemoteFileEditorIntegrationTests`) gives the editor views of its own instead of the WinForms ones: `TestTextView` (the document, the undo history
and the edit commands, the line commands, the selection, the search, the folds and the messages of the models; the posts and the questions of the loading
reach the UI thread through a control that only marshals) and `HexReplayView`; every editor gets a place, whose state stands for the `Visible` of the
editor's old hex control. The two checks that named the WinForms controls became one (the text view is the one given): 552 checks on both servers.
3E-5c: gone are `ReadonlyTextBrowser` (with `.Wrap`, `.DragScroll`, `.Navigation`), `ReadonlySmartScrollBrowser`, `EditableTextBrowser` (with `.Edits`),
`TextEditorControl` (with `.Typing`), `CodeEditorPP` (with `.Completion`, `.LineEditing`, `.MultiCursor`), `RemoteTextView`, `RemoteFileEditorPanel`,
`HexEditorControl`, `HexEditorMenu`, `HexDataInspector`, `ControlUiDispatcher`, `ControlUserPrompt`, `VirtualRowLayout`, `KeyShortcutText`,
`DpiAwareControl`, the whole of `Controls/TextView/` (the carets, the margins, the fold painter, the message view, the brushes of the highlights, the
WinForms popups of the completion and their factories), `SuggestionListView`, `TextHintControl`, `ArgumentLabel`, `Forms/SuggestionListForm`,
`Forms/TextHintForm` and `GUI/Backend/WPFScrollBar` (the scroll bars of the WinForms editor in the tab) with its style; and the stands that drove them:
`TextViewTests` (its logic is checked on the models by `CaretNavigatorTests`, `TextViewLayoutTests`, `LineCommandTests`, `TextSearchTests`,
`MultiCursorSessionTests`, `EditorCompletionModelTests` and `FoldingTests`; it is not replayed: the middle way chosen for 3E-5), `HexEditorControlTests` (replayed, above)
and `VirtualRowTests` (the row layout of the WinForms view; the rows of the WPF view are `TextViewLayoutTests`); `ModelInteropTests` lost the checks of
the brushes and of the key names of a shortcut (and `KeyShortcutGolden.txt`). `RemoteFileEditor` takes both views and makes none (`TextControl` and
`HexEditorControl` are gone); `HexSearchEventArgs` is in `GUI/HexView`, `HexDialogRequestedEventArgs` in `Controls/`; `DpiScaling` no longer looks for
text views to scale. `EditorTextTests` reads the WPF view and its menu where it read the WinForms view, panel and code editor. The number of test
projects in the first section is 139 now. What only the removed stands checked (the keys, the mouse, the painting and the focus of the WinForms views)
has no stand: the WPF views are checked by hand (the lists of 3B-2 to 3E-4).
