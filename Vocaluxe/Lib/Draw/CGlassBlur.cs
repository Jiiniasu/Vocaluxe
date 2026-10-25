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

using System;
using System.Drawing;

namespace Vocaluxe.Lib.Draw
{
    /// <summary>
    ///     CPU-side "glass twin" builder for frosted-glass backdrops: downscales a full-resolution BGRA video
    ///     frame to a small texture (256 px long edge) and box-blurs it, so glass panels can be drawn against
    ///     the blurred copy instead of the sharp video. No GL readback needed - the frame pixels are already
    ///     on the CPU.
    /// 
    ///     All buffers are static and reused between calls: <see cref="Process" /> must be called from a
    ///     single thread (the decoder's reader thread) and the returned array must be consumed (uploaded to a
    ///     texture) before the next call.
    /// </summary>
    public static class CGlassBlur
    {
        private const int _TargetLongEdge = 256;
        private const int _MaxRadius = 6; // [px at target size] blur radius at 100 % strength

        private static byte[] _Output; // final blurred image, returned to the caller (reused on the next call!)
        private static byte[] _Mid;    // subsampled intermediate image (~2x target size at most)
        private static byte[] _Temp;   // scratch buffer for the horizontal blur pass
        private static int[] _Accum;   // per-target-row channel accumulators for the box downscale
        private static int[] _ColMap;  // source x -> target x mapping for the box downscale
        private static int[] _RowSpan; // number of source rows each target row covers
        private static int[] _ColSpan; // number of source columns each target column covers

        /// <summary>
        ///     Computes the glass twin size (long edge capped at 256 px) for a source size.
        /// </summary>
        public static Size CalcTargetSize(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return new Size(0, 0);

            int longEdge = Math.Max(width, height);
            if (longEdge <= _TargetLongEdge)
                return new Size(width, height);

            float scale = (float)_TargetLongEdge / (float)longEdge;
            return new Size(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }

        /// <summary>
        ///     Processes a BGRA frame: downscales it to <see cref="CalcTargetSize" /> and box-blurs it according
        ///     to the strength.
        /// </summary>
        /// <param name="bgra">Packed BGRA pixels, row-major, width * height * 4 bytes</param>
        /// <param name="width">Source width in px</param>
        /// <param name="height">Source height in px</param>
        /// <param name="strength">Blur strength 0..100 (0 = downscale only, no extra blur)</param>
        /// <returns>Reused buffer holding the blurred BGRA data. Must be consumed before the next
        ///     <see cref="Process" /> call. Null on invalid input.</returns>
        public static byte[] Process(byte[] bgra, int width, int height, int strength)
        {
            if (bgra == null || width < 1 || height < 1 || bgra.Length < width * height * 4)
                return null;
            if (strength < 0)
                strength = 0;
            else if (strength > 100)
                strength = 100;

            Size target = CalcTargetSize(width, height);
            int dstW = target.Width;
            int dstH = target.Height;
            int dstBytes = dstW * dstH * 4;
            if (_Output == null || _Output.Length < dstBytes)
                _Output = new byte[dstBytes];

            if (dstW == width && dstH == height)
            {
                //Source is already small enough, only blurring is needed
                Buffer.BlockCopy(bgra, 0, _Output, 0, dstBytes);
            }
            else
            {
                //Subsampling keeps the number of source pixels to average small (1/stride^2) - a full box
                //filter over 1080p would blow the 2ms budget. The box downscale from the intermediate stays
                //exact, so strength 0 still looks like a clean soft copy, not a shimmering sample.
                int stride = Math.Max(2, Math.Max(width, height) / (2 * _TargetLongEdge));
                int midW = (width + stride - 1) / stride;
                int midH = (height + stride - 1) / stride;
                if (midW < dstW || midH < dstH)
                    stride = 1; //intermediate would be smaller than the target, downscale directly

                if (stride > 1)
                {
                    if (_Mid == null || _Mid.Length < midW * midH * 4)
                        _Mid = new byte[midW * midH * 4];
                    _Subsample(bgra, width, height, stride, _Mid, midW, midH);
                    _BoxDownscale(_Mid, midW, midH, _Output, dstW, dstH);
                }
                else
                    _BoxDownscale(bgra, width, height, _Output, dstW, dstH);
            }

            int radius = (int)Math.Round((float)strength * _MaxRadius / 100f);
            if (radius > 0)
            {
                if (_Temp == null || _Temp.Length < dstBytes)
                    _Temp = new byte[dstBytes];
                _BlurHorizontal(_Output, _Temp, dstW, dstH, radius);
                _BlurVertical(_Temp, _Output, dstW, dstH, radius);
            }
            return _Output;
        }

        /// <summary>
        ///     Copies every stride-th pixel (top-left of each stride x stride block) into the intermediate image.
        /// </summary>
        private static void _Subsample(byte[] src, int srcW, int srcH, int stride, byte[] dst, int dstW, int dstH)
        {
            for (int dy = 0; dy < dstH; dy++)
            {
                int s = dy * stride * srcW * 4;
                int d = dy * dstW * 4;
                for (int dx = 0; dx < dstW; dx++)
                {
                    int si = s + dx * stride * 4;
                    dst[d] = src[si];
                    dst[d + 1] = src[si + 1];
                    dst[d + 2] = src[si + 2];
                    dst[d + 3] = src[si + 3];
                    d += 4;
                }
            }
        }

        /// <summary>
        ///     Exact box downscale: each target pixel is the average of the source block it covers
        ///     (blockW x blockH, up to ~2x2 for typical strides).
        /// </summary>
        private static void _BoxDownscale(byte[] src, int srcW, int srcH, byte[] dst, int dstW, int dstH)
        {
            if (_ColMap == null || _ColMap.Length < srcW)
                _ColMap = new int[srcW];
            if (_ColSpan == null || _ColSpan.Length < dstW)
                _ColSpan = new int[dstW];
            if (_RowSpan == null || _RowSpan.Length < dstH)
                _RowSpan = new int[dstH];
            if (_Accum == null || _Accum.Length < dstW * 4)
                _Accum = new int[dstW * 4];

            int colStart = 0;
            for (int dx = 0; dx < dstW; dx++)
            {
                int colEnd = (dx + 1) * srcW / dstW;
                _ColSpan[dx] = colEnd - colStart;
                for (int x = colStart; x < colEnd; x++)
                    _ColMap[x] = dx;
                colStart = colEnd;
            }
            int rowStart = 0;
            for (int dy = 0; dy < dstH; dy++)
            {
                int rowEnd = (dy + 1) * srcH / dstH;
                _RowSpan[dy] = rowEnd - rowStart;
                rowStart = rowEnd;
            }

            for (int dy = 0; dy < dstH; dy++)
            {
                int top = dy * srcH / dstH;
                int bottom = (dy + 1) * srcH / dstH;

                for (int i = 0; i < dstW * 4; i++)
                    _Accum[i] = 0;

                for (int y = top; y < bottom; y++)
                {
                    int s = y * srcW * 4;
                    for (int x = 0; x < srcW; x++)
                    {
                        int a = _ColMap[x] * 4;
                        int si = s + x * 4;
                        _Accum[a] += src[si];
                        _Accum[a + 1] += src[si + 1];
                        _Accum[a + 2] += src[si + 2];
                        _Accum[a + 3] += src[si + 3];
                    }
                }

                int d = dy * dstW * 4;
                int rows = _RowSpan[dy];
                for (int dx = 0; dx < dstW; dx++)
                {
                    float inv = 1f / (float)(rows * _ColSpan[dx]);
                    dst[d] = (byte)((float)_Accum[dx * 4] * inv + 0.5f);
                    dst[d + 1] = (byte)((float)_Accum[dx * 4 + 1] * inv + 0.5f);
                    dst[d + 2] = (byte)((float)_Accum[dx * 4 + 2] * inv + 0.5f);
                    dst[d + 3] = (byte)((float)_Accum[dx * 4 + 3] * inv + 0.5f);
                    d += 4;
                }
            }
        }

        /// <summary>
        ///     First pass of the separable box blur: horizontal sliding window, edge pixels replicated.
        /// </summary>
        private static void _BlurHorizontal(byte[] src, byte[] dst, int w, int h, int radius)
        {
            int win = radius * 2 + 1;
            float inv = 1f / (float)win;
            for (int y = 0; y < h; y++)
            {
                int s = y * w * 4;
                int d = s;
                int sum0 = 0, sum1 = 0, sum2 = 0, sum3 = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int i = (k < 0 ? 0 : (k > w - 1 ? w - 1 : k)) * 4;
                    sum0 += src[s + i];
                    sum1 += src[s + i + 1];
                    sum2 += src[s + i + 2];
                    sum3 += src[s + i + 3];
                }
                for (int x = 0; x < w; x++)
                {
                    dst[d] = (byte)(sum0 * inv + 0.5f);
                    dst[d + 1] = (byte)(sum1 * inv + 0.5f);
                    dst[d + 2] = (byte)(sum2 * inv + 0.5f);
                    dst[d + 3] = (byte)(sum3 * inv + 0.5f);
                    d += 4;

                    int inX = x + radius + 1;
                    if (inX > w - 1)
                        inX = w - 1;
                    int outX = x - radius;
                    if (outX < 0)
                        outX = 0;
                    int iIn = inX * 4;
                    int iOut = outX * 4;
                    sum0 += src[s + iIn] - src[s + iOut];
                    sum1 += src[s + iIn + 1] - src[s + iOut + 1];
                    sum2 += src[s + iIn + 2] - src[s + iOut + 2];
                    sum3 += src[s + iIn + 3] - src[s + iOut + 3];
                }
            }
        }

        /// <summary>
        ///     Second pass of the separable box blur: vertical sliding window, edge pixels replicated.
        /// </summary>
        private static void _BlurVertical(byte[] src, byte[] dst, int w, int h, int radius)
        {
            int win = radius * 2 + 1;
            float inv = 1f / (float)win;
            int stride = w * 4;
            for (int x = 0; x < w; x++)
            {
                int c = x * 4;
                int sum0 = 0, sum1 = 0, sum2 = 0, sum3 = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int i = (k < 0 ? 0 : (k > h - 1 ? h - 1 : k)) * stride + c;
                    sum0 += src[i];
                    sum1 += src[i + 1];
                    sum2 += src[i + 2];
                    sum3 += src[i + 3];
                }
                for (int y = 0; y < h; y++)
                {
                    int d = y * stride + c;
                    dst[d] = (byte)(sum0 * inv + 0.5f);
                    dst[d + 1] = (byte)(sum1 * inv + 0.5f);
                    dst[d + 2] = (byte)(sum2 * inv + 0.5f);
                    dst[d + 3] = (byte)(sum3 * inv + 0.5f);

                    int inY = y + radius + 1;
                    if (inY > h - 1)
                        inY = h - 1;
                    int outY = y - radius;
                    if (outY < 0)
                        outY = 0;
                    int iIn = inY * stride + c;
                    int iOut = outY * stride + c;
                    sum0 += src[iIn] - src[iOut];
                    sum1 += src[iIn + 1] - src[iOut + 1];
                    sum2 += src[iIn + 2] - src[iOut + 2];
                    sum3 += src[iIn + 3] - src[iOut + 3];
                }
            }
        }
    }
}