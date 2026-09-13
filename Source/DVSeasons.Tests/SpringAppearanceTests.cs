using DVSeasons.Core;
using Xunit;

public class SpringAppearanceTests
{
    [Theory]
    [InlineData(.35f,.22f,.12f)]
    [InlineData(.4f,.4f,.4f)]
    [InlineData(.8f,.12f,.2f)]
    [InlineData(.95f,.95f,.95f)]
    public void BarkRockPetalsAndWhiteStayUnchanged(float r,float g,float b)
    {
        float originalR=r,originalG=g,originalB=b;
        SpringAppearance.Recolor(ref r,ref g,ref b,false);
        Assert.Equal(originalR,r);Assert.Equal(originalG,g);Assert.Equal(originalB,b);
    }
    [Fact]
    public void YoungGreensAreLighterAndConifersChangeLess()
    {
        float r=.24f,g=.34f,b=.09f,cr=r,cg=g,cb=b;
        SpringAppearance.Recolor(ref r,ref g,ref b,false);
        SpringAppearance.Recolor(ref cr,ref cg,ref cb,true);
        Assert.True(g>.41f);Assert.True(r>.27f);Assert.True(g>r && r>b);
        Assert.True(cg<g && cg>.34f);
    }
    [Fact]
    public void SpringFadesInAndOutAndLeavesSummerUntouched()
    {
        Assert.Equal(.4f,SpringAppearance.Weight(new SeasonState(3.9,SeasonKind.Winter,SeasonKind.Spring,.4f,.6f,-10,0)));
        Assert.Equal(.6f,SpringAppearance.Weight(new SeasonState(.9,SeasonKind.Spring,SeasonKind.Summer,.4f,0,15,0)));
        Assert.Equal(0,SpringAppearance.Weight(new SeasonState(1,SeasonKind.Summer,SeasonKind.Autumn,0,0,30,0)));
    }
    [Fact]
    public void SpringWeatherIsBetweenDrySummerAndWetAutumn()
    {
        var spring=new SeasonState(0,SeasonKind.Spring,SeasonKind.Summer,0,0,10,0);
        var summer=new SeasonState(1,SeasonKind.Summer,SeasonKind.Autumn,0,0,30,0);
        var autumn=new SeasonState(2,SeasonKind.Autumn,SeasonKind.Winter,0,0,8,0);
        foreach(bool cloud in new[]{false,true})
        {
            Assert.True(SeasonalClimateProfile.NoiseOffset(spring,cloud)>SeasonalClimateProfile.NoiseOffset(summer,cloud));
            Assert.True(SeasonalClimateProfile.NoiseOffset(spring,cloud)<SeasonalClimateProfile.NoiseOffset(autumn,cloud));
        }
        var rain=SeasonalPrecipitationProfile.FromState(spring);
        Assert.True(rain.StartThresholdOffset<-.1f);
        Assert.True(rain.StartThresholdOffset>SeasonalPrecipitationProfile.FromState(autumn).StartThresholdOffset);
        Assert.Equal(0,rain.MaximumThresholdOffset);
    }
}
