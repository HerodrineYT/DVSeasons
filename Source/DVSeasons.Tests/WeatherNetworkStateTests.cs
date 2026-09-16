using System;
using System.IO;
using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public sealed class WeatherNetworkStateTests
    {
        private static WeatherNetworkState Weather()
        {
            return new WeatherNetworkState
            {
                Available = true,
                Overrides = WeatherNetworkState.AllOverridesMask,
                Values = new[] { .1f, .2f, .3f, 8f, 270f, .6f, .7f, 18.5f, 1440f },
                RealDateTimeTicks = new DateTime(2026, 9, 16, 18, 30, 0).Ticks,
                TimeRevision = 7,
                BaseDayLengthInMinutes = 1440f
            };
        }

        private static SeasonNetworkState Season(WeatherNetworkState weather)
        {
            var state = SeasonNetworkState.FromState(new SeasonState(3d, SeasonKind.Winter,
                SeasonKind.Spring, 0f, 1f, -20f, .4f));
            state.Weather = weather;
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
        public void RoundTripPreservesEveryWeatherValueOverrideAndSeasonalSetting()
        {
            for (int flags = 0; flags < 32; flags++)
            {
                var expected = Weather();
                expected.SeasonalDaylight = (flags & 1) != 0;
                expected.SeasonalPrecipitation = (flags & 2) != 0;
                expected.WinterAdhesion = (flags & 4) != 0;
                expected.DisableWinterThunder = (flags & 8) != 0;
                expected.RespectExternalWetnessOverride = (flags & 16) != 0;
                var state = Decode(Encode(Season(expected)));
                Assert.True(state.IsValid());
                Assert.Equal(9, state.Protocol);
                var actual = state.Weather;
                Assert.True(actual.Available);
                Assert.Equal(expected.Overrides, actual.Overrides);
                Assert.Equal(expected.Values, actual.Values);
                Assert.Equal(expected.RealDateTimeTicks, actual.RealDateTimeTicks);
                Assert.Equal(expected.TimeRevision, actual.TimeRevision);
                Assert.Equal(expected.SeasonalDaylight, actual.SeasonalDaylight);
                Assert.Equal(expected.SeasonalPrecipitation, actual.SeasonalPrecipitation);
                Assert.Equal(expected.WinterAdhesion, actual.WinterAdhesion);
                Assert.Equal(expected.DisableWinterThunder, actual.DisableWinterThunder);
                Assert.Equal(expected.RespectExternalWetnessOverride, actual.RespectExternalWetnessOverride);
                Assert.Equal(expected.BaseDayLengthInMinutes, actual.BaseDayLengthInMinutes);
            }
        }

        [Fact]
        public void ClearingEveryOrIndividualOverridesSurvivesTheWire()
        {
            var weather = Weather();
            for (int slot = -1; slot < WeatherNetworkState.ValueCount; slot++)
            {
                weather.Overrides = slot < 0 ? (ushort)0 : (ushort)(1 << slot);
                var actual = Decode(Encode(Season(weather))).Weather;
                Assert.True(actual.IsValid());
                Assert.Equal(weather.Overrides, actual.Overrides);
                Assert.Equal(weather.Values, actual.Values);
            }
        }

        [Fact]
        public void DefaultUnavailableSnapshotRemainsValidAndRoundTrips()
        {
            var actual = Decode(Encode(Season(new WeatherNetworkState())));
            Assert.True(actual.IsValid());
            Assert.False(actual.Weather.Available);
            Assert.Equal((ushort)0, actual.Weather.Overrides);
        }

        [Theory]
        [InlineData(0, 0f, 1f)]
        [InlineData(1, 0f, 1f)]
        [InlineData(2, 0f, 1f)]
        [InlineData(3, 0f, 10f)]
        [InlineData(4, 0f, 360f)]
        [InlineData(5, 0f, 1f)]
        [InlineData(6, 0f, 1f)]
        [InlineData(7, 0f, 24f)]
        [InlineData(8, .01f, 100000f)]
        public void ActiveOverrideBoundsAndNonFiniteValuesAreRejected(int slot, float min, float max)
        {
            var weather = Weather();
            weather.Values[slot] = min; Assert.True(weather.IsValid());
            weather.Values[slot] = max; Assert.True(weather.IsValid());
            foreach (var invalid in new[] { min - 1f, max + 1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                weather.Values[slot] = invalid;
                Assert.False(Decode(Encode(Season(weather))).IsValid());
            }
            weather.Overrides = 0;
            Assert.False(weather.IsValid()); // Nonfinite data is rejected even in an inactive slot.
        }

        [Fact]
        public void InvalidMaskArrayClockAndDayLengthAreRejected()
        {
            Action<WeatherNetworkState>[] invalidate =
            {
                w => w.Overrides = 1 << 9,
                w => w.Values = null,
                w => w.Values = new float[8],
                w => w.Values = new float[10],
                w => w.RealDateTimeTicks = -1,
                w => w.RealDateTimeTicks = DateTime.MaxValue.Ticks + 1,
                w => w.BaseDayLengthInMinutes = 0,
                w => w.BaseDayLengthInMinutes = 100001f,
                w => w.BaseDayLengthInMinutes = float.NaN,
                w => w.BaseDayLengthInMinutes = float.PositiveInfinity
            };
            foreach (var mutate in invalidate)
            {
                var weather = Weather(); mutate(weather);
                Assert.False(Season(weather).IsValid());
            }
            var valid = Weather();
            valid.RealDateTimeTicks = DateTime.MinValue.Ticks; Assert.True(valid.IsValid());
            valid.RealDateTimeTicks = DateTime.MaxValue.Ticks; Assert.True(valid.IsValid());
            Assert.False(Season(null).IsValid());
        }

        [Theory]
        [InlineData(8)]
        [InlineData(10)]
        public void MixedProtocolsAreRejectedWithoutReadingTheirPayload(int protocol)
        {
            using (var stream = new MemoryStream())
            {
                new BinaryWriter(stream).Write(protocol);
                stream.Position = 0;
                Assert.False(SeasonNetworkState.ReadFrom(new BinaryReader(stream)).IsValid());
                Assert.Equal(4, stream.Position);
            }
        }

        [Fact]
        public void TruncatedCurrentProtocolNeverProducesAnAcceptedSnapshot()
        {
            var complete = Encode(Season(Weather()));
            for (int length = 0; length < complete.Length; length++)
            {
                var truncated = new byte[length];
                Array.Copy(complete, truncated, length);
                Assert.Throws<EndOfStreamException>(() => Decode(truncated));
            }
        }

        [Fact]
        public void CloneOwnsItsOverrideArray()
        {
            var original = Weather();
            var clone = original.Clone();
            clone.Values[0] = .9f;
            clone.Overrides = 0;
            clone.TimeRevision++;
            Assert.Equal(.1f, original.Values[0]);
            Assert.Equal(WeatherNetworkState.AllOverridesMask, original.Overrides);
            Assert.Equal(7u, original.TimeRevision);
        }
    }
}
