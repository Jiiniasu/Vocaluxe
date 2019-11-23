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
using System.Windows.Forms;
using VocaluxeLib.Draw;
using VocaluxeLib.PartyModes;
using VocaluxeLib.Songs;
using VocaluxeLib.Xml;
using System.Diagnostics;

namespace VocaluxeLib.Menu.SongMenu
{
    class CSongMenuDetails : CSongMenuFramework
    {
        private SRectF _ScrollRect;
        private List<CStatic> _Tiles;
        private List<CText> _Artists;
        private List<CText> _Titles;
        private List<CStatic> _Covers;
        private readonly CStatic _BigCover;
        private readonly CStatic _VideoBG;
        private readonly CStatic _TextBG;
        private readonly CStatic _DuetIcon;
        private readonly CStatic _VideoIcon;
        private readonly CStatic _MedleyCalcIcon;
        private readonly CStatic _MedleyTagIcon;

        private CTextureRef _VideoBGBGTexture;
        private CTextureRef _BigCoverBGTexture;
        private CTextureRef _CoverBGTexture;
        private CTextureRef _TileBGTexture;

        private readonly CText _Artist;
        private readonly CText _Title;
        private readonly CText _SongLength;

        private CStatic _Tile;
        private CStatic _TileSelected;
        private CStatic _ScrollBar;
        private CStatic _ScrollBarPointer;
        private float _TileBleedCount;
        private float _TileCoverH;
        private float _TileCoverW;
        private float _TileSpacing;
        private float _TileTextIndent;

        private int _ListLength;
        private float _ListTextWidth;

        // Offset is the song or categoryNr of the tile in the left upper corner
        private int _Offset;

        private float _Length = -1f;
        private int _LastKnownElements;
        private int _LastKnownCategory;

        private bool _MouseWasInRect;

        private int _OldMouseY;
        private float _ListDragDiffY;
        private float _OldListDragDiffY;
        private bool _DragActive;
        private Stopwatch _DragTimer;
        private int _LAdj = 2;

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
                _UpdateList();

                _UpdateTileSelection();
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

        public CSongMenuDetails(SThemeSongMenu theme, int partyModeID) : base(theme, partyModeID)
        {
            _Artist = new CText(_Theme.SongMenuDetails.TextArtist, _PartyModeID);
            _Title = new CText(_Theme.SongMenuDetails.TextTitle, _PartyModeID);
            _SongLength = new CText(_Theme.SongMenuDetails.TextSongLength, _PartyModeID);
            _TileBleedCount = _Theme.SongMenuDetails.TileBleedCount;
            _TileSpacing = _Theme.SongMenuDetails.TileSpacing;
            _TileTextIndent = _Theme.SongMenuDetails.TileTextIndent;
            _Tile = new CStatic(_Theme.SongMenuDetails.StaticTile, _PartyModeID);
            _TileSelected = new CStatic(_Theme.SongMenuDetails.StaticTileSelected, _PartyModeID);
            _ScrollBar = new CStatic(_Theme.SongMenuDetails.StaticScrollBar, _PartyModeID);
            _ScrollBarPointer = new CStatic(_Theme.SongMenuDetails.StaticScrollBarPointer, _PartyModeID);
            _Artist = new CText(_Theme.SongMenuDetails.TextArtist, _PartyModeID);
            _Title = new CText(_Theme.SongMenuDetails.TextTitle, _PartyModeID);
            _SongLength = new CText(_Theme.SongMenuDetails.TextSongLength, _PartyModeID);
            _VideoBG = new CStatic(_Theme.SongMenuDetails.StaticVideoBG, _PartyModeID);
            _BigCover = new CStatic(_Theme.SongMenuDetails.StaticBigCover, _PartyModeID);
            _TextBG = new CStatic(_Theme.SongMenuDetails.StaticTextBG, _PartyModeID);
            _DuetIcon = new CStatic(_Theme.SongMenuDetails.StaticDuetIcon, _PartyModeID);
            _VideoIcon = new CStatic(_Theme.SongMenuDetails.StaticVideoIcon, _PartyModeID);
            _MedleyCalcIcon = new CStatic(_Theme.SongMenuDetails.StaticMedleyCalcIcon, _PartyModeID);
            _MedleyTagIcon = new CStatic(_Theme.SongMenuDetails.StaticMedleyTagIcon, _PartyModeID);
            _SubElements.AddRange(new IMenuElement[] { _Artist, _Title, _SongLength, _DuetIcon, _VideoIcon, _MedleyCalcIcon, _MedleyTagIcon });
            _DragTimer = new Stopwatch();
        }

        private void _ReadSubTheme()
        {
            _Theme.SongMenuDetails.StaticTile = (SThemeStatic)_Tile.GetTheme();
            _Theme.SongMenuDetails.StaticTileSelected = (SThemeStatic)_TileSelected.GetTheme();
            _Theme.SongMenuDetails.StaticScrollBar = (SThemeStatic)_ScrollBar.GetTheme();
            _Theme.SongMenuDetails.StaticScrollBarPointer = (SThemeStatic)_ScrollBarPointer.GetTheme();
            _Theme.SongMenuDetails.TextArtist = (SThemeText)_Artist.GetTheme();
            _Theme.SongMenuDetails.TextSongLength = (SThemeText)_SongLength.GetTheme();
            _Theme.SongMenuDetails.TextTitle = (SThemeText)_Title.GetTheme();
            _Theme.SongMenuDetails.StaticVideoBG = (SThemeStatic)_VideoBG.GetTheme();
            _Theme.SongMenuDetails.StaticBigCover = (SThemeStatic)_BigCover.GetTheme();
            _Theme.SongMenuDetails.StaticDuetIcon = (SThemeStatic)_DuetIcon.GetTheme();
            _Theme.SongMenuDetails.StaticMedleyCalcIcon = (SThemeStatic)_MedleyCalcIcon.GetTheme();
            _Theme.SongMenuDetails.StaticMedleyTagIcon = (SThemeStatic)_MedleyTagIcon.GetTheme();
            _Theme.SongMenuDetails.StaticTextBG = (SThemeStatic)_TextBG.GetTheme();
            _Theme.SongMenuDetails.StaticVideoIcon = (SThemeStatic)_VideoIcon.GetTheme();
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
            _InitTiles();
        }

        private void _InitTiles()
        {
            MaxRect = _Theme.SongMenuDetails.TileAreaRect;

            _ListTextWidth = MaxRect.W - _ListTextWidth;

            _CoverBGTexture = CBase.Themes.GetSkinTexture(_Theme.CoverBackground, _PartyModeID);
            _VideoBGBGTexture = CBase.Themes.GetSkinTexture(_Theme.CoverBigBackground, _PartyModeID);
            _BigCoverBGTexture = CBase.Themes.GetSkinTexture(_Theme.CoverBigBackground, _PartyModeID);
            _TileBGTexture = CBase.Themes.GetSkinTexture(_Theme.TileBackground, _PartyModeID);

            //Create cover tiles
            _Covers = new List<CStatic>();
            _Tiles = new List<CStatic>();
            _Artists = new List<CText>();
            _Titles = new List<CText>();

            _ListLength = (int)(MaxRect.H / (_Tile.H + (_TileSpacing/2)));
            _TileCoverH = _Tile.H;
            _TileCoverW = _TileCoverH;

            float TileTextWidth = _Tile.W - _TileCoverW - (_TileTextIndent * 2);
            float TileTextArtistHeight = 22;
            float TileTextTitleHeight = 24;

            for (int i = 0; i < _ListLength; i++)
            {
                //Create Cover
                var rect = new SRectF(Rect.X, Rect.Y + (i * (_TileCoverH + _TileSpacing)), _TileCoverW, _TileCoverH, Rect.Z);
                var cover = new CStatic(_PartyModeID, _CoverBGTexture, _Color, rect);
                _Covers.Add(cover);

                //Create Tile
                var BGrect = new SRectF(MaxRect.X, MaxRect.Y + i * (_Tile.H + _TileSpacing), MaxRect.W, _Tile.H, Rect.Z + 0.5f);
                var tilebg = new CStatic(_PartyModeID, _TileBGTexture, new SColorF(0, 0, 0, 0.6f), BGrect);
                _Tiles.Add(tilebg);

                //Create text
                var artistRect = new SRectF(MaxRect.X + _TileCoverW + _TileTextIndent, Rect.Y + (_TileSpacing / 2) + i * (_Tile.H + _TileSpacing) + (_TileTextIndent / 2) + TileTextTitleHeight, TileTextWidth, TileTextArtistHeight, Rect.Z - 1);
                CText artist = new CText(artistRect.X, artistRect.Y, artistRect.Z,
                                       artistRect.H, artistRect.W, EAlignment.Left, EStyle.Bold,
                                       "Outline", _Artist.Color, "");
                artist.MaxRect = new SRectF(artist.MaxRect.X, artist.MaxRect.Y, MaxRect.W + MaxRect.X - artist.Rect.X - 5f, artist.MaxRect.H, artist.MaxRect.Z);
                artist.ResizeAlign = EHAlignment.Center;

                _Artists.Add(artist);

                var titleRect = new SRectF(MaxRect.X + _TileCoverW + _TileTextIndent, (Rect.Y + (_TileSpacing / 2) + i * (_Tile.H + _TileSpacing)) + ( _TileTextIndent / 2), TileTextWidth, TileTextTitleHeight, Rect.Z - 1);
                CText title = new CText(titleRect.X, titleRect.Y, titleRect.Z,
                                       titleRect.H, titleRect.W, EAlignment.Left, EStyle.Normal,
                                       "Outline", _Artist.Color, "");
                title.MaxRect = new SRectF(title.MaxRect.X, title.MaxRect.Y, MaxRect.W + MaxRect.X - title.Rect.X - 5f, title.MaxRect.H, title.MaxRect.Z);
                title.ResizeAlign = EHAlignment.Center;

                _Titles.Add(title);
            }

            _ScrollRect = MaxRect;
        }

        private void _UpdateTileSelection()
        {
            foreach (CStatic cover in _Covers)
                cover.Selected = false;

            foreach (CStatic tile in _Tiles)
                tile.Selected = false;

            int tileNr = _SelectionNr - _Offset + _LAdj;
            if (tileNr >= 0 && tileNr < _Covers.Count)
                _Covers[tileNr].Selected = _Tiles[tileNr].Selected = true;
        }

        public override void Update(SScreenSongOptions songOptions)
        {
            if (CBase.BackgroundMusic.GetSongID() != -1 && _PreviewNr != CBase.Songs.GetVisibleSongNumber(CBase.BackgroundMusic.GetSongID()))
            {
                _SelectionNr = CBase.Songs.GetVisibleSongNumber(CBase.BackgroundMusic.GetSongID());
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
            _DuetIcon.Visible = false;
            _VideoIcon.Visible = false;
            _MedleyCalcIcon.Visible = false;
            _MedleyTagIcon.Visible = false;
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
                _DuetIcon.Visible = song.IsDuet;
                _VideoIcon.Visible = song.VideoFileName != "";
                _MedleyCalcIcon.Visible = song.Medley.Source == EDataSource.Calculated;
                _MedleyTagIcon.Visible = song.Medley.Source == EDataSource.Tag;

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

            _LastKnownElements = -1; //Force refresh of list
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
            _UpdateListIfRequired();
        }

        public override bool HandleInput(ref SKeyEvent keyEvent, SScreenSongOptions options)
        {
            if (keyEvent.KeyPressed)
                return false;

            bool moveAllowed = !options.Selection.RandomOnly || (options.Selection.CategoryChangeAllowed && !CBase.Songs.IsInCategory());
            bool catChangePossible = CBase.Songs.GetTabs() == EOffOn.TR_CONFIG_ON && options.Selection.CategoryChangeAllowed;

            //If nothing selected set a reasonable default value
            if (keyEvent.IsArrowKey() && moveAllowed && _SelectionNr < 0)
                _SelectionNr = (_PreviewNr < 0) ? _Offset : _PreviewNr;

            switch (keyEvent.Key)
            {
                case Keys.Enter:
                    if (CBase.Songs.IsInCategory())
                    {
                        if (_SelectionNr >= 0 && _PreviewNr != _SelectionNr)
                        {
                            _PreviewSelectedSong();
                            keyEvent.Handled = true;
                        }
                    }
                    else
                    {
                        _EnterCategory(_PreviewNr);
                        keyEvent.Handled = true;
                    }
                    break;

                case Keys.Escape:
                case Keys.Back:
                    if (CBase.Songs.IsInCategory() && catChangePossible)
                    {
                        _LeaveCategory();
                        keyEvent.Handled = true;
                    }
                    break;

                case Keys.PageUp:
                    if (catChangePossible)
                    {
                        _PrevCategory();
                        keyEvent.Handled = true;
                    }
                    break;

                case Keys.PageDown:
                    if (catChangePossible)
                    {
                        _NextCategory();
                        keyEvent.Handled = true;
                    }
                    break;

                case Keys.Left:
                    //Check for >0 so we do not allow selection of nothing (-1)
                    if (_SelectionNr > 0 && moveAllowed)
                    {
                        keyEvent.Handled = true;
                    }
                    break;

                case Keys.Right:
                    if (moveAllowed)
                    {
                        keyEvent.Handled = true;
                    }
                    break;

                case Keys.Up:
                    if (keyEvent.ModShift)
                    {
                        if (catChangePossible)
                        {
                            _PrevCategory();
                            keyEvent.Handled = true;
                        }
                    }
                    else if (_SelectionNr >= 1 && moveAllowed)
                    {
                        _SelectionNr -= 1;
                        _AutoplayPreviewIfEnabled();
                        keyEvent.Handled = true;
                    }
                    break;

                case Keys.Down:
                    if (keyEvent.ModShift)
                    {
                        if (catChangePossible)
                        {
                            _NextCategory();
                            keyEvent.Handled = true;
                        }
                    }
                    else if (moveAllowed)
                    {
                        _SelectionNr += 1;
                        _AutoplayPreviewIfEnabled();
                        keyEvent.Handled = true;
                    }
                    break;
            }
            if (!CBase.Songs.IsInCategory())
                _PreviewSelectedSong();
            return keyEvent.Handled;
        }

        public override bool HandleMouse(ref SMouseEvent mouseEvent, SScreenSongOptions songOptions)
        {
            if (!songOptions.Selection.RandomOnly || (!CBase.Songs.IsInCategory() && songOptions.Selection.CategoryChangeAllowed))
            {
                if (mouseEvent.Wheel != 0 && CHelper.IsInBounds(_ScrollRect, mouseEvent))
                {
                    _UpdateList(_Offset + mouseEvent.Wheel);
                    _UpdateTileSelection();
                }

                int lastSelection = _SelectionNr;
            }
            _MouseWasInRect = CHelper.IsInBounds(Rect, mouseEvent);
            if (mouseEvent.LB && !_DragTimer.IsRunning && (CHelper.IsInBounds(_ScrollRect, mouseEvent) || CHelper.IsInBounds(_ScrollBar.Rect, mouseEvent)))
            {
                _DragTimer.Start();
                _OldMouseY = mouseEvent.Y;
                _DragActive = true;
                return true;
            }
            else if (mouseEvent.RB)
            {
                if (CBase.Songs.IsInCategory() && CBase.Songs.GetNumCategories() > 0 && CBase.Songs.GetTabs() == EOffOn.TR_CONFIG_ON &&
                    songOptions.Selection.CategoryChangeAllowed)
                {
                    _LeaveCategory();
                    return true;
                }
                if (CBase.Songs.GetTabs() == EOffOn.TR_CONFIG_OFF && !songOptions.Selection.PartyMode)
                {
                    CBase.Graphics.FadeTo(EScreen.Main);
                    return true;
                }
            }
            if (mouseEvent.LBH && _DragActive && CHelper.IsInBounds(_ScrollBar.Rect, mouseEvent))
            {
                float ebarpos = mouseEvent.Y - _ScrollBar.Y - (_ScrollBarPointer.H / 2);
                float offset = ((ebarpos / (_ScrollBar.H - _ScrollBarPointer.H)) * (CBase.Songs.GetNumSongsVisible() - _ListLength));
                _UpdateList((int)offset);
                _UpdateTileSelection();
            }
            else if (mouseEvent.LBH && _DragActive && CHelper.IsInBounds(_ScrollRect, mouseEvent))
            {
                _ListDragDiffY = _OldListDragDiffY + _OldMouseY - mouseEvent.Y;
                while(_ListDragDiffY > _Tile.H/2)
                {
                    _UpdateList(_Offset + 1);
                    _UpdateTileSelection();
                    _ListDragDiffY -= _Tile.H;
                    _OldMouseY -= (int)_Tile.H;
                }
                while (_ListDragDiffY < -_Tile.H/2)
                {
                    _UpdateList(_Offset - 1);
                    _UpdateTileSelection();
                    _ListDragDiffY += _Tile.H;
                    _OldMouseY += (int)_Tile.H;
                }
                
                return true;
            }
            else if (_DragActive && _DragTimer.ElapsedMilliseconds >= 300)
            {
                if (_DragTimer.IsRunning)
                    _DragTimer.Reset();
                //_ListDragDiffY = 0;
                _OldListDragDiffY = _ListDragDiffY;
                _DragActive = false;
                
                return true;
            }
            else if (_DragActive && _ListDragDiffY-_OldListDragDiffY < 25 && _DragTimer.ElapsedMilliseconds < 200 && CHelper.IsInBounds(_ScrollRect, mouseEvent))
            {
                if (_DragTimer.IsRunning)
                    _DragTimer.Reset();
                //_ListDragDiffY = 0;
                _OldListDragDiffY = _ListDragDiffY;
                _DragActive = false;

                int i = 0;
                foreach (CStatic tile in _Tiles)
                {
                    //create a rect including the cover and text of the song. 
                    //(this way mouse over text should make a selection as well)
                    SRectF songRect = new SRectF(tile.Rect.X, tile.Rect.Y, Rect.W, tile.Rect.H, tile.Rect.Z);
                    if (tile.Visible && CHelper.IsInBounds(songRect, mouseEvent))
                    {
                        _SelectionNr = i -_LAdj + _Offset;
                        if (!CBase.Songs.IsInCategory())
                            _PreviewNr = i -_LAdj + _Offset;
                        if (_SelectionNr >= _Offset +_Tiles.Count - 2 * _LAdj - 1 || _SelectionNr <= _Offset)
                            _OldListDragDiffY = _ListDragDiffY = 0;
                        break;
                    }
                    i++;
                }
                if (_SelectionNr >= 0 && _MouseWasInRect)
                {
                    if (CBase.Songs.IsInCategory())
                    {
                        if (_PreviewNr != _SelectionNr)
                        {
                            _PreviewNr = _SelectionNr;
                            return true;
                        } else
                        {
                            return false;
                        }
                    }
                    else
                    {
                        EnterSelectedCategory();
                        return true;
                    }
                    
                }
            }
            else
            {
                if(_DragTimer.IsRunning)
                    _DragTimer.Reset();
                //_ListDragDiffY = 0;
                _OldListDragDiffY = _ListDragDiffY;
                _DragActive = false;

                return true;
            }
            return false;
        }

        public override void Draw()
        {
            _DrawScrollBar();
            _DrawTiles();
            _DrawCovers();
            _DrawVideoPreview();

            _TextBG.Draw();
            _BigCover.Draw(EAspect.LetterBox);
            foreach (IMenuElement element in _SubElements)
                element.Draw();

            _DrawTileArtistTexts();
            _DrawTileSongTitleTexts();

            
        }

        public override CStatic GetSelectedSongCover()
        {
            return _Covers.FirstOrDefault(cover => cover.Selected);
        }

        public override bool IsMouseOverSelectedSong(SMouseEvent mEvent)
        {
            for (int i = 0; i < _Covers.Count; i++)
            {
                if (!_Tiles[i].Selected)
                    continue;
                return CHelper.IsInBounds(_Tiles[i].Rect, mEvent) || CHelper.IsInBounds(_Artists[i].Rect, mEvent) || CHelper.IsInBounds(_Titles[i].Rect, mEvent);
            }
            return false;
        }

        protected override void _EnterCategory(int categoryNr)
        {
            base._EnterCategory(categoryNr);

            _UpdateListIfRequired();
        }

        protected override void _LeaveCategory()
        {
            base._LeaveCategory();

            SetSelectedCategory(0);
            _UpdateListIfRequired();
        }

        private void _DrawTiles()
        {
            int i = 0;
            foreach (CStatic tilebg in _Tiles)
            {
                if (tilebg.Selected)
                {
                    SRectF selectedrect = new SRectF(_Tile.X, _Tile.Y, _Tile.W, _Tile.H, _Tile.Z);
                    selectedrect.Y = Rect.Y - _ListDragDiffY + i * (_TileCoverH + _TileSpacing);
                    selectedrect = selectedrect.Scale(SelectedTileZoomFactor);
                    selectedrect.X = (float)Math.Round(selectedrect.X);
                    selectedrect.Y = (float)Math.Round(selectedrect.Y);
                    selectedrect.H = (float)Math.Round(selectedrect.H);
                    selectedrect.W = (float)Math.Round(selectedrect.W);
                    tilebg.MaxRect = selectedrect;
                    tilebg.Color = _TileSelected.Color;
                    tilebg.Z = _Tile.Z - 0.1f;
                }
                else
                {
                    tilebg.MaxRect = _Tile.MaxRect;
                    tilebg.Y = Rect.Y - _ListDragDiffY + i * (_TileCoverH + _TileSpacing);
                    tilebg.Color = _Tile.Color;
                    tilebg.Z = _Tile.Z;
                }
                tilebg.Draw();
                i++;
            }
        }

        private void _DrawCovers()
        {
            int i = 0;
            foreach (CStatic cover in _Covers)
            {
                EAspect aspect = (cover.Texture != _CoverBGTexture) ? EAspect.Crop : EAspect.Stretch;
                if (cover.Selected)
                {
                    SRectF selectedrect = new SRectF(_Tile.X, _Tile.Y, _Tile.H, _Tile.H, _Tile.Z);
                    selectedrect.Y = Rect.Y - _ListDragDiffY + i * (_TileCoverH + _TileSpacing);
                    selectedrect = selectedrect.Scale(SelectedTileZoomFactor);
                    selectedrect.X = MaxRect.X - (MaxRect.W * (SelectedTileZoomFactor - 1) / 2);
                    selectedrect.Y = (float)Math.Round(selectedrect.Y);
                    selectedrect.H = (float)Math.Round(selectedrect.H);
                    selectedrect.W = (float)Math.Round(selectedrect.W);
                    selectedrect.X = MaxRect.X - (MaxRect.W * (SelectedTileZoomFactor - 1) / 2);
                    cover.MaxRect = selectedrect;
                    cover.Color.A = 1f;
                    cover.Z = Rect.Z - 0.1f;
                    cover.Draw();
                }
                else
                {
                    cover.MaxRect = new SRectF(_Tile.X, _Tile.Y, _Tile.H, _Tile.H, _Tile.Z);
                    cover.Y = Rect.Y - _ListDragDiffY + i * (_TileCoverH + _TileSpacing);
                    cover.Color.A = 0.5f;
                    cover.Draw(aspect);
                }
                i++;
            }
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

        private void _DrawTileArtistTexts()
        {
            int i = 0;
            foreach (CText text in _Artists)
            {
                if (i < _Covers.Count && _Covers[i].Selected)
                {
                    _DrawSelectedTileArtistText(text);
                }
                else if (i < _Covers.Count)
                {
                    CText drawtext = new CText(text);
                    drawtext.Color.A = 0.5f;
                    drawtext.X = MaxRect.X + _TileCoverW + _TileTextIndent;
                    drawtext.Y -= _ListDragDiffY;
                    drawtext.Draw();
                }
                else
                    text.Text = "";
                i++;
            }
        }

        private void _DrawTileSongTitleTexts()
        {
            int i = 0;
            foreach (CText text in _Titles)
            {
                if (i < _Covers.Count && _Covers[i].Selected)
                {
                    _DrawSelectedTileTitleText(text);
                }
                else if (i < _Covers.Count)
                {
                    CText drawtext = new CText(text);
                    drawtext.Color.A = 0.5f;
                    drawtext.Y -= _ListDragDiffY;
                    drawtext.Draw();
                }
                else
                    text.Text = "";
                i++;
            }
        }

        private void _DrawScrollBar()
        {
            if (CBase.Songs.GetNumSongsVisible() > _ListLength - 2 * _LAdj) {

                float Adjust = (((float)_Offset / (CBase.Songs.GetNumSongsVisible() - _ListLength + 2 * _LAdj)) * (_ScrollBar.H - _ScrollBarPointer.H));
                float ScrollBarPosition = _ScrollBar.Y + Adjust;
                _ScrollBarPointer.Y = ScrollBarPosition;

                _ScrollBar.Draw();
                _ScrollBarPointer.Draw();
            }
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

        private void _DrawSelectedTileText(CText text, float scaleFactor, EStyle style)
        {
            CText ScaledText = _ScaleText(text, scaleFactor, style);
            ScaledText.Color.A = 1f;
            float X = MaxRect.X - (MaxRect.W * (SelectedTileZoomFactor - 1) / 2);
            X += (_TileCoverW + _TileTextIndent) * SelectedTileZoomFactor;
            ScaledText.X = (float)Math.Round(X);
            ScaledText.Y -= _ListDragDiffY;
            ScaledText.Draw();
        }

        private void _DrawSelectedTileArtistText(CText text)
        {
            _DrawSelectedTileText(text, SelectedTileZoomFactor, EStyle.Bold);
        }

        private void _DrawSelectedTileTitleText(CText text)
        {
            _DrawSelectedTileText(text, SelectedTileZoomFactor, EStyle.Normal);
        }

        private void _NextCategory()
        {
            if (CBase.Songs.IsInCategory())
            {
                CBase.Songs.NextCategory();
                _EnterCategory(CBase.Songs.GetCurrentCategoryIndex());
            }
        }

        private void _PrevCategory()
        {
            if (CBase.Songs.IsInCategory())
            {
                CBase.Songs.PrevCategory();
                _EnterCategory(CBase.Songs.GetCurrentCategoryIndex());
            }
        }

        private void _UpdateListIfRequired()
        {
            int curElements = CBase.Songs.IsInCategory() ? CBase.Songs.GetNumSongsVisible() : CBase.Songs.GetNumCategories();
            if ((_LastKnownElements == curElements) && (_LastKnownCategory == CBase.Songs.GetCurrentCategoryIndex()))
                return;

            _LastKnownCategory = CBase.Songs.GetCurrentCategoryIndex();
            _LastKnownElements = curElements;
            CBase.Songs.UpdateRandomSongList();
            _UpdateList(true);
        }

        private void _UpdateList(bool force = false)
        {
            int offset;
            if (_SelectionNr < _Offset && _SelectionNr >= 0)
                offset = _SelectionNr;
            else if (_SelectionNr >= _Offset + _ListLength - 2 * _LAdj)
                offset = _SelectionNr - _ListLength + 1 + 2 * _LAdj;
            else
                offset = _Offset;
            _UpdateList(offset, force);
        }

        private void _UpdateList(int offset, bool force = false)
        {
            bool isInCategory = CBase.Songs.IsInCategory();
            int itemCount = isInCategory ? CBase.Songs.GetNumSongsVisible() : CBase.Songs.GetNumCategories();
            itemCount += 2 * _LAdj;
            int totalSongNumber = CBase.Songs.GetNumSongs();

            offset = offset.Clamp(0, itemCount - _ListLength, true);

            if (offset == _Offset && !force)
                return;

            for (int i = 0; i < _Covers.Count; i++)
            {
                if(offset + i < _LAdj || offset + i > itemCount - _LAdj -1)
                {
                    _Covers[i].Visible = false;
                    _Tiles[i].Visible = false;
                    _Covers[i].Texture = _CoverBGTexture;
                    _Artists[i].Text = "";
                    _Titles[i].Text = "";
                }
                else if (i - _LAdj + offset < itemCount - _LAdj)
                {
                    _Covers[i].Color = new SColorF(1f, 1f, 1f, 1f);
                    _Covers[i].Visible = true;
                    _Tiles[i].Visible = true;
                    if (isInCategory)
                    {
                        CSong currentSong = CBase.Songs.GetVisibleSong(i - _LAdj + offset);
                        _Covers[i].Texture = currentSong.CoverTextureSmall;
                        _Artists[i].Text = currentSong.Artist;
                        _Titles[i].Text = currentSong.Title;
                    }
                    else
                    {
                        CCategory currentCat = CBase.Songs.GetCategory(i - _LAdj + offset);
                        _Covers[i].Texture = currentCat.CoverTextureSmall;
                        int num = currentCat.GetNumSongsNotSung();
                        String songOrSongs = (num == 1) ? "TR_SCREENSONG_NUMSONG" : "TR_SCREENSONG_NUMSONGS";
                        _Titles[i].Text = currentCat.Name;
                        _Artists[i].Text = CBase.Language.Translate(songOrSongs).Replace("%v", num.ToString());
                    }
                }
                else
                {
                    _Covers[i].Visible = false;
                    _Tiles[i].Visible = false;
                    _Covers[i].Texture = _CoverBGTexture;
                    _Artists[i].Text = "";
                    _Titles[i].Text = "";
                }
            }
            _Offset = offset;
        }

        public override void LoadSkin()
        {
            foreach (IThemeable themeable in _SubElements.OfType<IThemeable>())
                themeable.LoadSkin();
            // Those are drawn seperately so they are not in the above list
            _VideoBG.LoadSkin();
            _BigCover.LoadSkin();
            _TextBG.LoadSkin();
            _Tile.LoadSkin();
            _TileSelected.LoadSkin();
            _ScrollBar.LoadSkin();
            _ScrollBarPointer.LoadSkin();

            base.LoadSkin();
        }
    }
}