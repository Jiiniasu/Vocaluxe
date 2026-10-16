#region license
// This file is part of Vocaluxe.
// 
// Vocaluxe is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Vocaluxe is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using VocaluxeLib;
using VocaluxeLib.Songs;

namespace Vocaluxe.Base
{
    /// <summary>
    ///     Game-mode framework. This port only ships the normal singing game (the former party
    ///     minigames were removed in the .NET 10 port), so this is a lightweight single-mode
    ///     facade: it holds the song-selection defaults and drives the round flow
    ///     (Sing -> Score -> Highscore -> Song).
    /// </summary>
    static class CParty
    {
        private static bool _Initialized;

        // Shared song-selection state of the normal game mode (same defaults the old CPartyModeNormal set up).
        private static SScreenSongOptions _SongOptions = new SScreenSongOptions
        {
            Selection = new SSelectionOptions
            {
                RandomOnly = false,
                PartyMode = false,
                CategoryChangeAllowed = true,
                NumJokers = null,
                TeamNames = null,
                SongIndex = -1,
                CategoryIndex = -1
            },
            Sorting = new SSortingOptions
            {
                SearchString = string.Empty,
                SearchActive = false,
                DuetOptions = EDuetOptions.All,
                FilterPlaylistID = -1
            }
        };

        public static bool Init()
        {
            if (_Initialized)
                return false; //Already initialized
            _Initialized = true;
            return true;
        }

        public static void SetNormalGameMode()
        {
            CSongs.ResetPartySongSung();
        }

        public static SScreenSongOptions GetSongSelectionOptions()
        {
            _SongOptions.Sorting.SongSorting = CBase.Config.GetSongSorting();
            _SongOptions.Sorting.Tabs = CBase.Config.GetTabs();

            if (_SongOptions.Sorting.SearchActive)
                _SongOptions.Sorting.Tabs = EOffOn.TR_CONFIG_OFF;

            _SongOptions.Sorting.IgnoreArticles = CBase.Config.GetIgnoreArticles();

            return _SongOptions;
        }

        // The normal game has no per-changed hooks.
        public static void OnSongChange(int songIndex, ref SScreenSongOptions screenSongOptions)
        {
        }

        public static void OnCategoryChange(int categoryIndex, ref SScreenSongOptions screenSongOptions)
        {
        }

        public static void SetSearchString(string searchString, bool visible)
        {
            _SongOptions.Sorting.SearchString = searchString;
            _SongOptions.Sorting.SearchActive = visible;
        }

        public static void JokerUsed(int teamNr)
        {
        }

        public static void SongSelected(int songID)
        {
            CBase.Graphics.FadeTo(EScreen.Sing);
        }

        public static void FinishedSinging()
        {
            CBase.Graphics.FadeTo(EScreen.Score);
        }

        public static void LeavingScore()
        {
            CBase.Graphics.FadeTo(EScreen.Highscore);
        }

        public static void LeavingHighscore()
        {
            CBase.Graphics.FadeTo(EScreen.Song);
        }
    }
}