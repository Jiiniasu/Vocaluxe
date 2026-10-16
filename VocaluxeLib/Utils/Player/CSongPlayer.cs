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
using System.IO;
using VocaluxeLib.Draw;
using VocaluxeLib.Songs;

namespace VocaluxeLib.Utils.Player
{
    public class CSongPlayer : CSoundPlayer
    {
        private CSong _Song;
        private bool _VideoEnabled;
        private CVideoStream _Video;
        private CFading _VideoFading;

        // -1 = not a preview (normal playback, e.g. the BGM player). >= 0 = this is a song
        // preview: keep the caller on the cover until the video has actually reached this
        // start time, instead of showing a held/initial frame that looks "stuck" while the
        // audio + video seek to the start position (which is the slow, visible part).
        private float _PreviewStart = -1f;
        private bool _VideoAtPlayStart;

        public bool VideoEnabled
        {
            get { return _VideoEnabled; }
            set
            {
                if (_VideoEnabled == value)
                    return;
                _VideoEnabled = value;
                if (_VideoEnabled)
                    _LoadVideo();
                else
                    _CloseVideo();
            }
        }

        public int SongID
        {
            get { return _Song == null ? -1 : _Song.ID; }
        }

        public bool SongHasVideo
        {
            get
            {
                return _Song != null && !String.IsNullOrEmpty(_Song.Folder) && !String.IsNullOrEmpty(_Song.VideoFileName) &&
                       File.Exists(Path.Combine(_Song.Folder, _Song.VideoFileName));
            }
        }

        public override string ArtistAndTitle
        {
            get
            {
                if (_Song != null && !String.IsNullOrEmpty(_Song.Artist) && !String.IsNullOrEmpty(_Song.Title))
                    return _Song.Artist + " - " + _Song.Title;
                return base.ArtistAndTitle;
            }
        }

        public CTextureRef Cover
        {
            get { return _Song == null ? CBase.Cover.GetNoCover() : _Song.CoverTextureBig; }
        }

        public CSongPlayer(bool loop = false) : base(loop) { }

        public CTextureRef GetVideoTexture()
        {
            if (_Video == null || _Song == null)
                return null;
            if (CBase.Video.GetFrame(_Video, CBase.Sound.GetPosition(_StreamID)))
            {
                // For a preview only: the video is still on its pre-start/initial frame while
                // it seeks to the start position. Keep the cover on screen until a real frame
                // at the start has been decoded, so it looks like an intentional fade-in
                // instead of a frozen "stuck" frame. Normal (non-preview) players have
                // _PreviewStart < 0 and are unaffected.
                if (_PreviewStart >= 0f && !_VideoAtPlayStart)
                {
                    if (_Video.VideoTime < _PreviewStart - 0.25f)
                        return null;
                    _VideoAtPlayStart = true;
                }

                if (_VideoFading != null)
                {
                    bool finished;
                    _Video.Texture.Color.A = _VideoFading.GetValue(out finished);
                    if (finished)
                        _VideoFading = null;
                }
                return _Video.Texture;
            }
            return null;
        }

        public void SetPreviewStart(float start)
        {
            _PreviewStart = start;
            _VideoAtPlayStart = false;
        }

        public void Load(CSong song, float position = 0f, bool autoplay = false)
        {
            if (song == null)
                throw new ArgumentNullException(nameof(song));

            Load(song.GetMP3(), position, autoplay);
            _Song = song;
            _LoadVideo();
        }

        private void _LoadVideo()
        {
            if (_Song == null)
                return;

            if (_Video != null || !SongHasVideo)
                return;
            string videoFilePath = Path.Combine(_Song.Folder, _Song.VideoFileName);
            _Video = CBase.Video.Load(videoFilePath);
            if (_Video == null)
                return;

            _VideoFading = new CFading(0f, 1f, 3f);

            if (IsPlaying)
            {
                CBase.Video.Skip(_Video, Position, _Song.VideoGap);
                CBase.Video.Resume(_Video);
            }
            else
                CBase.Video.Skip(_Video, 0f, _Song.VideoGap);
        }

        public override bool Play()
        {
            if (!base.Play())
                return false;

            if (_Video != null)
            {
                CBase.Video.Skip(_Video, Position, _Song.VideoGap);
                CBase.Video.Resume(_Video);
            }
            return true;
        }

        public override bool Pause()
        {
            if (!base.Pause())
                return false;

            if (_Video != null)
                CBase.Video.Pause(_Video);
            return true;
        }

        public override bool Stop()
        {
            if (!base.Stop())
                return false;
            if (_Video != null)
            {
                CBase.Video.Pause(_Video);
                CBase.Video.Skip(_Video, 0f, _Song.VideoGap);
            }
            return true;
        }

        private void _CloseVideo()
        {
            if (_Video != null)
                CBase.Video.Close(ref _Video);
        }

        public override void Close()
        {
            base.Close();

            _Song = null;
            _PreviewStart = -1f;
            _VideoAtPlayStart = false;
            _CloseVideo();
        }
    }
}