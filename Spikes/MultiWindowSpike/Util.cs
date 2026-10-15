using System;
using System.IO;

namespace MultiWindowSpike
{
    static class Util
    {
        static string _Root;

        /// <summary>Walks up from the build output to the repo root (needs .git), so results/ lands
        /// in Spikes/results/ no matter where the process is launched from.</summary>
        public static string RepoRoot()
        {
            if (_Root != null)
                return _Root;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                {
                    _Root = dir.FullName;
                    return _Root;
                }
                dir = dir.Parent;
            }
            _Root = AppContext.BaseDirectory;
            return _Root;
        }
    }
}
