using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public class LocomotiveSnowHeatTests
    {
        [Theory]
        [InlineData(false,20,false)]
        [InlineData(false,80,false)]
        [InlineData(true,20,true)]
        [InlineData(false,60,false)]
        public void SteamHeatFollowsItsOwnFire(bool fire,float temperature,bool hot)
        {Assert.Equal(hot,LocomotiveSnowHeat.SteamIsHot(fire,temperature));}
        [Theory]
        [InlineData(false,false,0.3f)]
        [InlineData(true,false,1f)]
        [InlineData(false,true,0f)]
        [InlineData(true,true,0f)]
        public void RunningLimitsAndBatteryException(bool steam,bool battery,float target)
        {
            float melt=0;
            for(int i=0;i<60;i++)melt=LocomotiveSnowHeat.Advance(melt,true,steam,battery,1f,1f);
            Assert.Equal(target,melt,4);
        }
        [Fact] public void WarmupIsGradualAndSnowOnlyReturnsDuringSnowfall()
        {
            var melted=LocomotiveSnowHeat.Advance(0,true,true,false,0,15);
            Assert.Equal(.5f,melted,5);
            Assert.Equal(melted,LocomotiveSnowHeat.Advance(melted,false,true,false,0,3600));
            Assert.Equal(0,LocomotiveSnowHeat.Advance(melted,false,true,false,.5f,90),5);
            Assert.Equal(0,LocomotiveSnowHeat.Advance(melted,false,true,false,1,60));
        }
        [Fact] public void PausedFramesDoNotMeltSnow()
        {Assert.Equal(.2f,LocomotiveSnowHeat.Advance(.2f,true,true,false,0,0));}
        [Fact] public void LightSnowRestoresHeatClearedCoverWithinTwoMinutes()
        {Assert.Equal(0,LocomotiveSnowHeat.Advance(1,false,true,false,.01f,120),5);}
        [Fact] public void OneMinuteMakesEverySupportedEngineVisiblyWarm()
        {
            Assert.Equal(1f,LocomotiveSnowHeat.Advance(0,true,true,false,1,60),5);
            Assert.Equal(.3f,LocomotiveSnowHeat.Advance(0,true,false,false,1,60),5);
        }
        [Theory]
        [InlineData(0,1)]
        [InlineData(.15,.75)]
        [InlineData(.3,.5)]
        [InlineData(1,0)]
        public void HeatReductionRemainsPartialButReadable(float melted,float remaining)
        {Assert.Equal(remaining,LocomotiveSnowHeat.VisibleRemaining(melted),5);}
    }
}
