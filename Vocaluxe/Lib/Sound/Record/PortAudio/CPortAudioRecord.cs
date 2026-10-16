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

using PortAudioSharp;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vocaluxe.Base;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Sound.Record.PortAudio
{
    class CPortAudioRecord : CRecordBase, IRecord
    {
        private bool _Initialized;
        private CPortAudioHandle _PaHandle;
        private Stream[] _RecHandle;
        // Kept alive for the lifetime of the streams so the native side does not call collected delegates.
        private Stream.Callback[] _RecCallbacks;

        /// <summary>
        ///     Init PortAudio and list record devices
        /// </summary>
        /// <returns>true if success</returns>
        public override bool Init()
        {
            if (!base.Init())
                return false;

            try
            {
                _PaHandle = new CPortAudioHandle();

                _EnumerateDevices();

                _RecHandle = new Stream[_Devices.Count];
                _RecCallbacks = new Stream.Callback[_Devices.Count];
                _Initialized = true;
            }
            catch (Exception e)
            {
                _Initialized = false;
                CLog.Error("Error initializing PortAudio: " + e.Message);
                Close();
                return false;
            }

            return true;
        }

        private void _EnumerateDevices()
        {
            // PortAudioSharp2 exposes no host-API selection (the old PortAudioSharp binding could
            // prefer the ASIO/ALSA host), so take every input-capable device; ASIO endpoints are
            // still available here when their driver is installed.
            int numDevices = PortAudioSharp.PortAudio.DeviceCount;
            for (int i = 0; i < numDevices; i++)
            {
                DeviceInfo info = PortAudioSharp.PortAudio.GetDeviceInfo(i);
                if (info.maxInputChannels > 0)
                {
                    var dev = new CRecordDevice(i, info.name, info.name + i, info.maxInputChannels);

                    for (int ch = 0; ch < dev.Channels; ++ch)
                    {
                        dev.PlayerChannel[ch] = CConfig.GetPlayerFromMicConfig(dev.Name, dev.Driver, ch + 1);
                    }

                    _Devices.Add(dev);
                }
            }
        }

        /// <summary>
        ///     Re-enumerate the record devices (e.g. to pick up a mic that was plugged in during the session)
        ///     and restart any running streams with the new device list
        /// </summary>
        /// <returns>true if success</returns>
        public bool Rescan()
        {
            if (!_Initialized)
                return false;

            bool wasRunning = _IsRunning();
            Stop();

            _Devices.Clear();
            try
            {
                _EnumerateDevices();
            }
            catch (Exception e)
            {
                CLog.Error("Error re-enumerating PortAudio devices: " + e.Message);
                return false;
            }

            _RecHandle = new Stream[_Devices.Count];
            _RecCallbacks = new Stream.Callback[_Devices.Count];
            if (wasRunning)
                return Start();

            return true;
        }

        /// <summary>
        ///     Checks if at least one stream is currently open
        /// </summary>
        private bool _IsRunning()
        {
            if (_RecHandle == null)
                return false;

            foreach (Stream handle in _RecHandle)
            {
                if (handle != null)
                    return true;
            }
            return false;
        }

        /// <summary>
        ///      Voice Capturing
        /// </summary>
        public bool Start()
        {
            if (!_Initialized)
                return false;

            Stop();
            _CloseAllStreams();

            foreach (CBuffer buffer in _Buffer)
                buffer.Reset();

            // PortAudio lists the same physical endpoint once per host API (MME/WASAPI/...), usually
            // under the same name. The mic config matches on name+channel, so it maps EVERY duplicate
            // to the same player and each duplicate's callback then feeds the same pitch buffer: 3x the
            // audio rate into a 4096-sample ring that discards oldest data on overflow. The tracker is
            // left with a scrambled sample stream, which kills long periods (low notes) first while short
            // periods (high notes) survive - the classic "jittery below C4" symptom. Open exactly one
            // stream per physical input: prefer the duplicate whose default rate is closest to our
            // 48 kHz request (no resampling), break ties with the lower device ID.
            var selected = new List<int>();
            for (int dev = 0; dev < _Devices.Count; dev++)
            {
                bool usingDevice = false;
                for (int ch = 0; ch < _Devices[dev].Channels; ++ch)
                {
                    if (_Devices[dev].PlayerChannel[ch] > 0)
                        usingDevice = true;
                }

                if (!usingDevice)
                    continue;

                int existing = selected.FindIndex(
                    d => string.Equals(_Devices[d].Name, _Devices[dev].Name, StringComparison.OrdinalIgnoreCase));

                if (existing != -1)
                {
                    double rate = Math.Abs(
                        PortAudioSharp.PortAudio.GetDeviceInfo(_Devices[dev].ID).defaultSampleRate - 48000.0);
                    double rateOld = Math.Abs(
                        PortAudioSharp.PortAudio.GetDeviceInfo(_Devices[selected[existing]].ID).defaultSampleRate - 48000.0);

                    if (rate < rateOld || (rate == rateOld && _Devices[dev].ID < _Devices[selected[existing]].ID))
                        selected[existing] = dev;
                    else
                        CLog.Information("[Rec] Skipping duplicate host entry '" + _Devices[dev].Name +
                                        "' (PA-ID=" + _Devices[dev].ID + "), using PA-ID=" + _Devices[selected[existing]].ID);
                    continue;
                }

                selected.Add(dev);
            }

            foreach (int dev in selected)
            {

                var inputParams = new StreamParameters
                {
                    channelCount = _Devices[dev].Channels,
                    device = _Devices[dev].ID,
                    sampleFormat = SampleFormat.Int16,
                    suggestedLatency = PortAudioSharp.PortAudio.GetDeviceInfo(_Devices[dev].ID).defaultLowInputLatency,
                    hostApiSpecificStreamInfo = IntPtr.Zero
                };

                // Per-device callback closure replaces the old shared-callback + userData(device index) scheme.
                int devIndex = dev;
                _RecCallbacks[dev] = (IntPtr input, IntPtr output, uint frameCount,
                                      ref StreamCallbackTimeInfo timeInfo,
                                      StreamCallbackFlags statusFlags, IntPtr userData)
                    => _ProcessRecordData(devIndex, input, frameCount);

                // 48 kHz + driver-chosen buffer size: works with low-latency (ASIO/WASAPI exclusive)
                // endpoints where a fixed 882-frame buffer is rejected or forced to a higher latency.
                CLog.Information("[Rec] Opening device '" + _Devices[dev].Name + "' (PA-ID=" + _Devices[dev].ID +
                                 ", channels=" + _Devices[dev].Channels + ", defRate=" +
                                 PortAudioSharp.PortAudio.GetDeviceInfo(_Devices[dev].ID).defaultSampleRate + ") @ 48000 Hz");
                try
                {
                    _RecHandle[dev] = _PaHandle.OpenInputStream(inputParams, 48000, PortAudioSharp.PortAudio.FramesPerBufferUnspecified,
                                                                StreamFlags.NoFlag, _RecCallbacks[dev]);
                }
                catch (Exception ex)
                {
                    CLog.Error(ex, "[Rec] OpenInputStream failed for '" + _Devices[dev].Name + "' (PA-ID=" +
                                   _Devices[dev].ID + ", channels=" + _Devices[dev].Channels + ") @ 48000 Hz:");
                    _CloseAllStreams();
                    return false;
                }
                if (_RecHandle[dev] == null)
                {
                    _CloseAllStreams();
                    return false;
                }

                try
                {
                    _RecHandle[dev].Start();
                }
                catch (Exception ex)
                {
                    CLog.Error(ex, "Start Stream (rec) failed:");
                    _CloseAllStreams();
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        ///     Stop Voice Capturing
        /// </summary>
        public bool Stop()
        {
            if (!_Initialized)
                return false;

            if (_RecHandle == null)
                return true;

            foreach (Stream handle in _RecHandle)
            {
                if (handle == null)
                    continue;

                try
                {
                    if (!handle.IsStopped)
                        handle.Stop();
                }
                catch (Exception ex)
                {
                    CLog.Error(ex, "Error stopping PortAudio record stream:");
                }
            }

            return true;
        }

        /// <summary>
        ///     Stop all voice capturing streams and terminate PortAudio
        /// </summary>
        public override void Close()
        {
            if (_RecHandle != null && _RecHandle.Length > 0)
            {
                Stop();
                _CloseAllStreams();
                _RecHandle = new Stream[_Devices.Count];
            }

            if (_PaHandle != null)
            {
                _PaHandle.Close();
                _PaHandle = null;
            }

            _Initialized = false;

            base.Close();
        }

        private void _CloseAllStreams()
        {
            if (_RecHandle == null || _PaHandle == null)
                return;

            for (int i = 0; i < _RecHandle.Length; i++)
            {
                if (_RecHandle[i] == null)
                    continue;

                try
                {
                    _PaHandle.CloseStream(_RecHandle[i]);
                }
                catch (Exception ex)
                {
                    CLog.Error(ex, "Error closing PortAudio record stream:");
                }
                finally
                {
                    _RecHandle[i] = null;
                }
            }
        }

        private StreamCallbackResult _ProcessRecordData(int devIndex, IntPtr input, uint frameCount)
        {
            try
            {
                if (frameCount > 0 && input != IntPtr.Zero)
                {
                    CRecordDevice dev = _Devices[devIndex];
                    uint numBytes = frameCount * (uint)dev.Channels * 2;

                    byte[] recbuffer = new byte[numBytes];

                    // copy from unmanaged to managed memory
                    Marshal.Copy(input, recbuffer, 0, (int)numBytes);
                    _HandleData(dev, recbuffer);
                }
            }
            catch (Exception e)
            {
                CLog.Error("Error on Stream Callback (rec): " + e);
            }

            return StreamCallbackResult.Continue;
        }
    }
}