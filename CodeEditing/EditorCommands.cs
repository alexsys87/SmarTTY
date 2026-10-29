using SmarTTY.Controls;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SmarTTY.CodeEditing;

public class EditorCommands
{
    public class DelegateCommand : KeyBindingMap.EditorCommand
    {
        public delegate void CommandAction(CodeEditorPP codeEditorPP);

        private CommandAction _action;

        public DelegateCommand(string name, string description, CommandAction callback)
            : base(name, description)
        {
            this._action = callback;
        }

        public override void Execute(object target)
        {
            _action(target as CodeEditorPP);
        }
    }

    public static List<KeyBindingMap.EditorCommand> All = new List<KeyBindingMap.EditorCommand>
    {
        new DelegateCommand("ShowCompletion", "Show the completion popup", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.ShowCompletion();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.Space
            }
        },
        new DelegateCommand("RetokenizeDocument", "Rebuild token pairing", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.Retokenize();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.R
            }
        },
        new DelegateCommand("GoToOpeningRoundBracket", "Go to next '('", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: false, (string tokenText) => tokenText == "(");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = Keys.D9
            }
        },
        new DelegateCommand("SelectToOpeningRoundBracket", "Select to next '('", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: true, (string tokenText) => tokenText == "(");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = Keys.D9
            }
        },
        new DelegateCommand("GoToClosingRoundBracket", "Go to next ')'", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: false, (string tokenText) => tokenText == ")");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = Keys.D0
            }
        },
        new DelegateCommand("SelectToClosingRoundBracket", "Select to next ')'", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: true, (string tokenText) => tokenText == ")");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = Keys.D0
            }
        },
        new DelegateCommand("GoToOpeningCurlyBracket", "Go to next '{'", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: false, (string tokenText) => tokenText == "{");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = Keys.OemOpenBrackets
            }
        },
        new DelegateCommand("SelectToOpeningCurlyBracket", "Select to next '{'", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: true, (string tokenText) => tokenText == "{");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Alt = true,
                Key = Keys.OemOpenBrackets
            }
        },
        new DelegateCommand("GoToClosingCurlyBracket", "Go to next '}'", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: false, (string tokenText) => tokenText == "}");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = Keys.OemCloseBrackets
            }
        },
        new DelegateCommand("SelectToClosingCurlyBracket", "Select to next '}'", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: true, (string tokenText) => tokenText == "}");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Alt = true,
                Key = Keys.OemCloseBrackets
            }
        },
        new DelegateCommand("GoToOpeningSquareBracket", "Go to next '['", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: false, (string tokenText) => tokenText == "[");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.OemOpenBrackets
            }
        },
        new DelegateCommand("SelectToOpeningSquareBracket", "Select to next '['", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: true, (string tokenText) => tokenText == "[");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = Keys.OemOpenBrackets
            }
        },
        new DelegateCommand("GoToClosingSquareBracket", "Go to next ']'", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: false, (string tokenText) => tokenText == "]");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.OemCloseBrackets
            }
        },
        new DelegateCommand("SelectToClosingSquareBracket", "Select to next ']'", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: true, (string tokenText) => tokenText == "]");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = Keys.OemCloseBrackets
            }
        },
        new DelegateCommand("GoToOpeningAngularBracket", "Go to next '<'", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: false, (string tokenText) => tokenText == "<");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.Oemcomma
            }
        },
        new DelegateCommand("SelectToOpeningAngularBracket", "Select to next '<'", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: true, isOpening: false, selectText: true, (string tokenText) => tokenText == "<");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = Keys.Oemcomma
            }
        },
        new DelegateCommand("GoToClosingAngularBracket", "Go to next '>'", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: false, (string tokenText) => tokenText == ">");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.OemPeriod
            }
        },
        new DelegateCommand("SelectToClosingAngularBracket", "Select to next '>'", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(searchPrevious: false, isOpening: false, selectText: true, (string tokenText) => tokenText == ">");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = Keys.OemPeriod
            }
        },
        new DelegateCommand("ExpandHiddenBlockNearCursor", "Expand a fold", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.ExpandCurrentFold();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = Keys.Right
            }
        },
        new DelegateCommand("CollapseHiddenBlockNearCursor", "Collapse a fold", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.CollapseCurrentFold();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Alt = true,
                Key = Keys.Left
            }
        },
        new DelegateCommand("DuplicateLines", "Duplicate line", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.DuplicateLines();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = Keys.D
            }
        },
        new DelegateCommand("MoveLinesUp", "Move line up", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveLines(-1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Alt = true,
                Key = Keys.Up
            }
        },
        new DelegateCommand("MoveLinesDown", "Move line down", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveLines(1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Alt = true,
                Key = Keys.Down
            }
        },
        new DelegateCommand("DeleteLines", "Delete line", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.DeleteLines();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = Keys.K
            }
        },
        new DelegateCommand("ToggleComment", "Comment / uncomment", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.ToggleComment();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.OemQuestion
            }
        },
        new DelegateCommand("ToggleCommentNumpad", "Comment / uncomment (numpad)", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.ToggleComment();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.Divide
            }
        },
        new DelegateCommand("ToggleCommentShiftedDigit", "Comment / uncomment (Ctrl+/ on layouts where / is Shift+7)", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.ToggleComment();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = Keys.D7
            }
        },
        new DelegateCommand("ZoomIn", "Zoom in", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.ZoomBy(1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.Oemplus
            }
        },
        new DelegateCommand("ZoomInShifted", "Zoom in (Ctrl+Plus)", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.ZoomBy(1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Shift = true,
                Key = Keys.Oemplus
            }
        },
        new DelegateCommand("ZoomInNumpad", "Zoom in (numpad)", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.ZoomBy(1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.Add
            }
        },
        new DelegateCommand("ZoomOut", "Zoom out", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.ZoomBy(-1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.OemMinus
            }
        },
        new DelegateCommand("ZoomOutNumpad", "Zoom out (numpad)", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.ZoomBy(-1);
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.Subtract
            }
        },
        new DelegateCommand("ZoomReset", "Reset zoom", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.ResetZoom();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.D0
            }
        },
        new DelegateCommand("ZoomResetNumpad", "Reset zoom (numpad)", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.ResetZoom();
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.NumPad0
            }
        }
    };
}
