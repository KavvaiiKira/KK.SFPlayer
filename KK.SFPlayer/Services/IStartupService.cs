namespace KK.SFPlayer.Services
{
    internal interface IStartupService
    {
        bool IsSupported { get; }

        bool IsEnabled { get; }

        void SetEnabled(bool enabled);
    }
}
