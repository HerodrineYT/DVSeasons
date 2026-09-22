using System;
using System.IO;
using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public sealed class VehicleSideSnowNetworkTests
    {
        private static SeasonNetworkState Snapshot()
        {
            var state = SeasonNetworkState.FromState(new SeasonState(3d, SeasonKind.Winter,
                SeasonKind.Spring, 0f, 1f, -20f, .4f));
            state.HasSideSnowSnapshot = true;
            return state;
        }

        private static byte[] Encode(SeasonNetworkState state)
        {
            using (var stream = new MemoryStream())
            {
                state.WriteTo(new BinaryWriter(stream));
                return stream.ToArray();
            }
        }

        private static SeasonNetworkState Decode(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
                return SeasonNetworkState.ReadFrom(new BinaryReader(stream));
        }

        [Fact]
        public void EveryFaceAndVehicleSurvivesTheWireWithBoundedQuantization()
        {
            var expected = Snapshot();
            expected.SideSnow = new VehicleSideSnowNetworkState[VehicleSideSnowNetworkState.MaxVehicleCount];
            for (int i = 0; i < expected.SideSnow.Length; i++)
                expected.SideSnow[i] = new VehicleSideSnowNetworkState
                {
                    CarId = "vehicle-" + i, PositiveX = i / 4095f, NegativeX = 1f - i / 4095f,
                    PositiveZ = .2f, NegativeZ = .75f
                };
            var bytes = Encode(expected);
            Assert.True(bytes.Length < 100000); // 4096 cars, one bounded snapshot per second.
            var actual = Decode(bytes);
            Assert.True(actual.IsValid());
            Assert.True(actual.HasSideSnowSnapshot);
            Assert.Equal(expected.SideSnow.Length, actual.SideSnow.Length);
            for (int i = 0; i < expected.SideSnow.Length; i++)
            {
                Assert.Equal(expected.SideSnow[i].CarId, actual.SideSnow[i].CarId);
                Assert.InRange(Math.Abs(expected.SideSnow[i].PositiveX - actual.SideSnow[i].PositiveX), 0f, 1f / 65535f);
                Assert.InRange(Math.Abs(expected.SideSnow[i].NegativeX - actual.SideSnow[i].NegativeX), 0f, 1f / 65535f);
                Assert.InRange(Math.Abs(expected.SideSnow[i].PositiveZ - actual.SideSnow[i].PositiveZ), 0f, 1f / 65535f);
                Assert.InRange(Math.Abs(expected.SideSnow[i].NegativeZ - actual.SideSnow[i].NegativeZ), 0f, 1f / 65535f);
            }
        }

        [Fact]
        public void WeatherOnlyPacketDiffersFromAnAuthoritativeEmptySnapshot()
        {
            var state = Snapshot();
            Assert.True(Decode(Encode(state)).HasSideSnowSnapshot);
            state.HasSideSnowSnapshot = false;
            state.SideSnow = new[] { new VehicleSideSnowNetworkState { CarId = "omitted", PositiveX = .8f } };
            var actual = Decode(Encode(state));
            Assert.False(actual.HasSideSnowSnapshot);
            Assert.Same(VehicleSideSnowNetworkState.Empty, actual.SideSnow);
            state.HasSideSnowSnapshot = true;
            state.SideSnow = VehicleSideSnowNetworkState.Empty;
            Assert.Empty(Decode(Encode(state)).SideSnow);
        }

        [Fact]
        public void InvalidAmountsIdentifiersAndOversizedArraysAreRejected()
        {
            foreach (float invalid in new[] { -.1f, 1.1f, float.NaN, float.PositiveInfinity })
                for (int face = 0; face < 4; face++)
                {
                    var state = Snapshot();
                    var entry = new VehicleSideSnowNetworkState { CarId = "car" };
                    if (face == 0) entry.PositiveX = invalid;
                    if (face == 1) entry.NegativeX = invalid;
                    if (face == 2) entry.PositiveZ = invalid;
                    if (face == 3) entry.NegativeZ = invalid;
                    state.SideSnow = new[] { entry };
                    Assert.False(state.IsValid());
                    Assert.Throws<InvalidDataException>(() => Encode(state));
                }
            foreach (string invalid in new[] { null, "", "contains space", "car\n", "машина", new string('x', 65) })
            {
                var state = Snapshot();
                state.SideSnow = new[] { new VehicleSideSnowNetworkState { CarId = invalid } };
                Assert.False(state.IsValid());
                Assert.Throws<InvalidDataException>(() => Encode(state));
            }
            var oversized = Snapshot();
            oversized.SideSnow = new VehicleSideSnowNetworkState[4097];
            Assert.False(oversized.IsValid());
            Assert.Throws<InvalidDataException>(() => Encode(oversized));
        }

        [Fact]
        public void UntrustedCountsAndIdentifierLengthsAreRejectedBeforeTheirPayloadIsRead()
        {
            var bytes = Encode(Snapshot());
            bytes[bytes.Length - 3] = 1;
            bytes[bytes.Length - 2] = 16; // 4097 entries, followed by the optional thermal flag.
            Assert.Throws<InvalidDataException>(() => Decode(bytes));
            var state = Snapshot();
            state.SideSnow = new[] { new VehicleSideSnowNetworkState { CarId = "car" } };
            var valid = Encode(state);
            int lengthPosition = bytes.Length - 1;
            foreach (byte invalid in new byte[] { 0, 65, 255 })
            {
                var malformed = new byte[lengthPosition + 1];
                Array.Copy(valid, malformed, malformed.Length);
                malformed[lengthPosition] = invalid;
                Assert.Throws<InvalidDataException>(() => Decode(malformed));
            }
        }

        [Fact]
        public void TruncatedSideSnowNeverProducesAnAcceptedPartialSnapshot()
        {
            var state = Snapshot();
            state.SideSnow = new[] { new VehicleSideSnowNetworkState { CarId = "car-123", PositiveZ = .9f } };
            byte[] complete = Encode(state);
            for (int length = 0; length < complete.Length; length++)
            {
                byte[] truncated = new byte[length];
                Array.Copy(complete, truncated, length);
                Assert.Throws<EndOfStreamException>(() => Decode(truncated));
            }
        }
    }
}
