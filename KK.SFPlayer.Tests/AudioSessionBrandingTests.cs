using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using KK.SFPlayer.Constants;
using KK.SFPlayer.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;

namespace KK.SFPlayer.Tests
{
    public sealed class AudioSessionBrandingTests
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        [Fact]
        public void MixerIconCanBeExtractedFromApplicationExecutable()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var executable = Path.Combine(AppContext.BaseDirectory, ApplicationConstants.AppName + ".exe");
            Assert.Equal(executable + ",-32512", AudioSessionBranding.IconLocation);
            var module = LoadLibraryEx(executable, IntPtr.Zero, 0x22);
            Assert.NotEqual(IntPtr.Zero, module);
            try
            {
                Assert.NotEqual(IntPtr.Zero, FindResource(module, new IntPtr(AudioSessionConstants.IconResourceId), new IntPtr(14)));
            }
            finally
            {
                FreeLibrary(module);
            }

            var count = ExtractIconEx(executable, -AudioSessionConstants.IconResourceId, out var large, out var small, 1);
            try
            {
                Assert.True(count > 0);
                Assert.NotEqual(IntPtr.Zero, large);
                Assert.NotEqual(IntPtr.Zero, small);
            }
            finally
            {
                if (large != IntPtr.Zero)
                {
                    DestroyIcon(large);
                }

                if (small != IntPtr.Zero)
                {
                    DestroyIcon(small);
                }
            }
        }

        [Fact]
        public void NamesOnlyOwnedAudioSessionWithoutChangingVolumeOrMute()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var icon = AudioSessionBranding.IconLocation;
            using var output = new WasapiPlayerBuilder().Build();
            output.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
            output.Volume = 0;
            output.Play();
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioSessionManager.RefreshSessions();
            var sessions = device.AudioSessionManager.Sessions;
            AudioSessionControl? owned = null;
            for (var index = 0; index < sessions.Count; index++)
            {
                var session = sessions[index];
                if (session.GetProcessID == Environment.ProcessId)
                {
                    owned = session;
                    break;
                }

                session.Dispose();
            }

            Assert.NotNull(owned);
            using (owned)
            {
                var name = owned.DisplayName;
                var volume = owned.SimpleAudioVolume.Volume;
                var muted = owned.SimpleAudioVolume.Mute;
                Assert.Equal(0, AudioSessionBranding.Apply(new HashSet<int> { int.MaxValue }, ApplicationConstants.AppName, icon, CancellationToken.None));
                Assert.Equal(name, owned.DisplayName);
                Assert.True(AudioSessionBranding.Apply(new HashSet<int> { Environment.ProcessId }, ApplicationConstants.AppName, icon, CancellationToken.None) > 0);
                Assert.Equal(ApplicationConstants.AppName, owned.DisplayName);
                Assert.Equal(icon, owned.IconPath);
                Assert.Equal(AudioSessionConstants.GroupId, owned.GetGroupingParam());
                Assert.Equal(volume, owned.SimpleAudioVolume.Volume);
                Assert.Equal(muted, owned.SimpleAudioVolume.Mute);
            }

            output.Stop();
        }
    }
}
