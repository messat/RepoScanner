using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RepoScanner.Scanner.Tests.Files
{
    // Stops the current user listing one folder for the length of a test, and puts the permission back
    // when disposed. Windows: a deny rule for ListDirectory. Linux and macOS: removing every permission,
    // which root ignores, so Applied says whether the folder really cannot be listed.
    internal sealed class FolderListingDenied : IDisposable
    {
        private readonly string _folder;
        private readonly FileSystemAccessRule? _denyRule;
        private readonly UnixFileMode _originalMode;

        private FolderListingDenied(string folder, FileSystemAccessRule? denyRule, UnixFileMode originalMode)
        {
            _folder = folder;
            _denyRule = denyRule;
            _originalMode = originalMode;
            Applied = !CanList(folder);
        }

        public bool Applied { get; }

        public static FolderListingDenied On(string folder)
        {
            if (OperatingSystem.IsWindows())
            {
                DirectoryInfo directory = new DirectoryInfo(folder);
                FileSystemAccessRule denyRule = new FileSystemAccessRule(
                    WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
                DirectorySecurity security = directory.GetAccessControl();
                security.AddAccessRule(denyRule);
                directory.SetAccessControl(security);
                return new FolderListingDenied(folder, denyRule, UnixFileMode.None);
            }

            UnixFileMode originalMode = File.GetUnixFileMode(folder);
            File.SetUnixFileMode(folder, UnixFileMode.None);
            return new FolderListingDenied(folder, null, originalMode);
        }

        public void Dispose()
        {
            if (OperatingSystem.IsWindows())
            {
                DirectoryInfo directory = new DirectoryInfo(_folder);
                DirectorySecurity security = directory.GetAccessControl();
                security.RemoveAccessRule(_denyRule!);
                directory.SetAccessControl(security);
            }
            else
            {
                File.SetUnixFileMode(_folder, _originalMode);
            }
        }

        private static bool CanList(string folder)
        {
            try
            {
                Directory.GetFileSystemEntries(folder);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
