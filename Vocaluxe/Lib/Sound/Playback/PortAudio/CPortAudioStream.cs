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
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Vocaluxe.Base;
using Vocaluxe.Lib.Sound.Playback.Decoder;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Sound.Playback.PortAudio
{
    class CPortAudioStream : CAudioStreamBase
    {
        private const int _Bufsize = 1000000;
        private const int _BeginRefill = 800000;

        private readonly CSyncTimer _SyncTimer = new CSyncTimer(0f, 1f, 0.02f);
        private int _ByteCount;

        private CPortAudioHandle _PaHandle;
        private PortAudioSharp.DeviceInfo _OutputDeviceInfo;
        private PortAudioSharp.Stream _Stream;

        private PortAudioSharp.Stream.Callback _PaStreamCallback;
        private IAudioDecoder _Decoder;
        private float _BytesPerSecond;
        private float _Latency;
        private bool _NoMoreData;

        private bool _FileOpened;

        private bool _Skip;

        // Self-kept playback-position reference: seconds of output audio written to the ring buffer
        // since the last skip/stop. Intentionally NOT the decoder-supplied timestamp: for some files
        // the native decoder's PTS-based timecode freezes permanently mid-stream (acinerella clamps
        // per-packet timestamp deltas; e.g. this library's "Ash Again.mp3" stalls at 152.2 s of 298.5 s,
        // which froze the sync timer and stopped the notes mid-song while the audio played on).
        // Accounting the actual output bytes is always exact and monotonically increasing.
        private volatile float _TimeCode;

        private volatile bool _Paused;

        private CRingBuffer _Data;
        private volatile float _SetStart;
        private float _Start;
        private volatile bool _SetSkip;
        private volatile bool _Terminated;

        // Reused work buffer for the real-time callback. The PortAudio audio thread must never
        // allocate (each allocation is GC pressure that can stall the callback past its deadline and
        // produce crackle/underruns). The buffer is sized once and only grown if a callback ever
        // requests more frames.
        private byte[] _WorkBuf = Array.Empty<byte>();

        private Thread _DecoderThread;

        private AutoResetEvent _EventDecode = new AutoResetEvent(false);

        private readonly Object _LockData = new Object();
        private readonly Object _LockSyncSignals = new Object();

        public override float Position
        {
            get
            {
                lock (_LockData)
                {
                    // Position is driven by the self-kept byte accounting (_TimeCode, accumulated in
                    // _DoDecode) smoothed by the synctimer. This must NOT depend on the native decoder's
                    // timestamp: for some files it freezes mid-stream (e.g. VBR MP3s with broken PTS),
                    // which stopped the notes mid-song. If you change this, test for monotonic
                    // timestamps, especially for ogg files.
                    float time = Math.Max(0f, _SyncTimer.Time);
                    if (time > Length)
                    {
                        _SyncTimer.Pause();
                        time = Length;
                    }
                    return time;
                }
            }
            set
            {
                lock (_LockSyncSignals)
                {
                    _SetStart = value;
                    _SetSkip = true;
                    _EventDecode.Set();
                }
            }
        }

        public override bool IsPaused
        {
            get { return _Paused; }
            set
            {
                if (_Paused == value)
                    return;
                if (!_FileOpened || _Terminated)
                    return;
                _Paused = value;
                lock (_LockData)
                {
                    if (_Paused)
                    {
                        //Freeze the position clock BEFORE the (blocking) Stop() call: the callback
                        //already sees _Paused and emits silence, so the audible audio stops now.
                        //Stopping the clock afterwards let the position over-run for the whole Stop()
                        //duration (tens to hundreds of ms on slow backends like ASIO) - the song
                        //"skipped ahead ~a beat" when the user pressed pause.
                        _SyncTimer.Pause();
                        try { _Stream.Stop(); }
                        catch (Exception ex) { CLog.Error(ex, "StopStream (playback) error:"); }
                    }
                    else
                    {
                        try { _Stream.Start(); }
                        catch (Exception ex) { CLog.Error(ex, "StartStream error:"); }
                        _SyncTimer.Resume();
                        _EventDecode.Set();
                    }
                }
            }
        }

        public override bool IsFinished
        {
            get
            {
                lock (_LockData)
                {
                    return _NoMoreData && _Data.BytesNotRead == 0 && _SyncTimer.Time >= Length;
                }
            }
        }

        public CPortAudioStream(int id, string medium, bool loop) : base(id, medium, loop) { }

        public override bool Open(bool prescan)
        {
            Debug.Assert(!_FileOpened);
            if (_FileOpened)
                return false;

            if (!File.Exists(_Medium))
            {
                Dispose();
                return false;
            }

            try
            {
                _PaHandle = new CPortAudioHandle();

                _OutputDeviceInfo = PortAudioSharp.PortAudio.GetDeviceInfo(PortAudioSharp.PortAudio.DefaultOutputDevice);

                _PaStreamCallback = _ProcessNewData;
            }
            catch (Exception)
            {
                Dispose();
                CLog.Error("Error Init PortAudio Playback");
                return false;
            }

            _Decoder = new CAudioDecoderFFmpeg();
            if (!_Decoder.Open(_Medium))
            {
                Dispose();
                CLog.Error("Error opening audio file: " + _Medium);
                return false;
            }

            SFormatInfo format = _Decoder.GetFormatInfo();
            if (format.SamplesPerSecond == 0)
            {
                Dispose();
                CLog.Error("Error Init PortAudio Playback (samples=0)");
                return false;
            }

            Length = _Decoder.GetLength();
            _ByteCount = 2 * format.ChannelCount;
            _BytesPerSecond = format.SamplesPerSecond * _ByteCount;
            _SyncTimer.Pause();
            _SyncTimer.Time = 0f;

            uint framesPerBuffer = (uint)CConfig.Config.Sound.AudioBufferSize / 2;
            // Pre-size the real-time work buffer so the callback never has to allocate.
            int workBufSize = (int)framesPerBuffer * _ByteCount;
            if (_WorkBuf.Length < workBufSize)
                _WorkBuf = new byte[workBufSize];

            double outputLatency = Math.Max(0.1, _OutputDeviceInfo.defaultLowOutputLatency);
            var outputParams = new PortAudioSharp.StreamParameters
            {
                channelCount = format.ChannelCount,
                device = PortAudioSharp.PortAudio.DefaultOutputDevice,
                sampleFormat = PortAudioSharp.SampleFormat.Int16,
                suggestedLatency = outputLatency,
                hostApiSpecificStreamInfo = IntPtr.Zero
            };

            _Stream = _PaHandle.OpenOutputStream(
                outputParams,
                format.SamplesPerSecond,
                framesPerBuffer,
                PortAudioSharp.StreamFlags.NoFlag,
                _PaStreamCallback);
            if (_Stream == null)
            {
                Dispose();
                return false;
            }

            // PortAudioSharp2 does not expose per-stream latency; approximate with the device's default.
            _Latency = CConfig.Config.Sound.AudioLatency / 1000f + (float)outputLatency;

            //From now on closing the driver and the decoder is handled by the thread ONLY!

            _Paused = true;
            _FileOpened = true;
            _Data = new CRingBuffer(_Bufsize);
            _NoMoreData = false;
            _DecoderThread = new Thread(_Execute) { Priority = ThreadPriority.Normal, Name = Path.GetFileName(_Medium) };
            _DecoderThread.Start();

            return true;
        }

        protected override void _Dispose(bool disposing)
        {
            base._Dispose(disposing);
            if (!_Terminated)
            {
                _Terminated = true;
                if (_DecoderThread != null)
                    _EventDecode.Set();
                else
                    _DoFree();
            }
        }

        public override void Play()
        {
            if (!_FileOpened || _Terminated || !IsPaused)
                return;
            IsPaused = false;
        }

        public override void Stop()
        {
            if (!_FileOpened || _Terminated)
                return;
            IsPaused = true;
            Position = 0f;
        }

        #region Threading
        private void _DoSkip()
        {
            _Decoder.SetPosition(_Start);
            lock (_LockData)
            {
                _SyncTimer.Time = _Start;
                _TimeCode = _Start;
                _Data.Reset();
                _NoMoreData = false;
            }
        }

        private void _Execute()
        {
            while (!_Terminated)
            {
                lock (_LockSyncSignals)
                {
                    if (_SetSkip)
                    {
                        _Skip = true;
                        _SetSkip = false;
                    }

                    _Start = _SetStart;
                }

                if (_Skip)
                {
                    _DoSkip();
                    _Skip = false;
                }

                _DoDecode();
                _EventDecode.WaitOne();
            }

            _DoFree();
        }

        private void _DoDecode()
        {
            if (_Paused || _Terminated || _NoMoreData)
                return;

            float timecode;
            byte[] buffer;

            lock (_LockData)
            {
                if (_Data.BytesNotRead > _BeginRefill)
                    return;
            }

            _Decoder.Decode(out buffer, out timecode);

            if (buffer == null)
            {
                if (_Loop)
                {
                    _Start = 0f;
                    _DoSkip();
                }
                else
                    _NoMoreData = true;
                return;
            }

            lock (_LockData)
            {
                _Data.Write(buffer);
                // Drive the sync timer from actual output bytes written, not from the decoder
                // timestamp, which can freeze mid-file for some encodings (see _TimeCode).
                _TimeCode += buffer.Length / _BytesPerSecond;
                if (_Data.BytesNotRead < _BeginRefill)
                    _EventDecode.Set();
            }
        }

        private void _DoFree()
        {
            if (_PaHandle != null)
            {
                if (_Stream != null)
                    _PaHandle.CloseStream(_Stream);
                _PaHandle.Close();
                _PaHandle = null;
            }
            if (_DecoderThread != null)
            {
                if (Thread.CurrentThread.ManagedThreadId != _DecoderThread.ManagedThreadId)
                    throw new Exception("Another thread should never free the decoder thread!");
                _DecoderThread = null;
            }
            if (_Decoder != null)
            {
                _Decoder.Close();
                _Decoder = null;
            }
            if (_EventDecode != null)
            {
                _EventDecode.Close();
                _EventDecode = null;
            }
            if (_CloseStreamListener != null)
                _CloseStreamListener.OnCloseStream(this);
        }
        #endregion Threading

        #region Callbacks
        private PortAudioSharp.StreamCallbackResult _ProcessNewData(
            IntPtr input,
            IntPtr output,
            uint frameCount,
            ref PortAudioSharp.StreamCallbackTimeInfo timeInfo,
            PortAudioSharp.StreamCallbackFlags statusFlags,
            IntPtr userData)
        {
            // Real-time path: NO allocations here. Allocate/GC work on the audio thread stalls the
            // callback past its deadline and audibly produces crackle/dropouts (verified on this
            // machine with a raw PortAudio harness). Reuse the pre-sized work buffer, growing only
            // if a callback ever asks for more frames than we pre-allocated.
            int needed = (int)frameCount * _ByteCount;
            if (needed > _WorkBuf.Length)
                _WorkBuf = new byte[needed];
            byte[] buf = _WorkBuf;

            // Start from silence so underruns and the tail of a non-looping file stay quiet.
            System.Array.Clear(buf, 0, needed);

            if (_Paused)
            {
                try
                {
                    Marshal.Copy(buf, 0, output, needed);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                }
                return PortAudioSharp.StreamCallbackResult.Continue;
            }

            lock (_LockData)
            {
                int dataLen = _Data.BytesNotRead;
                if ((_NoMoreData && dataLen > 0) || dataLen >= buf.Length)
                {
                    dataLen = Math.Min(dataLen, needed);
                    _Data.Read(buf);
                    //We want to scale all values. No matter how many channels we have (_ByteCount=2 or 4) we have short values
                    //So just process 2 bytes a time. Done in place with integer math: the previous
                    //BitConverter.GetBytes allocated a new array per sample, driving GC on the
                    //real-time thread and causing crackle (verified with a raw PortAudio harness).
                    int volFix = (int)(Volume * VolumeMax * 32768f); // 16.16 fixed point
                    for (int i = 0; i < dataLen; i += 2)
                    {
                        int s = (short)(buf[i] | (buf[i + 1] << 8));
                        int v = (s * volFix) / 32768; // C# int division truncates toward zero, as (Int16)(s*volume) did
                        if (v > 32767) v = 32767;
                        if (v < -32768) v = -32768;
                        buf[i] = (byte)(v & 0xFF);
                        buf[i + 1] = (byte)((v >> 8) & 0xFF);
                    }
                    float latency = (float)needed / _BytesPerSecond + _Latency;
                    float time = _TimeCode - _Data.BytesNotRead / _BytesPerSecond - latency;
                    _SyncTimer.Update(time);
                }

                if (_Data.BytesNotRead < _BeginRefill && !_NoMoreData)
                    _EventDecode.Set();
            }

            try
            {
                Marshal.Copy(buf, 0, output, needed);
            }
            catch (Exception e)
            {
                CLog.Error("Error PortAudio.StreamCallback: " + e.Message);
            }

            return PortAudioSharp.StreamCallbackResult.Continue;
        }
        #endregion Callbacks
    }
}