using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public class SnowTrailProfileTests
    {
        [Theory]
        [InlineData(0,1,0)]
        [InlineData(50,1,0)]
        [InlineData(60,1,.2)]
        [InlineData(75,.5,.25)]
        [InlineData(100,1,1)]
        [InlineData(160,.4,.4)]
        public void TrailStartsAboveFiftyAndScalesWithSnow(float speed,float coverage,float expected)
        {Assert.Equal(expected,SnowTrailProfile.Intensity(speed,coverage),5);}
    }
}
