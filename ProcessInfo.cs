using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TrayMixer
{
    sealed class ProcessDetails
    {
        public string ImagePath;
        public string ExeName;
        public bool IsDiscordAudioService;
    }

    static class ProcessInfo
    {
        static readonly HashSet<string> DiscordExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "discord.exe", "discordptb.exe", "discordcanary.exe", "discorddevelopment.exe",
        };

        // Discord plays its sound effects through Chromium's audio service child process; voice audio comes from elsewhere.
        const string AudioServiceMarker = "audio.mojom.AudioService";

        public static bool IsDiscordExe(string exeName) => exeName != null && DiscordExeNames.Contains(exeName);

        public static ProcessDetails Describe(uint pid)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                string image = GetImagePath(handle);
                if (image == null)
                    return null;
                string exe = Path.GetFileName(image).ToLowerInvariant();
                bool audioService = false;
                if (IsDiscordExe(exe))
                {
                    string commandLine = GetCommandLine(handle);
                    audioService = commandLine != null && commandLine.IndexOf(AudioServiceMarker, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                return new ProcessDetails { ImagePath = image, ExeName = exe, IsDiscordAudioService = audioService };
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        public static string GetDisplayName(string imagePath)
        {
            try
            {
                string description = FileVersionInfo.GetVersionInfo(imagePath).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description))
                    return description;
            }
            catch (FileNotFoundException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            return Path.GetFileNameWithoutExtension(imagePath);
        }

        static string GetImagePath(IntPtr handle)
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }

        static string GetCommandLine(IntPtr handle)
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out int length);
            if (length <= 0)
                return null;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _) != 0)
                    return null;
                var text = Marshal.PtrToStructure<UnicodeString>(buffer);
                return text.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        const int ProcessQueryLimitedInformation = 0x1000;
        const int ProcessCommandLineInformation = 60;

        [StructLayout(LayoutKind.Sequential)]
        struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        [DllImport("ntdll.dll")]
        static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int infoLength, out int returnLength);
    }
}
