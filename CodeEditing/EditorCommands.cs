using SmarTTY.TextEditing;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

public class EditorCommands
{
    public class DelegateCommand : KeyBindingMap.EditorCommand
    {
        public delegate void CommandAction(IEditorCommandTarget codeEditorPP);

        private CommandAction _action;

        public DelegateCommand(string name, string description, CommandAction callback)
            : base(name, description)
        {
            this._action = callback;
        }

        public override void Execute(object target)
        {
            _action(target as IEditorCommandTarget);
        }
    }

    public static List<KeyBindingMap.EditorCommand> All = new List<KeyBindingMap.EditorCommand>
    {
        new DelegateCommand("ShowCompletion", "Show the completion popup", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.ShowCompletion();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.Space
            }
        },
        new DelegateCommand("RetokenizeDocument", "Rebuild token pairing", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.Retokenize();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.R
            }
        },
        new DelegateCommand("GoToOpeningRoundBracket", "Go to next '('", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: false, (string tokenText) => tokenText == "(");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = VirtualKey.D9
            }
        },
        new DelegateCommand("SelectToOpeningRoundBracket", "Select to next '('", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: true, (string tokenText) => tokenText == "(");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = VirtualKey.D9
            }
        },
        new DelegateCommand("GoToClosingRoundBracket", "Go to next ')'", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: false, (string tokenText) => tokenText == ")");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = VirtualKey.D0
            }
        },
        new DelegateCommand("SelectToClosingRoundBracket", "Select to next ')'", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: true, (string tokenText) => tokenText == ")");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = VirtualKey.D0
            }
        },
        new DelegateCommand("GoToOpeningCurlyBracket", "Go to next '{'", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: false, (string tokenText) => tokenText == "{");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = VirtualKey.OemOpenBrackets
            }
        },
        new DelegateCommand("SelectToOpeningCurlyBracket", "Select to next '{'", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: true, (string tokenText) => tokenText == "{");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Alt = true,
                Key = VirtualKey.OemOpenBrackets
            }
        },
        new DelegateCommand("GoToClosingCurlyBracket", "Go to next '}'", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: false, (string tokenText) => tokenText == "}");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = VirtualKey.OemCloseBrackets
            }
        },
        new DelegateCommand("SelectToClosingCurlyBracket", "Select to next '}'", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: true, (string tokenText) => tokenText == "}");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Alt = true,
                Key = VirtualKey.OemCloseBrackets
            }
        },
        new DelegateCommand("GoToOpeningSquareBracket", "Go to next '['", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: false, (string tokenText) => tokenText == "[");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.OemOpenBrackets
            }
        },
        new DelegateCommand("SelectToOpeningSquareBracket", "Select to next '['", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: true, (string tokenText) => tokenText == "[");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = VirtualKey.OemOpenBrackets
            }
        },
        new DelegateCommand("GoToClosingSquareBracket", "Go to next ']'", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: false, (string tokenText) => tokenText == "]");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.OemCloseBrackets
            }
        },
        new DelegateCommand("SelectToClosingSquareBracket", "Select to next ']'", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: true, (string tokenText) => tokenText == "]");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = VirtualKey.OemCloseBrackets
            }
        },
        new DelegateCommand("GoToOpeningAngularBracket", "Go to next '<'", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: false, (string tokenText) => tokenText == "<");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.Oemcomma
            }
        },
        new DelegateCommand("SelectToOpeningAngularBracket", "Select to next '<'", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: true, (string tokenText) => tokenText == "<");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = VirtualKey.Oemcomma
            }
        },
        new DelegateCommand("GoToClosingAngularBracket", "Go to next '>'", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: false, (string tokenText) => tokenText == ">");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.OemPeriod
            }
        },
        new DelegateCommand("SelectToClosingAngularBracket", "Select to next '>'", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: true, (string tokenText) => tokenText == ">");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = VirtualKey.OemPeriod
            }
        },
        new DelegateCommand("ExpandHiddenBlockNearCursor", "Expand a fold", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.ExpandCurrentFold();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = VirtualKey.Right
            }
        },
        new DelegateCommand("CollapseHiddenBlockNearCursor", "Collapse a fold", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.CollapseCurrentFold();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = VirtualKey.Left
            }
        },
        new DelegateCommand("AddNextOccurrence", "Add the next occurrence of the word to the selection (multiple cursors)", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.AddNextOccurrence();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.D
            }
        },
        new DelegateCommand("SelectAllOccurrences", "Select all occurrences of the word (multiple cursors)", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.SelectAllOccurrences();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = VirtualKey.L
            }
        },
        new DelegateCommand("AddCursorAbove", "Add a cursor on the line above", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.AddCursorAbove();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = VirtualKey.Up
            }
        },
        new DelegateCommand("AddCursorBelow", "Add a cursor on the line below", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.AddCursorBelow();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = VirtualKey.Down
            }
        },
        new DelegateCommand("DuplicateLines", "Duplicate line", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.DuplicateLines();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = VirtualKey.D
            }
        },
        new DelegateCommand("MoveLinesUp", "Move line up", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveLines(-1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Alt = true,
                Key = VirtualKey.Up
            }
        },
        new DelegateCommand("MoveLinesDown", "Move line down", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.MoveLines(1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Alt = true,
                Key = VirtualKey.Down
            }
        },
        new DelegateCommand("DeleteLines", "Delete line", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.DeleteLines();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = VirtualKey.K
            }
        },
        new DelegateCommand("ToggleComment", "Comment / uncomment", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.ToggleComment();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.OemQuestion
            }
        },
        new DelegateCommand("ToggleCommentNumpad", "Comment / uncomment (numpad)", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.ToggleComment();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.Divide
            }
        },
        new DelegateCommand("ToggleCommentShiftedDigit", "Comment / uncomment (Ctrl+/ on layouts where / is Shift+7)", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.ToggleComment();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = VirtualKey.D7
            }
        },
        new DelegateCommand("ZoomIn", "Zoom in", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.ZoomBy(1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.Oemplus
            }
        },
        new DelegateCommand("ZoomInShifted", "Zoom in (Ctrl+Plus)", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.ZoomBy(1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = VirtualKey.Oemplus
            }
        },
        new DelegateCommand("ZoomInNumpad", "Zoom in (numpad)", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.ZoomBy(1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.Add
            }
        },
        new DelegateCommand("ZoomOut", "Zoom out", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.ZoomBy(-1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.OemMinus
            }
        },
        new DelegateCommand("ZoomOutNumpad", "Zoom out (numpad)", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.ZoomBy(-1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.Subtract
            }
        },
        new DelegateCommand("ZoomReset", "Reset zoom", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.ResetZoom();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.D0
            }
        },
        new DelegateCommand("ZoomResetNumpad", "Reset zoom (numpad)", (IEditorCommandTarget codeEditorPP) =>
        {
            codeEditorPP.ResetZoom();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = VirtualKey.NumPad0
            }
        }
    };
}
