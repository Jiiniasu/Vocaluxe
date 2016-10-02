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
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Vocaluxe.Base;

namespace Vocaluxe.Lib.Draw
{
    public delegate bool MessageEventHandler(ref Message m);

    public interface IFormHook
    {
        MessageEventHandler OnMessage { set; }
    }

    abstract class CDrawBaseWindows<TTextureType> : CDrawBase<TTextureType> where TTextureType : CTextureBase, IDisposable
    {
        private struct SClientRect
        {
            public Point Location;
            public int Width;
            public int Height;
        }
        protected Form[] _Form = new Form[CConfig.Config.Graphics.NumScreens];
        private SClientRect[] _Restore = new SClientRect[CConfig.Config.Graphics.NumScreens];
        protected Size[] _SizeBeforeMinimize = new Size[CConfig.Config.Graphics.NumScreens];

        public override void Close()
        {
            base.Close();
            try
            {
                foreach(Form form in _Form)
                {
                    form.Close();
                }
            }
            catch {}
        }

        protected void _CenterToScreen()
        {
            foreach (Form form in _Form)
            {
                Screen screen = Screen.FromControl(form);
                form.Location = new Point((screen.WorkingArea.Width - form.Width) / 2,
                                           (screen.WorkingArea.Height - form.Height) / 2);
            }
        }

        private static bool _OnMessageAvoidScreenOff(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x112: // WM_SYSCOMMAND
                    switch ((int)m.WParam & 0xFFF0)
                    {
                        case 0xF100: // SC_KEYMENU
                            m.Result = IntPtr.Zero;
                            return false;
                        case 0xF140: // SC_SCREENSAVER
                        case 0xF170: // SC_MONITORPOWER
                            return false;
                    }
                    break;
            }
            return true;
        }

        protected override void _EnterFullScreen()
        {
            Debug.Assert(!_Fullscreen);
            _Fullscreen = true;

            for (int i = 0; i < _Form.Length; i++)
            {
                _Restore[i].Location = _Form[i].Location;
                _Restore[i].Width = _Form[i].Width;
                _Restore[i].Height = _Form[i].Height;

                _Form[i].FormBorderStyle = FormBorderStyle.None;

                Screen screen = Screen.FromControl(_Form[i]);
                _Form[i].DesktopBounds = new Rectangle(screen.Bounds.Location, new Size(screen.Bounds.Width, screen.Bounds.Height));

                if (_Form[i].WindowState == FormWindowState.Maximized)
                {
                    _Form[i].WindowState = FormWindowState.Normal;
                    _DoResize();
                    _Form[i].WindowState = FormWindowState.Maximized;
                }
                else
                    _DoResize();
            }            
        }

        protected override void _LeaveFullScreen()
        {
            Debug.Assert(_Fullscreen);
            _Fullscreen = false;
            for (int i = 0; i < _Form.Length; i++)
            {
                _Form[i].FormBorderStyle = FormBorderStyle.Sizable;
                _Form[i].DesktopBounds = new Rectangle(_Restore[i].Location, new Size(_Restore[i].Width, _Restore[i].Height));
            }
        }

        #region form event handlers
        private void _OnClose(object sender, CancelEventArgs e)
        {
            _Run = false;
        }

        private void _OnLoad(object sender, EventArgs e)
        {
            _ClearScreen();
        }

        protected virtual void _OnResize(object sender, EventArgs e)
        {
            _DoResize();
        }

        #region mouse event handlers
        protected void _OnMouseMove(object sender, MouseEventArgs e)
        {
            _Mouse.MouseMove(e);
        }

        protected void _OnMouseWheel(object sender, MouseEventArgs e)
        {
            _Mouse.MouseWheel(e);
        }

        protected void _OnMouseDown(object sender, MouseEventArgs e)
        {
            _Mouse.MouseDown(e);
        }

        protected void _OnMouseUp(object sender, MouseEventArgs e)
        {
            _Mouse.MouseUp(e);
        }

        protected void _OnMouseLeave(object sender, EventArgs e)
        {
            _Mouse.Visible = false;
            #if !WIN && !DEBUG
            _Form.Cursor = Cursors.Default;
            #endif
            Cursor.Show();
        }

        protected void _OnMouseEnter(object sender, EventArgs e)
        {
            Cursor.Hide();
            _Mouse.Visible = true;
            #if !WIN && !DEBUG //don't want to be stuck without a cursor when debugging
            _Form.Cursor = new Cursor("Linux/blank.cur"); //Cursor.Hide() doesn't work in Mono
            #endif
        }
        #endregion

        #region keyboard event handlers
        protected void _OnPreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            _OnKeyDown(sender, new KeyEventArgs(e.KeyData));
        }

        protected void _OnKeyDown(object sender, KeyEventArgs e)
        {
            _Keys.KeyDown(e);
        }

        protected void _OnKeyPress(object sender, KeyPressEventArgs e)
        {
            _Keys.KeyPress(e);
        }

        protected void _OnKeyUp(object sender, KeyEventArgs e)
        {
            _Keys.KeyUp(e);
        }
        #endregion keyboard event handlers

        #endregion

        public override bool Init()
        {
            if (!base.Init())
                return false;
            for (int i = 0; i < _Form.Length; i++)
            {
                _Form[i].Icon = new Icon(Path.Combine(CSettings.ProgramFolder, CSettings.FileNameIcon));
                _Form[i].Text = CSettings.GetFullVersionText();
                _Form[i].Text += " Screen "+i;
                ((IFormHook)_Form[i]).OnMessage = _OnMessageAvoidScreenOff;
                _Form[i].Closing += _OnClose;
                _Form[i].Resize += _OnResize;
                _Form[i].Load += _OnLoad;

                _SizeBeforeMinimize[i] = _Form[i].ClientSize;
            }

            _CenterToScreen();

            return true;
        }

        public override void MainLoop()
        {
            for (int i = 0; i < _Form.Length; i++)
            {
                _Form[i].Show();
            }
            base.MainLoop();
            for (int i = 0; i < _Form.Length; i++)
            {
                _Form[i].Hide();
            }
        }
    }
}