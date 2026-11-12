#region license
// This file is part of Vocaluxe.
// 
// Vocaluxe is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Vocaluxe is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Vocaluxe.Base;
using Vocaluxe.Base.Fonts;
using Vocaluxe.Base.ThemeSystem;
using VocaluxeLib;
using VocaluxeLib.Log;

namespace Vocaluxe
{
    class CLoadingException : Exception
    {
        public CLoadingException(string component)
            : base("Failed to load " + component) { }
    }

    static class CMainProgram
    {
        [STAThread]
        // ReSharper disable InconsistentNaming
        private static void Main(string[] args)
        // ReSharper restore InconsistentNaming
        {
            // Run from the executable's directory so the relative data paths (Themes, Languages,
            // Fonts, ...) resolve regardless of the launch working directory.
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

#if !DEBUG
            AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler;
#endif
            AppDomain.CurrentDomain.AssemblyResolve += _AssemblyResolver;
#if WIN
            COSFunctions.AddEnvironmentPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs\\unmanaged\\"));
#if ARCH_X86
            COSFunctions.AddEnvironmentPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs\\unmanaged\\x86\\"));
#endif
#if ARCH_X64
            COSFunctions.AddEnvironmentPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs\\unmanaged\\x64\\"));
#endif
#endif

            // Close program if there is another instance running
            if (!_EnsureSingleInstance())
                return;
#if !DEBUG 
            try
            {
                _Run(args);
            }
            catch (Exception e)
            {
                CLog.Fatal(e, "Unhandled error: {ErrorMessage}", CLog.Params(e.Message));
            }
#else
            _Run(args);
#endif
            _CloseProgram();
        }

        private static void _Run(string[] args)
        {
            try
            {
                // Create data folder
                Directory.CreateDirectory(CSettings.DataFolder);

                // Init Log
                CLog.Init(CSettings.FolderNameLogs,
                    CSettings.FileNameMainLog,
                    CSettings.FileNameSongLog,
                    CSettings.FileNameCloudLog,
                    CSettings.FileNameCrashMarker,
                    CSettings.GetFullVersionText(),
                    null, // CReporter (WinForms) removed; crash dialogs are logged only
                    ELogLevel.Information);

                if (!CProgramHelper.CheckRequirements())
                    return;
                CProgramHelper.Init();

                using (CBenchmark.Time("Init Program"))
                {
                    CMain.Init();

                    // Init Language
                    using (CBenchmark.Time("Init Language"))
                    {
                        if (!CLanguage.Init())
                            throw new CLoadingException("Language");
                    }

                    // load config
                    using (CBenchmark.Time("Init Config"))
                    {
                        CConfig.LoadCommandLineParams(args);
                        CConfig.UseCommandLineParamsBefore();
                        CConfig.Init();
                        CConfig.UseCommandLineParamsAfter();
                    }

                    // Create folders
                    CSettings.CreateFolders();

                    // Init Draw
                    using (CBenchmark.Time("Init Draw"))
                    {
                        if (!CDraw.Init())
                            throw new CLoadingException("drawing");
                    }

                    // Show the logo splash right away so the user sees branding while the rest of the
                    // (multi-second) init runs, instead of a blank screen / no window.
                    CDraw.ShowSplash();

                    // The component init maps onto the 0..0.4 part of the bar: a step advances the bar
                    // only once it is complete, and the caption names the step that is running. The song
                    // and cover phases continue on 0.4..0.95 with their real counts (_SongsPhase), and
                    // 1.0 is only reached once everything is done.
                    int initStepsDone = 0;
                    const int initStepsTotal = 13;

                    void StepProgress(string status)
                    {
                        CDraw.UpdateSplash(0.4f * initStepsDone / initStepsTotal, status);
                    }

                    StepProgress("Initializing audio…");
                    using (CBenchmark.Time("Init Playback"))
                    {
                        if (!CSound.Init())
                            throw new CLoadingException("playback");
                        initStepsDone++;
                    }

                    StepProgress("Initializing recording…");
                    using (CBenchmark.Time("Init Record"))
                    {
                        if (!CRecord.Init())
                            throw new CLoadingException("record");
                        initStepsDone++;
                    }

                    StepProgress("Initializing video playback…");
                    using (CBenchmark.Time("Init VideoDecoder"))
                    {
                        if (!CVideo.Init())
                            throw new CLoadingException("video");
                        initStepsDone++;
                    }

                    StepProgress("Loading database…");
                    using (CBenchmark.Time("Init Database"))
                    {
                        if (!CDataBase.Init())
                            throw new CLoadingException("database");
                        initStepsDone++;
                    }

                    StepProgress("Preparing background music…");
                    using (CBenchmark.Time("Init Background Music"))
                    {
                        CBackgroundMusic.Init();
                        initStepsDone++;
                    }

                    StepProgress("Loading profiles…");
                    using (CBenchmark.Time("Init Profiles"))
                    {
                        CProfiles.Init();
                        initStepsDone++;
                    }

                    StepProgress("Loading fonts…");
                    using (CBenchmark.Time("Init Fonts"))
                    {
                        if (!CFonts.Init())
                            throw new CLoadingException("fonts");
                        initStepsDone++;
                    }

                    StepProgress("Loading theme…");
                    using (CBenchmark.Time("Init Theme"))
                    {
                        if (!CThemes.Init())
                            throw new CLoadingException("theme");
                        initStepsDone++;
                    }

                    StepProgress("Applying theme…");
                    using (CBenchmark.Time("Load Theme"))
                    {
                        CThemes.Load();
                        initStepsDone++;
                    }

                    StepProgress("Preparing cover generator…");
                    using (CBenchmark.Time("Init Cover"))
                    {
                        if (!CCover.Init())
                            throw new CLoadingException("covertheme");
                        initStepsDone++;
                    }

                    StepProgress("Building interface…");
                    using (CBenchmark.Time("Init Screens"))
                    {
                        CGraphics.Init();
                        initStepsDone++;
                    }

                    StepProgress("Preparing game…");
                    using (CBenchmark.Time("Init Game"))
                    {
                        CGame.Init();
                        CProfiles.Update();
                        CConfig.UsePlayers();
                        initStepsDone++;
                    }

                    StepProgress("Loading game modes…");
                    using (CBenchmark.Time("Init Game Mode"))
                    {
                        if (!CParty.Init())
                            throw new CLoadingException("Game Mode");
                        initStepsDone++;
                    }

                    // Cloud communication (SSIS-Remote) starts before the song load so its websocket
                    // attempts overlap with the local work.
                    if (CConfig.UseCloudServer)
                    {
                        using (CBenchmark.Time("Init Cloud Communication"))
                        {
                            CCloud.Init();
                        }

                        // Retry any rounds that were queued for a later upload (e.g. from a previous
                        // session, or made while offline). Fire-and-forget on the single-flight
                        // flush worker, off the loop thread - the reconnect path in CCloud also
                        // triggers a flush, so this only speeds up the first chance to deliver them.
                        CCloud.RequestUploadFlush();
                    }

                    // Songs + cloud sync: the last loading work, with live progress and (for the cloud)
                    // a user-driven retry. When the user closed the window during it, run the normal
                    // shutdown instead of entering the main loop.
                    if (!_LoadSongsAndSync())
                    {
                        _CloseProgram();
                        return;
                    }

                    CDraw.UpdateSplash(1f, "Starting.");

                    //Only reasonable point to call GC.Collect() because initialization may cause lots of garbage
                    //Rely on GC doing its job afterwards and call Dispose methods where appropriate
                    GC.Collect();
                }
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error on start up: {ExceptionMessage}", CLog.Params(e.Message), show: true);
                _CloseProgram();
                return;
            }

            // Start Main Loop
            CDraw.MainLoop();
        }

        /// <summary>
        ///     The final loading phase: the local song load (with live progress), then the cloud sync if
        ///     the cloud is enabled. The bar uses the real counts: 0.4..0.8 for the parsed song files,
        ///     0.8..0.95 for the covers (ATSTART mode), and holds at 0.95 while the cloud is waited on.
        ///     Returns false when the user closed the window.
        /// </summary>
        private static bool _LoadSongsAndSync()
        {
            if (!_SongsPhase())
                return false;

            if (CConfig.UseCloudServer && !_CloudPhase())
                return false;

            return true;
        }

        private static bool _SongsPhase()
        {
            Exception error = null;

            using (var done = new ManualResetEventSlim(false))
            {
                void RunLoader()
                {
                    try
                    {
                        CSongs.LoadLocalSongs();
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                    finally
                    {
                        done.Set();
                    }
                }

                var loader = new Thread(RunLoader)
                {
                    Name = "SongLoader",
                    IsBackground = true
                };
                loader.Start();

                while (!done.Wait(50))
                {
                    if (CDraw.WantClose)
                        return false;
                    CDraw.UpdateSplash(_SongsProgress(), _SongsStatus());
                }
            }

            if (error != null)
                throw error;

            return true;
        }

        private static float _SongsProgress()
        {
            if (!CSongs.SongsLoaded)
            {
                int totalFiles = CSongs.TotalSongFiles;
                if (totalFiles <= 0)
                    return 0.4f;
                return 0.4f + 0.4f * Math.Min(1f, (float)CSongs.NumAllSongs / totalFiles);
            }

            // DYNAMIC covers keep loading in the background; the bar does not claim them.
            if (CConfig.Config.Theme.CoverLoading != ECoverLoading.TR_CONFIG_COVERLOADING_ATSTART)
                return 0.95f;

            int totalSongs = CSongs.NumAllSongs;
            if (totalSongs <= 0)
                return 0.95f;
            return 0.8f + 0.15f * Math.Min(1f, (float)CSongs.NumSongsWithCoverLoaded / totalSongs);
        }

        private static string _SongsStatus()
        {
            if (!CSongs.SongsLoaded)
                return "Loading songs (" + CSongs.NumAllSongs + " of " + CSongs.TotalSongFiles + ")…";

            if (CConfig.Config.Theme.CoverLoading == ECoverLoading.TR_CONFIG_COVERLOADING_ATSTART)
                return "Loading covers (" + CSongs.NumSongsWithCoverLoaded + " of " + CSongs.NumAllSongs + ")…";

            return "Songs loaded (" + CSongs.NumAllSongs + ")";
        }

        // Cloud sync phase: a few automatic attempts first, then a prompt that only advances on user
        // request. When the cloud is enabled it is considered required: startup never proceeds past
        // this phase while the server is unreachable. Returns false when the user closed the window.
        private static bool _CloudPhase()
        {
            const int autoRetries = 3;

            while (true)
            {
                bool ok = false;
                for (int attempt = 1; attempt <= autoRetries && !ok; attempt++)
                {
                    if (!_CloudSyncAttempt(attempt, autoRetries, out ok))
                        return false;
                }

                if (ok)
                {
                    CDraw.UpdateSplash(0.95f, "Cloud sync complete");
                    return true;
                }

                if (!_WaitForCloudRetry())
                    return false;

                CCloud.RequestReconnect();
            }
        }

        // Runs one sync attempt with live status. Returns false (and leaves ok undefined) when the user
        // closed the window; otherwise ok carries the attempt result.
        private static bool _CloudSyncAttempt(int attempt, int autoRetries, out bool ok)
        {
            ok = false;
            bool success = false;
            Exception error = null;

            using (var done = new ManualResetEventSlim(false))
            {
                void RunSync()
                {
                    try
                    {
                        success = CSongs.SyncWithCloud();
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                    finally
                    {
                        done.Set();
                    }
                }

                var sync = new Thread(RunSync)
                {
                    Name = "CloudSync",
                    IsBackground = true
                };
                sync.Start();

                while (!done.Wait(100))
                {
                    if (CDraw.WantClose)
                        return false;
                    CDraw.UpdateSplash(0.95f, "Syncing with cloud… (attempt " + attempt + "/" + autoRetries +
                                            ", WebSocket: " + (CCloud.IsConnected ? "online" : "offline") + ")");
                }
            }

            if (error != null)
                CLog.CCloudLog.Error(error, "Cloud sync attempt {n} failed: {msg}", CLog.Params(attempt, error.Message));

            ok = success && error == null;
            if (!ok)
                CDraw.UpdateSplash(0.95f, "Cloud sync failed (attempt " + attempt + "/" + autoRetries + ")");

            return true;
        }

        // Parks at the retry prompt until the user presses R (or closes the window, false). Any other
        // key is consumed here so it cannot leak into the first menu screen.
        private static bool _WaitForCloudRetry()
        {
            while (true)
            {
                if (CDraw.WantClose)
                    return false;

                CDraw.UpdateSplash(0.95f, "Cloud unavailable - press R to retry", "R - retry cloud connection");

                SKeyEvent key;
                while (CDraw.PollKey(out key))
                {
                    if (key.Key == Keys.R || key.Unicode == 'r' || key.Unicode == 'R')
                        return true;
                }

                Thread.Sleep(16);
            }
        }

        private static void _CloseProgram()
        {
            if (CConfig.UseCloudServer)
            {
                CCloud.Shutdown();
            }
            // Unloading in reverse order
            try
            {
                CGraphics.Close();
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error during shutdown! (CGraphics)");
            }

            try
            {
                CThemes.Close();
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error during shutdown! (CThemes)");
            }

            try
            {
                CCover.Close();
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error during shutdown! (CCover)");
            }

            try
            {
                CFonts.Close();
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error during shutdown! (CFonts)");
            }

            try
            {
                CBackgroundMusic.Close();
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error during shutdown! (CBackgroundMusic)");
            }

            try
            {
                CDataBase.Close();
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error during shutdown! (CDataBase)");
            }

            try
            {
                CVideo.Close();
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error during shutdown! (CVideo)");
            }

            try
            {
                CRecord.Close();
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error during shutdown! (CRecord)");
            }

            try
            {
                CSound.Close();
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error during shutdown! (CSound)");
            }

            try
            {
                CDraw.Close();
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error during shutdown! (CDraw)");
            }

            GC.Collect(); // Do a GC run here before we close logs to have finalizers run
            try
            {
                CLog.Close(); // Do this last, so we get all log entries!
            }
            catch (Exception) { }
            Environment.Exit(Environment.ExitCode);
        }

#if !DEBUG
        private static void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs args)
        {
            var e = (Exception)args.ExceptionObject;
            CLog.Fatal(e, "Unhandled exception: {ExceptionMessage}", CLog.Params(e.Message));
        }
#endif

        private static Assembly _AssemblyResolver(Object sender, ResolveEventArgs args)
        {
            // a fix to handle mscorlib.resources
            if (args.Name.Contains(".resources", StringComparison.Ordinal))
                return null;

            Assembly assembly = null;
            string[] arr = args.Name.Split(new char[] { ',' });
            if (arr.Length > 0)
            {
#if ARCH_X86
                string arch = "x86";
#else
                string arch = "x64";
#endif
                string path = Path.Combine(CSettings.ProgramFolder, arch, arr[0] + ".dll");
                try
                {
                    assembly = Assembly.LoadFrom(path);
                }
                catch (FileLoadException e1)
                {
                    //possibly loading from network, this would work always but better use the normal LoadFrom for more specific errors
                    try
                    {
                        assembly = Assembly.Load(File.ReadAllBytes(path));
                    }
                    catch (Exception e)
                    {
                        CLog.Error("Cannot load assembly " + args.Name + " from " + path + ": " + e + " (outer error: " + e1 + ")");
                    }
                }
#if LINUX
                catch (FileNotFoundException) { }
#endif
            }
            return assembly;
        }

        private static readonly Mutex _Mutex = new Mutex(false, "Vocaluxe-SingleInstanceMutex");

        private static bool _EnsureSingleInstance()
        {
            // wait a few seconds in case that the instance is just shutting down
            if (!_Mutex.WaitOne(TimeSpan.FromSeconds(2), false))
            {
                CLog.Error("Another instance of Vocaluxe is already running!");
#if WIN
                Process currentProcess = Process.GetCurrentProcess();
                Process[] processes = Process.GetProcessesByName(currentProcess.ProcessName);

                foreach (Process process in processes.Where(t => t.Id != currentProcess.Id))
                {
                    IntPtr wnd = process.MainWindowHandle;
                    if (wnd != IntPtr.Zero)
                        COSFunctions.SetForegroundWindow(wnd);
                }
#endif
                return false;
            }
            return true;
        }
    }
}