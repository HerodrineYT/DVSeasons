using System;

namespace DVSeasons.Core
{
    /// <summary>Deterministic autumn effect curves shared by host and clients.</summary>
    public static class AutumnEffectsProfile
    {
        // WeatherDriver wetness 0..0.5 maps to the game's native 0..50% adhesion
        // reduction. A value of 0.005 therefore adds only a barely perceptible
        // 0.5% autumn loss on otherwise dry rail.
        public const float WetnessEquivalent = 0.005f;
        public const float TrainLiftStartKmh = 8f;
        public const float TrainLiftFullKmh = 55f;
        public const float TrainWakeSideRadiusMetres = 6.5f;
        public const float TrainWakeTailLengthMetres = 14f;
        public const float GroundWindLiftStartMetresPerSecond = 1.5f;
        public const float GroundWindLiftFullMetresPerSecond = 12f;
        public const float GroundWindLiftMaximumLeavesPerSecond = 18f;
        public const float HiddenLeafIngressStartMetresPerSecond = 8f;
        public const float HiddenLeafIngressFullMetresPerSecond = 13.5f;
        public const float HiddenLeafIngressMaximumLeavesPerSecond = 54f;
        public const float HiddenLeafSourceMinimumDistanceMetres = 35f;
        public const float HiddenLeafSourceMaximumDistanceMetres = 115f;
        public const float HiddenLeafSourceMinimumUpwindAlignment = 0.25f;
        public const float HiddenLeafSourceMaximumCrosswindDistanceMetres = 58f;
        public const float HiddenLeafSourceViewportMargin = 0.04f;

        public static float GetWeight(SeasonState state)
        {
            if (state == null) return 0f;
            return GetWeight(state.Current, state.Next, state.Transition);
        }

        public static float GetWeight(SeasonKind current, SeasonKind next, float transition)
        {
            transition = Clamp01(transition);
            if (current == SeasonKind.Autumn)
                return next == SeasonKind.Winter ? 1f - transition : 1f;
            if (next == SeasonKind.Autumn)
                return transition;
            return 0f;
        }

        public static float GetLeafEmissionRate(SeasonState state, float windSpeedMetresPerSecond)
        {
            var weight = GetWeight(state);
            if (weight <= 0f) return 0f;
            if (float.IsNaN(windSpeedMetresPerSecond) || float.IsInfinity(windSpeedMetresPerSecond))
                windSpeedMetresPerSecond = 0f;
            var wind = Clamp01(Math.Max(0f, windSpeedMetresPerSecond) / 14f);
            // A calm canopy now becomes visible sooner, while wind still controls
            // most of the increase and the runtime pool remains strictly bounded.
            return weight * (7f + 35f * wind);
        }

        /// <summary>
        /// Bounded rate at which the game's wind lifts already settled leaves.
        /// A light breeze only rolls an occasional leaf, while a strong wind
        /// keeps a visible part of the local carpet moving without scaling work
        /// with the number of particles.
        /// </summary>
        public static float GetGroundWindLiftRate(float autumnWeight,
            float windSpeedMetresPerSecond)
        {
            if (!IsFinite(windSpeedMetresPerSecond)) windSpeedMetresPerSecond = 0f;
            autumnWeight = Clamp01(autumnWeight);
            if (autumnWeight <= 0f || windSpeedMetresPerSecond <= GroundWindLiftStartMetresPerSecond)
                return 0f;
            var strength = Clamp01((windSpeedMetresPerSecond - GroundWindLiftStartMetresPerSecond) /
                (GroundWindLiftFullMetresPerSecond - GroundWindLiftStartMetresPerSecond));
            return autumnWeight * GroundWindLiftMaximumLeavesPerSecond * SmoothStep(strength);
        }

        /// <summary>
        /// Extra leaves released from real, off-screen tree crowns during strong
        /// wind. This is separate from the ordinary visible canopy fall so the
        /// additional population can be kept behind a side of the camera.
        /// </summary>
        public static float GetHiddenLeafIngressStrength(float windSpeedMetresPerSecond)
        {
            if (!IsFinite(windSpeedMetresPerSecond) ||
                windSpeedMetresPerSecond <= HiddenLeafIngressStartMetresPerSecond)
                return 0f;
            var strength = Clamp01((windSpeedMetresPerSecond -
                HiddenLeafIngressStartMetresPerSecond) /
                (HiddenLeafIngressFullMetresPerSecond -
                    HiddenLeafIngressStartMetresPerSecond));
            return SmoothStep(strength);
        }

        public static float GetHiddenLeafIngressRate(SeasonState state,
            float windSpeedMetresPerSecond)
        {
            return GetWeight(state) * HiddenLeafIngressMaximumLeavesPerSecond *
                GetHiddenLeafIngressStrength(windSpeedMetresPerSecond);
        }

        /// <summary>
        /// Pure viewport/path test used by the runtime after projecting a real
        /// tree crown. The complete crown must be beyond a horizontal edge, and
        /// the wind must carry it broadly through the camera's local area.
        /// </summary>
        public static bool IsHiddenLeafIngressSource(float viewportX, float viewportY,
            float viewportDepth, float projectedCrownRadius, float distanceMetres,
            float upwindAlignment, float crosswindDistanceMetres)
        {
            if (!IsFinite(viewportX) || !IsFinite(viewportY) ||
                !IsFinite(viewportDepth) || !IsFinite(projectedCrownRadius) ||
                !IsFinite(distanceMetres) || !IsFinite(upwindAlignment) ||
                !IsFinite(crosswindDistanceMetres)) return false;
            if (viewportDepth <= 0f || viewportY < -0.5f || viewportY > 1.5f ||
                distanceMetres < HiddenLeafSourceMinimumDistanceMetres ||
                distanceMetres > HiddenLeafSourceMaximumDistanceMetres ||
                upwindAlignment < HiddenLeafSourceMinimumUpwindAlignment ||
                Math.Abs(crosswindDistanceMetres) >
                    HiddenLeafSourceMaximumCrosswindDistanceMetres) return false;

            var edge = Math.Max(0f, projectedCrownRadius) +
                HiddenLeafSourceViewportMargin;
            return viewportX <= -edge || viewportX >= 1f + edge;
        }

        public static float GetTrainLiftIntensity(SeasonState state, float speedKmh)
        {
            if (float.IsNaN(speedKmh) || float.IsInfinity(speedKmh)) return 0f;
            var speed = Clamp01((Math.Max(0f, speedKmh) - TrainLiftStartKmh) /
                (TrainLiftFullKmh - TrainLiftStartKmh));
            return GetWeight(state) * speed;
        }

        /// <summary>
        /// Strength of the air displaced by one moving car at a leaf position.
        /// Longitudinal and lateral coordinates are expressed in the car's travel
        /// frame, so this describes a volume around the whole car plus a fading
        /// wake behind it rather than an emitter at the end of the train.
        /// </summary>
        public static float GetTrainWakeStrength(float autumnWeight, float speedKmh,
            float longitudinalMetres, float lateralMetres, float carHalfLengthMetres,
            float carHalfWidthMetres)
        {
            if (!IsFinite(speedKmh) || !IsFinite(longitudinalMetres) ||
                !IsFinite(lateralMetres) || !IsFinite(carHalfLengthMetres) ||
                !IsFinite(carHalfWidthMetres)) return 0f;
            autumnWeight = Clamp01(autumnWeight);
            if (autumnWeight <= 0f) return 0f;
            var speed = Clamp01((Math.Max(0f, speedKmh) - TrainLiftStartKmh) /
                (TrainLiftFullKmh - TrainLiftStartKmh));
            if (speed <= 0f) return 0f;

            var halfLength = Math.Max(0.5f, carHalfLengthMetres);
            var halfWidth = Math.Max(0.5f, carHalfWidthMetres);
            const float frontMargin = 1.5f;
            if (longitudinalMetres > halfLength + frontMargin ||
                longitudinalMetres < -halfLength - TrainWakeTailLengthMetres)
                return 0f;
            var sideDistance = Math.Max(0f, Math.Abs(lateralMetres) - halfWidth);
            var side = SmoothStep(1f - Clamp01(sideDistance / TrainWakeSideRadiusMetres));
            var length = 1f;
            if (longitudinalMetres < -halfLength)
                length = SmoothStep(1f - Clamp01((-halfLength - longitudinalMetres) /
                    TrainWakeTailLengthMetres));
            else if (longitudinalMetres > halfLength)
                length = 1f - Clamp01((longitudinalMetres - halfLength) / frontMargin);
            return autumnWeight * speed * side * length;
        }

        private static float SmoothStep(float value)
        {
            value = Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float Clamp01(float value)
        {
            return Math.Max(0f, Math.Min(1f, value));
        }
    }
}
