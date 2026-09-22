using System;
using System.IO;

namespace DVSeasons.Core
{
    public enum BlizzardNotice : byte { None, Early, OneHour, Ending }

    // Host-owned elapsed game time; rewinds, real-world downtime and client clocks
    // never independently choose a storm or consume its duration.
    public sealed class BlizzardState
    {
        public double Hours, NextHours, StartHours, EndHours, EarlyHours, EndingHours, OutageHours;
        public double EarliestStartHours;
        public uint EventId, NoticeSequence;
        public BlizzardNotice Notice;
        public byte Notices;
        public long NoticeUtcTicks, HostUtcTicks;
        public bool Scheduled;
        public bool Active => Scheduled && Hours >= StartHours && Hours < EndHours;
        public bool Blackout => Active && Hours >= OutageHours;
        public float Strength => Active ? (float)Math.Min(1, (Hours - StartHours) / .15) : 0f;
        public BlizzardState Clone() => (BlizzardState)MemberwiseClone();

        public bool IsValid()
        {
            foreach (double v in new[] { Hours, NextHours, StartHours, EndHours, EarlyHours, EndingHours, OutageHours, EarliestStartHours })
                if (double.IsNaN(v) || double.IsInfinity(v) || v < 0 || v > 1e9) return false;
            return (byte)Notice <= 3 && Notices <= 7 && NoticeUtcTicks >= 0 && NoticeUtcTicks <= DateTime.MaxValue.Ticks &&
                HostUtcTicks >= 0 && HostUtcTicks <= DateTime.MaxValue.Ticks && (!Scheduled ||
                (StartHours - EarlyHours >= 1.999 && StartHours - EarlyHours <= 4.001 &&
                 EndHours - StartHours >= 3.999 && EndHours - StartHours <= 36.001 &&
                 EndHours - EndingHours >= .999 && EndHours - EndingHours <= 2.001 &&
                 OutageHours >= StartHours && OutageHours < EndHours));
        }

        public void Schedule(Random random, long utcTicks)
        {
            if (Active) return;
            EventId++; Scheduled = true; Notices = 0; Notice = BlizzardNotice.None;
            double lead = 2 + random.NextDouble() * 2;
            EarlyHours = Math.Max(Hours, EarliestStartHours - lead); StartHours = EarlyHours + lead;
            EndHours = StartHours + 4 + random.NextDouble() * 32;
            EndingHours = EndHours - 1 - random.NextDouble();
            OutageHours = StartHours + .25 + random.NextDouble() * 1.75;
            if (Hours >= EarlyHours) Announce(BlizzardNotice.Early, 1, utcTicks);
        }

        public bool Advance(double elapsedHours, bool winter, bool enabled, Random random, long utcTicks)
        {
            var oldActive = Active; var oldBlackout = Blackout; var oldSequence = NoticeSequence; var oldScheduled = Scheduled;
            if (elapsedHours >= 0 && elapsedHours <= 31 * 24) Hours += elapsedHours;
            if (Scheduled && Hours >= EndHours) EarliestStartHours = Math.Max(EarliestStartHours, EndHours + 240);
            if (!winter || !enabled)
            {
                Cancel(); NextHours = 0;
                return oldScheduled;
            }
            if (Scheduled && Hours >= EndHours) Cancel();
            if (!Scheduled)
            {
                if (NextHours <= 0) NextHours = Math.Max(Hours + 8 + random.NextDouble() * 28, EarliestStartHours - 4);
                if (Hours >= NextHours) Schedule(random, utcTicks);
            }
            if (Scheduled)
            {
                if (Hours >= EarlyHours && Hours < StartHours - 1 && (Notices & 1) == 0)
                    Announce(BlizzardNotice.Early, 1, utcTicks);
                // Sleeping past a threshold must not broadcast an obsolete forecast.
                if (Hours >= StartHours) Notices |= 3;
                else if (Hours >= StartHours - 1 && (Notices & 2) == 0) Announce(BlizzardNotice.OneHour, 2, utcTicks);
                if (Hours >= EndingHours && (Notices & 4) == 0) Announce(BlizzardNotice.Ending, 4, utcTicks);
            }
            return oldActive != Active || oldBlackout != Blackout || oldScheduled != Scheduled || oldSequence != NoticeSequence;
        }

        public void Cancel()
        {
            if (Scheduled)
            {
                if (Hours >= StartHours) EarliestStartHours = Math.Max(EarliestStartHours, Math.Min(Hours, EndHours) + 240);
                NextHours = Math.Max(Hours + 8 + (EventId * 37 % 25), EarliestStartHours - 4);
            }
            Scheduled = false; Notice = BlizzardNotice.None;
        }

        private void Announce(BlizzardNotice notice, byte mask, long utcTicks)
        {
            Notice = notice; Notices |= mask; NoticeSequence++;
            // A short common lead gives reliable MP packets time to reach listeners.
            NoticeUtcTicks = utcTicks + 2 * TimeSpan.TicksPerSecond;
        }

        public void WriteTo(BinaryWriter w)
        {
            w.Write(Hours); w.Write(NextHours); w.Write(StartHours); w.Write(EndHours);
            w.Write(EarlyHours); w.Write(EndingHours); w.Write(OutageHours);
            w.Write(EventId); w.Write(NoticeSequence); w.Write((byte)Notice); w.Write(Notices);
            w.Write(NoticeUtcTicks); w.Write(HostUtcTicks); w.Write(Scheduled);
            w.Write(EarliestStartHours);
        }
        public static BlizzardState ReadFrom(BinaryReader r) => new BlizzardState
        {
            Hours = r.ReadDouble(), NextHours = r.ReadDouble(), StartHours = r.ReadDouble(), EndHours = r.ReadDouble(),
            EarlyHours = r.ReadDouble(), EndingHours = r.ReadDouble(), OutageHours = r.ReadDouble(),
            EventId = r.ReadUInt32(), NoticeSequence = r.ReadUInt32(), Notice = (BlizzardNotice)r.ReadByte(),
            Notices = r.ReadByte(), NoticeUtcTicks = r.ReadInt64(), HostUtcTicks = r.ReadInt64(), Scheduled = r.ReadBoolean(),
            EarliestStartHours = r.ReadDouble()
        };
    }
}
