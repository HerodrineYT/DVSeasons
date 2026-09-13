using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public class SpringLifeTests
    {
        private static SeasonState Spring(float temperature)
        { return new SeasonState(0,SeasonKind.Spring,SeasonKind.Summer,0,0,temperature,0); }
        [Fact]
        public void PollinatorsRestDuringColdRainDarknessAndStormWind()
        {
            Assert.Equal(1,SpringLifeProfile.Activity(Spring(18),0,1,0));
            Assert.Equal(0,SpringLifeProfile.Activity(Spring(5),0,1,0));
            Assert.Equal(0,SpringLifeProfile.Activity(Spring(18),.3f,1,0));
            Assert.Equal(0,SpringLifeProfile.Activity(Spring(18),0,0,0));
            Assert.Equal(0,SpringLifeProfile.Activity(Spring(18),0,1,14));
            Assert.InRange(SpringLifeProfile.Activity(Spring(11),0,.6f,2),.1f,.8f);
        }
        [Fact]
        public void OtherSeasonsDoNotEmitSpringInsects()
        {
            var summer = new SeasonState(1,SeasonKind.Summer,SeasonKind.Autumn,0,0,30,0);
            Assert.Equal(0,SpringLifeProfile.Activity(summer,0,1,0));
        }
    }
}
