using System;
using System.IO;

namespace DVSeasons.Core
{
    public enum ColdStartHintStage : byte { None, Starter, Primer }
    public struct ColdStartHintState
    {
        public static readonly ColdStartHintState[] Empty=new ColdStartHintState[0];
        public string CarId;
        public ColdStartHintStage Stage;
        public float RemainingSeconds;
        public bool De6;
        public bool IsValid() => new VehicleThermalNetworkState{CarId=CarId}.IsValid() &&
            (Stage==ColdStartHintStage.Starter || Stage==ColdStartHintStage.Primer) &&
            RemainingSeconds>=0 && RemainingSeconds<=60 && (Stage!=ColdStartHintStage.Primer || (De6 && RemainingSeconds<=3));
        public static bool IsValid(ColdStartHintState[] values)
        {
            if(values==null || values.Length>VehicleThermalNetworkState.MaxVehicles)return false;
            // Only currently starting engines are sent, normally zero or one.
            for(int i=0;i<values.Length;i++)
            {
                if(!values[i].IsValid())return false;
                for(int j=0;j<i;j++)if(string.Equals(values[i].CarId,values[j].CarId,StringComparison.OrdinalIgnoreCase))return false;
            }
            return true;
        }
        public ColdStartHintState After(float seconds)
        {
            var copy=this;
            // Primer time advances only while the actual lever is held on the
            // host. A receiver must never count down an unpressed primer.
            if(Stage==ColdStartHintStage.Starter && seconds>=0 && !float.IsInfinity(seconds))
                copy.RemainingSeconds=Math.Max(0,RemainingSeconds-seconds);
            return copy;
        }
        internal static void WriteTo(BinaryWriter writer,ColdStartHintState[] values)
        {
            if(!IsValid(values))throw new InvalidDataException("Invalid cold start hint state.");
            writer.Write((ushort)values.Length);
            foreach(var value in values)
            {
                writer.Write((byte)value.CarId.Length);foreach(char c in value.CarId)writer.Write((byte)c);
                writer.Write((byte)value.Stage);writer.Write(value.RemainingSeconds);writer.Write(value.De6);
            }
        }
        internal static ColdStartHintState[] ReadFrom(BinaryReader reader)
        {
            int count=reader.ReadUInt16();if(count>VehicleThermalNetworkState.MaxVehicles)throw new InvalidDataException("Too many cold start hints.");
            if(count==0)return Empty;
            var values=new ColdStartHintState[count];var chars=new char[VehicleThermalNetworkState.MaxIdLength];
            for(int i=0;i<count;i++)
            {
                int length=reader.ReadByte();if(length==0 || length>chars.Length)throw new InvalidDataException("Invalid cold start car ID.");
                for(int j=0;j<length;j++)chars[j]=(char)reader.ReadByte();
                values[i]=new ColdStartHintState{CarId=new string(chars,0,length),Stage=(ColdStartHintStage)reader.ReadByte(),
                    RemainingSeconds=reader.ReadSingle(),De6=reader.ReadBoolean()};
            }
            if(!IsValid(values))throw new InvalidDataException("Invalid cold start hints.");
            return values;
        }
    }
}
