using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using KK.SFPlayer.Constants;

namespace KK.SFPlayer.Services
{
    internal static class AudioSessionBranding
    {
        public static string IconLocation => $"{Path.Combine(AppContext.BaseDirectory, ApplicationConstants.AppName + ".exe")},0";

        public static int Apply(IReadOnlySet<int> processIds, string name, string iconPath, CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows() || processIds.Count == 0)
            {
                return 0;
            }

            var count = 0;
            using var enumerator = new MMDeviceEnumerator();
            using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                using (device)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var sessions = device.AudioSessionManager.Sessions;
                        for (var index = 0; index < sessions.Count; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            using var session = sessions[index];
                            if (!processIds.Contains((int)session.GetProcessID))
                            {
                                continue;
                            }

                            if (session.DisplayName != name)
                            {
                                session.DisplayName = name;
                            }

                            if (session.IconPath != iconPath)
                            {
                                session.IconPath = iconPath;
                            }

                            if (session.GetGroupingParam() != AudioSessionConstants.GroupId)
                            {
                                session.SetGroupingParam(AudioSessionConstants.GroupId, Guid.Empty);
                            }

                            count++;
                        }
                    }
                    catch (COMException)
                    {
                        // The output device or its sessions can disappear during enumeration.
                    }
                }
            }

            return count;
        }
    }
}
