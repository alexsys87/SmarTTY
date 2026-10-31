using System;
using System.Collections.Generic;
using System.Drawing;
using SmarTTY.TextEditing;

// What the message provider and the row layout are written against, in the shape the WinForms control has them. The control and
// System.Drawing.Common cannot be loaded by the harness; none of this is run by the checks (nothing is drawn).
namespace SmarTTY.Controls
{
    public class DocumentStub
    {
        public int IndexToColumn(int line, int index, int tabSize) { return index; }
    }

    public class ReadonlyTextBrowser
    {
        public DocumentStub Document = new DocumentStub();

        public int TabSize = 4;

        public interface IVirtualLine
        {
            void Draw(Graphics graphics, ReadonlyTextBrowser readonlyTextBrowser, int row, int firstColumn, int line, Rectangle rectangle, SizeF sizeF);
        }

        public interface ILineDecorator
        {
            string Transform(string text, out CharacterRange characterRange, out Brush brush);

            void DrawDecoration(Graphics graphics, SizeF sizeF, Rectangle rectangle, CharacterRange characterRange);

            TextDocument.TextPosition MapPosition(string text, TextDocument.TextPosition textPosition);
        }

        public interface IVirtualLineProvider
        {
            int GetTotalVirtualRows();

            int GetTotalHiddenLines();

            int GetNextSpecialLine(int line);

            int GetVirtualRowCount(int line);

            void GetVirtualLines(int line, List<IVirtualLine> list);

            int GetHiddenLineCount(int line);

            ILineDecorator GetDecorator(int line);

            bool OnCaretMoved(int line, int column);
        }
    }

    internal static class Icons
    {
        public static Image Load(string name) { return null; }
    }
}

namespace System.Drawing
{
    public enum GraphicsUnit { Pixel }

    public struct CharacterRange { }

    public class Brush { }

    public class SolidBrush : Brush, IDisposable
    {
        public SolidBrush(Color color) { }

        public void Dispose() { }
    }

    public class Pen : IDisposable
    {
        public Pen(Color color) { }

        public void Dispose() { }
    }

    public class FontFamily
    {
        public static FontFamily GenericSerif => new FontFamily();
    }

    public class Font : IDisposable
    {
        public Font(FontFamily family, float size) { }

        public void Dispose() { }
    }

    public class Image
    {
        public int Width;

        public int Height;
    }

    public class StringFormat : IDisposable
    {
        public StringFormat(StringFormat other) { }

        public static StringFormat GenericDefault => new StringFormat(null);

        public void Dispose() { }
    }

    public class Graphics
    {
        public SizeF MeasureString(string text, Font font) { return new SizeF(10, 10); }

        public void FillRectangle(Brush brush, Rectangle rectangle) { }

        public void DrawImage(Image image, Rectangle rectangle, int x, int y, int width, int height, GraphicsUnit unit, System.Drawing.Imaging.ImageAttributes attributes) { }

        public void DrawString(string text, Font font, Brush brush, RectangleF rectangle, StringFormat format) { }

        public void DrawRectangle(Pen pen, Rectangle rectangle) { }
    }
}

namespace System.Drawing.Imaging
{
    public class ImageAttributes : IDisposable
    {
        public void SetColorKey(Color low, Color high) { }

        public void Dispose() { }
    }
}
