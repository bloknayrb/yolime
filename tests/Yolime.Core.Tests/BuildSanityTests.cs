using Yolime.Core;

namespace Yolime.Core.Tests;

public class BuildSanityTests
{
#if !YOLIME_E2E
    [Fact]
    public void E2EDefine_IsOffInNormalBuilds() => Assert.False(BuildFlags.IsE2E);
#endif

#if YOLIME_E2E
    [Fact]
    public void E2EDefine_IsOnInE2EBuilds() => Assert.True(BuildFlags.IsE2E);
#endif
}
