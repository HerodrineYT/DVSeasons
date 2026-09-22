using System;
using System.IO;
using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public sealed class ColdStartHintTests
    {
        static byte[] Encode(SeasonNetworkState state)
        {using(var data=new MemoryStream()){state.WriteTo(new BinaryWriter(data));return data.ToArray();}}
        static SeasonNetworkState Decode(byte[] data)
        {using(var stream=new MemoryStream(data))return SeasonNetworkState.ReadFrom(new BinaryReader(stream));}
        [Fact]
        public void HostRuleAndBothStagesRoundTripAndEmptyStopsHints()
        {
            var state=VehicleThermalNetworkTests.Packet();state.IgnoreVanillaColdStarts=true;
            state.ColdStarts=new[]{new ColdStartHintState{CarId="de2",Stage=ColdStartHintStage.Starter,RemainingSeconds=7.25f},
                new ColdStartHintState{CarId="de6",Stage=ColdStartHintStage.Primer,RemainingSeconds=2.5f,De6=true}};
            var client=Decode(Encode(state));Assert.True(client.IsValid());Assert.True(client.IgnoreVanillaColdStarts);
            Assert.Equal(state.ColdStarts,client.ColdStarts);Assert.Equal(state.VehicleThermal,client.VehicleThermal);
            state.ColdStarts=ColdStartHintState.Empty;Assert.Empty(Decode(Encode(state)).ColdStarts);
        }
        [Fact]
        public void PrimerDoesNotCountDownWithoutHostHoldAndStarterPredictionDoesNotGoNegative()
        {
            var hint=new ColdStartHintState{CarId="de6",Stage=ColdStartHintStage.Primer,RemainingSeconds=3,De6=true};
            Assert.Equal(3,hint.After(1.9f).RemainingSeconds);
            hint.Stage=ColdStartHintStage.Starter;
            Assert.Equal(1.5f,hint.After(1.5f).RemainingSeconds);
            Assert.Equal(0,hint.After(99).RemainingSeconds);
            Assert.Equal(3,hint.After(float.NaN).RemainingSeconds);
        }
        [Fact]
        public void InvalidPacketsAndEveryTruncationAreRejected()
        {
            var state=VehicleThermalNetworkTests.Packet();
            var valid=new ColdStartHintState{CarId="de6",Stage=ColdStartHintStage.Primer,RemainingSeconds=3,De6=true};
            state.ColdStarts=new[]{valid};var bytes=Encode(state);
            for(int i=0;i<bytes.Length;i++)
            {var truncated=new byte[i];Array.Copy(bytes,truncated,i);Assert.Throws<EndOfStreamException>(()=>Decode(truncated));}
            foreach(float invalid in new[]{-1f,3.1f,float.NaN,float.PositiveInfinity})
            {var bad=valid;bad.RemainingSeconds=invalid;Assert.False(ColdStartHintState.IsValid(new[]{bad}));}
            state.ColdStarts=new[]{valid,valid};Assert.False(state.IsValid());Assert.Throws<InvalidDataException>(()=>Encode(state));
            Assert.False(ColdStartHintState.IsValid(new ColdStartHintState[1025]));
            var old=BitConverter.GetBytes(12);Assert.False(Decode(old).IsValid());
        }
    }
}
