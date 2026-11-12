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

#if WIN
using System;
using System.Runtime.InteropServices;

namespace Vocaluxe.Lib.Draw
{
    /// <summary>
    ///     Best-effort query of the dedicated GPU video memory via DXGI (Windows only). Returns 0 when it cannot
    ///     be determined (no adapter, unsupported call, or any COM failure), so callers treat 0 as "unknown" and
    ///     simply skip the VRAM safety valve. The ComImport interfaces below must declare every vtable slot in
    ///     order up to the one that is actually used.
    /// </summary>
    internal static class CVramProbe
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_QUERY_VIDEO_MEMORY_INFO
        {
            public UIntPtr NodeMemoryLocalSegment;
            public UIntPtr NodeMemorySharedSystemMemory;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr SharedSystemMemory;
            public UIntPtr DedicatedAllocationGranularity;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public int LowPart;
            public uint HighPart;
        }

        [ComImport, Guid("00000000-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUnknown
        {
            [PreserveSig] int QueryInterface(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
            [PreserveSig] int AddRef();
            [PreserveSig] int Release();
        }

        [ComImport, Guid("a6feb760-39f2-11e0-8ca4-48d5bd2fce0c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1 : IUnknown
        {
            //IDXGIFactory
            int SetPrivateData(ref Guid guid, int dataSize, IntPtr pvData);
            int SetPrivateDataInterface(ref Guid guid, IntPtr unknown);
            int GetPrivateData(ref Guid guid, out int dataSize, IntPtr pvData);
            int GetPrivateDataInterface(ref Guid guid, [MarshalAs(UnmanagedType.IUnknown)] out object unknown);
            int CreateSwapChain(IntPtr device, IntPtr swapChainDesc, [MarshalAs(UnmanagedType.IUnknown)] out object swapChain);
            int CreateSurface(IntPtr device, IntPtr surfaceDesc, [MarshalAs(UnmanagedType.IUnknown)] out object surface);
            int IsSupported(IntPtr device, ref Guid iid, out IntPtr ppvObject);
            //IDXGIFactory1
            int IsCurrent();
            [return: MarshalAs(UnmanagedType.Interface)] IDXGIAdapter1 EnumAdapter1(int adapter, int flags);
            int EnumWarpAdapters(int start, int flags, [MarshalAs(UnmanagedType.IUnknown)] out object adapter);
            int EnumWarpAdapters1(int start, int flags, [MarshalAs(UnmanagedType.IUnknown)] out object adapter1);
            int SetDebugLevel(int flags, int level);
            int GetDebugLevel(out int flags, out int level);
        }

        [ComImport, Guid("291e9c95-355d-413f-88bc-bb330a52a2f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1 : IUnknown
        {
            //IDXGIAdapter
            int SetPrivateData(ref Guid guid, int dataSize, IntPtr pvData);
            int SetPrivateDataInterface(ref Guid guid, IntPtr unknown);
            int GetPrivateData(ref Guid guid, out int dataSize, IntPtr pvData);
            int GetPrivateDataInterface(ref Guid guid, [MarshalAs(UnmanagedType.IUnknown)] out object unknown);
            int EnumDisplayModes(int format, int count, IntPtr modes, out int returned);
            int GetDisplayModeList(int format, int count, IntPtr modes, out int returned);
            int FindBestDisplayMode(int format, IntPtr request, int flags, out IntPtr mode);
            int GetDesc(out IntPtr desc);
            int CheckInterface(ref Guid iid, out IntPtr ppvObject);
            int GetAdapterLuid(out LUID luid);
            //IDXGIAdapter1
            int GetDesc1(out IntPtr desc1);
            int QueryVideoMemoryInfo(int adapterIndex, int type, out DXGI_QUERY_VIDEO_MEMORY_INFO info);
            int RecommendVideoMemorySize(int adapterIndex, ulong totalUsage, out ulong recommended);
        }

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

        public static long QueryVramBytes()
        {
            try
            {
                Guid riid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref riid, out IDXGIFactory1 factory) != 0 || factory == null)
                    return 0;
                IDXGIAdapter1 adapter = factory.EnumAdapter1(0, 0);
                if (adapter == null)
                    return 0;
                DXGI_QUERY_VIDEO_MEMORY_INFO info;
                if (adapter.QueryVideoMemoryInfo(0, 0, out info) != 0)
                    return 0;
                long vram = (long)info.DedicatedVideoMemory;
                return vram > 0 ? vram : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
#endif