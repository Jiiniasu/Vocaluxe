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

using VocaluxeLib.Menu;

namespace VocaluxeLib.Draw
{
    /// <summary>
    ///     Frosted-glass backdrops for theme elements (Static Glass="true"): a darkened, blurred copy of
    ///     the current background is drawn behind the element, shaped by a mask texture, so the video
    ///     doesn't shine through the panels.
    /// </summary>
    public interface IGlass
    {
        /// <summary>
        ///     True if the glass backdrops are enabled in the settings
        /// </summary>
        bool IsEnabled();

        /// <summary>
        ///     Registers the current background the glass is drawn against: the background image texture
        ///     (image backgrounds) or the video stream's pre-blurred glass twin (video backgrounds,
        ///     source null). Both are borrowed references and never disposed by the glass.
        ///     aspect is how the background fills the panel (EAspect.Crop etc., the same value the
        ///     background draw uses) so the glass can sample the part of the twin behind a panel.
        /// </summary>
        void SetBackground(CTextureRef source, CTextureRef videoTwin, EAspect aspect);

        /// <summary>
        ///     Draws the glass backdrop behind a glass static (no-op if the static has no glass or none
        ///     is enabled)
        /// </summary>
        void DrawGlass(CStatic cStatic);

        /// <summary>
        ///     Called when the theme or the graphics settings were reloaded: drops all generated
        ///     textures (blurred background twins, masks) so they are rebuilt from the new content
        /// </summary>
        void OnSettingsChanged();
    }
}