using System;
using System.IO;
using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public sealed class VehicleThermalNetworkTests
    {
        internal static SeasonNetworkState Packet()
        {
            var state = SeasonNetworkState.FromState(new SeasonState(3d, SeasonKind.Winter,
                SeasonKind.Spring, 0, 1, -50, .4f));
            var climate = new WindowWinterClimate();
            climate.Advance(0, -30, true, 80, 1, false, 1);
            for (int i = 0; i < 60; i++) climate.Advance(1, -30, true, 80, 1, false, 1);
            state.HasThermalSnapshot = true;
            state.VehicleThermal = new[] { VehicleThermalNetworkState.Capture("de6-a", climate, .23f),
                VehicleThermalNetworkState.Capture("s060-b", null, .94f) };
            return state;
        }
        private static byte[] Encode(SeasonNetworkState state)
        { using (var s = new MemoryStream()) { state.WriteTo(new BinaryWriter(s)); return s.ToArray(); } }
        private static SeasonNetworkState Decode(byte[] data)
        { using (var s = new MemoryStream(data)) return SeasonNetworkState.ReadFrom(new BinaryReader(s)); }

        [Fact]
        public void TemperaturesFrostFogAndMeltRoundTripWithoutQuantizationOrLocalAdvance()
        {
            var host = Packet(); var client = Decode(Encode(host));
            Assert.True(client.IsValid()); Assert.True(client.HasThermalSnapshot);
            Assert.Equal(host.VehicleThermal, client.VehicleThermal);
            var climate = new WindowWinterClimate(); climate.Restore(client.VehicleThermal[0].ClimateState());
            Assert.Equal(host.VehicleThermal[0].Cabin, climate.CabinTemperature);
            Assert.Equal(host.VehicleThermal[0].Glass, climate.GlassTemperature);
            Assert.Equal(host.VehicleThermal[0].Frost, climate.Frost);
            Assert.Equal(host.VehicleThermal[0].Fog, climate.Fog);
            Assert.True(climate.IsInitialized);
            Assert.False(client.VehicleThermal[1].HasClimate);
            Assert.Equal(.94f, client.VehicleThermal[1].MeltedSnow);
        }
        [Fact]
        public void RestoreAlsoRestoresThawStage()
        {
            var climate = new WindowWinterClimate();
            climate.Restore(new WindowClimateState { Initialized=true, Glass=1, Frost=.7f });
            Assert.Equal(WinterGlassStage.Thawing, climate.Stage);
            climate.Restore(new WindowClimateState { Initialized=true, Glass=6, Frost=0, Fog=.3f });
            Assert.Equal(WinterGlassStage.Fogged, climate.Stage);
        }
        [Fact]
        public void OmittedSnapshotIsDifferentFromExplicitEmptySnapshot()
        {
            var state = Packet(); state.HasThermalSnapshot = false;
            var omitted = Decode(Encode(state)); Assert.False(omitted.HasThermalSnapshot); Assert.Empty(omitted.VehicleThermal);
            state.HasThermalSnapshot = true; state.VehicleThermal = VehicleThermalNetworkState.Empty;
            var empty = Decode(Encode(state)); Assert.True(empty.HasThermalSnapshot); Assert.Empty(empty.VehicleThermal);
        }
        [Fact]
        public void InvalidClimateDuplicateIdsAndOversizeAreRejected()
        {
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, -61, 151 })
            {
                var state = Packet(); state.VehicleThermal[0].Cabin = invalid;
                Assert.False(state.IsValid()); Assert.Throws<InvalidDataException>(() => Encode(state));
            }
            var duplicate = Packet(); duplicate.VehicleThermal[1].CarId = "DE6-A";
            Assert.False(duplicate.IsValid()); Assert.Throws<InvalidDataException>(() => Encode(duplicate));
            var oversized = Packet(); oversized.VehicleThermal = new VehicleThermalNetworkState[1025];
            Assert.False(oversized.IsValid()); Assert.Throws<InvalidDataException>(() => Encode(oversized));
        }
        [Fact]
        public void EveryTruncatedThermalSnapshotFailsBeforeApplication()
        {
            var full = Encode(Packet());
            for (int length = 0; length < full.Length; length++)
            {
                var data = new byte[length]; Array.Copy(full, data, length);
                Assert.Throws<EndOfStreamException>(() => Decode(data));
            }
        }
        [Fact]
        public void CountAndIdLengthAreBoundedBeforeReadingOrAllocatingTheirPayload()
        {
            var state = Packet(); state.VehicleThermal = VehicleThermalNetworkState.Empty;
            var bytes = Encode(state); bytes[bytes.Length - 2] = 1; bytes[bytes.Length - 1] = 4;
            Assert.Throws<InvalidDataException>(() => Decode(bytes));
            var full = Encode(Packet()); int idOffset = bytes.Length;
            foreach (byte size in new byte[] { 0, 81, 255 })
            {
                var data = new byte[idOffset + 1]; Array.Copy(full, data, data.Length); data[idOffset] = size;
                Assert.Throws<InvalidDataException>(() => Decode(data));
            }
        }
    }
}
