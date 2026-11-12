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

using System.Drawing;
using VocaluxeLib;
using VocaluxeLib.Draw;

namespace Vocaluxe.Lib.Draw
{
    interface IDraw
    {
        bool Init();
        void ShowSplash();
        void UpdateSplash(float progress, string status = null, string hint = null);
        bool WantClose { get; }
        bool PollKey(out SKeyEvent keyEvent);
        void DrainInput();
        void MainLoop();
        void Close();

        int GetScreenWidth();
        int GetScreenHeight();

        int GetTextureCount();

        /// <summary>
        ///     The maximum side size a cover should be generated/cached at, resolved from the display the app
        ///     presents to and the GPU's video memory (TR_CONFIG_TEXTURE_AUTO) or the CoverSize knob otherwise.
        /// </summary>
        int GetMaxCoverSide();

        CTextureRef CopyScreen();
        void CopyScreen(ref CTextureRef texture);
        void MakeScreenShot();

        CTextureRef AddTexture(string texturePath);
        CTextureRef AddTexture(int w, int h, byte[] data);
        void UpdateTexture(CTextureRef texture, int w, int h, byte[] data);
        CTextureRef EnqueueTexture(string texturePath);
        CTextureRef EnqueueTexture(int w, int h, byte[] data);
        void EnqueueTextureUpdate(CTextureRef textureRef, int w, int h, byte[] data);
        CTextureRef CopyTexture(CTextureRef textureRef);
        byte[] CopyTextureData(CTextureRef textureRef, out int width, out int height);
        void RemoveTexture(ref CTextureRef texture);

        // Basic Draw Methods
        void DrawRect(SColorF color, SRectF rect, bool allMonitors = true);
        void DrawRectReflection(SColorF color, SRectF rect, float space, float height, bool allMonitors = true);

        void DrawTexture(CTextureRef texture, SRectF rect, SColorF color, bool mirrored = false, bool allMonitors = true);
        void DrawTexture(CTextureRef texture, SRectF rect, SColorF color, SRectF bounds, bool mirrored = false, bool allMonitors = true);
        void DrawTexture(CTextureRef texture, SRectF rect, SColorF color, float begin, float end, bool allMonitors = true);

        void DrawGlassTexture(CTextureRef texture, SRectF rect, SColorF color, CTextureRef mask, SRectF maskRect, bool allMonitors = true);
        void DrawGlassTexture(CTextureRef texture, SRectF rect, SColorF color, CTextureRef mask, SRectF maskRect, SRectF uv, bool allMonitors = true);

        void DrawTextureReflection(CTextureRef texture, SRectF rect, SColorF color, SRectF bounds, float space, float height, bool allMonitors = true, float mirrorLine = 0f, float fadeOrigin = 0f, float fadeSpan = 0f);
    }
}