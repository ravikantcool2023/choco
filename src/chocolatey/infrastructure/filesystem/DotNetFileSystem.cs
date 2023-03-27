﻿// Copyright © 2017 - 2021 Chocolatey Software, Inc
// Copyright © 2011 - 2017 RealDimensions Software, LLC
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
//
// You may obtain a copy of the License at
//
// 	http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace chocolatey.infrastructure.filesystem
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using adapters;
    using app;
    using logging;
    using platforms;
    using tolerance;
    using Assembly = adapters.Assembly;
    using Environment = adapters.Environment;

    /// <summary>
    ///   Implementation of IFileSystem for Dot Net
    /// </summary>
    /// <remarks>Normally we avoid regions, however this has so many methods that we are making an exception.</remarks>
    public sealed class DotNetFileSystem : IFileSystem
    {
        private readonly int TIMES_TO_TRY_OPERATION = 3;
        private static Lazy<IEnvironment> environment_initializer = new Lazy<IEnvironment>(() => new Environment());
        private const int MaxPathFile = 255;
        private const int MaxPathDirectory = 248;

        private void AllowRetries(Action action, bool isSilent = false)
        {
            FaultTolerance.Retry(
                TIMES_TO_TRY_OPERATION,
                action,
                waitDurationMilliseconds: 200,
                increaseRetryByMilliseconds: 100,
                isSilent: isSilent);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public void InitializeWith(Lazy<IEnvironment> environment)
        {
            environment_initializer = environment;
        }

        private static IEnvironment Environment
        {
            get { return environment_initializer.Value; }
        }

        #region Path

        public string CombinePaths(string leftItem, params string[] rightItems)
        {
            if (leftItem == null)
            {
                var methodName = string.Empty;
                var stackFrame = new System.Diagnostics.StackFrame(1);
                if (stackFrame != null) methodName = stackFrame.GetMethod().Name;
                throw new ApplicationException("Path to combine cannot be empty. Tried to combine null with '{0}'.{1}".FormatWith(string.Join(",", rightItems), string.IsNullOrWhiteSpace(methodName) ? string.Empty : " Method called from '{0}'".FormatWith(methodName)));
            }

            var combinedPath = Platform.GetPlatform() == PlatformType.Windows ? leftItem : leftItem.Replace('\\', '/');
            foreach (var rightItem in rightItems)
            {
                if (rightItem.Contains(":")) throw new ApplicationException("Cannot combine a path with ':' attempted to combine '{0}' with '{1}'".FormatWith(rightItem, combinedPath));

                var rightSide = Platform.GetPlatform() == PlatformType.Windows ? rightItem : rightItem.Replace('\\', '/');
                if (rightSide.StartsWith(Path.DirectorySeparatorChar.ToStringSafe()) || rightSide.StartsWith(Path.AltDirectorySeparatorChar.ToStringSafe()))
                {
                    combinedPath = Path.Combine(combinedPath, rightSide.Substring(1));
                }
                else
                {
                    combinedPath = Path.Combine(combinedPath, rightSide);
                }
            }

            return combinedPath;
        }

        public string GetFullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;

            try
            {
                return Path.GetFullPath(path);
            }
            catch (IOException)
            {
                return Alphaleonis.Win32.Filesystem.Path.GetFullPath(path);
            }
        }

        public string GetTempPath()
        {
            var path = Path.GetTempPath();

            if (System.Environment.UserName.ContainsSafe(ApplicationParameters.Environment.SystemUserName) || path.ContainsSafe("config\\systemprofile"))
            {
                path = System.Environment.ExpandEnvironmentVariables(System.Environment.GetEnvironmentVariable(ApplicationParameters.Environment.Temp, EnvironmentVariableTarget.Machine).ToStringSafe());
            }

            return path;
        }

        public char GetPathDirectorySeparatorChar()
        {
            return Path.DirectorySeparatorChar;
        }

        public char GetPathSeparator()
        {
            return Path.PathSeparator;
        }

        public string GetExecutablePath(string executableName)
        {
            if (string.IsNullOrWhiteSpace(executableName)) return string.Empty;

            var isWindows = Platform.GetPlatform() == PlatformType.Windows;
            IList<string> extensions = new List<string>();

            if (GetFilenameWithoutExtension(executableName).IsEqualTo(executableName) && isWindows)
            {
                var pathExtensions = Environment.GetEnvironmentVariable(ApplicationParameters.Environment.PathExtensions).ToStringSafe().Split(new[] { ApplicationParameters.Environment.EnvironmentSeparator }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var extension in pathExtensions.OrEmpty())
                {
                    extensions.Add(extension.StartsWith(".") ? extension : ".{0}".FormatWith(extension));
                }
            }

            // Always add empty, for when the executable name is enough.
            extensions.Add(string.Empty);

            // Gets the path to an executable based on looking in current
            // working directory, next to the running process, then among the
            // derivatives of Path and Pathext variables, applied in order.
            var searchPaths = new List<string>();
            searchPaths.Add(GetCurrentDirectory());
            searchPaths.Add(GetDirectoryName(GetCurrentAssemblyPath()));
            searchPaths.AddRange(Environment.GetEnvironmentVariable(ApplicationParameters.Environment.Path).ToStringSafe().Split(new[] { GetPathSeparator() }, StringSplitOptions.RemoveEmptyEntries));

            foreach (var path in searchPaths.OrEmpty())
            {
                foreach (var extension in extensions.OrEmpty())
                {
                    var possiblePath = CombinePaths(path, "{0}{1}".FormatWith(executableName, extension.ToLowerSafe()));
                    if (FileExists(possiblePath)) return possiblePath;
                }
            }

            // If not found, return the same as passed in - it may work,
            // but possibly not.
            return executableName;
        }

        public string GetCurrentAssemblyPath()
        {
            return Assembly.GetExecutingAssembly().CodeBase.Replace(Platform.GetPlatform() == PlatformType.Windows ? "file:///" : "file://", string.Empty);
        }

        #endregion

        #region File

        public IEnumerable<string> GetFiles(string directoryPath, string pattern = "*.*", SearchOption option = SearchOption.TopDirectoryOnly)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) return new List<string>();
            if (!DirectoryExists(directoryPath))
            {
                this.Log().Warn("Directory '{0}' does not exist.".FormatWith(directoryPath));
                return new List<string>();
            }

            return Directory.EnumerateFiles(directoryPath, pattern, option);
        }

        public IEnumerable<string> GetFiles(string directoryPath, string[] extensions, SearchOption option = SearchOption.TopDirectoryOnly)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) return new List<string>();

            return Directory.EnumerateFiles(directoryPath, "*.*", option)
                            .Where(f => extensions.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase)));
        }

        public bool FileExists(string filePath)
        {
            try
            {
                return File.Exists(filePath);
            }
            catch (IOException)
            {
                return Alphaleonis.Win32.Filesystem.File.Exists(filePath);
            }
        }

        public string GetFileName(string filePath)
        {
            return Path.GetFileName(filePath);
        }

        public string GetFilenameWithoutExtension(string filePath)
        {
            if (Platform.GetPlatform() == PlatformType.Windows) return Path.GetFileNameWithoutExtension(filePath);

            return Path.GetFileNameWithoutExtension(filePath.Replace('\\', '/'));
        }

        public string GetFileExtension(string filePath)
        {
            if (Platform.GetPlatform() == PlatformType.Windows) return Path.GetExtension(filePath);

            return Path.GetExtension(filePath.Replace('\\', '/'));
        }

        public dynamic GetFileInfoFor(string filePath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(filePath) && filePath.Length >= MaxPathFile)
                {
                    return new Alphaleonis.Win32.Filesystem.FileInfo(filePath);
                }

                return new FileInfo(filePath);
            }
            catch (IOException)
            {
                return new Alphaleonis.Win32.Filesystem.FileInfo(filePath);
            }
        }

        public System.DateTime GetFileModifiedDate(string filePath)
        {
            return new FileInfo(filePath).LastWriteTime;
        }

        public long GetFileSize(string filePath)
        {
            return new FileInfo(filePath).Length;
        }

        public string GetFileVersionFor(string filePath)
        {
            return FileVersionInfo.GetVersionInfo(GetFullPath(filePath)).FileVersion;
        }

        public bool IsSystemFile(dynamic file)
        {
            bool isSystemFile = ((file.Attributes & FileAttributes.System) == FileAttributes.System);
            if (!isSystemFile)
            {
                //check the directory to be sure
                var directoryInfo = GetDirectoryInfo(file.DirectoryName);
                isSystemFile = ((directoryInfo.Attributes & FileAttributes.System) == FileAttributes.System);
            }
            else
            {
                string fullName = file.FullName;
                this.Log().Debug(ChocolateyLoggers.Verbose, () => "File \"{0}\" is a system file.".FormatWith(fullName));
            }

            return isSystemFile;
        }

        public bool IsReadOnlyFile(dynamic file)
        {
            bool isReadOnlyFile = ((file.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
            if (!isReadOnlyFile)
            {
                //check the directory to be sure
                dynamic directoryInfo = GetDirectoryInfo(file.DirectoryName);
                isReadOnlyFile = ((directoryInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
            }
            else
            {
                string fullName = file.FullName;
                this.Log().Debug(ChocolateyLoggers.Verbose, () => "File \"{0}\" is a readonly file.".FormatWith(fullName));
            }

            return isReadOnlyFile;
        }

        public bool IsHiddenFile(dynamic file)
        {
            bool isHiddenFile = ((file.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden);
            if (!isHiddenFile)
            {
                //check the directory to be sure
                var directoryInfo = GetDirectoryInfo(file.DirectoryName);
                isHiddenFile = ((directoryInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden);
            }
            else
            {
                string fullName = file.FullName;
                this.Log().Debug(ChocolateyLoggers.Verbose, () => "File \"{0}\" is a hidden file.".FormatWith(fullName));
            }

            return isHiddenFile;
        }

        public bool IsEncryptedFile(dynamic file)
        {
            bool isEncrypted = ((file.Attributes & FileAttributes.Encrypted) == FileAttributes.Encrypted);
            string fullName = file.FullName;
            this.Log().Debug(ChocolateyLoggers.Verbose, () => "Is file \"{0}\" an encrypted file? {1}".FormatWith(fullName, isEncrypted.ToStringSafe()));
            return isEncrypted;
        }

        public string GetFileDate(dynamic file)
        {
            return file.CreationTime < file.LastWriteTime
                       ? file.CreationTime.Date.ToString("yyyyMMdd")
                       : file.LastWriteTime.Date.ToString("yyyyMMdd");
        }

        public void MoveFile(string filePath, string newFilePath)
        {
            EnsureDirectoryExists(GetDirectoryName(newFilePath), ignoreError: true);

            AllowRetries(
                () =>
                {
                    try
                    {
                        File.Move(filePath, newFilePath);
                    }
                    catch (IOException)
                    {
                        Alphaleonis.Win32.Filesystem.File.Move(filePath, newFilePath);
                    }
                });
            //Thread.Sleep(10);
        }

        public void CopyFile(string sourceFilePath, string destinationFilePath, bool overwriteExisting)
        {
            this.Log().Debug(ChocolateyLoggers.Verbose, () => "Attempting to copy \"{0}\"{1} to \"{2}\".".FormatWith(sourceFilePath, Environment.NewLine, destinationFilePath));
            EnsureDirectoryExists(GetDirectoryName(destinationFilePath), ignoreError: true);

            AllowRetries(
                () =>
                {
                    try
                    {
                        File.Copy(sourceFilePath, destinationFilePath, overwriteExisting);
                    }
                    catch (IOException)
                    {
                        Alphaleonis.Win32.Filesystem.File.Copy(sourceFilePath, destinationFilePath, overwriteExisting);
                    }
                });
        }

        public bool CopyFileUnsafe(string sourceFilePath, string destinationFilePath, bool overwriteExisting)
        {
            if (Platform.GetPlatform() != PlatformType.Windows)
            {
                CopyFile(sourceFilePath, destinationFilePath, overwriteExisting);
                return true;
            }

            this.Log().Debug(ChocolateyLoggers.Verbose, () => "Attempting to copy from \"{0}\" to \"{1}\".".FormatWith(sourceFilePath, destinationFilePath));
            EnsureDirectoryExists(GetDirectoryName(destinationFilePath), ignoreError: true);

            //Private Declare Function apiCopyFile Lib "kernel32" Alias "CopyFileA" _
            int success = CopyFileW(sourceFilePath, destinationFilePath, overwriteExisting ? 0 : 1);
            //if (success == 0)
            //{
            //    var error = Marshal.GetLastWin32Error();

            //}
            return success != 0;
        }

        public void ReplaceFile(string sourceFilePath, string destinationFilePath, string backupFilePath)
        {
            this.Log().Debug(ChocolateyLoggers.Verbose, () => "Attempting to replace \"{0}\"{1} with \"{2}\".{1} Backup placed at \"{3}\".".FormatWith(destinationFilePath, Environment.NewLine, sourceFilePath, backupFilePath));

            AllowRetries(
                () =>
                {
                    try
                    {
                        // File.Replace is very sensitive to issues with file access
                        // the docs mention that using a backup fixes this, but this
                        // has not been the case with choco - the backup file has been
                        // the most sensitive to issues with file locking
                        //File.Replace(sourceFilePath, destinationFilePath, backupFilePath);

                        // move existing file to backup location
                        if (!string.IsNullOrEmpty(backupFilePath) && FileExists(destinationFilePath))
                        {
                            try
                            {
                                this.Log().Trace("Backing up '{0}' to '{1}'.".FormatWith(destinationFilePath, backupFilePath));

                                if (FileExists(backupFilePath))
                                {
                                    this.Log().Trace("Deleting existing backup file at '{0}'.".FormatWith(backupFilePath));
                                    DeleteFile(backupFilePath);
                                }
                                this.Log().Trace("Moving '{0}' to '{1}'.".FormatWith(destinationFilePath, backupFilePath));
                                MoveFile(destinationFilePath, backupFilePath);
                            }
                            catch (Exception ex)
                            {
                                this.Log().Debug("Error capturing backup of '{0}':{1} {2}".FormatWith(destinationFilePath, Environment.NewLine, ex.Message));
                            }
                        }

                        // copy source file to destination
                        this.Log().Trace("Copying '{0}' to '{1}'.".FormatWith(sourceFilePath, destinationFilePath));
                        CopyFile(sourceFilePath, destinationFilePath, overwriteExisting: true);

                        // delete source file
                        try
                        {
                            this.Log().Trace("Removing '{0}'".FormatWith(sourceFilePath));
                            DeleteFile(sourceFilePath);
                        }
                        catch (Exception ex)
                        {
                            this.Log().Debug("Error removing '{0}':{1} {2}".FormatWith(sourceFilePath, Environment.NewLine, ex.Message));
                        }
                    }
                    catch (IOException)
                    {
                        Alphaleonis.Win32.Filesystem.File.Replace(sourceFilePath, destinationFilePath, backupFilePath);
                    }
                });
        }

        // ReSharper disable InconsistentNaming

        // http://msdn.microsoft.com/en-us/library/windows/desktop/aa363851.aspx
        // http://www.pinvoke.net/default.aspx/kernel32.copyfile
        /*
            BOOL WINAPI CopyFile(
              _In_  LPCTSTR lpExistingFileName,
              _In_  LPCTSTR lpNewFileName,
              _In_  BOOL bFailIfExists
            );
         */

        [DllImport("kernel32", SetLastError = true)]
        private static extern int CopyFileW(string lpExistingFileName, string lpNewFileName, int bFailIfExists);

        // ReSharper restore InconsistentNaming

        public void DeleteFile(string filePath)
        {
            this.Log().Debug(ChocolateyLoggers.Verbose, () => "Attempting to delete file \"{0}\".".FormatWith(filePath));
            if (FileExists(filePath))
            {
                AllowRetries(
                    () =>
                    {
                        try
                        {
                            File.Delete(filePath);
                        }
                        catch (IOException)
                        {
                            Alphaleonis.Win32.Filesystem.File.Delete(filePath);
                        }
                    });
            }
        }

        public FileStream CreateFile(string filePath)
        {
            return new FileStream(filePath, FileMode.OpenOrCreate);
        }

        public string ReadFile(string filePath)
        {
            try
            {
                return File.ReadAllText(filePath, GetFileEncoding(filePath));
            }
            catch (IOException)
            {
                return Alphaleonis.Win32.Filesystem.File.ReadAllText(filePath, GetFileEncoding(filePath));
            }
        }

        public byte[] ReadFileBytes(string filePath)
        {
            return File.ReadAllBytes(filePath);
        }

        public FileStream OpenFileReadonly(string filePath)
        {
            return File.OpenRead(filePath);
        }

        public FileStream OpenFileExclusive(string filePath)
        {
            return File.Open(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        public void WriteFile(string filePath, string fileText)
        {
            WriteFile(filePath, fileText, FileExists(filePath) ? GetFileEncoding(filePath) : Encoding.UTF8);
        }

        public void WriteFile(string filePath, string fileText, Encoding encoding)
        {
            AllowRetries(() =>
                {
                    using (FileStream fileStream = File.Open(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                    using (var streamWriter = new StreamWriter(fileStream, encoding))
                    {
                        streamWriter.Write(fileText);
                        streamWriter.Flush();
                        streamWriter.Close();
                        fileStream.Close();
                    }
                });
        }

        public void WriteFile(string filePath, Func<Stream> getStream)
        {
            using (Stream incomingStream = getStream())
            using (Stream fileStream = File.Open(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                incomingStream.CopyTo(fileStream);
                fileStream.Close();
            }
        }

        #endregion

        #region Directory

        public string GetCurrentDirectory()
        {
            return Directory.GetCurrentDirectory();
        }

        public IEnumerable<string> GetDirectories(string directoryPath)
        {
            if (!DirectoryExists(directoryPath)) return new List<string>();

            return Directory.EnumerateDirectories(directoryPath);
        }

        public IEnumerable<string> GetDirectories(string directoryPath, string pattern, SearchOption option = SearchOption.TopDirectoryOnly)
        {
            if (!DirectoryExists(directoryPath)) return new List<string>();

            return Directory.EnumerateDirectories(directoryPath, pattern, option);
        }

        public bool DirectoryExists(string directoryPath)
        {
            return Directory.Exists(directoryPath);
        }

        public string GetDirectoryName(string filePath)
        {
            if (Platform.GetPlatform() != PlatformType.Windows && !string.IsNullOrWhiteSpace(filePath))
            {
                filePath = filePath.Replace('\\', '/');
            }

            try
            {
                return Path.GetDirectoryName(filePath);
            }
            catch (IOException)
            {
                return Alphaleonis.Win32.Filesystem.Path.GetDirectoryName(filePath);
            }
        }

        public dynamic GetDirectoryInfo(string directoryPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(directoryPath) && directoryPath.Length >= MaxPathDirectory)
                {
                    return new Alphaleonis.Win32.Filesystem.DirectoryInfo(directoryPath);
                }

                return new DirectoryInfo(directoryPath);
            }
            catch (IOException)
            {
                return new Alphaleonis.Win32.Filesystem.DirectoryInfo(directoryPath);
            }
        }

        public dynamic GetFileDirectoryInfo(string filePath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(filePath) && filePath.Length >= MaxPathFile)
                {
                    return new Alphaleonis.Win32.Filesystem.DirectoryInfo(filePath).Parent;
                }

                return new DirectoryInfo(filePath).Parent;
            }
            catch (IOException)
            {
                return new Alphaleonis.Win32.Filesystem.DirectoryInfo(filePath).Parent;
            }
        }

        public void CreateDirectory(string directoryPath)
        {
            this.Log().Debug(ChocolateyLoggers.Verbose, () => "Attempting to create directory \"{0}\".".FormatWith(GetFullPath(directoryPath)));
            AllowRetries(
                () =>
                {
                    try
                    {
                        Directory.CreateDirectory(directoryPath);
                    }
                    catch (IOException)
                    {
                        Alphaleonis.Win32.Filesystem.Directory.CreateDirectory(directoryPath);
                    }
                });
        }

        public void MoveDirectory(string directoryPath, string newDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(newDirectoryPath)) throw new ApplicationException("You must provide a directory to move from or to.");

            // Linux / macOS do not have a SystemDrive environment variable, instead, everything is under "/"
            var systemDrive = Platform.GetPlatform() == PlatformType.Windows ? Environment.GetEnvironmentVariable("SystemDrive") : "/";
            if (CombinePaths(directoryPath, "").IsEqualTo(CombinePaths(systemDrive, ""))) throw new ApplicationException("Cannot move or delete the root of the system drive");

            try
            {
                this.Log().Debug(ChocolateyLoggers.Verbose, "Moving '{0}'{1} to '{2}'".FormatWith(directoryPath, Environment.NewLine, newDirectoryPath));
                AllowRetries(
                    () =>
                    {
                        try
                        {
                            Directory.Move(directoryPath, newDirectoryPath);
                        }
                        catch (IOException)
                        {
                            Alphaleonis.Win32.Filesystem.Directory.Move(directoryPath, newDirectoryPath);
                        }
                    });
            }
            catch (Exception ex)
            {
                this.Log().Warn(ChocolateyLoggers.Verbose, "Move failed with message:{0} {1}{0} Attempting backup move method.".FormatWith(Environment.NewLine, ex.Message));

                EnsureDirectoryExists(newDirectoryPath, ignoreError: true);
                foreach (var file in GetFiles(directoryPath, "*.*", SearchOption.AllDirectories).OrEmpty())
                {
                    var destinationFile = file.Replace(directoryPath, newDirectoryPath);
                    if (FileExists(destinationFile)) DeleteFile(destinationFile);

                    EnsureDirectoryExists(GetDirectoryName(destinationFile), ignoreError: true);
                    this.Log().Debug(ChocolateyLoggers.Verbose, "Moving '{0}'{1} to '{2}'".FormatWith(file, Environment.NewLine, destinationFile));
                    MoveFile(file, destinationFile);
                }

                Thread.Sleep(1000); // let the moving files finish up
                DeleteDirectoryChecked(directoryPath, recursive: true);
            }

            Thread.Sleep(2000); // sleep for enough time to allow the folder to be cleared
        }

        public void CopyDirectory(string sourceDirectoryPath, string destinationDirectoryPath, bool overwriteExisting)
        {
            EnsureDirectoryExists(destinationDirectoryPath, ignoreError: true);

            foreach (var file in GetFiles(sourceDirectoryPath, "*.*", SearchOption.AllDirectories).OrEmpty())
            {
                var destinationFile = file.Replace(sourceDirectoryPath, destinationDirectoryPath);
                EnsureDirectoryExists(GetDirectoryName(destinationFile), ignoreError: true);
                //this.Log().Debug(ChocolateyLoggers.Verbose, "Copying '{0}' {1} to '{2}'".format_with(file, Environment.NewLine, destinationFile));
                CopyFile(file, destinationFile, overwriteExisting);
            }

            Thread.Sleep(1500); // sleep for enough time to allow the folder to finish copying
        }

        public void EnsureDirectoryExists(string directoryPath)
        {
            EnsureDirectoryExists(directoryPath, false);
        }

        private void EnsureDirectoryExists(string directoryPath, bool ignoreError)
        {
            if (!DirectoryExists(directoryPath))
            {
                try
                {
                    CreateDirectory(directoryPath);
                }
                catch (SystemException e)
                {
                    if (!ignoreError)
                    {
                        this.Log().Error("Cannot create directory \"{0}\". Error was:{1}{2}", GetFullPath(directoryPath), Environment.NewLine, e);
                        throw;
                    }
                }
            }
        }

        public void DeleteDirectory(string directoryPath, bool recursive)
        {
            DeleteDirectory(directoryPath, recursive, overrideAttributes: false, isSilent: false);
        }

        public void DeleteDirectory(string directoryPath, bool recursive, bool overrideAttributes)
        {
            DeleteDirectory(directoryPath, recursive, overrideAttributes: overrideAttributes, isSilent: false);
        }

        public void DeleteDirectory(string directoryPath, bool recursive, bool overrideAttributes, bool isSilent)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) throw new ApplicationException("You must provide a directory to delete.");

            // Linux / macOS do not have a SystemDrive environment variable, instead, everything is under "/"
            var systemDrive = Platform.GetPlatform() == PlatformType.Windows ? Environment.GetEnvironmentVariable("SystemDrive") : "/";
            if (CombinePaths(directoryPath, "").IsEqualTo(CombinePaths(systemDrive, ""))) throw new ApplicationException("Cannot move or delete the root of the system drive");

            if (overrideAttributes)
            {
                foreach (var file in GetFiles(directoryPath, "*.*", SearchOption.AllDirectories))
                {
                    var filePath = GetFullPath(file);
                    var fileInfo = GetFileInfoFor(filePath);

                    if (IsSystemFile(fileInfo)) EnsureFileAttributeRemoved(filePath, FileAttributes.System);
                    if (IsReadOnlyFile(fileInfo)) EnsureFileAttributeRemoved(filePath, FileAttributes.ReadOnly);
                    if (IsHiddenFile(fileInfo)) EnsureFileAttributeRemoved(filePath, FileAttributes.Hidden);
                }
            }

            if (!isSilent) this.Log().Debug(ChocolateyLoggers.Verbose, () => "Attempting to delete directory \"{0}\".".FormatWith(GetFullPath(directoryPath)));
            AllowRetries(
                () =>
                {
                    try
                    {
                        Directory.Delete(directoryPath, recursive);
                    }
                    catch (IOException)
                    {
                        Alphaleonis.Win32.Filesystem.Directory.Delete(directoryPath, recursive);
                    }
                }, isSilent: isSilent);
        }

        public void DeleteDirectoryChecked(string directoryPath, bool recursive)
        {
            DeleteDirectoryChecked(directoryPath, recursive, overrideAttributes: false, isSilent: false);
        }

        public void DeleteDirectoryChecked(string directoryPath, bool recursive, bool overrideAttributes)
        {
            DeleteDirectoryChecked(directoryPath, recursive, overrideAttributes: overrideAttributes, isSilent: false);
        }

        public void DeleteDirectoryChecked(string directoryPath, bool recursive, bool overrideAttributes, bool isSilent)
        {
            if (DirectoryExists(directoryPath))
            {
                DeleteDirectory(directoryPath, recursive, overrideAttributes, isSilent);
            }
        }

        #endregion

        public void EnsureFileAttributeSet(string path, FileAttributes attributes)
        {
            if (DirectoryExists(path))
            {
                var directoryInfo = GetDirectoryInfo(path);
                if ((directoryInfo.Attributes & attributes) != attributes)
                {
                    this.Log().Debug(ChocolateyLoggers.Verbose, () => "Adding '{0}' attribute(s) to '{1}'.".FormatWith(attributes.ToStringSafe(), path));
                    directoryInfo.Attributes |= attributes;
                }
            }
            if (FileExists(path))
            {
                var fileInfo = GetFileInfoFor(path);
                if ((fileInfo.Attributes & attributes) != attributes)
                {
                    this.Log().Debug(ChocolateyLoggers.Verbose, () => "Adding '{0}' attribute(s) to '{1}'.".FormatWith(attributes.ToStringSafe(), path));
                    fileInfo.Attributes |= attributes;
                }
            }
        }

        public void EnsureFileAttributeRemoved(string path, FileAttributes attributes)
        {
            if (DirectoryExists(path))
            {
                var directoryInfo = GetDirectoryInfo(path);
                if ((directoryInfo.Attributes & attributes) == attributes)
                {
                    this.Log().Debug(ChocolateyLoggers.Verbose, () => "Removing '{0}' attribute(s) from '{1}'.".FormatWith(attributes.ToStringSafe(), path));
                    directoryInfo.Attributes &= ~attributes;
                }
            }
            if (FileExists(path))
            {
                var fileInfo = GetFileInfoFor(path);
                if ((fileInfo.Attributes & attributes) == attributes)
                {
                    this.Log().Debug(ChocolateyLoggers.Verbose, () => "Removing '{0}' attribute(s) from '{1}'.".FormatWith(attributes.ToStringSafe(), path));
                    fileInfo.Attributes &= ~attributes;
                }
            }
        }

        /// <summary>
        ///   Takes a guess at the file encoding by looking to see if it has a BOM
        /// </summary>
        /// <param name="filePath">Path to the file name</param>
        /// <returns>A best guess at the encoding of the file</returns>
        /// <remarks>http://www.west-wind.com/WebLog/posts/197245.aspx</remarks>
        public Encoding GetFileEncoding(string filePath)
        {
            // *** Use Default of Encoding.Default (Ansi CodePage)
            Encoding enc = Encoding.Default;

            // *** Detect byte order mark if any - otherwise assume default
            var buffer = new byte[5];
            var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            file.Read(buffer, 0, 5);
            file.Close();

            if (buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf) enc = Encoding.UTF8;
            else if (buffer[0] == 0xfe && buffer[1] == 0xff) enc = Encoding.Unicode;
            else if (buffer[0] == 0 && buffer[1] == 0 && buffer[2] == 0xfe && buffer[3] == 0xff) enc = Encoding.UTF32;
            else if (buffer[0] == 0x2b && buffer[1] == 0x2f && buffer[2] == 0x76) enc = Encoding.UTF7;

            //assume xml is utf8
            //if (enc == Encoding.Default && get_file_extension(filePath).is_equal_to(".xml")) enc = Encoding.UTF8;

            return enc;
        }
    }
}
