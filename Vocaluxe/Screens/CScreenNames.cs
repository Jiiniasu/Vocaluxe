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

namespace Vocaluxe.Screens
{
    public class CScreenNames : CMenu
    {
        // Version number for theme files. Increment it, if you've changed something on the theme files!
        protected override int _ScreenVersion
        {
            get { return 3; }
        }

        private CStatic _ChooseAvatarStatic;
        private int _OldMouseX;
        private int _OldMouseY;

        private const string _SelectSlidePlayerNumber = "SelectSlidePlayerNumber";
        private string _NameSelection = CConfig.GetNumScreens() + "ScreenNameSelection";
        private const string _ButtonBack = "ButtonBack";
        private const string _ButtonNewProfile = "ButtonNewProfile";
        private const string _ButtonStart = "ButtonStart";
        private const string _TextWarningMics = "TextWarningMics";
        private const string _StaticWarningMics = "StaticWarningMics";
        private const string _TextWarningProfiles = "TextWarningProfiles";
        private const string _StaticWarningProfiles = "StaticWarningProfiles";
        private string[] _MetaRelativePlayerPanel;
        private string[] _MetaPlayersPanel;
        private string[] _StaticScreenBG;
        private string[,] _StaticPlayer;
        private string[,] _StaticPlayerAvatar;
        private string[] _TextScreen;
        private string[,] _TextPlayer;
        private string[,] _ButtonPlayer;
        private string[,] _EqualizerPlayer;
        private string[,] _SelectSlideDuetPlayer;
        private readonly CTextureRef[] _OriginalPlayerAvatarTextures = new CTextureRef[CSettings.MaxNumPlayer];

        private string[] _PlayerStatic;
        private string[] _PlayerStaticAvatar;
        private string[] _PlayerText;
        private string[] _PlayerButton;
        private string[] _PlayerEqualizer;
        private string[] _PlayerSelectSlideDuet;

        private bool _SelectingKeyboardActive;
        private bool _SelectingFast;
        private int _SelectingSwitchNr = -1;
        private int _SelectingFastPlayerNr;
        private Guid _SelectedProfileID = Guid.Empty;
        private bool _AvatarsChanged;
        private bool _ProfilesChanged;
        private int _PreviousPlayerSelection = -1;

        public override EMusicType CurrentMusicType
        {
            get { return EMusicType.BackgroundPreview; }
        }

        #region public methods
        public override void Init()
        {
            base.Init();

            _BuildElementStrings();

            _ChooseAvatarStatic = GetNewStatic();
            _ChooseAvatarStatic.Visible = false;
            _ChooseAvatarStatic.Aspect = EAspect.Crop;

            CProfiles.AddProfileChangedCallback(_OnProfileChanged);
        }

        public override void LoadTheme(string xmlPath)
        {
            base.LoadTheme(xmlPath);

            for (int i = 0; i < CSettings.MaxNumPlayer; i++)
            {
                _OriginalPlayerAvatarTextures[i] = _Statics["StaticPlayerAvatar"].Texture;
            }
            for (int s = 1; s <= CSettings.MaxNumScreens; s++)
            {
                if (CConfig.GetNumScreens() != s)
                {
                    _NameSelections[s + "ScreenNameSelection"].Visible = false;
                }
            }
            
            _CreatePlayerElements();
            _Statics["StaticPlayerAvatar"].Aspect = EAspect.Crop;
            _AddStatic(_ChooseAvatarStatic);
            _SelectSlides[_SelectSlidePlayerNumber].Selectable = false;
        }

        public override bool HandleInput(SKeyEvent keyEvent)
        {
            switch (keyEvent.Key)
            {
                case Keys.Add:
                    if (CConfig.Config.Game.NumPlayers + 1 <= CSettings.MaxNumPlayer)
                    {
                        _SelectSlides[_SelectSlidePlayerNumber].Selection = CConfig.Config.Game.NumPlayers;
                        _UpdatePlayerNumber();
                        //Update Tiles-List
                        _NameSelections[_NameSelection].UpdateList();
                    }
                    break;

                case Keys.Subtract:
                    if (CConfig.Config.Game.NumPlayers - 1 > 0)
                    {
                        _SelectSlides[_SelectSlidePlayerNumber].Selection = CConfig.Config.Game.NumPlayers - 2;
                        _UpdatePlayerNumber();
                        //Update Tiles-List
                        _NameSelections[_NameSelection].UpdateList();
                    }
                    break;

                case Keys.P:
                    if (!_SelectingKeyboardActive)
                    {
                        _SelectingFastPlayerNr = 1;
                        _SelectingFast = true;
                        _ResetPlayerSelections();
                    }
                    else
                    {
                        if (_SelectingFastPlayerNr + 1 <= CGame.NumPlayers)
                            _SelectingFastPlayerNr++;
                        else
                            _SelectingFastPlayerNr = 1;
                        _NameSelections[_NameSelection].FastSelection(true, _SelectingFastPlayerNr);
                    }
                    break;
                case Keys.N:
                    CGraphics.ShowPopup(EPopupScreens.PopupNewPlayer);
                    break;
            }
            //Check if selecting with keyboard is active
            if (_SelectingKeyboardActive)
            {
                //Handle left/right/up/down
                _NameSelections[_NameSelection].HandleInput(keyEvent);
                int numberPressed = -1;
                bool resetSelection = false;
                switch (keyEvent.Key)
                {
                    case Keys.Enter:
                        //Check, if a player is selected
                        if (_NameSelections[_NameSelection].SelectedID != Guid.Empty)
                        {
                            _SelectedProfileID = _NameSelections[_NameSelection].SelectedID;

                            if (!CProfiles.IsProfileIDValid(_SelectedProfileID))
                                return true;

                            _UpdateSelectedProfile(_SelectingFastPlayerNr - 1, _SelectedProfileID);
                        }
                        //Started selecting with 'P'
                        if (_SelectingFast)
                        {
                            if (_SelectingFastPlayerNr == CGame.NumPlayers)
                            {
                                resetSelection = true;
                                _SelectElement(_Buttons[_ButtonStart]);
                            }
                            else
                            {
                                _SelectingFastPlayerNr++;
                                _NameSelections[_NameSelection].FastSelection(true, _SelectingFastPlayerNr);
                            }
                        }
                        else
                            resetSelection = true;
                        break;

                    case Keys.D1:
                    case Keys.NumPad1:
                        numberPressed = 1;
                        break;
                    case Keys.D2:
                    case Keys.NumPad2:
                        numberPressed = 2;
                        break;
                    case Keys.D3:
                    case Keys.NumPad3:
                        numberPressed = 3;
                        break;
                    case Keys.D4:
                    case Keys.NumPad4:
                        numberPressed = 4;
                        break;
                    case Keys.D5:
                    case Keys.NumPad5:
                        numberPressed = 5;
                        break;
                    case Keys.D6:
                    case Keys.NumPad6:
                        numberPressed = 6;
                        break;

                    case Keys.Escape:
                        resetSelection = true;
                        _SelectElement(_SelectSlides[_SelectSlidePlayerNumber]);
                        break;

                    case Keys.Delete:
                        //Delete profile-selection
                        _ResetPlayerSelection(_SelectingFastPlayerNr - 1);
                        //Reset all values
                        _SelectingFastPlayerNr = 0;
                        _SelectingKeyboardActive = false;
                        _NameSelections[_NameSelection].FastSelection(false, -1);
                        //Update Tiles-List
                        _NameSelections[_NameSelection].UpdateList();
                        break;

                    case Keys.F10:
                        if (CGame.GetNumSongs() == 1 && CGame.GetSong(0).IsDuet)
                        {
                            CSelectSlide selectSlideDuetPart = _SelectSlides[_PlayerSelectSlideDuet[_SelectingFastPlayerNr - 1]];
                            selectSlideDuetPart.Selection = (selectSlideDuetPart.Selection + 1) % 2;
                            //Reset all values
                            _SelectingFastPlayerNr = 0;
                            _SelectingKeyboardActive = false;
                            _SelectingFast = false;
                            _NameSelections[_NameSelection].FastSelection(false, -1);
                            _SelectElement(_Buttons[_ButtonStart]);
                        }
                        break;
                }
                if (numberPressed > 0 || resetSelection)
                {
                    if (numberPressed == _SelectingFastPlayerNr || resetSelection)
                    {
                        //Reset all values
                        _SelectingFastPlayerNr = 0;
                        _SelectingKeyboardActive = false;
                        _SelectElement(_SelectSlides[_SelectSlidePlayerNumber]);
                        _NameSelections[_NameSelection].FastSelection(false, -1);
                    }
                    else if (numberPressed <= CConfig.Config.Game.NumPlayers)
                    {
                        _SelectingFastPlayerNr = numberPressed;
                        _NameSelections[_NameSelection].FastSelection(true, numberPressed);
                    }
                    _SelectingFast = false;
                    if (_PreviousPlayerSelection > -1)
                    {
                        _SelectElement(_Buttons[_PlayerButton[_PreviousPlayerSelection]]);
                        _PreviousPlayerSelection = -1;
                    }
                }
            }
                //Normal Keyboard handling
            else
            {
                base.HandleInput(keyEvent);
                switch (keyEvent.Key)
                {
                    case Keys.Escape:
                    case Keys.Back:
                        CGraphics.FadeTo(EScreen.Song);
                        break;

                    case Keys.Enter:

                        if (_Buttons[_ButtonBack].Selected)
                            CGraphics.FadeTo(EScreen.Song);
                        else if (_Buttons[_ButtonStart].Selected)
                            _StartSong();
                        else if (_Buttons[_ButtonNewProfile].Selected)
                            CGraphics.ShowPopup(EPopupScreens.PopupNewPlayer);
                        for (int p = 0; p < CGame.NumPlayers; p++)
                        {
                            if (_Buttons[_PlayerButton[p]].Selected)
                            {
                                _PreviousPlayerSelection = p;
                                _SelectingFastPlayerNr = p + 1;
                            }
                        }
                        break;

                    case Keys.D1:
                    case Keys.NumPad1:
                        _SelectingFastPlayerNr = 1;
                        break;

                    case Keys.D2:
                    case Keys.NumPad2:
                        _SelectingFastPlayerNr = 2;
                        break;

                    case Keys.D3:
                    case Keys.NumPad3:
                        _SelectingFastPlayerNr = 3;
                        break;

                    case Keys.D4:
                    case Keys.NumPad4:
                        _SelectingFastPlayerNr = 4;
                        break;

                    case Keys.D5:
                    case Keys.NumPad5:
                        _SelectingFastPlayerNr = 5;
                        break;

                    case Keys.D6:
                    case Keys.NumPad6:
                        _SelectingFastPlayerNr = 6;
                        break;
                    default:
                        _UpdatePlayerNumber();
                        break;
                }

                if (_SelectingFastPlayerNr > 0 && _SelectingFastPlayerNr <= CConfig.Config.Game.NumPlayers)
                {
                    _SelectingKeyboardActive = true;
                    _NameSelections[_NameSelection].FastSelection(true, _SelectingFastPlayerNr);
                }
                if (_NameSelections[_NameSelection].Selected && !_SelectingKeyboardActive)
                {
                    _SelectingKeyboardActive = true;
                    _SelectingFast = true;
                    _SelectingFastPlayerNr = 1;
                    _SelectingKeyboardActive = true;
                    _NameSelections[_NameSelection].FastSelection(true, _SelectingFastPlayerNr);
                }
            }

            return true;
        }

        public override bool HandleMouse(SMouseEvent mouseEvent)
        {
            bool stopSelectingFast = false;

            if (_SelectingFast)
                _NameSelections[_NameSelection].HandleMouse(mouseEvent);
            else
                base.HandleMouse(mouseEvent);

            //Check if LeftButton is hold and Select-Mode inactive
            if (mouseEvent.LBH && _SelectedProfileID == Guid.Empty && !_SelectingFast)
            {
                //Save mouse-coords
                _OldMouseX = mouseEvent.X;
                _OldMouseY = mouseEvent.Y;
                //Check if mouse if over tile
                if (_NameSelections[_NameSelection].IsOverTile(mouseEvent))
                {
                    //Get player-number of tile
                    _SelectedProfileID = _NameSelections[_NameSelection].TilePlayerID(mouseEvent);
                    if (_SelectedProfileID != Guid.Empty)
                    {
                        //Update of Drag/Drop-Texture
                        CStatic selectedPlayer = _NameSelections[_NameSelection].TilePlayerAvatar(mouseEvent);
                        _ChooseAvatarStatic.Visible = true;
                        _ChooseAvatarStatic.MaxRect = selectedPlayer.Rect;
                        _ChooseAvatarStatic.Z = CSettings.ZNear;
                        _ChooseAvatarStatic.Color = new SColorF(1, 1, 1, 1);
                        _ChooseAvatarStatic.Texture = selectedPlayer.Texture;
                    }
                }
                else
                {
                    for (int i = 0; i < CGame.NumPlayers; i++)
                    {
                        if (CHelper.IsInBounds(_Statics[_PlayerStatic[i]].Rect, mouseEvent))
                        {
                            _SelectingSwitchNr = i;
                            _SelectedProfileID = CGame.Players[i].ProfileID;
                            //Update of Drag/Drop-Texture
                            CStatic selectedPlayer = _Statics[_PlayerStaticAvatar[i]];
                            _ChooseAvatarStatic.Visible = true;
                            _ChooseAvatarStatic.MaxRect = selectedPlayer.Rect;
                            _ChooseAvatarStatic.Z = CSettings.ZNear;
                            _ChooseAvatarStatic.Color = new SColorF(1, 1, 1, 1);
                            _ChooseAvatarStatic.Texture = selectedPlayer.Texture;
                            break;
                        }
                    }
                }
            }

            //Check if LeftButton is hold and Select-Mode active
            if (mouseEvent.LBH && _SelectedProfileID != Guid.Empty && !_SelectingFast)
            {
                //Update coords for Drag/Drop-Texture
                _ChooseAvatarStatic.X += mouseEvent.X - _OldMouseX;
                _ChooseAvatarStatic.Y += mouseEvent.Y - _OldMouseY;
                _OldMouseX = mouseEvent.X;
                _OldMouseY = mouseEvent.Y;
            }
                // LeftButton isn't hold anymore, but Select-Mode is still active -> "Drop" of Avatar
            else if (_SelectedProfileID != Guid.Empty && !_SelectingFast)
            {
                //Foreach Drop-Area
                for (int i = 0; i < _PlayerStatic.Length; i++)
                {
                    //Check first, if area is "Active"
                    if (!_Statics[_PlayerStatic[i]].Visible)
                        continue;
                    //Check if Mouse is in area
                    if (CHelper.IsInBounds(_Statics[_PlayerStatic[i]].Rect, mouseEvent))
                    {
                        if (_SelectingSwitchNr > -1 && CGame.Players[i].ProfileID != Guid.Empty)
                            _UpdateSelectedProfile(_SelectingSwitchNr, CGame.Players[i].ProfileID);
                        else if (_SelectingSwitchNr > -1)
                            _ResetPlayerSelection(_SelectingSwitchNr);

                        if (!CProfiles.IsProfileIDValid(_SelectedProfileID))
                            return true;

                        _UpdateSelectedProfile(i, _SelectedProfileID);
                        break;
                    }
                    //Selected player is dropped out of area
                    if (_SelectingSwitchNr > -1)
                        _ResetPlayerSelection(_SelectingSwitchNr);
                }
                _SelectingSwitchNr = -1;
                _SelectedProfileID = Guid.Empty;
                //Reset variables
                _ChooseAvatarStatic.Visible = false;
            }

            if (mouseEvent.LB && _SelectingFast)
            {
                if (_NameSelections[_NameSelection].IsOverTile(mouseEvent))
                {
                    //Get player-number of tile
                    _SelectedProfileID = _NameSelections[_NameSelection].TilePlayerID(mouseEvent);
                    if (_SelectedProfileID != Guid.Empty)
                    {
                        if (!CProfiles.IsProfileIDValid(_SelectedProfileID))
                            return true;

                        _UpdateSelectedProfile(_SelectingFastPlayerNr - 1, _SelectedProfileID);

                        _SelectingFastPlayerNr++;
                        if (_SelectingFastPlayerNr <= CGame.NumPlayers)
                            _NameSelections[_NameSelection].FastSelection(true, _SelectingFastPlayerNr);
                        else
                            stopSelectingFast = true;
                    }
                    else
                        stopSelectingFast = true;
                }
            }
            else if (mouseEvent.LB && _IsMouseOverCurSelection(mouseEvent))
            {
                if (_Buttons[_ButtonBack].Selected)
                    CGraphics.FadeTo(EScreen.Song);
                else if (_Buttons[_ButtonStart].Selected)
                    _StartSong();
                else if (_Buttons[_ButtonNewProfile].Selected)
                    CGraphics.ShowPopup(EPopupScreens.PopupNewPlayer);
                else if (_Buttons["ButtonScrollUp"].Selected)
                    _NameSelections[_NameSelection].UpdateList(_NameSelections[_NameSelection].Offset - 1);
                else if (_Buttons["ButtonScrollDown"].Selected)
                    _NameSelections[_NameSelection].UpdateList(_NameSelections[_NameSelection].Offset + 1);
                else
                    _UpdatePlayerNumber();
                //Update Tiles-List
                _NameSelections[_NameSelection].UpdateList();
            }

            if (mouseEvent.LD && _NameSelections[_NameSelection].IsOverTile(mouseEvent) && !_SelectingFast)
            {
                _SelectedProfileID = _NameSelections[_NameSelection].TilePlayerID(mouseEvent);
                if (_SelectedProfileID != Guid.Empty)
                {
                    for (int i = 0; i < CGame.NumPlayers; i++)
                    {
                        if (CGame.Players[i].ProfileID == Guid.Empty)
                        {
                            if (!CProfiles.IsProfileIDValid(_SelectedProfileID))
                                return true;

                            _UpdateSelectedProfile(i, _SelectedProfileID);
                            break;
                        }
                    }
                }
            }

            if (mouseEvent.RB && _SelectingFast)
                stopSelectingFast = true;
            else if (mouseEvent.RB)
            {
                bool exit = true;
                //Remove profile-selection
                for (int i = 0; i < CConfig.Config.Game.NumPlayers; i++)
                {
                    if (CHelper.IsInBounds(_Statics[_PlayerStatic[i]].Rect, mouseEvent))
                    {
                        _ResetPlayerSelection(i);
                        exit = false;
                    }
                }
                if (exit)
                    CGraphics.FadeTo(EScreen.Song);
            }

            if (mouseEvent.MB && _SelectingFast)
            {
                _SelectingFastPlayerNr++;
                if (_SelectingFastPlayerNr <= CGame.NumPlayers)
                    _NameSelections[_NameSelection].FastSelection(true, _SelectingFastPlayerNr);
                else
                    stopSelectingFast = true;
            }
            else if (mouseEvent.MB)
            {
                _ResetPlayerSelections();
                _SelectingFast = true;
                _SelectingFastPlayerNr = 1;
                _SelectingKeyboardActive = true;
                _NameSelections[_NameSelection].FastSelection(true, _SelectingFastPlayerNr);
            }

            //Check mouse-wheel for scrolling
            if (mouseEvent.Wheel != 0)
            {
                if (CHelper.IsInBounds(_NameSelections[_NameSelection].Rect, mouseEvent))
                {
                    int offset = _NameSelections[_NameSelection].Offset + mouseEvent.Wheel;
                    _NameSelections[_NameSelection].UpdateList(offset);
                }
            }

            if (stopSelectingFast)
            {
                _SelectingFast = false;
                _SelectingFastPlayerNr = 0;
                _SelectingKeyboardActive = false;
                _NameSelections[_NameSelection].FastSelection(false, -1);
            }
            return true;
        }

        public override bool UpdateGame()
        {
            if (_ProfilesChanged || _AvatarsChanged)
                _LoadProfiles();

            _UpdateEqualizers();

            return true;
        }

        public override void OnShow()
        {
            base.OnShow();

            if (CConfig.UseCloudServer)
            {
                CCloud.AssignPlayersFromCloud();
            }

            CRecord.Start();

            _NameSelections[_NameSelection].Init();

            _LoadProfiles();
            
            _SelectElement(_Buttons[_ButtonStart]);
        }

        public override void OnClose()
        {
            base.OnClose();
            CRecord.Stop();
        }
        #endregion public methods

        #region private methods
        private void _OnProfileChanged(EProfileChangedFlags flags)
        {
            if (EProfileChangedFlags.Avatar == (EProfileChangedFlags.Avatar & flags))
                _AvatarsChanged = true;

            if (EProfileChangedFlags.Profile == (EProfileChangedFlags.Profile & flags))
                _ProfilesChanged = true;
        }

        private void _UpdateEqualizers()
        {
            for (int i = 0; i < CGame.NumPlayers; i++)
            {
                CRecord.AnalyzeBuffer(i);
                _Equalizers[_PlayerEqualizer[i]].Update(CRecord.ToneWeigth(i), CRecord.GetMaxVolume(i));
            }
        }

        private void _BuildElementStrings()
        {
            _MetaRelativePlayerPanel = new string[CSettings.MaxScreenPlayer];
            _MetaPlayersPanel= new string[CConfig.GetNumScreens()];
            _ButtonPlayer = new string[CConfig.GetNumScreens(), CSettings.MaxScreenPlayer];
            _StaticScreenBG = new string[CConfig.GetNumScreens()];
            _StaticPlayer = new string[CConfig.GetNumScreens(), CSettings.MaxScreenPlayer];
            _StaticPlayerAvatar = new string[CConfig.GetNumScreens(), CSettings.MaxScreenPlayer];
            _TextScreen = new string[CConfig.GetNumScreens()];
            _TextPlayer = new string[CConfig.GetNumScreens(), CSettings.MaxScreenPlayer];
            _EqualizerPlayer = new string[CConfig.GetNumScreens(), CSettings.MaxScreenPlayer];
            _SelectSlideDuetPlayer = new string[CConfig.GetNumScreens(), CSettings.MaxScreenPlayer];

            var statics = new List<string>
            {
                _StaticWarningMics,
                _StaticWarningProfiles
            };

            var texts = new List<string>
            {
                _TextWarningMics,
                _TextWarningProfiles
            };

            var buttons = new List<string>
            {
                _ButtonBack,
                _ButtonNewProfile,
                _ButtonStart
            };

            var nameselections = new List<string>
            {
                _NameSelection
            };

            var equalizers = new List<string>();

            var selectslides = new List<string>
            {
                _SelectSlidePlayerNumber
            };

            var metas = new List<string>();

            buttons.Add("ButtonPlayer");
            statics.Add("StaticScreenBG");
            statics.Add("StaticPlayer");
            statics.Add("StaticPlayerAvatar");
            texts.Add("TextScreen");
            texts.Add("TextPlayer");
            equalizers.Add("EqualizerPlayer");
            selectslides.Add("SelectSlideDuetPlayer");

            for (int screen = 0; screen <  CConfig.GetNumScreens(); screen++)
            {
                _StaticScreenBG[screen] = "StaticScreenBGS" + (screen + 1);
                _TextScreen[screen] = "TextScreenS" + (screen + 1);
                _MetaPlayersPanel[screen] = CConfig.GetNumScreens() + "ScreenMetaPlayersPanelS" + (screen + 1);
                metas.Add(_MetaPlayersPanel[screen]);
            }

            for (int player = 0; player < CSettings.MaxScreenPlayer; player++)
            {
                _MetaRelativePlayerPanel[player] = "MetaRelativePlayerPanel" + (player + 1);
                metas.Add(_MetaRelativePlayerPanel[player]);
                for (int screen = 0; screen < CConfig.GetNumScreens(); screen++)
                {
                    _ButtonPlayer[screen, player] = "ButtonPlayerS" + (screen + 1) + "P" + (player + 1);
                    _StaticPlayer[screen, player] = "StaticPlayerS" + (screen + 1) + "P" + (player + 1);
                    _StaticPlayerAvatar[screen, player] = "StaticPlayerAvatarS" + (screen + 1) + "P" + (player + 1);
                    _TextPlayer[screen, player] = "TextPlayerS" + (screen + 1) + "P" + (player + 1);
                    _EqualizerPlayer[screen, player] = "EqualizerPlayerS" + (screen + 1) + "P" + (player + 1);
                    _SelectSlideDuetPlayer[screen, player] = "SelectSlideDuetPlayerS" + (screen + 1) + "P" + (player + 1);
                }
            }
            _ThemeStatics = statics.ToArray();
            _ThemeTexts = texts.ToArray();
            _ThemeSelectSlides = selectslides.ToArray();
            _ThemeButtons = buttons.ToArray();
            _ThemeNameSelections = nameselections.ToArray();
            _ThemeEqualizers = equalizers.ToArray();
            _ThemeMetas = metas.ToArray();
        }

        private void _CreatePlayerElements()
        {
            for (int screen = 0; screen < CConfig.GetNumScreens(); screen++)
            {
                _AddStatic(GetNewStatic(_Statics["StaticScreenBG"]), _StaticScreenBG[screen]);
                _Statics[_StaticScreenBG[screen]].X += _Metas[_MetaPlayersPanel[screen]].X;
                _Statics[_StaticScreenBG[screen]].Y += _Metas[_MetaPlayersPanel[screen]].Y;
                _AddText(GetNewText(_Texts["TextScreen"]), _TextScreen[screen]);
                _Texts[_TextScreen[screen]].X += _Metas[_MetaPlayersPanel[screen]].X;
                _Texts[_TextScreen[screen]].Y += _Metas[_MetaPlayersPanel[screen]].Y;
                _Texts[_TextScreen[screen]].Text = "Screen " + (screen + 1);
                for (int player = 0; player < CSettings.MaxScreenPlayer; player++)
                {
                    _AddButton(GetNewButton(_Buttons["ButtonPlayer"]), _ButtonPlayer[screen, player]);
                    _Buttons[_ButtonPlayer[screen, player]].X += _Metas[_MetaPlayersPanel[screen]].X + _Metas[_MetaRelativePlayerPanel[player]].X;
                    _Buttons[_ButtonPlayer[screen, player]].Y += _Metas[_MetaPlayersPanel[screen]].Y + _Metas[_MetaRelativePlayerPanel[player]].Y;

                    _AddStatic(GetNewStatic(_Statics["StaticPlayer"]), _StaticPlayer[screen, player]);
                    _Statics[_StaticPlayer[screen, player]].X += _Metas[_MetaPlayersPanel[screen]].X + _Metas[_MetaRelativePlayerPanel[player]].X;
                    _Statics[_StaticPlayer[screen, player]].Y += _Metas[_MetaPlayersPanel[screen]].Y + _Metas[_MetaRelativePlayerPanel[player]].Y;

                    _AddStatic(GetNewStatic(_Statics["StaticPlayerAvatar"]), _StaticPlayerAvatar[screen, player]);
                    _Statics[_StaticPlayerAvatar[screen, player]].X += _Metas[_MetaPlayersPanel[screen]].X + _Metas[_MetaRelativePlayerPanel[player]].X;
                    _Statics[_StaticPlayerAvatar[screen, player]].Y += _Metas[_MetaPlayersPanel[screen]].Y + _Metas[_MetaRelativePlayerPanel[player]].Y;

                    _AddText(GetNewText(_Texts["TextPlayer"]), _TextPlayer[screen, player]);
                    _Texts[_TextPlayer[screen, player]].X += _Metas[_MetaPlayersPanel[screen]].X + _Metas[_MetaRelativePlayerPanel[player]].X;
                    _Texts[_TextPlayer[screen, player]].Y += _Metas[_MetaPlayersPanel[screen]].Y + _Metas[_MetaRelativePlayerPanel[player]].Y;

                    _AddEqualizer(GetNewEqualizer(_Equalizers["EqualizerPlayer"]), _EqualizerPlayer[screen, player]);
                    _Equalizers[_EqualizerPlayer[screen, player]].X += _Metas[_MetaPlayersPanel[screen]].X + _Metas[_MetaRelativePlayerPanel[player]].X;
                    _Equalizers[_EqualizerPlayer[screen, player]].Y += _Metas[_MetaPlayersPanel[screen]].Y + _Metas[_MetaRelativePlayerPanel[player]].Y;

                    _AddSelectSlide(GetNewSelectSlide(_SelectSlides["SelectSlideDuetPlayer"]), _SelectSlideDuetPlayer[screen, player]);
                    _SelectSlides[_SelectSlideDuetPlayer[screen, player]].X += _Metas[_MetaPlayersPanel[screen]].X + _Metas[_MetaRelativePlayerPanel[player]].X;
                    _SelectSlides[_SelectSlideDuetPlayer[screen, player]].Y += _Metas[_MetaPlayersPanel[screen]].Y + _Metas[_MetaRelativePlayerPanel[player]].Y;
                }
            }
            _Buttons["ButtonPlayer"].Visible = false;
            _Statics["StaticScreenBG"].Visible = false;
            _Statics["StaticPlayer"].Visible = false;
            _Statics["StaticPlayerAvatar"].Visible = false;
            _Texts["TextScreen"].Visible = false;
            _Texts["TextPlayer"].Visible = false;
            _Equalizers["EqualizerPlayer"].Visible = false;
            _SelectSlides["SelectSlideDuetPlayer"].Visible = false;
        }

        private void _LoadProfiles()
        {
            _NameSelections[_NameSelection].UpdateList();

            _UpdateSlides();
            _UpdatePlayerNumber();
            _CheckMics();
            //_CheckPlayers();

            _LoadPlayerPanels();
            
            _NameSelections[_NameSelection].UpdateList();
            _ProfilesChanged = false;
            _AvatarsChanged = false;
        }

        private void _LoadPlayerPanels()
        {
            for (int i = 0; i < CGame.NumPlayers; i++)
            {
                _NameSelections[_NameSelection].UseProfile(CGame.Players[i].ProfileID);
                _Statics[_PlayerStaticAvatar[i]].Texture = CProfiles.IsProfileIDValid(CGame.Players[i].ProfileID) ?
                                                               CProfiles.GetAvatarTextureFromProfile(CGame.Players[i].ProfileID) :
                                                               _OriginalPlayerAvatarTextures[i];
                _Texts[_PlayerText[i]].Text = CProfiles.GetPlayerName(CGame.Players[i].ProfileID, i + 1);
            }
            _PopulateVoiceSelection();
        }

        private void _PopulateVoiceSelection()
        {
            CSong firstSong = CGame.GetSong(0);
            for (int s = 0; s < CConfig.GetNumScreens(); s++)
            {
                for(int p = 0; p < CSettings.MaxScreenPlayer; p++)
                {
                    if (CGame.GetNumSongs() == 1 && firstSong.IsDuet)
                    {
                        _SelectSlides[_SelectSlideDuetPlayer[s, p]].Clear();

                        for (int j = 0; j < firstSong.Notes.VoiceCount; j++)
                            _SelectSlides[_SelectSlideDuetPlayer[s, p]].AddValue(firstSong.Notes.VoiceNames[j]);
                    }
                    else
                    _SelectSlides[_SelectSlideDuetPlayer[s, p]].Visible = false;
                }
            }
            /*for (int i = 0; i < (CConfig.GetNumScreens() * CSettings.MaxScreenPlayer); i++)
            {
                if (CGame.GetNumSongs() == 1 && firstSong.IsDuet)
                {
                    _SelectSlides[_PlayerSelectSlideDuet[i]].Clear();
                    _SelectSlides[_PlayerSelectSlideDuet[i]].Visible = i + 1 <= CGame.NumPlayers;

                    for (int j = 0; j < firstSong.Notes.VoiceCount; j++)
                        _SelectSlides[_PlayerSelectSlideDuet[i]].AddValue(firstSong.Notes.VoiceNames[j]);
                    _SelectSlides[_PlayerSelectSlideDuet[i]].Selection = i % 2;
                }
                else
                    _SelectSlides[_PlayerSelectSlideDuet[i]].Visible = false;
            }*/
        }

        private void _StartSong()
        {
            if (CGame.GetNumSongs() == 1 && CGame.GetSong(0).IsDuet)
            {
                for (int i = 0; i < CGame.NumPlayers; i++)
                    CGame.Players[i].VoiceNr = _SelectSlides[_PlayerSelectSlideDuet[i]].Selection;
            }
            CGraphics.FadeTo(EScreen.Sing);
        }

        private void _UpdateSlides()
        {
            _SelectSlides[_SelectSlidePlayerNumber].Clear();
            for (int i = 1; i <= CConfig.Config.Graphics.NumScreens * CSettings.MaxScreenPlayer; i++)
                _SelectSlides[_SelectSlidePlayerNumber].AddValue(CLanguage.Translate("TR_SCREENNAMES_" + i + "PLAYER"));
            _SelectSlides[_SelectSlidePlayerNumber].Selection = CConfig.Config.Game.NumPlayers - 1;
        }

        private void _ResetPlayerElements()
        {
            for (int player = 0; player < CSettings.MaxScreenPlayer; player++)
            {
                for (int screen = 0; screen < CConfig.GetNumScreens(); screen++)
                {
                    _Statics[_StaticPlayer[screen, player]].Visible = false;
                    _Statics[_StaticPlayerAvatar[screen, player]].Visible = false;
                    _Buttons[_ButtonPlayer[screen, player]].Visible = false;
                    _Buttons[_ButtonPlayer[screen, player]].Selectable = false;
                    _Texts[_TextPlayer[screen, player]].Visible = false;
                    _Equalizers[_EqualizerPlayer[screen, player]].Visible = false;
                    _SelectSlides[_SelectSlideDuetPlayer[screen, player]].Visible = false;
                }
            }
        }

        private void _UpdatePlayerNumber()
        {
            CConfig.Config.Game.NumPlayers = _SelectSlides[_SelectSlidePlayerNumber].Selection + 1;
            CGame.NumPlayers = _SelectSlides[_SelectSlidePlayerNumber].Selection + 1;
            _AssignPlayerElements();
            //_LoadPlayerPanels();
            _ResetPlayerElements();

            for (int i = 0; i < CSettings.MaxNumPlayer; i++)
            {
                if (i < CGame.NumPlayers)
                {
                    _Buttons[_PlayerButton[i]].Visible = true;
                    _Statics[_PlayerStatic[i]].Visible = true;
                    _Statics[_PlayerStaticAvatar[i]].Visible = true;
                    _Texts[_PlayerText[i]].Visible = true;
                    if (CGame.Players[i].ProfileID != Guid.Empty)
                    {
                        _Statics[_PlayerStaticAvatar[i]].Texture = CProfiles.GetAvatarTexture(CProfiles.GetAvatarID(CGame.Players[i].ProfileID));
                        _Texts[_PlayerText[i]].Text = CProfiles.GetPlayerName(CGame.Players[i].ProfileID, i + 1);
                    }
                    else
                    {
                        _Texts[_PlayerText[i]].Text = CProfiles.GetPlayerName(Guid.Empty, i + 1);
                        _Statics[_PlayerStaticAvatar[i]].Texture = _OriginalPlayerAvatarTextures[i];
                    }
                    _Texts[_PlayerText[i]].Color = CBase.Themes.GetPlayerColor(i + 1);
                    _Equalizers[_PlayerEqualizer[i]].Color.R = CBase.Themes.GetPlayerColor(i + 1).R;
                    _Equalizers[_PlayerEqualizer[i]].Color.G = CBase.Themes.GetPlayerColor(i + 1).G;
                    _Equalizers[_PlayerEqualizer[i]].Color.B = CBase.Themes.GetPlayerColor(i + 1).B;
                    _Equalizers[_PlayerEqualizer[i]].MaxColor.R = CBase.Themes.GetPlayerColor(i + 1).R;
                    _Equalizers[_PlayerEqualizer[i]].MaxColor.G = CBase.Themes.GetPlayerColor(i + 1).G;
                    _Equalizers[_PlayerEqualizer[i]].MaxColor.B = CBase.Themes.GetPlayerColor(i + 1).B;
                    _Equalizers[_PlayerEqualizer[i]].Visible = true;
                    if (CGame.GetNumSongs() == 1 && CGame.GetSong(0).IsDuet)
                        _SelectSlides[_PlayerSelectSlideDuet[i]].Visible = true;
                }
                else
                {
                    _ResetPlayerSelection(i);
                }
            }
            CConfig.SaveConfig();
            _CheckMics();
            //_CheckPlayers();
        }

        private void _UpdateSelectedProfile(int playerNum, Guid profileId)
        {
            _NameSelections[_NameSelection].RemoveUsedProfile(CGame.Players[playerNum].ProfileID);
            _NameSelections[_NameSelection].UseProfile(profileId);
            //Update Game-infos with new player
            CGame.Players[playerNum].ProfileID = profileId;
            //Update config for default players.
            CConfig.Config.Game.Players[playerNum] = CProfiles.GetProfileFileName(profileId);
            CConfig.SaveConfig();
            //Update texture and name
            _Statics[_PlayerStaticAvatar[playerNum]].Texture = CProfiles.GetAvatarTextureFromProfile(profileId);
            _Texts[_PlayerText[playerNum]].Text = CProfiles.GetPlayerName(profileId);
            //Update profile-warning
            //_CheckPlayers();
            //Update Tiles-List
            _NameSelections[_NameSelection].UpdateList();
        }

        private void _ResetPlayerSelections()
        {
            for (int i = 0; i < CGame.NumPlayers; i++)
            {
                _NameSelections[_NameSelection].RemoveUsedProfile(CGame.Players[i].ProfileID);
                CGame.Players[i].ProfileID = Guid.Empty;
                //Update config for default players.
                CConfig.Config.Game.Players[i] = String.Empty;
                //Update texture and name
                _Statics[_PlayerStaticAvatar[i]].Texture = _OriginalPlayerAvatarTextures[i];
                _Texts[_PlayerText[i]].Text = CProfiles.GetPlayerName(Guid.Empty, i + 1);
            }
            _NameSelections[_NameSelection].UpdateList();
            CConfig.SaveConfig();
        }

        private void _ResetPlayerSelection(int playerNum)
        {
            _NameSelections[_NameSelection].RemoveUsedProfile(CGame.Players[playerNum].ProfileID);
            CGame.Players[playerNum].ProfileID = Guid.Empty;
            //Update config for default players.
            CConfig.Config.Game.Players[playerNum] = String.Empty;
            CConfig.SaveConfig();
            //Update texture and name
            if(playerNum < _PlayerStaticAvatar.Length)
                _Statics[_PlayerStaticAvatar[playerNum]].Texture = _OriginalPlayerAvatarTextures[playerNum];
            if (playerNum < _PlayerText.Length)
                _Texts[_PlayerText[playerNum]].Text = CProfiles.GetPlayerName(Guid.Empty, playerNum + 1);
            //Update profile-warning
            //_CheckPlayers();
            //Update Tiles-List
            _NameSelections[_NameSelection].UpdateList();
        }

        private void _CheckMics()
        {
            var playerWithoutMicro = new List<int>();
            for (int player = 0; player < CConfig.Config.Game.NumPlayers; player++)
            {
                if (!CConfig.IsMicConfig(player + 1))
                    playerWithoutMicro.Add(player + 1);
            }
            if (playerWithoutMicro.Count > 0)
            {
                _Statics[_StaticWarningMics].Visible = true;
                _Texts[_TextWarningMics].Visible = true;

                if (playerWithoutMicro.Count > 1)
                {
                    string playerNums = string.Empty;
                    for (int i = 0; i < playerWithoutMicro.Count; i++)
                    {
                        if (playerWithoutMicro.Count - 1 == i)
                            playerNums += playerWithoutMicro[i].ToString();
                        else if (playerWithoutMicro.Count - 2 == i)
                            playerNums += playerWithoutMicro[i] + " " + CLanguage.Translate("TR_GENERAL_AND") + " ";
                        else
                            playerNums += playerWithoutMicro[i] + ", ";
                    }

                    _Texts[_TextWarningMics].Text = CLanguage.Translate("TR_SCREENNAMES_WARNING_MICS_PL").Replace("%v", playerNums);
                }
                else
                    _Texts[_TextWarningMics].Text = CLanguage.Translate("TR_SCREENNAMES_WARNING_MICS_SG").Replace("%v", playerWithoutMicro[0].ToString());
            }
            else
            {
                _Statics[_StaticWarningMics].Visible = false;
                _Texts[_TextWarningMics].Visible = false;
            }
        }

        private void _CheckPlayers()
        {
            var playerWithoutProfile = new List<int>();
            for (int player = 0; player < CConfig.Config.Game.NumPlayers; player++)
            {
                if (CGame.Players[player].ProfileID == Guid.Empty)
                    playerWithoutProfile.Add(player + 1);
            }

            if (playerWithoutProfile.Count > 0)
            {
                _Statics[_StaticWarningProfiles].Visible = true;
                _Texts[_TextWarningProfiles].Visible = true;

                if (playerWithoutProfile.Count > 1)
                {
                    string playerNums = string.Empty;
                    for (int i = 0; i < playerWithoutProfile.Count; i++)
                    {
                        if (playerWithoutProfile.Count - 1 == i)
                            playerNums += playerWithoutProfile[i].ToString();
                        else if (playerWithoutProfile.Count - 2 == i)
                            playerNums += playerWithoutProfile[i] + " " + CLanguage.Translate("TR_GENERAL_AND") + " ";
                        else
                            playerNums += playerWithoutProfile[i] + ", ";
                    }

                    _Texts[_TextWarningProfiles].Text = CLanguage.Translate("TR_SCREENNAMES_WARNING_PROFILES_PL").Replace("%v", playerNums);
                }
                else
                    _Texts[_TextWarningProfiles].Text = CLanguage.Translate("TR_SCREENNAMES_WARNING_PROFILES_SG").Replace("%v", playerWithoutProfile[0].ToString());
            }
            else
            {
                _Statics[_StaticWarningProfiles].Visible = false;
                _Texts[_TextWarningProfiles].Visible = false;
            }
        }

        private void _AssignPlayerElements()
        {
            _PlayerStatic = new String[CGame.NumPlayers];
            _PlayerStaticAvatar = new String[CGame.NumPlayers];
            _PlayerText = new String[CGame.NumPlayers];
            _PlayerButton = new String[CGame.NumPlayers];
            _PlayerEqualizer = new String[CGame.NumPlayers];
            _PlayerSelectSlideDuet = new String[CGame.NumPlayers];

            int screenPlayers = CGame.NumPlayers / CConfig.GetNumScreens();
            int remainingPlayers = CGame.NumPlayers - (screenPlayers * CConfig.GetNumScreens());
            int player = 0;

            for (int s = 0; s < CConfig.GetNumScreens(); s++)
            {
                for (int p = 0; p < screenPlayers; p++)
                {
                    if (remainingPlayers > 0)
                    {
                        if (screenPlayers == 3 && p > 1)
                        {
                            _LinkPlayerElementsToPlayer(player, s, p + 1);
                            player++;
                        }
                        else
                        {
                            _LinkPlayerElementsToPlayer(player, s, p);
                            player++;
                        }
                        if (p == screenPlayers - 1)
                        {
                            if (screenPlayers == 3 && p > 1)
                            {
                                _LinkPlayerElementsToPlayer(player, s, p + 2);
                                player++;
                            }
                            else
                            {
                                _LinkPlayerElementsToPlayer(player, s, p + 1);
                                player++;
                            }
                            remainingPlayers--;
                        }
                    }
                    else
                    {
                        if(screenPlayers == 4 && p > 1)
                        {
                            _LinkPlayerElementsToPlayer(player, s, p + 1);
                            player++;
                        } else
                        {
                            _LinkPlayerElementsToPlayer(player, s, p);
                            player++;
                        }
                    }
                }
                //Handle when players < screens
                if (screenPlayers == 0 && remainingPlayers > 0)
                {
                    _LinkPlayerElementsToPlayer(player, s, 0);
                    player++;
                    remainingPlayers--;
                }
            }
            //_PopulateVoiceSelection();
        }
        private void _LinkPlayerElementsToPlayer(int player, int screen, int element)
        {
            _PlayerStatic[player] = _StaticPlayer[screen, element];
            _PlayerStaticAvatar[player] = _StaticPlayerAvatar[screen, element];
            _PlayerText[player] = _TextPlayer[screen, element];
            _PlayerButton[player] = _ButtonPlayer[screen, element];
            _PlayerEqualizer[player] = _EqualizerPlayer[screen, element];
            _PlayerSelectSlideDuet[player] = _SelectSlideDuetPlayer[screen, element];
        }

        #endregion private methods
    }
}