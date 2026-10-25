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
using System.Linq;
using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Menu;

namespace Vocaluxe.Screens
{
    public class CScreenOptionsVideo : CMenu
    {
        // Version number for theme files. Increment it, if you've changed something on the theme files!
        protected override int _ScreenVersion
        {
            get { return 8; }
        }

        private const string _SelectSlideVideoBackgrounds = "SelectSlideVideoBackgrounds";
        private const string _SelectSlideVideosInSongs = "SelectSlideVideosInSongs";
        private const string _SelectSlideVideosToBackground = "SelectSlideVideosToBackground";
        private const string _SelectSlideGlassBlur = "SelectSlideGlassBlur";
        private const string _SelectSlideGlassStrength = "SelectSlideGlassStrength";
        private const string _SelectSlideGlassDarken = "SelectSlideGlassDarken";

        private const string _ButtonExit = "ButtonExit";

        public override void Init()
        {
            base.Init();

            _ThemeButtons = new string[] { _ButtonExit };
            _ThemeSelectSlides = new string[]
                {
                    _SelectSlideVideoBackgrounds, _SelectSlideVideosInSongs, _SelectSlideVideosToBackground,
                    _SelectSlideGlassBlur, _SelectSlideGlassStrength, _SelectSlideGlassDarken
                };
        }

        public override void LoadTheme(string xmlPath)
        {
            base.LoadTheme(xmlPath);

            _SelectSlides[_SelectSlideVideoBackgrounds].SetValues<EOffOn>((int)CConfig.Config.Video.VideoBackgrounds);
            _SelectSlides[_SelectSlideVideosInSongs].SetValues<EOffOn>((int)CConfig.Config.Video.VideosInSongs);
            _SelectSlides[_SelectSlideVideosToBackground].SetValues<EOffOn>((int)CConfig.Config.Video.VideosToBackground);

            //The glass enums have non-consecutive values (0-100 percent), so select by tag instead of by index
            _SelectSlides[_SelectSlideGlassBlur].SetValues<EOffOn>((int)CConfig.Config.Graphics.GlassBlur);
            _SelectSlides[_SelectSlideGlassStrength].AddValues(Enum.GetNames(typeof(EGlassStrength)), Enum.GetValues(typeof(EGlassStrength)).Cast<int>());
            _SelectSlides[_SelectSlideGlassStrength].SelectedTag = (int)CConfig.Config.Graphics.GlassStrength;
            _SelectSlides[_SelectSlideGlassDarken].AddValues(Enum.GetNames(typeof(EGlassDarken)), Enum.GetValues(typeof(EGlassDarken)).Cast<int>());
            _SelectSlides[_SelectSlideGlassDarken].SelectedTag = (int)CConfig.Config.Graphics.GlassDarken;
        }

        public override bool HandleInput(SKeyEvent keyEvent)
        {
            base.HandleInput(keyEvent);

            if (keyEvent.KeyPressed) { }
            else
            {
                switch (keyEvent.Key)
                {
                    case Keys.Escape:
                    case Keys.Back:
                        _SaveConfig();
                        CGraphics.FadeTo(EScreen.Options);
                        break;

                    case Keys.S:
                        CParty.SetNormalGameMode();
                        _SaveConfig();
                        CGraphics.FadeTo(EScreen.Song);
                        break;

                    case Keys.Enter:
                        if (_Buttons[_ButtonExit].Selected)
                        {
                            _SaveConfig();
                            CGraphics.FadeTo(EScreen.Options);
                        }
                        break;
                }
            }
            return true;
        }

        public override bool HandleMouse(SMouseEvent mouseEvent)
        {
            base.HandleMouse(mouseEvent);

            if (mouseEvent.RB)
            {
                _SaveConfig();
                CGraphics.FadeTo(EScreen.Options);
            }

            if (mouseEvent.LB && _IsMouseOverCurSelection(mouseEvent))
            {
                _SaveConfig();
                if (_Buttons[_ButtonExit].Selected)
                    CGraphics.FadeTo(EScreen.Options);
            }
            return true;
        }

        public override bool UpdateGame()
        {
            _SelectSlides[_SelectSlideVideosToBackground].Selection = (int)CConfig.Config.Video.VideosToBackground;
            return true;
        }

        private void _SaveConfig()
        {
            CConfig.Config.Video.VideoBackgrounds = (EOffOn)_SelectSlides[_SelectSlideVideoBackgrounds].Selection;
            CConfig.Config.Video.VideosInSongs = (EOffOn)_SelectSlides[_SelectSlideVideosInSongs].Selection;
            CConfig.Config.Video.VideosToBackground = (EOffOn)_SelectSlides[_SelectSlideVideosToBackground].Selection;

            CConfig.Config.Graphics.GlassBlur = (EOffOn)_SelectSlides[_SelectSlideGlassBlur].Selection;
            CConfig.Config.Graphics.GlassStrength = (EGlassStrength)_SelectSlides[_SelectSlideGlassStrength].SelectedTag;
            CConfig.Config.Graphics.GlassDarken = (EGlassDarken)_SelectSlides[_SelectSlideGlassDarken].SelectedTag;

            CBackgroundMusic.VideoEnabled = CConfig.Config.Video.VideoBackgrounds == EOffOn.TR_CONFIG_ON && CConfig.Config.Video.VideosToBackground == EOffOn.TR_CONFIG_ON;

            //The glass twins/masks were generated with the old settings: drop them so they are rebuilt
            CBase.Glass?.OnSettingsChanged();

            CConfig.SaveConfig();
        }
    }
}