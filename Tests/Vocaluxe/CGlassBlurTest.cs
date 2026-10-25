#region license
// This file is part of Vocaluxe.
// 
// Vocaluxe is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Vocaluxe is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using NUnit.Framework;
using System;
using System.Drawing;
using Vocaluxe.Lib.Draw;

namespace Tests.Vocaluxe
{
    [TestFixture]
    public class CGlassBlurTest
    {
        private static byte[] _Solid(int w, int h, byte b, byte g, byte r, byte a)
        {
            byte[] data = new byte[w * h * 4];
            for (int i = 0; i < data.Length; i += 4)
            {
                data[i] = b;
                data[i + 1] = g;
                data[i + 2] = r;
                data[i + 3] = a;
            }
            return data;
        }

        [Test]
        public void CalcTargetSize_CapsLongEdgeAt256()
        {
            Assert.AreEqual(new Size(256, 144), CGlassBlur.CalcTargetSize(1920, 1080));
            Assert.AreEqual(new Size(144, 256), CGlassBlur.CalcTargetSize(1080, 1920));
            Assert.AreEqual(new Size(256, 256), CGlassBlur.CalcTargetSize(3000, 3000));
        }

        [Test]
        public void CalcTargetSize_KeepsSmallImages()
        {
            Assert.AreEqual(new Size(40, 60), CGlassBlur.CalcTargetSize(40, 60));
            Assert.AreEqual(new Size(256, 256), CGlassBlur.CalcTargetSize(256, 256));
            Assert.AreEqual(new Size(1, 1), CGlassBlur.CalcTargetSize(1, 1));
        }

        [Test]
        public void CalcTargetSize_RejectsNonPositiveSizes()
        {
            Assert.AreEqual(new Size(0, 0), CGlassBlur.CalcTargetSize(0, 10));
            Assert.AreEqual(new Size(0, 0), CGlassBlur.CalcTargetSize(-5, 10));
        }

        [Test]
        public void Process_ReturnsNull_OnInvalidInput()
        {
            Assert.IsNull(CGlassBlur.Process(null, 10, 10, 50));
            Assert.IsNull(CGlassBlur.Process(new byte[100], 10, 10, 50));
            Assert.IsNull(CGlassBlur.Process(new byte[400], 0, 10, 50));
            Assert.IsNull(CGlassBlur.Process(new byte[400], 10, 0, 50));
        }

        [Test]
        public void Process_ReturnsAtLeastTargetBytes()
        {
            byte[] data = _Solid(1920, 1080, 10, 20, 30, 255);
            byte[] result = CGlassBlur.Process(data, 1920, 1080, 0);

            Size target = CGlassBlur.CalcTargetSize(1920, 1080);
            Assert.NotNull(result);
            Assert.GreaterOrEqual(result.Length, target.Width * target.Height * 4);
        }

        [Test]
        public void Process_PreservesSolidColor()
        {
            //A box downscale and a box blur of a solid image are exactly that solid
            byte[] data = _Solid(640, 360, 21, 67, 143, 255);
            for (int strength = 0; strength <= 100; strength += 25)
            {
                byte[] result = CGlassBlur.Process(data, 640, 360, strength);
                Size target = CGlassBlur.CalcTargetSize(640, 360);
                int bytes = target.Width * target.Height * 4;
                for (int i = 0; i < bytes; i += 4)
                {
                    Assert.AreEqual(21, result[i]);
                    Assert.AreEqual(67, result[i + 1]);
                    Assert.AreEqual(143, result[i + 2]);
                    Assert.AreEqual(255, result[i + 3]);
                }
            }
        }

        [Test]
        public void Process_StrengthZero_IsExactForSmallSources()
        {
            //No downscale (source fits the 256 px cap) and no blur: output must be byte-identical to the input
            int w = 128, h = 128;
            byte[] data = new byte[w * h * 4];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)((i * 7) % 251); //deterministic pattern
            byte[] result = CGlassBlur.Process(data, w, h, 0);
            for (int i = 0; i < data.Length; i++)
                Assert.AreEqual(data[i], result[i]);
        }

        [Test]
        public void Process_HigherStrength_SmoothsMore()
        {
            //High-frequency pattern on a small source (no downscale): more strength means lower total variation
            int w = 256, h = 256;
            byte[] data = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    byte v = ((x / 2 + y / 2) % 2) == 0 ? (byte)255 : (byte)0; //2 px checkerboard
                    data[i] = v;
                    data[i + 1] = v;
                    data[i + 2] = v;
                    data[i + 3] = 255;
                }

            float tvLow = _TotalVariation(CGlassBlur.Process(data, w, h, 10), w, h);
            float tvHigh = _TotalVariation(CGlassBlur.Process(data, w, h, 100), w, h);
            Assert.Less(tvHigh, tvLow);
        }

        private static float _TotalVariation(byte[] data, int w, int h)
        {
            float tv = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    int v = data[i] + data[i + 1] + data[i + 2];
                    if (x + 1 < w)
                        tv += Math.Abs(v - (data[i + 4] + data[i + 5] + data[i + 6]));
                    if (y + 1 < h)
                    {
                        int b = i + w * 4;
                        tv += Math.Abs(v - (data[b] + data[b + 1] + data[b + 2]));
                    }
                }
            return tv;
        }
    }
}