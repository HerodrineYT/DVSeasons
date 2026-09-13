using System.IO;
using DVSeasons.Core;
using DVSeasons.Multiplayer;
using Xunit;

namespace MPAPI.Interfaces.Packets
{
    public interface ISerializablePacket {void Serialize(BinaryWriter writer);void Deserialize(BinaryReader reader);}
}
public sealed class CabHeaterTransferTests
{
    [Fact]
    public void SnapshotPreservesSeparateCarsIncludingOffAndReplacesOldSession()
    {
        var packet=new CabHeaterSnapshotPacket();packet.States["de6-a"]=1;packet.States["de6-b"]=0;
        using(var stream=new MemoryStream())
        {
            packet.Serialize(new BinaryWriter(stream));stream.Position=0;
            var restored=new CabHeaterSnapshotPacket();restored.States["old-session"]=1;
            restored.Deserialize(new BinaryReader(stream));
            Assert.Equal(2,restored.States.Count);Assert.Equal(1,restored.States["de6-a"]);Assert.Equal(0,restored.States["de6-b"]);
        }
    }
    [Theory]
    [InlineData(-1)] [InlineData(1025)]
    public void RejectsInvalidSnapshotSize(int count)
    {
        using(var stream=new MemoryStream()) {new BinaryWriter(stream).Write(count);stream.Position=0;
            Assert.Throws<InvalidDataException>(()=>new CabHeaterSnapshotPacket().Deserialize(new BinaryReader(stream)));}
    }
    [Fact]
    public void ClientMayOnlyChangeTheLocomotiveItOccupies()
    {
        Assert.True(CabHeaterAccess.CanChange("a","a",true,true,true,1));
        Assert.False(CabHeaterAccess.CanChange("b","a",true,true,true,1));
        Assert.False(CabHeaterAccess.CanChange("a","a",false,true,true,1));
        Assert.False(CabHeaterAccess.CanChange("a","a",true,false,true,1));
        Assert.False(CabHeaterAccess.CanChange("a","a",true,true,false,1));
        Assert.False(CabHeaterAccess.CanChange("a","a",true,true,true,float.NaN));
    }
    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(-1f)] [InlineData(.5f)]
    public void RejectsMalformedCommand(float level)
    {
        using(var stream=new MemoryStream()) {new CabHeaterChangePacket {CarId="a",Level=level}.Serialize(new BinaryWriter(stream));stream.Position=0;
            Assert.Throws<InvalidDataException>(()=>new CabHeaterChangePacket().Deserialize(new BinaryReader(stream)));}
    }
}
