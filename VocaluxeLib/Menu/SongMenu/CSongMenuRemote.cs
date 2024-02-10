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
using System.Linq;
using VocaluxeLib.Draw;
using VocaluxeLib.PartyModes;
using VocaluxeLib.Songs;

namespace VocaluxeLib.Menu.SongMenu
{
    class CSongMenuRemote : CSongMenuFramework
    {
        private readonly CStatic _BigCover;
        private readonly CStatic _VideoBG;
        private readonly CStatic _TextBG;

        private CTextureRef _VideoBGBGTexture;
        private CTextureRef _BigCoverBGTexture;
        private CTextureRef _CoverBGTexture;
        private CTextureRef _TileBGTexture;

        private readonly CText _Artist;
        private readonly CText _Title;
        private readonly CText _SongLength;

        private float _Length = -1f;

        private readonly List<IMenuElement> _SubElements = new List<IMenuElement>();

        public override float SelectedTileZoomFactor
        {
            get { return 1.05f; }
        }

        protected override int _SelectionNr
        {
            set
            {
                int max = CBase.Songs.IsInCategory() ? CBase.Songs.GetNumSongsVisible() : CBase.Songs.GetNumCategories();
                base._SelectionNr = value.Clamp(-1, max - 1, true);
                //Update list in case we scrolled 
            }
        }

        protected override int _PreviewNr
        {
            set
            {
                if (value == base._PreviewNr)
                {
                    if (!CBase.BackgroundMusic.IsPlaying() && value != -1)
                        CBase.BackgroundMusic.Play();
                    return;
                }
                base._PreviewNr = value;
                _UpdatePreview();
            }
        }

        public CSongMenuRemote(SThemeSongMenu theme, int partyModeID) : base(theme, partyModeID)
        {
            _Artist = new CText(_Theme.SongMenuRemote.TextArtist, _PartyModeID);
            _Title = new CText(_Theme.SongMenuRemote.TextTitle, _PartyModeID);
            _SongLength = new CText(_Theme.SongMenuRemote.TextSongLength, _PartyModeID);
            _Artist = new CText(_Theme.SongMenuRemote.TextArtist, _PartyModeID);
            _Title = new CText(_Theme.SongMenuRemote.TextTitle, _PartyModeID);
            _SongLength = new CText(_Theme.SongMenuRemote.TextSongLength, _PartyModeID);
            _VideoBG = new CStatic(_Theme.SongMenuRemote.StaticVideoBG, _PartyModeID);
            _BigCover = new CStatic(_Theme.SongMenuRemote.StaticBigCover, _PartyModeID);
            _TextBG = new CStatic(_Theme.SongMenuRemote.StaticTextBG, _PartyModeID);
            _SubElements.AddRange(new IMenuElement[] { _Artist, _Title, _SongLength});
        }

        private void _ReadSubTheme()
        {
            _Theme.SongMenuRemote.TextArtist = (SThemeText)_Artist.GetTheme();
            _Theme.SongMenuRemote.TextSongLength = (SThemeText)_SongLength.GetTheme();
            _Theme.SongMenuRemote.TextTitle = (SThemeText)_Title.GetTheme();
            _Theme.SongMenuRemote.StaticVideoBG = (SThemeStatic)_VideoBG.GetTheme();
            _Theme.SongMenuRemote.StaticBigCover = (SThemeStatic)_BigCover.GetTheme();
            _Theme.SongMenuRemote.StaticTextBG = (SThemeStatic)_TextBG.GetTheme();
        }

        public override object GetTheme()
        {
            _ReadSubTheme();
            return base.GetTheme();
        }

        public override void Init()
        {
            base.Init();
            _PreviewNr = -1;
        }

        public override void Update(SScreenSongOptions songOptions)
        {
            if (CBase.BackgroundMusic.GetSongID() != -1 && _PreviewNr != CBase.Songs.GetVisibleSongNumber(CBase.BackgroundMusic.GetSongID()))
            {
                _SelectionNr = CBase.Songs.GetVisibleSongNumber(CBase.BackgroundMusic.GetSongID());
                _PreviewNr = _SelectionNr;
            }
            
            if (CBase.BackgroundMusic.IsFinished() && !CBase.BackgroundMusic.IsLoading() && CBase.Graphics.GetNextScreenType() == EScreen.Unknown)
            {
                Random rng = new Random();
                int max = CBase.Songs.GetNumSongs() - 1;
                _SelectionNr = rng.Next(0, max);
                _PreviewNr = _SelectionNr;
            }
            if (songOptions.Selection.RandomOnly)
                 _PreviewNr = _SelectionNr;

             if (_Length < 0 && CBase.Songs.IsInCategory() && CBase.BackgroundMusic.GetLength() > 0)
                 _UpdateLength(CBase.Songs.GetVisibleSong(_PreviewNr));
        }

        private void _UpdatePreview()
        {
            //First hide everything so we just have to set what we actually want
            _VideoBG.Texture = _VideoBGBGTexture;
            _BigCover.Texture = _BigCoverBGTexture;
            _Artist.Text = String.Empty;
            _Title.Text = String.Empty;
            _SongLength.Text = String.Empty;
            _Length = -1f;

            //Check if nothing is selected (for preview)
            if (_PreviewNr < 0)
                return;

            if (CBase.Songs.IsInCategory())
            {
                CSong song = CBase.Songs.GetVisibleSong(_PreviewNr);
                //Check if we have a valid song (song still visible, index >=0 etc is checked by framework)
                if (song == null)
                {
                    //Display at least the category
                    CCategory category = CBase.Songs.GetCategory(CBase.Songs.GetCurrentCategoryIndex());
                    //Check if we have a valid category
                    if (category == null)
                        return;
                    _VideoBG.Texture = category.CoverTextureBig;
                    _Artist.Text = category.Name;
                    return;
                }
                _VideoBG.Texture = song.CoverTextureBig;
                _BigCover.Texture = song.CoverTextureBig;
                _Artist.Text = song.Artist;
                _Title.Text = song.Title;

                _UpdateLength(song);
            }
            else
            {
                CCategory category = CBase.Songs.GetCategory(_PreviewNr);
                //Check if we have a valid category
                if (category == null)
                    return;
                _VideoBG.Texture = category.CoverTextureBig;
                _Artist.Text = category.Name;

                int num = category.GetNumSongsNotSung();
                String songOrSongs = (num == 1) ? "TR_SCREENSONG_NUMSONG" : "TR_SCREENSONG_NUMSONGS";
                _Title.Text = CBase.Language.Translate(songOrSongs).Replace("%v", num.ToString());
            }
        }

        private void _UpdateLength(CSong song)
        {
            if (song == null)
                return;
            float time = CBase.BackgroundMusic.GetLength();
            if (Math.Abs(song.Finish) > 0.001)
                time = song.Finish;

            // The audiobackend is ready to return the length
            if (time > 0)
            {
                time -= song.Start;
                var min = (int)Math.Floor(time / 60f);
                var sec = (int)(time - min * 60f);
                _SongLength.Text = min.ToString("00") + ":" + sec.ToString("00");
                _Length = time;
            }
            else
                _SongLength.Text = "...";
        }

        public override void OnShow()
        {
            int song = CBase.BackgroundMusic.GetSongID();
            if (song != -1)
            {
                _EnterCategory(0);
                _SelectionNr = CBase.Songs.GetVisibleSongNumber(song);
                _PreviewNr = _SelectionNr;
            }

            if (!CBase.Songs.IsInCategory())
            {
                if ((CBase.Songs.GetTabs() == EOffOn.TR_CONFIG_OFF && CBase.Songs.GetNumCategories() > 0) || CBase.Songs.GetNumCategories() == 1)
                    _EnterCategory(0);
            }
            if (CBase.Songs.IsInCategory())
                SetSelectedSong(_SelectionNr < 0 ? 0 : _SelectionNr);
            else
                SetSelectedCategory(_SelectionNr < 0 ? 0 : _SelectionNr);
            _PreviewNr = _SelectionNr;
        }

        public override bool HandleInput(ref SKeyEvent keyEvent, SScreenSongOptions options)
        {
            return false;
        }

        public override bool HandleMouse(ref SMouseEvent mouseEvent, SScreenSongOptions songOptions)
        {
            return false;
        }

        public override void Draw()
        {
            _DrawVideoPreview();

            _TextBG.Draw();
            _BigCover.Draw(EAspect.LetterBox);
            foreach (IMenuElement element in _SubElements)
                element.Draw();
            
        }

        public override CStatic GetSelectedSongCover()
        {
            return new CStatic(0);
        }

        private void _DrawVideoPreview()
        {
            CTextureRef vidtex = CBase.BackgroundMusic.IsPlayingPreview() ? CBase.BackgroundMusic.GetVideoTexture() : null;

            if (vidtex != null)
            {
                if (vidtex.Color.A < 1)
                    _VideoBG.Draw(EAspect.Crop);
                SRectF rect = CHelper.FitInBounds(_VideoBG.Rect, vidtex.OrigAspect, EAspect.Crop);
                rect.Z = _VideoBG.Z;
                CBase.Drawing.DrawTexture(vidtex, rect, vidtex.Color, _VideoBG.Rect);
                CBase.Drawing.DrawTextureReflection(vidtex, rect, vidtex.Color, _VideoBG.Rect, _VideoBG.ReflectionSpace, _VideoBG.ReflectionHeight);
            }
            else
                _VideoBG.Draw(EAspect.Crop);
        }

        private CText _ScaleText(CText text, float scaleFactor, EStyle style)
        {
            SRectF ScaledRect = new SRectF(text.X, text.Y, text.W, text.H, text.Z);
            ScaledRect = ScaledRect.Scale(scaleFactor);
            CText ScaledText = new CText(ScaledRect.X, ScaledRect.Y, ScaledRect.Z,
                                    ScaledRect.H, ScaledRect.W, text.Align, style,
                                    "Outline", text.Color, "");
            ScaledText.MaxRect = new SRectF(ScaledText.MaxRect.X, ScaledText.MaxRect.Y, MaxRect.W + MaxRect.X - ScaledText.Rect.X - 5f, ScaledText.MaxRect.H, ScaledText.MaxRect.Z);
            ScaledText.ResizeAlign = EHAlignment.Center;
            ScaledText.Text = text.Text;
            return ScaledText;
        }

        public override void LoadSkin()
        {
            foreach (IThemeable themeable in _SubElements.OfType<IThemeable>())
                themeable.LoadSkin();
            // Those are drawn seperately so they are not in the above list
            _VideoBG.LoadSkin();
            _BigCover.LoadSkin();
            _TextBG.LoadSkin();

            base.LoadSkin();
        }
    }
}