using System;
using System.IO;
using KK.SFPlayer.Constants;
using Xunit;

namespace KK.SFPlayer.Tests
{
    public sealed class PlaybackConfigurationTests
    {
        [Fact]
        public void PlaybackPageHasMappedHttpsOriginAndExistsInOutput()
        {
            var page = new Uri(StreamingConstants.Page);
            Assert.Equal(Uri.UriSchemeHttps, page.Scheme);
            Assert.Equal(StreamingConstants.Host, page.Host);
            var file = Path.Combine(AppContext.BaseDirectory, "Assets", "Playback", page.AbsolutePath.TrimStart('/'));
            Assert.True(File.Exists(file));
        }
    }
}
