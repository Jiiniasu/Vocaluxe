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
using VocaluxeLib.Menu;
using VocaluxeLib.Profile;

namespace Vocaluxe.Screens
{

    public class CPopupScreenNewPlayer : CMenu
    {
        // Version number for theme files. Increment it, if you've changed something on the theme files!
        protected override int _ScreenVersion
        {
            get { return 1; }
        }

        //private const string _SelectSlideProfiles = "SelectSlideProfiles";
        private const string _SelectSlideDifficulty = "SelectSlideDifficulty";
        private const string _SelectSlideAvatars = "SelectSlideAvatars";
        //private const string _SelectSlideUserRole = "SelectSlideUserRole";
        //private const string _SelectSlideActive = "SelectSlideActive";
        private const string _ButtonPlayerName = "ButtonPlayerName";
        private const string _ButtonCancel = "ButtonCancel";
        private const string _ButtonSave = "ButtonSave";
        //private const string _ButtonNew = "ButtonNew";
        //private const string _ButtonDelete = "ButtonDelete";

        private readonly string[] _StaticButtonPlayerName = new string[] { "StaticButtonPlayerName0", "StaticButtonPlayerName1", "StaticButtonPlayerName2" };

        private Guid _NewProfileID;
        private const string _StaticAvatar = "StaticAvatar";
        private bool _ProfilesChanged;
        private bool _AvatarsChanged;
        private bool _CursorBlink;
        private static Timer _Timer;

        private EEditMode _EditMode;

        public override void Init()
        {
            base.Init();

            _ThemeButtons = new string[]
                {_ButtonPlayerName, _ButtonCancel, _ButtonSave};
            _ThemeSelectSlides = new string[] { _SelectSlideDifficulty, _SelectSlideAvatars };
            _ThemeStatics = new string[] { _StaticAvatar };

            _EditMode = EEditMode.None;
            _ProfilesChanged = false;
            _AvatarsChanged = false;
            _CursorBlink = false;
            _Timer = new Timer();
            _Timer.Tick += new EventHandler(_TimerEvent);
            _Timer.Interval = 400;
            _Timer.Start();
            CProfiles.AddProfileChangedCallback(_OnProfileChanged);
        }

        private void _TimerEvent(Object sender, EventArgs e)
        {
            _CursorBlink = !_CursorBlink;
        }

        public override void LoadTheme(string xmlPath)
        {
            base.LoadTheme(xmlPath);

            _SelectSlides[_SelectSlideDifficulty].SetValues<EGameDifficulty>(0);
            //_SelectSlides[_SelectSlideUserRole].SetValues<EUserRole>(0);
            //_SelectSlides[_SelectSlideActive].SetValues<EOffOn>(0);
            _Statics[_StaticAvatar].Aspect = EAspect.Crop;
        }

        public override bool HandleInput(SKeyEvent keyEvent)
        {
            if ((keyEvent.Key == Keys.Up || keyEvent.Key == Keys.Down) && _EditMode == EEditMode.PlayerName)
                _EditMode = EEditMode.None;
            if (_EditMode == EEditMode.None)
                base.HandleInput(keyEvent);

            if (keyEvent.KeyPressed && !Char.IsControl(keyEvent.Unicode))
            {
                switch (_EditMode)
                {
                    case EEditMode.None:
                        break;
                    case EEditMode.PlayerName:
                        CProfiles.AddGetPlayerName(_NewProfileID, keyEvent.Unicode);
                        _ProfilesChanged = true;
                        break;
                }
            }
            else
            {
                switch (keyEvent.Key)
                {
                    case Keys.Escape:
                        _DeleteProfile();
                        _ClosePopup();
                        break;

                    case Keys.Enter:
                        if (_Buttons[_ButtonCancel].Selected)
                        {
                            _DeleteProfile();
                            _ClosePopup();
                        }

                        else if (_Buttons[_ButtonSave].Selected)
                        {
                            _SaveProfiles();
                            _ClosePopup();
                        }

                        /*else if (_Buttons[_ButtonNew].Selected)
                            _NewProfile();*/
                        else if (_Buttons[_ButtonPlayerName].Selected)
                        {
                            if (CProfiles.NumProfiles > 0 && _EditMode != EEditMode.PlayerName)
                                _EditMode = EEditMode.PlayerName;
                            else
                                _EditMode = EEditMode.None;
                        }
                        /*else if (_Buttons[_ButtonDelete].Selected)
                            _DeleteProfile();*/
                        break;

                    case Keys.Back:
                        if (_EditMode == EEditMode.PlayerName)
                        {
                            CProfiles.GetDeleteCharInPlayerName(_NewProfileID);
                            _ProfilesChanged = true;
                        }
                        else
                            _ClosePopup();
                        break;

                    case Keys.Delete:
                        _DeleteProfile();
                        break;
                }
                if (_SelectSlides[_SelectSlideDifficulty].Selected)
                {
                    CProfiles.SetDifficulty(_NewProfileID,
                                            (EGameDifficulty)_SelectSlides[_SelectSlideDifficulty].Selection);
                }
                else if (_SelectSlides[_SelectSlideAvatars].Selected)
                {
                    CProfiles.SetAvatar(_NewProfileID,
                                        _SelectSlides[_SelectSlideAvatars].SelectedTag);
                }
                /*else if (_SelectSlides[_SelectSlideUserRole].Selected)
                {
                    CProfiles.SetUserRoleProfile(_NewProfileID,
                                                 (EUserRole)_SelectSlides[_SelectSlideUserRole].Selection);
                }
                else if (_SelectSlides[_SelectSlideActive].Selected)
                {
                    CProfiles.SetActive(_NewProfileID,
                                        (EOffOn)_SelectSlides[_SelectSlideActive].Selection);
                }*/
            }

            return true;
        }

        public override bool HandleMouse(SMouseEvent mouseEvent)
        {
            if (_EditMode == EEditMode.None)
                base.HandleMouse(mouseEvent);

            if (mouseEvent.LB && _IsMouseOverCurSelection(mouseEvent))
            {
                if (_Buttons[_ButtonCancel].Selected)
                {
                    _DeleteProfile();
                    _ClosePopup();
                }
                else if (_Buttons[_ButtonSave].Selected)
                {
                    _SaveProfiles();
                    _ClosePopup();
                }
                /*else if (_Buttons[_ButtonNew].Selected)
                    _NewProfile();
                else if (_Buttons[_ButtonDelete].Selected)
                    _DeleteProfile();*/
                else if (_Buttons[_ButtonPlayerName].Selected)
                {
                    if (CProfiles.NumProfiles > 0 && _EditMode != EEditMode.PlayerName)
                        _EditMode = EEditMode.PlayerName;
                    else
                        _EditMode = EEditMode.None;
                }
                else if (_SelectSlides[_SelectSlideDifficulty].Selected)
                {
                    CProfiles.SetDifficulty(_NewProfileID,
                                            (EGameDifficulty)_SelectSlides[_SelectSlideDifficulty].Selection);
                }
                else if (_SelectSlides[_SelectSlideAvatars].Selected)
                {
                    CProfiles.SetAvatar(_NewProfileID,
                                        _SelectSlides[_SelectSlideAvatars].SelectedTag);
                }
                /*else if (_SelectSlides[_SelectSlideUserRole].Selected)
                {
                    CProfiles.SetUserRoleProfile(_NewProfileID,
                                                 (EUserRole)_SelectSlides[_SelectSlideUserRole].Selection);
                }
                else if (_SelectSlides[_SelectSlideActive].Selected)
                {
                    CProfiles.SetActive(_NewProfileID,
                                        (EOffOn)_SelectSlides[_SelectSlideActive].Selection);
                }
                */
            }

            if (mouseEvent.RB)
                _ClosePopup();
            return true;
        }

        public override bool UpdateGame()
        {
            if (_Buttons[_ButtonPlayerName].Selected)
            {
                _EditMode = EEditMode.PlayerName;
            }
            //BODGE!
            for (int i = 0; i < 3; i++)
            {
                _Texts["TextSelectSlideDifficulty" + i].Text = _SelectSlides[_SelectSlideDifficulty].SelectedValue;
                _Statics["StaticSelectSlideDifficultyL" + i].Visible = true;
                _Statics["StaticSelectSlideDifficultyR" + i].Visible = true;
                if (_SelectSlides[_SelectSlideDifficulty].Selection == 0)
                    _Statics["StaticSelectSlideDifficultyL" + i].Visible = false;
                if (_SelectSlides[_SelectSlideDifficulty].Selection == _SelectSlides[_SelectSlideDifficulty].NumValues - 1)
                    _Statics["StaticSelectSlideDifficultyR" + i].Visible = false;

                _Texts["TextSelectSlideAvatars" + i].Text = _SelectSlides[_SelectSlideAvatars].SelectedValue;
                _Statics["StaticSelectSlideAvatarsL" + i].Visible = true;
                _Statics["StaticSelectSlideAvatarsR" + i].Visible = true;
                if (_SelectSlides[_SelectSlideAvatars].Selection == 0)
                    _Statics["StaticSelectSlideAvatarsL" + i].Visible = false;
                if (_SelectSlides[_SelectSlideAvatars].Selection == _SelectSlides[_SelectSlideAvatars].NumValues - 1)
                    _Statics["StaticSelectSlideAvatarsR" + i].Visible = false;

                _Statics["StaticAvatar" + i].Aspect = _Statics["StaticAvatar"].Aspect;
                _Statics["StaticAvatar" + i].Texture = _Statics["StaticAvatar"].Texture;
                _Texts["TextPlayerName" + i].Text = _Buttons[_ButtonPlayerName].Text.Text;
                if (_Buttons[_ButtonPlayerName].Selected)
                {
                    _Statics[_StaticButtonPlayerName[i]].Visible = true;
                    _Statics[_StaticButtonPlayerName[i]].Texture = _Buttons[_ButtonPlayerName].SelTexture;
                    _Statics[_StaticButtonPlayerName[i]].Color = _Buttons[_ButtonPlayerName].SelColor;
                }
                else
                {
                    _Statics[_StaticButtonPlayerName[i]].Visible = false;
                }
                if (_Buttons[_ButtonCancel].Selected)
                {
                    _Statics["StaticButtonCancel" + i].Color = _Buttons[_ButtonCancel].SelColor;
                    _Texts["TextButtonCancel" + i].Selected = true;
                }
                else
                {
                    _Statics["StaticButtonCancel" + i].Color = _Buttons[_ButtonCancel].Color;
                    _Texts["TextButtonCancel" + i].Selected = false;
                }
                if (_Buttons[_ButtonSave].Selected)
                {
                    _Statics["StaticButtonSave" + i].Color = _Buttons[_ButtonSave].SelColor;
                    _Texts["TextButtonSave" + i].Selected = true;
                }
                else
                {
                    _Statics["StaticButtonSave" + i].Color = _Buttons[_ButtonSave].Color;
                    _Texts["TextButtonSave" + i].Selected = false;
                }

                if (_SelectSlides[_SelectSlideDifficulty].Selected)
                {
                    _Statics["StaticSelectSlideDifficulty" + i].Visible = true;
                    _Texts["TextSelectSlideDifficulty" + i].Selected = true;
                }
                else
                {
                    _Statics["StaticSelectSlideDifficulty" + i].Visible = false;
                    _Texts["TextSelectSlideDifficulty" + i].Selected = false;
                }

                if (_SelectSlides[_SelectSlideAvatars].Selected)
                {
                    _Statics["StaticSelectSlideAvatars" + i].Visible = true;
                    _Texts["TextSelectSlideAvatars" + i].Selected = true;
                }
                else
                {
                    _Statics["StaticSelectSlideAvatars" + i].Visible = false;
                    _Texts["TextSelectSlideAvatars" + i].Selected = false;
                }
            }

            if (_AvatarsChanged)
                _LoadAvatars(true);

            if (_ProfilesChanged)
                _LoadProfiles(true);

            //if (_SelectSlides[_SelectSlideProfiles].Selection > -1)
            //{
            _Buttons[_ButtonPlayerName].Text.Text = CProfiles.GetPlayerName(_NewProfileID);
            if (_EditMode == EEditMode.PlayerName && _CursorBlink)
                _Buttons[_ButtonPlayerName].Text.Text = " " + _Buttons[_ButtonPlayerName].Text.Text + "_";
            else _Buttons[_ButtonPlayerName].Text.Text = " " + _Buttons[_ButtonPlayerName].Text.Text + " ";

            _SelectSlides[_SelectSlideDifficulty].Selection = (int)CProfiles.GetDifficulty(_NewProfileID);
            //_SelectSlides[_SelectSlideUserRole].Selection = (int)CProfiles.GetUserRoleProfile(_NewProfileID);
            //_SelectSlides[_SelectSlideActive].Selection = (int)CProfiles.GetActive(_NewProfileID);

            int avatarID = CProfiles.GetAvatarID(_NewProfileID);
            _SelectSlides[_SelectSlideAvatars].SelectedTag = avatarID;
            _Statics[_StaticAvatar].Texture = CProfiles.GetAvatarTexture(avatarID);
            //}

            return true;
        }

        public override void OnShow()
        {
            base.OnShow();
            _LoadAvatars(false);
            _LoadProfiles(false);
            UpdateGame();
            _NewProfile();
        }

        public override void OnClose()
        {
            base.OnClose();
            _EditMode = EEditMode.None;
        }

        private void _OnProfileChanged(EProfileChangedFlags flags)
        {
            if (EProfileChangedFlags.Avatar == (EProfileChangedFlags.Avatar & flags))
                _AvatarsChanged = true;

            if (EProfileChangedFlags.Profile == (EProfileChangedFlags.Profile & flags))
                _ProfilesChanged = true;
        }

        private void _ClosePopup()
        {
            string _NewProfileName = CProfiles.GetPlayerName(_NewProfileID);
            if (_NewProfileName.Length < 1)
            {
                _DeleteProfile();
            }

            CGraphics.HidePopup(EPopupScreens.PopupNewPlayer);
        }

        private void _NewProfile()
        {
            _EditMode = EEditMode.None;
            Guid id = CProfiles.NewProfile();
            //_LoadProfiles(false);
            _NewProfileID = id;

            CProfiles.SetAvatar(_NewProfileID, _SelectSlides[_SelectSlideAvatars].SelectedTag);

            _SelectElement(_Buttons[_ButtonPlayerName]);
            //_EditMode = EEditMode.PlayerName;
        }

        private void _SaveProfiles()
        {
            _EditMode = EEditMode.None;
            string _NewProfileName = CProfiles.GetPlayerName(_NewProfileID);
            if (_NewProfileName.Length > 1)
                CProfiles.SaveProfiles();
            _ProfilesChanged = true;
        }

        private void _DeleteProfile()
        {
            _EditMode = EEditMode.None;

            CProfiles.DeleteProfile(_NewProfileID);
        }

        private void _LoadProfiles(bool keep)
        {
            /*string name = String.Empty;
            if (_EditMode == EEditMode.PlayerName)
                name = CProfiles.GetPlayerName(_NewProfileID);
                //name = CProfiles.GetPlayerName(_SelectSlides[_SelectSlideProfiles].SelectedTag);*/

            Guid selectedProfileID = _NewProfileID;
            //_SelectSlides[_SelectSlideProfiles].Clear();

            CProfile[] profiles = CProfiles.GetProfiles();
            /*foreach (CProfile profile in profiles)
                _SelectSlides[_SelectSlideProfiles].AddValue(profile.PlayerName, null, profile.ID);*/

            if (CProfiles.NumProfiles > 0 && CProfiles.NumAvatars > 0)
            {
                /*if (selectedProfileID != -1)
                    _SelectSlides[_SelectSlideProfiles].SelectedTag = selectedProfileID;
                else
                {
                    _SelectSlides[_SelectSlideProfiles].Selection = 0;
                    selectedProfileID = _SelectSlides[_SelectSlideProfiles].SelectedTag;
                }*/

                if (!keep)
                {
                    _SelectSlides[_SelectSlideDifficulty].Selection = (int)CProfiles.GetDifficulty(selectedProfileID);
                    //_SelectSlides[_SelectSlideUserRole].Selection = (int)CProfiles.GetUserRoleProfile(selectedProfileID);
                    //_SelectSlides[_SelectSlideActive].Selection = (int)CProfiles.GetActive(selectedProfileID);
                    _SelectSlides[_SelectSlideAvatars].SelectedTag = CProfiles.GetAvatarID(selectedProfileID);
                }

                /*if (_EditMode == EEditMode.PlayerName)
                    CProfiles.SetPlayerName(_SelectSlides[_SelectSlideProfiles].SelectedTag, name);*/
            }
            _ProfilesChanged = false;
        }

        private void _LoadAvatars(bool keep)
        {
            int selectedAvatarID = _SelectSlides[_SelectSlideAvatars].SelectedTag;
            _SelectSlides[_SelectSlideAvatars].Clear();
            IEnumerable<CAvatar> avatars = CProfiles.GetAvatars();
            if (avatars != null)
            {
                foreach (CAvatar avatar in avatars)
                    _SelectSlides[_SelectSlideAvatars].AddValue(avatar.GetDisplayName(), null, avatar.ID);
            }

            if (keep)
            {
                _SelectSlides[_SelectSlideAvatars].SelectedTag = selectedAvatarID;
                CProfiles.SetAvatar(_NewProfileID, selectedAvatarID);
            }
            else
                _SelectSlides[_SelectSlideAvatars].SelectedTag = CProfiles.GetAvatarID(_NewProfileID);

            _AvatarsChanged = false;
        }
    }
}