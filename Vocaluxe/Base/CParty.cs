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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using VocaluxeLib;
using VocaluxeLib.Menu;
using VocaluxeLib.PartyModes;

namespace Vocaluxe.Base
{
    static class CParty
    {
        private static readonly Dictionary<int, SPartyMode> _PartyModes = new Dictionary<int, SPartyMode>();
        private static IPartyMode _CurrentPartyMode;

        #region public stuff
        public static bool Init()
        {
            if (_PartyModes.Count > 0)
                return false; //Already initialized
            SPartyMode pm = new SPartyMode
            {
                Info = new SPartyModeInfos { Author = "Vocaluxe Team", Description = "Normal game", Name = "Normal", TargetAudience = "Just a normal game for everyone" },
                PartyMode = new CPartyModeNormal(-1),
                PartyModeSystemVersion = 2,
                ScreenFiles = new List<string>()
            };
            _PartyModes.Add(-1, pm);
            _CurrentPartyMode = pm.PartyMode;
            Debug.Assert(_CurrentPartyMode != null && _CurrentPartyMode.ID == -1);
            return _CurrentPartyMode.Init();
        }

        public static int CurrentPartyModeID
        {
            get { return _CurrentPartyMode.ID; }
            set
            {
                if (_CurrentPartyMode.ID == value)
                {
                    _CurrentPartyMode.SetDefaults();
                    return;
                }
                if (value != -1 && !_PartyModes.ContainsKey(value))
                    throw new ArgumentException("Partymode with ID=" + value + " does not exist!");
                IPartyMode pm = _PartyModes[value].PartyMode;
                if (pm.Init())
                    _CurrentPartyMode = pm;
                else
                {
                    CLog.Error("Could not init PartyMode \"" + _PartyModes[value].Info.Name + "\"! Removing...", true);
                    _PartyModes.Remove(value);
                }
            }
        }

        public static int Count
        {
            get { return _PartyModes.Count; }
        }

        public static void ReloadTheme()
        {
            foreach (SPartyMode pm in _PartyModes.Values)
                pm.PartyMode.ReloadTheme();
        }

        public static void ReloadSkin()
        {
            foreach (SPartyMode pm in _PartyModes.Values)
                pm.PartyMode.ReloadSkin();
        }

        public static void SaveThemes()
        {
            foreach (SPartyMode pm in _PartyModes.Values)
                pm.PartyMode.SaveScreens();
        }

        public static List<SPartyModeInfos> GetPartyModeInfos()
        {
            List<SPartyModeInfos> list = new List<SPartyModeInfos>();
            foreach (SPartyMode pm in _PartyModes.Values)
            {
                if (pm.PartyMode.ID >= 0)
                    list.Add(pm.Info);
            }
            return list;
        }

        public static void SetNormalGameMode()
        {
            CSongs.ResetPartySongSung();
            CurrentPartyModeID = -1;
        }

        public static void SetPartyMode(int partyModeID)
        {
            CurrentPartyModeID = partyModeID;
        }
        #endregion public stuff

        #region Interface
        public static void UpdateGame()
        {
            _CurrentPartyMode.UpdateGame();
        }

        public static IMenu GetStartScreen()
        {
            return _CurrentPartyMode.GetStartScreen();
        }

        public static SScreenSongOptions GetSongSelectionOptions()
        {
            return _CurrentPartyMode.GetScreenSongOptions();
        }

        public static void OnSongChange(int songIndex, ref SScreenSongOptions screenSongOptions)
        {
            _CurrentPartyMode.OnSongChange(songIndex, ref screenSongOptions);
        }

        public static void OnCategoryChange(int categoryIndex, ref SScreenSongOptions screenSongOptions)
        {
            _CurrentPartyMode.OnCategoryChange(categoryIndex, ref screenSongOptions);
        }

        public static void SetSearchString(string searchString, bool visible)
        {
            _CurrentPartyMode.SetSearchString(searchString, visible);
        }

        public static void JokerUsed(int teamNr)
        {
            _CurrentPartyMode.JokerUsed(teamNr);
        }

        public static void SongSelected(int songID)
        {
            _CurrentPartyMode.SongSelected(songID);
        }

        public static void FinishedSinging()
        {
            _CurrentPartyMode.FinishedSinging();
        }

        public static void LeavingScore()
        {
            _CurrentPartyMode.LeavingScore();
        }

        public static void LeavingHighscore()
        {
            _CurrentPartyMode.LeavingHighscore();
        }
        #endregion Interface
    }
}
