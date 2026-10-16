using System;
using System.Drawing;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Forms;

public partial class PaletteEditForm : OKCancelFormBase
{
    private class SwatchClickHandler
    {
        private int _colorIndex;

        private Panel _swatch;

        private bool _isBright;

        private PaletteEditForm _owner;

        public SwatchClickHandler(PaletteEditForm paletteEditForm, int value2, Panel panel, bool flag)
        {
            this._colorIndex = value2;
            _swatch = panel;
            this._isBright = flag;
            this._owner = paletteEditForm;
        }

        internal void OnClick(object sender, EventArgs e2)
        {
            _owner._colorDialog.Color = _swatch.BackColor;
            if (_owner._colorDialog.ShowDialog() == DialogResult.OK)
            {
                _swatch.BackColor = _owner._colorDialog.Color;
                if (this._isBright)
                {
                    _owner._palette.BrightColors[_colorIndex] = _owner._colorDialog.Color;
                }
                else
                {
                    _owner._palette.NormalColors[_colorIndex] = _owner._colorDialog.Color;
                }
            }
        }
    }

    private ConsolePalette _palette;

    public ConsolePalette Palette => _palette;

    public PaletteEditForm(ConsolePalette consolePalette)
    {
        InitializeComponent();
        _palette = consolePalette.Clone();
        if (_palette.NormalColors.Length < tableLayoutPanel1.RowCount - 1 || _palette.BrightColors.Length < tableLayoutPanel1.RowCount - 1)
        {
            _palette = ConsolePalette.PuTTY.Clone();
        }
        for (int num = 1; num < tableLayoutPanel1.RowCount; num++)
        {
            tableLayoutPanel1.Controls.Add(new Label
            {
                Text = (num - 1).ToString(),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, num);
            Panel panel = new Panel
            {
                BorderStyle = BorderStyle.Fixed3D,
                BackColor = _palette.NormalColors[num - 1]
            };
            panel.Click += new SwatchClickHandler(this, num - 1, panel, flag: false).OnClick;
            tableLayoutPanel1.Controls.Add(panel, 1, num);
            panel = new Panel
            {
                BorderStyle = BorderStyle.Fixed3D,
                BackColor = _palette.BrightColors[num - 1]
            };
            tableLayoutPanel1.Controls.Add(panel, 2, num);
            panel.Click += new SwatchClickHandler(this, num - 1, panel, flag: true).OnClick;
        }
    }

    private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        _palette = ConsolePalette.PuTTY.Clone();
        for (int num = 1; num < tableLayoutPanel1.RowCount; num++)
        {
            tableLayoutPanel1.GetControlFromPosition(1, num).BackColor = _palette.NormalColors[num - 1];
            tableLayoutPanel1.GetControlFromPosition(2, num).BackColor = _palette.BrightColors[num - 1];
        }
    }
}
