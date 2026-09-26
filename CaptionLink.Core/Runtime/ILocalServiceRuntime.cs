using System.Diagnostics;

namespace CaptionLink.Core.Runtime;

/// <summary>
/// Provides the platform-specific environment used to run
/// local CaptionLink services.
/// </summary>
public interface ILocalServiceRuntime
{
    string GetRepositoryDirectory(
        string relativeDirectory);

    ProcessStartInfo CreateShellStartInfo(
        string command,
        bool redirectOutput);
}
