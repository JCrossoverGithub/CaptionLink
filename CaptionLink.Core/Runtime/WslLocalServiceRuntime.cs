using System.Diagnostics;

namespace CaptionLink.Core.Runtime;

/// <summary>
/// Runs local TransGo services through the user's default
/// Windows Subsystem for Linux distribution.
/// </summary>
public sealed class WslLocalServiceRuntime
    : ILocalServiceRuntime
{
    public string GetRepositoryDirectory(
        string relativeDirectory)
    {
        return WslRuntime.GetLinuxRepositoryDirectory(
            relativeDirectory);
    }

    public ProcessStartInfo CreateShellStartInfo(
        string command,
        bool redirectOutput)
    {
        return WslRuntime.CreateStartInfo(
            command,
            redirectOutput);
    }
}
