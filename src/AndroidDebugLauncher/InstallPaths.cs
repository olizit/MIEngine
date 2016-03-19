﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;

namespace AndroidDebugLauncher
{
    internal class InstallPaths
    {
        private InstallPaths()
        {
        }

        /// <summary>
        /// Resolves the various file paths used by the AndroidDebugLauncher and returns an initialized InstallPaths object
        /// </summary>
        /// <param name="token">token to check for cancelation</param>
        /// <param name="launchOptions">[Required] launch options object</param>
        /// <returns>[Required] Created InstallPaths object</returns>
        public static InstallPaths Resolve(CancellationToken token, AndroidLaunchOptions launchOptions, MICore.Logger logger)
        {
            var result = new InstallPaths();

            if (launchOptions.SDKRoot != null)
            {
                result.SDKRoot = launchOptions.SDKRoot;
            }
            else
            {
                result.SDKRoot = GetDirectoryFromRegistry(@"SOFTWARE\Android SDK Tools", "Path", checkBothBitnesses: true, externalProductName: LauncherResources.ProductName_SDK);
            }

            string ndkRoot = launchOptions.NDKRoot;
            if (ndkRoot == null)
            {
                ndkRoot = GetDirectoryFromRegistry(RegistryRoot.Value + @"\Setup\VS\SecondaryInstaller\AndroidNDK", "NDK_HOME", checkBothBitnesses: false, externalProductName: LauncherResources.ProductName_NDK);
            }

            string ndkReleaseVersionFile = Path.Combine(ndkRoot, "RELEASE.TXT");
            if (!File.Exists(ndkReleaseVersionFile))
            {
                ThrowExternalFileNotFoundException(ndkReleaseVersionFile, LauncherResources.ProductName_NDK);
            }

            NdkReleaseId ndkReleaseId;
            NdkReleaseId.TryParseFile(ndkReleaseVersionFile, out ndkReleaseId);
            logger.WriteLine("Using NDK '{0}' from path '{1}'", ndkReleaseId, ndkRoot);

            string targetArchitectureName;
            NDKToolChainFilePath[] possibleGDBPaths;
            switch (launchOptions.TargetArchitecture)
            {
                case MICore.TargetArchitecture.X86:
                    targetArchitectureName = "x86";
                    possibleGDBPaths = NDKToolChainFilePath.x86_GDBPaths();
                    break;

                case MICore.TargetArchitecture.X64:
                    targetArchitectureName = "x64";
                    possibleGDBPaths = NDKToolChainFilePath.x64_GDBPaths();
                    break;

                case MICore.TargetArchitecture.ARM:
                    targetArchitectureName = "arm";
                    possibleGDBPaths = NDKToolChainFilePath.ARM_GDBPaths();
                    break;

                case MICore.TargetArchitecture.ARM64:
                    targetArchitectureName = "arm64";
                    possibleGDBPaths = NDKToolChainFilePath.ARM64_GDBPaths();
                    break;

                default:
                    Debug.Fail("Should be impossible");
                    throw new InvalidOperationException();
            }

            NDKToolChainFilePath matchedPath;
            result.GDBPath = GetNDKFilePath(
                string.Concat("Android-", targetArchitectureName, "-GDBPath"),
                ndkRoot,
                possibleGDBPaths,
                out matchedPath
                );
            if (launchOptions.TargetArchitecture == MICore.TargetArchitecture.X86 && matchedPath != null)
            {
                var r10b = new NdkReleaseId(10, 'b', true);

                // Before r10b, the 'windows-x86_64' ndk didn't support x86 debugging
                if (ndkReleaseId.IsValid && ndkReleaseId.CompareVersion(r10b) < 0 && matchedPath.PartialFilePath.Contains(@"\windows-x86_64\"))
                {
                    throw new LauncherException(Telemetry.LaunchFailureCode.NoReport, LauncherResources.Error_64BitNDKNotSupportedForX86);
                }
            }

            token.ThrowIfCancellationRequested();

            return result;
        }

        /// <summary>
        /// [Required] Path to the Android SDK
        /// </summary>
        public string SDKRoot
        {
            get;
            private set;
        }

        /// <summary>
        /// [Required] Path to GDB
        /// </summary>
        public string GDBPath
        {
            get;
            private set;
        }

        /// <summary>
        /// Reads a directory name from the MDD registry, throwing if it isn't found or doesn't exist
        /// </summary>
        /// <param name="keyName">[Required] Name of the HKLM registry key to read</param>
        /// <param name="valueName">[Optional] Name of the registry value to read</param>
        /// <param name="checkBothBitnesses">If true, both the 64-bit and 32-bit registry should be checked when running on a 64-bit OS</param>
        /// <param name="externalProductName">Name of the external product that the directory is part of</param>
        /// <returns>[Required] directory from the registry</returns>
        private static string GetDirectoryFromRegistry(string keyName, string valueName, bool checkBothBitnesses, string externalProductName)
        {
            string value;
            if (!checkBothBitnesses || !Environment.Is64BitOperatingSystem)
            {
                value = TryGetRegistryValue(keyName, valueName, RegistryView.Default);
            }
            else
            {
                value = TryGetRegistryValue(keyName, valueName, RegistryView.Registry32);
                if (value == null)
                {
                    value = TryGetRegistryValue(keyName, valueName, RegistryView.Registry64);
                }
            }

            if (value == null)
            {
                throw new LauncherException(Telemetry.LaunchFailureCode.DirectoryFromRegistryFailure, string.Format(CultureInfo.CurrentCulture, LauncherResources.Error_MDDRegValueNotFound, keyName, valueName));
            }

            if (!Directory.Exists(value))
            {
                ThrowExternalFileNotFoundException(value, externalProductName);
            }

            return value;
        }

        /// <summary>
        /// Reads the value from the registry
        /// </summary>
        /// <param name="keyName">[Required] Name of the HKLM registry key to read</param>
        /// <param name="valueName">[Optional] value name</param>
        /// <param name="view">Specifies if the 64-bit, 32-bit, or default registry should be used</param>
        /// <returns>[Optional] value, if it exists</returns>
        private static string TryGetRegistryValue(string keyName, string valueName, RegistryView view)
        {
            string value = null;

            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            {
                RegistryKey key = baseKey.OpenSubKey(keyName);
                if (key != null)
                {
                    using (key)
                    {
                        value = key.GetValue(valueName) as string;
                    }
                }
            }
            return value;
        }

        /// <summary>
        /// Obtains the path to an NDK file
        /// </summary>
        /// <param name="registryValueName">[Required] registry value to check first</param>
        /// <param name="ndkRoot">[Required] Path to the NDK</param>
        /// <param name="possiblePaths">[Required] Array of possible paths with in the NDK where the file may be found</param>
        /// <param name="matchedPath">[Optional] If the returned path comes from an NDK location, returns the source object</param>
        /// <returns>[Required] value to use, file path will exist</returns>
        private static string GetNDKFilePath(string registryValueName, string ndkRoot, NDKToolChainFilePath[] possiblePaths, out NDKToolChainFilePath matchedPath)
        {
            matchedPath = null;

            if (string.IsNullOrEmpty(ndkRoot))
            {
                throw new ArgumentNullException("ndkRoot");
            }

            if (possiblePaths == null || possiblePaths.Length <= 0)
            {
                throw new ArgumentOutOfRangeException("possiblePaths");
            }

            string value = TryGetRegistryValue(RegistryRoot.Value + @"\Debugger", registryValueName, RegistryView.Default);
            if (value != null)
            {
                if (File.Exists(value))
                {
                    return value;
                }

                // fall through to throw an exception
            }
            else
            {
                foreach (NDKToolChainFilePath pathObject in possiblePaths)
                {
                    value = pathObject.TryResolve(ndkRoot);
                    if (value != null)
                    {
                        matchedPath = pathObject;
                        return value;
                    }
                }

                value = possiblePaths.First().GetSearchPathDescription(ndkRoot);
                // fall through to throw an exception
            }

            ThrowExternalFileNotFoundException(value, LauncherResources.ProductName_NDK);
            return null; // unreachable code
        }

        /// <summary>
        /// Throws an exception for the file not existing
        /// </summary>
        /// <param name="filePath">[Required] path to the file which didn't exist</param>
        /// <param name="externalProductName">Name of the external product that the file is from</param>
        private static void ThrowExternalFileNotFoundException(string filePath, string externalProductName)
        {
            string fileName = Path.GetFileName(filePath);
            throw new LauncherException(Telemetry.LaunchFailureCode.NoReport, string.Format(CultureInfo.CurrentCulture, LauncherResources.Error_ExternalFileNotFound, externalProductName, fileName, filePath));
        }
    }
}
