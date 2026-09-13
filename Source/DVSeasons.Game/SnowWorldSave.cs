using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Logical state only: no render targets, transient instance IDs or wall clock.
    [Serializable] internal sealed class SnowWorldSave
    {
        public int Version = 1;
        public bool HasCoverage;
        public float Coverage;
        public List<RailSnowStamp> Rails = new List<RailSnowStamp>();
        public List<CarSnowState> Cars = new List<CarSnowState>();
        public List<CabFrostState> Cabs = new List<CabFrostState>();
        public List<VehicleSnowMask> VehicleMasks = new List<VehicleSnowMask>();
        public List<WindowSnowMask> WindowMasks = new List<WindowSnowMask>();
        public static bool Unit(float value) { return value >= 0 && value <= 1; }
        public string Encode()
        {
            using(var output=new System.IO.MemoryStream())
            {
                using(var zip=new System.IO.Compression.DeflateStream(output,System.IO.Compression.CompressionLevel.Fastest,true))
                using(var writer=new System.IO.BinaryWriter(zip))
                {
                    writer.Write(0x4456534e);writer.Write(Version);writer.Write(HasCoverage);writer.Write(Coverage);
                    writer.Write(Rails.Count);
                    foreach(var r in Rails) { WriteVector(writer,r.A);WriteVector(writer,r.B);WriteVector(writer,r.Width);writer.Write(r.Age); }
                    writer.Write(Cars.Count);
                    foreach(var c in Cars) { WriteText(writer,c.Id);writer.Write(c.Melted); }
                    writer.Write(Cabs.Count);
                    foreach(var c in Cabs)
                    {
                        WriteText(writer,c.Id);var s=c.Climate;writer.Write(s.Initialized);writer.Write(s.EngineWarmth);
                        writer.Write(s.Heater);writer.Write(s.Cabin);writer.Write(s.Glass);writer.Write(s.Frost);writer.Write(s.Fog);
                    }
                    writer.Write(VehicleMasks.Count);
                    foreach(var m in VehicleMasks)
                    {
                        m.Pack();WriteText(writer,m.Id);WriteText(writer,m.Packed);
                        writer.Write(m.Area.x);writer.Write(m.Area.y);writer.Write(m.Area.z);writer.Write(m.Area.w);
                    }
                    writer.Write(WindowMasks.Count);
                    foreach(var m in WindowMasks) { WriteText(writer,m.Id);WriteText(writer,m.Packed); }
                }
                return Convert.ToBase64String(output.ToArray());
            }
        }
        public static SnowWorldSave Decode(string encoded)
        {
            if(encoded.Length>128*1024*1024) throw new System.IO.InvalidDataException("Snow snapshot too large");
            using(var input=new System.IO.MemoryStream(Convert.FromBase64String(encoded)))
            using(var zip=new System.IO.Compression.DeflateStream(input,System.IO.Compression.CompressionMode.Decompress))
            using(var reader=new System.IO.BinaryReader(zip))
            {
                if(reader.ReadInt32()!=0x4456534e || reader.ReadInt32()!=1) throw new System.IO.InvalidDataException("Unknown snow snapshot version");
                var result=new SnowWorldSave {HasCoverage=reader.ReadBoolean(),Coverage=reader.ReadSingle()};
                int count=ReadCount(reader,65536);
                for(int i=0;i<count;i++) result.Rails.Add(new RailSnowStamp {A=ReadVector(reader),B=ReadVector(reader),Width=ReadVector(reader),Age=reader.ReadSingle()});
                count=ReadCount(reader,16384);
                for(int i=0;i<count;i++) result.Cars.Add(new CarSnowState {Id=ReadText(reader,80),Melted=reader.ReadSingle()});
                count=ReadCount(reader,16384);
                for(int i=0;i<count;i++) result.Cabs.Add(new CabFrostState {Id=ReadText(reader,80),Climate=new WindowClimateState {
                    Initialized=reader.ReadBoolean(),EngineWarmth=reader.ReadSingle(),Heater=reader.ReadSingle(),Cabin=reader.ReadSingle(),
                    Glass=reader.ReadSingle(),Frost=reader.ReadSingle(),Fog=reader.ReadSingle()} });
                count=ReadCount(reader,16384);
                for(int i=0;i<count;i++) result.VehicleMasks.Add(new VehicleSnowMask {Id=ReadText(reader,80),Packed=ReadText(reader,200000),
                    Area=new Vector4(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle())});
                count=ReadCount(reader,65536);
                for(int i=0;i<count;i++) result.WindowMasks.Add(new WindowSnowMask {Id=ReadText(reader,4096),Packed=ReadText(reader,60000)});
                if(reader.BaseStream.ReadByte()!=-1) throw new System.IO.InvalidDataException("Trailing snow data");
                return result;
            }
        }
        private static int ReadCount(System.IO.BinaryReader reader,int max)
        { int count=reader.ReadInt32();if(count<0 || count>max) throw new System.IO.InvalidDataException("Invalid snow count");return count; }
        private static void WriteText(System.IO.BinaryWriter writer,string text)
        { var bytes=System.Text.Encoding.UTF8.GetBytes(text??"");writer.Write(bytes.Length);writer.Write(bytes); }
        private static string ReadText(System.IO.BinaryReader reader,int max)
        {
            int size=ReadCount(reader,max);var bytes=reader.ReadBytes(size);
            if(bytes.Length!=size) throw new System.IO.EndOfStreamException();
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        private static void WriteVector(System.IO.BinaryWriter writer,Vector3 v) {writer.Write(v.x);writer.Write(v.y);writer.Write(v.z);}
        private static Vector3 ReadVector(System.IO.BinaryReader reader) {return new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());}
    }
    [Serializable] internal sealed class RailSnowStamp
    {
        public Vector3 A, B, Width;
        public float Age;
        public bool IsValid()
        { return SnowWorldSave.Unit(Age) && Finite(A) && Finite(B) && Finite(Width) &&
            (B-A).sqrMagnitude < 225 && Width.sqrMagnitude < 1; }
        private static bool Finite(Vector3 v)
        { return Math.Abs(v.x)<1000000 && Math.Abs(v.y)<1000000 && Math.Abs(v.z)<1000000; }
    }
    [Serializable] internal sealed class CarSnowState { public string Id; public float Melted; }
    [Serializable] internal sealed class CabFrostState { public string Id; public WindowClimateState Climate; }
    [Serializable] internal sealed class WindowSnowMask { public string Id, Packed; }
    [Serializable] internal sealed class VehicleSnowMask
    {
        public string Id, Packed;
        public Vector4 Area;
        [NonSerialized] public byte[] Raw;
        public void Pack()
        {
            if(Raw==null || Packed!=null) return;
            using(var stream=new System.IO.MemoryStream())
            {
                using(var compressor=new System.IO.Compression.DeflateStream(stream,System.IO.Compression.CompressionLevel.Fastest,true))
                    compressor.Write(Raw,0,Raw.Length);
                Packed=Convert.ToBase64String(stream.ToArray());
            }
        }
        public byte[] Unpack()
        {
            if(Raw!=null) return Raw;
            if(string.IsNullOrEmpty(Packed) || Packed.Length>200000) return null;
            using(var input=new System.IO.MemoryStream(Convert.FromBase64String(Packed)))
            using(var decompressor=new System.IO.Compression.DeflateStream(input,System.IO.Compression.CompressionMode.Decompress))
            {
                var bytes=new byte[256*256*2]; int total=0, read;
                while(total<bytes.Length && (read=decompressor.Read(bytes,total,bytes.Length-total))>0) total+=read;
                if(total!=bytes.Length || decompressor.ReadByte()!=-1) return null;
                return Raw=bytes;
            }
        }
    }
}
