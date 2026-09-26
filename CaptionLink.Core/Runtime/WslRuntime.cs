using System.Diagnostics;

namespace CaptionLink.Core.Runtime;

/// <summary>
/// Resolves the development repository and launches commands
/// through the user's default WSL distribution.
/// </summary>
public static class WslRuntime
{
    private const string RepositoryEnvironmentVariable =
        "TRANSGO_REPOSITORY_ROOT";

    private const string SolutionFileName =
        "CaptionLink.slnx";

    public static string GetLinuxRepositoryDirectory(
        string relativeDirectory)
    {
        string repositoryRoot =
            FindRepositoryRoot();

        string windowsDirectory =
            Path.GetFullPath(
                Path.Combine(
                    repositoryRoot,
                    relativeDirectory));

        if (!Directory.Exists(windowsDirectory))
        {
            throw new DirectoryNotFoundException(
                "CaptionLink service directory was not found: " +
                windowsDirectory);
        }

        return ConvertWindowsPathToLinux(
            windowsDirectory);
    }

    public static ProcessStartInfo CreateStartInfo(
        string linuxCommand,
        bool redirectOutput)
    {
        var startInfo =
            new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput =
                    redirectOutput,
                RedirectStandardError =
                    redirectOutput,
            };

        /*
         * Do not pin a distribution name here. The bootstrap
         * and runtime use the developer's default WSL distro.
         */
        /*
         * --exec is important here. Without it, WSL may route
         * the command through its default shell before Bash sees
         * the -lc payload. That can prematurely expand variables
         * such as $HOME and $NEMO_PYTHON.
         */
        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(linuxCommand);

        return startInfo;
    }

    private static string FindRepositoryRoot()
    {
        string? configuredRoot =
            Environment.GetEnvironmentVariable(
                RepositoryEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(
                configuredRoot))
        {
            string expandedRoot =
                Environment.ExpandEnvironmentVariables(
                    configuredRoot);

            string fullRoot =
                Path.GetFullPath(expandedRoot);

            if (IsRepositoryRoot(fullRoot))
            {
                return fullRoot;
            }

            throw new DirectoryNotFoundException(
                $"{RepositoryEnvironmentVariable} points " +
                "to a directory that does not contain " +
                $"{SolutionFileName}: {fullRoot}");
        }

        string[] searchRoots =
        [
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        ];

        foreach (string searchRoot in searchRoots)
        {
            DirectoryInfo? directory =
                new(Path.GetFullPath(searchRoot));

            while (directory is not null)
            {
                if (IsRepositoryRoot(
                        directory.FullName))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the CaptionLink repository. " +
            "Run the application from a repository clone " +
            $"or set {RepositoryEnvironmentVariable}.");
    }

    private static bool IsRepositoryRoot(
        string directory)
    {
        return File.Exists(
            Path.Combine(
                directory,
                SolutionFileName));
    }

    private static string ConvertWindowsPathToLinux(
        string windowsPath)
    {
        /*
         * Avoid passing the Windows path as a Linux command
         * argument. Backslashes can be interpreted during the
         * Windows -> WSL command transition.
         *
         * Instead, start WSL with the Windows directory as its
         * working directory. WSL performs the path translation,
         * and /bin/pwd reports the resulting Linux path.
         */
        var startInfo =
            new ProcessStartInfo
            {
                FileName = "wsl.exe",
                WorkingDirectory = windowsPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add("/bin/pwd");

        using var process =
            new Process
            {
                StartInfo = startInfo,
            };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Windows could not start WSL path " +
                "conversion.");
        }

        string output =
            process.StandardOutput
                .ReadToEnd();

        string error =
            process.StandardError
                .ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "WSL could not convert the repository " +
                $"path. {error.Trim()}");
        }

        string linuxPath =
            output.Trim();

        if (string.IsNullOrWhiteSpace(linuxPath))
        {
            throw new InvalidOperationException(
                "WSL returned an empty repository path.");
        }

        return linuxPath;
    }
}
