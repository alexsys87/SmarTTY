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

        public SwatchClickHandler(PaletteEditForm paletteEditForm, int colorIndex, Panel swatch, bool isBright)
        {
            this._colorIndex = colorIndex;
            _swatch = swatch;
            this._isBright = isBright;
            this._owner = paletteEditForm;
        }

        internal void OnClick(object sender, EventArgs e)
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
        FormIcons.Apply(this, "palette");
        _palette = consolePalette.Clone();
        if (_palette.NormalColors.Length < tableLayoutPanel1.RowCount - 1 || _palette.BrightColors.Length < tableLayoutPanel1.RowCount - 1)
        {
            _palette = ConsolePalette.PuTTY.Clone();
        }
        for (int rowIdx = 1; rowIdx < tableLayoutPanel1.RowCount; rowIdx++)
        {
            tableLayoutPanel1.Controls.Add(new Label
            {
                Text = (rowIdx - 1).ToString(),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, rowIdx);
            Panel panel = new Panel
            {
                BorderStyle = BorderStyle.Fixed3D,
                BackColor = _palette.NormalColors[rowIdx - 1]
            };
            panel.Click += new SwatchClickHandler(this, rowIdx - 1, panel, isBright: false).OnClick;
            tableLayoutPanel1.Controls.Add(panel, 1, rowIdx);
            panel = new Panel
            {
                BorderStyle = BorderStyle.Fixed3D,
                BackColor = _palette.BrightColors[rowIdx - 1]
            };
            tableLayoutPanel1.Controls.Add(panel, 2, rowIdx);
            panel.Click += new SwatchClickHandler(this, rowIdx - 1, panel, isBright: true).OnClick;
        }
    }

    private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        _palette = ConsolePalette.PuTTY.Clone();
        for (int rowIdx = 1; rowIdx < tableLayoutPanel1.RowCount; rowIdx++)
        {
            tableLayoutPanel1.GetControlFromPosition(1, rowIdx).BackColor = _palette.NormalColors[rowIdx - 1];
            tableLayoutPanel1.GetControlFromPosition(2, rowIdx).BackColor = _palette.BrightColors[rowIdx - 1];
        }
    }
}
