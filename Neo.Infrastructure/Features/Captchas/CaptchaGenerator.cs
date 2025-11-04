using System.Drawing;
using System.Drawing.Drawing2D;
using System.Security.Cryptography;

namespace Neo.Infrastructure.Features.Captchas;

public class CaptchaGenerator
{
    public enum CharStyle
    {
        NumbersOnly = 0,
        UpperCaseAlphabets = 1,
        LowerCaseAlphabets = 2,
        Alphabets = 3,
        AlphaNumerics = 4,
        PersianAlphaNumerics = 5
    }
    public enum ComplexityLevel
    {
        Medium = 0,
        High = 1
    }
    public class MkCaptcha(CharStyle characterStyle, int length, bool useDarkColors, int minSize, int maxSize, int width,
        int height, ComplexityLevel complexityLevel = ComplexityLevel.Medium)
    {
        private readonly Random _rand = new();

        // Draw a deformed character at this position.
        private int _previousAngle = 0;

        public (Bitmap captchaImage, string value) GenerateImageCaptcha()
        {
            var captchaValue = GetRandomStyleString(length);
            var bmp = MakeImageCaptcha(captchaValue, minSize, maxSize, width, height, complexityLevel);
            return (bmp, captchaValue);
        }
        private string GetRandomStyleString(int length)
        {
            const string alphanumericCharacters =
                "ABCDEFGHJKLMNOPQRSTUVWXYZ" +
                "abcdefghijkmnopqrstuvwxyz" +
                "0123456789";
            const string persianAlphaNumeric =
                "ا آ ب پ ت ث ج چ ح خ د ذ ر ز ژ س ش ص ض ط ظ ع غ ف ق ک گ ل م ن و ه ی" +
                "۱۲۳۴۵۶۷۸۹";
            const string upperCaseCharacters = "ABCDEFGHJKLMNOPQRSTUVWXYZ";
            const string lowerCaseCharacters = "abcdefghijkmnopqrstuvwxyz";
            const string alphabets = "ABCDEFGHJKLMNOPQRSTUVWXYZ" + "abcdefghijkmnopqrstuvwxyz";
            const string numbersOnly = "0123456789";

            return characterStyle switch
            {
                CharStyle.NumbersOnly => GetRandomString(length, numbersOnly),
                CharStyle.UpperCaseAlphabets => GetRandomString(length, upperCaseCharacters),
                CharStyle.LowerCaseAlphabets => GetRandomString(length, lowerCaseCharacters),
                CharStyle.AlphaNumerics => GetRandomString(length, alphanumericCharacters),
                CharStyle.PersianAlphaNumerics => GetRandomString(length, persianAlphaNumeric),
                CharStyle.Alphabets => GetRandomString(length, alphabets),
#pragma warning disable CA2208
                _ => throw new ArgumentOutOfRangeException
#pragma warning restore CA2208
                {
                    HelpLink = null,
                    HResult = 0,
                    Source = null
                }
            };
        }
        private string GetRandomString(int length, IEnumerable<char> characterSet)
        {
            switch (length)
            {
                case < 0:
                    throw new ArgumentException("length must not be negative", nameof(length));
                // 250 million chars ought to be enough for anybody
                case > int.MaxValue / 8:
                    throw new ArgumentException("length is too big", nameof(length));
            }

            if (characterSet == null)
                throw new ArgumentNullException(nameof(characterSet));

            var characterArray = characterSet.Distinct().ToArray();
            if (characterArray.Length == 0)
                throw new ArgumentException("characterSet must not be empty", nameof(characterSet));

            var bytes = new byte[length * 8];
            var result = new char[length];

            // تولید بایت‌های تصادفی ایمن
            RandomNumberGenerator.Fill(bytes); // پر کردن آرایه با داده‌های تصادفی

            for (var i = 0; i < length; i++)
            {
                var value = BitConverter.ToUInt64(bytes, i * 8);
                result[i] = characterArray[value % (uint) characterArray.Length];
            }

            return new string(result);
        }
        private static string GetRandomFont()
        {
            List<string> fonts = [
                "calibri",
                "DejaVuSans",
                "DejaVuSans Bold",
                "Times New Roman",
            ];

            var length = fonts.Count;
            return fonts[new Random().Next(length)];
        }
        // Make a captcha image for the text.
        private Bitmap MakeImageCaptcha(string text, int minSize, int maxSize, int width, int height, ComplexityLevel complexityLevel)
        {
            // Make the bitmap and associated Graphics object.
            var bitmap = new Bitmap(width, height);
            using var gr = Graphics.FromImage(bitmap);
            gr.SmoothingMode = SmoothingMode.HighQuality;
            //gr.Clear(Color.White);
            var backgroundColor = useDarkColors ? GetRandomDeepColor() : GetRandomLightColor();
            gr.Clear(backgroundColor);
            // See how much room is available for each character.
            var charWidth = width / text.Length;
            // Draw each character.
            for (var i = 0; i < text.Length; i++)
            {
                float fontSize = _rand.Next(minSize, maxSize);
                using var font = new Font(GetRandomFont(), fontSize, FontStyle.Bold);
                DrawCharacter(text.Substring(i, 1), gr, font, i * charWidth, charWidth, width, height);
            }

            if (width <= 120 && height <= 30) return bitmap;

            // draw disorder lines for properly large pictures
            var linePen = new Pen(new SolidBrush(Color.Black), 3);
            for (var i = 0; i < _rand.Next(3, 5); i++)
            {
                linePen.Color = GetRandomDeepColor();

                var startPoint = new Point(_rand.Next(0, width), _rand.Next(0, height));
                var endPoint = new Point(_rand.Next(0, width), _rand.Next(0, height));
                gr.DrawLine(linePen, startPoint, endPoint);

                if (complexityLevel != ComplexityLevel.High) continue;

                // make it harder
                var bezierPoint1 = new Point(_rand.Next(0, width), _rand.Next(0, height));
                var bezierPoint2 = new Point(_rand.Next(0, width), _rand.Next(0, height));

                gr.DrawBezier(linePen, startPoint, bezierPoint1, bezierPoint2, endPoint);
            }

            return bitmap;
        }
        private void DrawCharacter(string txt, Graphics gr, Font font, int xFactor, int charWidth, int width, int height)
        {
            // Center the text.
            using var stringFormat = new StringFormat();
            stringFormat.Alignment = StringAlignment.Center;
            stringFormat.LineAlignment = StringAlignment.Center;
            var rectangle = new RectangleF(xFactor, 0, charWidth, height);
            // Convert the text into a path.
            using var graphicsPath = new GraphicsPath();
            graphicsPath.AddString(txt, font.FontFamily, (int) font.Style, font.Size, rectangle, stringFormat);
            // Make random warping parameters.

            var x1 = (float) (xFactor + _rand.Next(charWidth) / 2);
            // ReSharper disable once PossibleLossOfFraction
            var y1 = (float) (_rand.Next(height) / 2);
            var x2 = (float) (xFactor + charWidth / 2 + _rand.Next(charWidth) / 2);
            var y2 = (float) (height / 2 + _rand.Next(height) / 2);
            PointF[] pts =
            [
                new PointF(
                     xFactor + _rand.Next(charWidth) / 4,
                     _rand.Next(height) / 4),
                new PointF(
                     xFactor + charWidth - _rand.Next(charWidth) / 4,
                     _rand.Next(height) / 4),
                new PointF(
                     xFactor + _rand.Next(charWidth) / 4,
                     height - _rand.Next(height) / 4),
                new PointF(
                     xFactor + charWidth - _rand.Next(charWidth) / 4,
                     height - _rand.Next(height) / 4)
            ];

            if (complexityLevel == ComplexityLevel.High)
            {
                var mat = new Matrix();
                graphicsPath.Warp(pts, rectangle, mat, WarpMode.Perspective, 0);
            }

            // Rotate a bit randomly.
            var dx = (float) (xFactor + charWidth / 2);
            // ReSharper disable once PossibleLossOfFraction
            var dy = (float) (height / 2);
            gr.TranslateTransform(-dx, -dy, MatrixOrder.Append);
            int angle;
            do
            {
                angle = _rand.Next(-30, 30);
            } while (Math.Abs(angle - _previousAngle) < 20);

            _previousAngle = angle;
            gr.RotateTransform(angle, MatrixOrder.Append);
            gr.TranslateTransform(dx, dy, MatrixOrder.Append);

            // Draw the text.
            gr.FillPath(PickRandomBrush(), graphicsPath);
            gr.ResetTransform();
        }
        private Brush PickRandomBrush()
        {
            Brush brush = new SolidBrush(!useDarkColors ? GetRandomDeepColor() : GetRandomLightColor());
            return brush;
        }
        private Color GetRandomDeepColor()
        {
            const int redLow = 160;
            const int greenLow = 100;
            const int blueLow = 160;
            return Color.FromArgb(_rand.Next(redLow), _rand.Next(greenLow), _rand.Next(blueLow));
        }
        private Color GetRandomLightColor()
        {
            const int low = 180;
            const int high = 255;

            var nRend = _rand.Next(high) % (high - low) + low;
            var nGreen = _rand.Next(high) % (high - low) + low;
            var nBlue = _rand.Next(high) % (high - low) + low;

            return Color.FromArgb(nRend, nGreen, nBlue);
        }
    }
}