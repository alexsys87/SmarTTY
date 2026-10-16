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

        public DelegateCommand(string text, string text2, CommandAction callback)
            : base(text, text2)
        {
            this._action = callback;
        }

        public override void Execute(object obj)
        {
            _action(obj as CodeEditorPP);
        }
    }

    public static List<KeyBindingMap.EditorCommand> All = new List<KeyBindingMap.EditorCommand>
    {
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
            codeEditorPP.MoveToToken(flag2: true, flag3: false, flag4: false, (string text) => text == "(");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.D9
            }
        },
        new DelegateCommand("SelectToOpeningRoundBracket", "Select to next '('", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(flag2: true, flag3: false, flag4: true, (string text) => text == "(");
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
            codeEditorPP.MoveToToken(flag2: false, flag3: false, flag4: false, (string text) => text == ")");
        })
        {
            DefaultShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = true,
                Key = Keys.D0
            }
        },
        new DelegateCommand("SelectToClosingRoundBracket", "Select to next ')'", (CodeEditorPP codeEditorPP) =>
        {
            codeEditorPP.MoveToToken(flag2: false, flag3: false, flag4: true, (string text) => text == ")");
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
            codeEditorPP.MoveToToken(flag2: true, flag3: false, flag4: false, (string text) => text == "{");
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
            codeEditorPP.MoveToToken(flag2: true, flag3: false, flag4: true, (string text) => text == "{");
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
            codeEditorPP.MoveToToken(flag2: false, flag3: false, flag4: false, (string text) => text == "}");
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
            codeEditorPP.MoveToToken(flag2: false, flag3: false, flag4: true, (string text) => text == "}");
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
            codeEditorPP.MoveToToken(flag2: true, flag3: false, flag4: false, (string text) => text == "[");
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
            codeEditorPP.MoveToToken(flag2: true, flag3: false, flag4: true, (string text) => text == "[");
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
            codeEditorPP.MoveToToken(flag2: false, flag3: false, flag4: false, (string text) => text == "]");
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
            codeEditorPP.MoveToToken(flag2: false, flag3: false, flag4: true, (string text) => text == "]");
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
            codeEditorPP.MoveToToken(flag2: true, flag3: false, flag4: false, (string text) => text == "<");
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
            codeEditorPP.MoveToToken(flag2: true, flag3: false, flag4: true, (string text) => text == "<");
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
            codeEditorPP.MoveToToken(flag2: false, flag3: false, flag4: false, (string text) => text == ">");
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
            codeEditorPP.MoveToToken(flag2: false, flag3: false, flag4: true, (string text) => text == ">");
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
        }
    };
}
