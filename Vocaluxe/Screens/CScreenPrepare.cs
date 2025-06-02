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
using System.Collections.Generic;
using System.Windows.Forms;
using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Draw;
using VocaluxeLib.Menu;
using VocaluxeLib.Songs;
using VocaluxeLib.Profile;
using System.Runtime.ConstrainedExecution;

namespace Vocaluxe.Screens
{
    public class CScreenPrepare : CMenu
    {
        // Version number for theme files. Increment it, if you've changed something on the theme files!
        protected override int _ScreenVersion
        {
            get { return 1; }
        }

        private readonly CTextureRef[] _OriginalPlayerAvatarTextures = new CTextureRef[CSettings.MaxNumPlayer];

        private string[] _PlayerStaticBG = new string[CGame.NumPlayers];
        private string[] _PlayerStaticIndicator = new string[CGame.NumPlayers];
        private string[] _PlayerStaticAvatar = new string[CGame.NumPlayers];
        private string[] _PlayerTextName = new string[CGame.NumPlayers];
        private string[] _PlayerTextDifficulty = new string[CGame.NumPlayers];
        private string[] _PlayerTextVoice = new string[CGame.NumPlayers];
        private string[] _PlayerEqualizer = new string[CGame.NumPlayers];

        private DateTime _LastProfileUpdateCheck;

        private DateTime showTime;
        private int timeout;

        public override EMusicType CurrentMusicType
        {
            get { return EMusicType.BackgroundPreview; }
        }

        #region public methods
        public override void Init()
        {
            base.Init();

            _BuildThemeElementStrings();
        }

        public override void LoadTheme(string xmlPath)
        {
            base.LoadTheme(xmlPath);

            for (int i = 0; i < CSettings.MaxNumPlayer; i++)
            {
                _OriginalPlayerAvatarTextures[i] = _Statics["StaticPlayerAvatar"].Texture;
            }
            
            _CreatePlayerElements();
            _Statics["StaticPlayerAvatar"].Aspect = EAspect.Crop;
            _Statics["StaticVideoBackground"].Aspect = EAspect.Crop;
        }

        public override bool HandleInput(SKeyEvent keyEvent)
        {
            base.HandleInput(keyEvent);
            switch (keyEvent.Key)
            {
                case Keys.Escape:
                case Keys.Back:
                    CGraphics.FadeTo(EScreen.Song);
                    break;
                case Keys.Enter:
                    CGraphics.FadeTo(EScreen.Sing);
                    break;
            }

            return true;
        }

        public override bool HandleMouse(SMouseEvent mouseEvent)
        {
            base.HandleMouse(mouseEvent);

            if (mouseEvent.RB)
            {
                    CGraphics.FadeTo(EScreen.Song);
            }

            return true;
        }

        public override bool UpdateGame()
        {
            if (CCloud.isUpdated(_LastProfileUpdateCheck) && timeout > 0)
                _LoadProfiles();

            _UpdateEqualizers();

            if (CBackgroundMusic.SongHasVideo)
                _Statics["StaticVideoBackground"].Texture = CBackgroundMusic.GetVideoTexture();
            else
                _Statics["StaticVideoBackground"].Texture = CBackgroundMusic.Cover;

            int elapsedSeconds = (int) Math.Round((DateTime.Now - showTime).TotalSeconds);

            if (elapsedSeconds > timeout && timeout > 0)
            {
                timeout = 0;
                CGraphics.FadeTo(EScreen.Sing);
            } else if (elapsedSeconds < timeout)
            {
                _Texts["TextCountdown"].Text = CLanguage.Translate("TR_SCREENPREPARE_STARTING_IN").Replace("%v", Math.Round(timeout - (DateTime.Now - showTime).TotalSeconds).ToString());
            } else
            {
                _Texts["TextCountdown"].Text = CLanguage.Translate("TR_SCREENPREPARE_LETS_GO");
            }

                return true;
        }

        public override void OnShow()
        {
            _LastProfileUpdateCheck = DateTime.MinValue;

            _Statics["StaticCover"].Texture = CBackgroundMusic.Cover;
            _Texts["TextArtist"].Text = CGame.GetSong(0).Artist;
            _Texts["TextTitle"].Text = CGame.GetSong(0).Title;

            base.OnShow();

            CCloud.AssignPlayersFromCloud();

            CRecord.Start();

            showTime = DateTime.Now;
            timeout = 10;
        }

        public override void OnClose()
        {
            base.OnClose();
            CRecord.Stop();
        }
        #endregion public methods

        #region private methods
        private void _UpdateEqualizers()
        {
            for (int i = 0; i < CGame.NumPlayers; i++)
            {
                CRecord.AnalyzeBuffer(i);
                _Equalizers[_PlayerEqualizer[i]].Update(CRecord.ToneWeigth(i), CRecord.GetMaxVolume(i));
            }
        }

        private void _BuildThemeElementStrings()
        {
            List<string> statics = new List<string>
            {
                "StaticPlayerBG",
                "StaticPlayerIndicator",
                "StaticPlayerAvatar",
            };

            List<string> texts = new List<string>
            {
                "TextPlayerName",
                "TextPlayerDifficulty",
                "TextPlayerVoice",
            };

            List<string> equalizers = new List<string>
            {
                "Equalizer"
            };

            List<string> metas = new List<string>();

            for (int numplayer = 0; numplayer <= CSettings.MaxScreenPlayer;  ++numplayer)
            {
                for (int player = 0; player <= numplayer; ++player)
                {
                    metas.Add("MetaPlayerPanel" + player + "N" + numplayer);
                }
            }

            for ( int player = 0; player < CGame.NumPlayers; player++)
            {
                _PlayerStaticBG[player] = "StaticPlayerBGP" + (player + 1);
                _PlayerStaticIndicator[player] = "StaticPlayerIndicatorP" + (player + 1);
                _PlayerStaticAvatar[player] = "StaticPlayerAvatarP" + (player + 1);
                _PlayerTextName[player] = "TextPlayerNameP" + (player + 1);
                _PlayerTextDifficulty[player] = "TextPlayerDifficultyP" + (player + 1);
                _PlayerTextVoice[player] = "TextPlayerVoiceP" + (player + 1);
                _PlayerEqualizer[player] = "EqualizerP" + (player + 1);
            }

            _ThemeStatics = statics.ToArray();
            _ThemeTexts = texts.ToArray();
            _ThemeEqualizers = equalizers.ToArray();
            _ThemeMetas = metas.ToArray();
        }

        private void _CreatePlayerElements()
        {
            _Statics["StaticPlayerBG"].AllMonitors = false;
            _Statics["StaticPlayerIndicator"].AllMonitors = false;
            _Statics["StaticPlayerAvatar"].AllMonitors = false;
            _Texts["TextPlayerName"].AllMonitors = false;
            _Texts["TextPlayerDifficulty"].AllMonitors = false;
            _Texts["TextPlayerVoice"].AllMonitors = false;
            _Equalizers["Equalizer"].AllMonitors = false;


            int screenPlayers = CGame.NumPlayers / CConfig.GetNumScreens();
            int remainingPlayers = CGame.NumPlayers % screenPlayers;
            int player = 0;

            for (int screen = 0; screen < CConfig.GetNumScreens(); screen++)
            {
                for ( int screenPlayer = 0; screenPlayer < screenPlayers; screenPlayer++ )
                {
                    int screenPlayerCount = screenPlayers;

                    if (remainingPlayers > 0)
                    {
                        screenPlayerCount++;
                        remainingPlayers--;
                    }

                    _AddStatic(GetNewStatic(_Statics["StaticPlayerBG"]), _PlayerStaticBG[player]);
                    _Statics[_PlayerStaticBG[player]].X += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].X + (screen * CSettings.RenderW);
                    _Statics[_PlayerStaticBG[player]].Y += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].Y;

                    _AddStatic(GetNewStatic(_Statics["StaticPlayerIndicator"]), _PlayerStaticIndicator[player]);
                    _Statics[_PlayerStaticIndicator[player]].X += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].X + (screen * CSettings.RenderW);
                    _Statics[_PlayerStaticIndicator[player]].Y += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].Y;
                    _Statics[_PlayerStaticIndicator[player]].Color = CBase.Themes.GetPlayerColor(player + 1);

                    _AddStatic(GetNewStatic(_Statics["StaticPlayerAvatar"]), _PlayerStaticAvatar[player]);
                    _Statics[_PlayerStaticAvatar[player]].X += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].X + (screen * CSettings.RenderW);
                    _Statics[_PlayerStaticAvatar[player]].Y += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].Y;

                    _AddText(GetNewText(_Texts["TextPlayerName"]), _PlayerTextName[player]);
                    _Texts[_PlayerTextName[player]].X += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].X + (screen * CSettings.RenderW);
                    _Texts[_PlayerTextName[player]].Y += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].Y;

                    _AddText(GetNewText(_Texts["TextPlayerDifficulty"]), _PlayerTextDifficulty[player]);
                    _Texts[_PlayerTextDifficulty[player]].X += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].X + (screen * CSettings.RenderW);
                    _Texts[_PlayerTextDifficulty[player]].Y += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].Y;

                    _AddText(GetNewText(_Texts["TextPlayerVoice"]), _PlayerTextVoice[player]);
                    _Texts[_PlayerTextVoice[player]].X += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].X + (screen * CSettings.RenderW);
                    _Texts[_PlayerTextVoice[player]].Y += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].Y;

                    _AddEqualizer(GetNewEqualizer(_Equalizers["Equalizer"]), _PlayerEqualizer[player]);
                    _Equalizers[_PlayerEqualizer[player]].X += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].X + (screen * CSettings.RenderW);
                    _Equalizers[_PlayerEqualizer[player]].Y += _Metas["MetaPlayerPanel" + (screenPlayer + 1) + "N" + screenPlayerCount].Y;
                    _Equalizers[_PlayerEqualizer[player]].Color.R = CBase.Themes.GetPlayerColor(player + 1).R;
                    _Equalizers[_PlayerEqualizer[player]].Color.G = CBase.Themes.GetPlayerColor(player + 1).G;
                    _Equalizers[_PlayerEqualizer[player]].Color.B = CBase.Themes.GetPlayerColor(player + 1).B;
                    _Equalizers[_PlayerEqualizer[player]].MaxColor.R = CBase.Themes.GetPlayerColor(player + 1).R;
                    _Equalizers[_PlayerEqualizer[player]].MaxColor.G = CBase.Themes.GetPlayerColor(player + 1).G;
                    _Equalizers[_PlayerEqualizer[player]].MaxColor.B = CBase.Themes.GetPlayerColor(player + 1).B;

                    player++;
                }
            }

            _Statics["StaticPlayerBG"].Visible = false;
            _Statics["StaticPlayerIndicator"].Visible = false;
            _Statics["StaticPlayerAvatar"].Visible = false;
            _Texts["TextPlayerName"].Visible = false;
            _Texts["TextPlayerDifficulty"].Visible = false;
            _Texts["TextPlayerVoice"].Visible = false;
            _Equalizers["Equalizer"].Visible = false;
        }

        private void _LoadProfiles()
        {
            for (int playerIndex = 0; playerIndex < CGame.NumPlayers; playerIndex++)
            {
                _Statics[_PlayerStaticAvatar[playerIndex]].Texture = CProfiles.IsProfileIDValid(CGame.Players[playerIndex].ProfileID) ?
                                                               CProfiles.GetAvatarTextureFromProfile(CGame.Players[playerIndex].ProfileID) :
                                                               _OriginalPlayerAvatarTextures[playerIndex];
                _Texts[_PlayerTextName[playerIndex]].Text = CProfiles.GetPlayerName(CGame.Players[playerIndex].ProfileID, playerIndex + 1);
                _Texts[_PlayerTextDifficulty[playerIndex]].Text = CLanguage.Translate(CGame.Players[playerIndex].Difficulty.ToString());
                _Texts[_PlayerTextVoice[playerIndex]].Text = CLanguage.Translate("TR_SCREENPREPARE_SINGING_AS").Replace("%v", CGame.GetSong(0).Notes.VoiceNames[CGame.Players[playerIndex].VoiceNr]);
            }

            _LastProfileUpdateCheck = DateTime.Now;
        }
        #endregion private methods
    }
}