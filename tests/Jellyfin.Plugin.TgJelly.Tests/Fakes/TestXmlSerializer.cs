using System;
using System.IO;
using System.Xml.Serialization;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// Mirrors Jellyfin's MyXmlSerializer (a thin wrapper over XmlSerializer), which is not in the NuGet packages.
/// </summary>
public sealed class TestXmlSerializer : IXmlSerializer
{
    public object DeserializeFromStream(Type type, Stream stream) => new XmlSerializer(type).Deserialize(stream)!;

    public void SerializeToStream(object obj, Stream stream) => new XmlSerializer(obj.GetType()).Serialize(stream, obj);

    public void SerializeToFile(object obj, string file)
    {
        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
        SerializeToStream(obj, stream);
    }

    public object DeserializeFromFile(Type type, string file)
    {
        using var stream = File.OpenRead(file);
        return DeserializeFromStream(type, stream);
    }

    public object DeserializeFromBytes(Type type, byte[] buffer)
    {
        using var stream = new MemoryStream(buffer, writable: false);
        return DeserializeFromStream(type, stream);
    }
}
