﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;


namespace MICore
{
    public class LocalLinuxTransport : StreamTransport
    {
        private const string PtraceScopePath = "/proc/sys/kernel/yama/ptrace_scope";
        private const string PKExecPath = "/usr/bin/pkexec";
        private const string SudoPath = "/usr/bin/sudo";
        private const string GnomeTerminalPath = "/usr/bin/gnome-terminal";

        private void MakeGdbFifo(string path)
        {
            // Mod is normally in octal, but C# has no octal values. This is 384 (rw owner, no rights anyone else)
            const int rw_owner = 384;
            int result = LinuxNativeMethods.MkFifo(path, rw_owner);

            if (result != 0)
            {
                // Failed to create the fifo. Bail.
                Logger?.WriteLine("Failed to create gdb fifo");
                throw new ArgumentException("MakeGdbFifo failed to create fifo at path {0}", path);
            }
        }

        private bool IsValidMiDebuggerPath(string debuggerPath)
        {
            if (!File.Exists(debuggerPath))
            {
                return false;
            }
            else
            {
                // Verify the target is a file and not a directory
                FileAttributes attr = File.GetAttributes(debuggerPath);
                if ((attr & FileAttributes.Directory) != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private int GetPtraceScope()
        {
            // See: https://www.kernel.org/doc/Documentation/security/Yama.txt
            if (!File.Exists(LocalLinuxTransport.PtraceScopePath))
            {
                // If the scope file doesn't exist, security is disabled
                return 0;
            }

            try
            {
                string scope = File.ReadAllText(LocalLinuxTransport.PtraceScopePath);
                return Int32.Parse(scope, CultureInfo.CurrentCulture);
            }
            catch
            {
                // If we were unable to determine the current scope setting, assume we need root
                return -1;
            }
        }

        public override void InitStreams(LaunchOptions options, out StreamReader reader, out StreamWriter writer)
        {
            LocalLaunchOptions localOptions = (LocalLaunchOptions)options;

            if (!this.IsValidMiDebuggerPath(localOptions.MIDebuggerPath))
            {
                throw new Exception(MICoreResources.Error_InvalidMiDebuggerPath);
            }

            // Default working directory is next to the app
            string debuggeeDir;
            if (Path.IsPathRooted(options.ExePath) && File.Exists(options.ExePath))
            {
                debuggeeDir = System.IO.Path.GetDirectoryName(options.ExePath);
            }
            else
            {
                // If we don't know where the app is, default to HOME, and if we somehow can't get that, go with the root directory.
                debuggeeDir = Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(debuggeeDir))
                    debuggeeDir = "/";
            }

            string gdbStdInName = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            string gdbStdOutName = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            MakeGdbFifo(gdbStdInName);
            MakeGdbFifo(gdbStdOutName);

            // Setup the streams on the fifos as soon as possible.
            System.IO.FileStream gdbStdInStream = new FileStream(gdbStdInName, FileMode.Open);
            System.IO.FileStream gdbStdOutStream = new FileStream(gdbStdOutName, FileMode.Open);

            // If running as root, make sure the new console is also root. 
            bool isRoot = LinuxNativeMethods.GetEUid() == 0;

            // If "ptrace_scope" is a value other than 0, only root can attach to arbitrary processes
            bool requiresRootAttach = this.GetPtraceScope() != 0;

            // Spin up a new bash shell, cd to the working dir, execute a tty command to get the shell tty and store it
            // start the debugger in mi mode setting the tty to the terminal defined earlier and redirect stdin/stdout
            // to the correct pipes. After gdb exits, cleanup the FIFOs. This is done using the trap command to add a 
            // signal handler for SIGHUP on the console (executing the two rm commands)
            //
            // NOTE: sudo launch requires sudo or the terminal will fail to launch. The first argument must then be the terminal path
            // TODO: this should be configurable in launch options to allow for other terminals with a default of gnome-terminal so the user can change the terminal
            // command. Note that this is trickier than it sounds since each terminal has its own set of parameters. For now, rely on remote for those scenarios
            Process terminalProcess = new Process();
            terminalProcess.StartInfo.CreateNoWindow = false;
            terminalProcess.StartInfo.UseShellExecute = false;
            terminalProcess.StartInfo.WorkingDirectory = debuggeeDir;
            terminalProcess.StartInfo.FileName = !isRoot ? GnomeTerminalPath : SudoPath;

            string debuggerCmd = localOptions.MIDebuggerPath;

            // If the system doesn't allow a non-root process to attach to another process, try to run GDB as root
            if (localOptions.DebuggerMIMode == MIMode.Gdb && localOptions.ProcessId != 0 && !isRoot && requiresRootAttach)
            {
                // Prefer pkexec for a nice graphical prompt, but fall back to sudo if it's not available
                if (File.Exists(LocalLinuxTransport.PKExecPath))
                {
                    debuggerCmd = String.Concat(LocalLinuxTransport.PKExecPath, " ", debuggerCmd);
                }
                else if (File.Exists(LocalLinuxTransport.SudoPath))
                {
                    debuggerCmd = String.Concat(LocalLinuxTransport.SudoPath, " ", debuggerCmd);
                }
                else
                {
                    Debug.Fail("Root required to attach, but no means of elevating available!");
                }
            }

            string argumentString = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "--title DebuggerTerminal -x bash -c \"cd {0}; DbgTerm=`tty`; trap 'rm {2}; rm {3}' EXIT; {1} --interpreter=mi --tty=$DbgTerm < {2} > {3};\"",
                    debuggeeDir,
                    debuggerCmd,
                    gdbStdInName,
                    gdbStdOutName
                    );

            terminalProcess.StartInfo.Arguments = !isRoot ? argumentString : String.Concat(GnomeTerminalPath, " ", argumentString);
            Logger?.WriteLine("LocalLinuxTransport command: " + terminalProcess.StartInfo.FileName + " " + terminalProcess.StartInfo.Arguments);

            if (localOptions.Environment != null)
            {
                foreach (EnvironmentEntry entry in localOptions.Environment)
                {
                    terminalProcess.StartInfo.Environment.Add(entry.Name, entry.Value);
                }
            }

            terminalProcess.Start();

            // The in/out names are confusing in this case as they are relative to gdb.
            // What that means is the names are backwards wrt miengine hence the reader
            // being the writer and vice-versa
            writer = new StreamWriter(gdbStdInStream);
            reader = new StreamReader(gdbStdOutStream);
        }

        protected override string GetThreadName()
        {
            return "MI.LocalLinuxTransport";
        }
    }
}
